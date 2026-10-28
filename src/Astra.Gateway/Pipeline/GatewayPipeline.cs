using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Plugins;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;
using Astra.Gateway.Protocol.Responses;
using Astra.Gateway.Routing;
using Astra.Gateway.Tracing;
using Astra.Plugins.Host;
using Astra.Plugins.Middleware;
using Astra.Providers.Subscription;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The gateway request pipeline (plan §6): authenticate the local client key, pick the upstream endpoint,
/// forward (pass-through fast path) or translate through the IR, stream the answer back, and record timing,
/// usage and cost for every request. What it needs from its host is a contract (PipelineContracts.cs): the server's
/// SQLite-backed services, or the relay's per-request ones; a null optional feature is one the host does not have.
/// </summary>
public sealed class GatewayPipeline(
    IGatewayRouter router,
    CodecRegistry codecs,
    GatewayHttpClients http,
    IUpstreamAuthResolver auth,
    AccountScheduler scheduler,
    IEffectiveModelResolver models,
    IModelProtocolResolver modelProtocols,
    IGatewaySettings settings,
    PrivacyGuardService? privacy,
    BodyStore? bodies,
    IRequestSink usageWriter,
    LiveRequestFeed live,
    MiddlewareEngine? middleware,
    PluginFetchHandler? pluginTransport,
    TokenLimiter? limiter,
    TraceLinker linker,
    PoolRouting? pools,
    ProviderPoolScheduler poolScheduler,
    ProviderHealth health,
    ISmartRouter smartRouter,
    ILogger<GatewayPipeline> logger,
    RequestBodyLimits? bodyLimits = null)
{
    // A plugin provider's requests go through its plugin (PluginFetchHandler), not the network.
    private readonly HttpClient? _pluginClient = pluginTransport is null ? null : new(pluginTransport, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly RequestBodyLimits _bodyLimits = bodyLimits ?? RequestBodyLimits.Default;

    internal const long MaxRequestBytes = 64L * 1024 * 1024;
    private const int MaxErrorBodyBytes = 1024 * 1024;
    private const string DefaultAnthropicVersion = "2023-06-01";

    /// <summary><c>astra.attempt.retry</c> of an attempt whose failure handed the request to the next pool member.</summary>
    public const string ProviderFailoverRetry = "provider_failover";
    private static readonly string UserAgent = $"Astra/{typeof(GatewayPipeline).Assembly.GetName().Version?.ToString(3) ?? "0"}";

    // Top-level Responses fields the ChatGPT (Codex) backend rejects with 400 "Unsupported parameter".
    private static readonly string[] CodexUnsupportedParams =
        ["user", "safety_identifier", "max_output_tokens", "temperature", "top_p", "prompt_cache_retention"];

    // Rate-limit state an attempt's response reports, kept on its span (.plan/observability.md §2.3).
    private static readonly string[] RateLimitHeaders =
    [
        "retry-after", "x-ratelimit-remaining-requests", "x-ratelimit-remaining-tokens", "x-ratelimit-limit-requests",
        "x-ratelimit-reset-requests", "anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-tokens-remaining",
        "anthropic-ratelimit-unified-status", "anthropic-ratelimit-unified-reset",
    ];

    /// <summary>Handles one generation request (chat / responses / messages / generateContent).</summary>
    /// <param name="pathModel">Gemini: model from the URL.</param>
    /// <param name="pathStream">Gemini: streamGenerateContent (true) or generateContent (false).</param>
    /// <param name="pathAltSse">Gemini streaming: whether the client passed ?alt=sse (without it the answer is a JSON array).</param>
    public async Task HandleAsync(HttpContext ctx, ApiProtocol inbound, string? pathModel = null, bool? pathStream = null, bool? pathAltSse = null)
    {
        var sw = Stopwatch.StartNew();
        var ct = ctx.RequestAborted;
        var s = settings.Current;
        var record = new RequestRecord
        {
            Id = Ulid.NewUlid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            InboundProtocol = inbound.ToId(),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 300),
            Remote = Http.RequestOrigin.IsRemote(ctx),
            ClientIp = ClientIp(ctx),
        };
        ctx.Response.Headers["x-astra-request-id"] = record.Id;
        // Live request log: the request shows up the moment it arrives, then follows its progress.
        live.Start(record);
        var capture = s.DebugBodies && bodies is not null ? new BodyCapture() : null;
        // Tracing (.plan/observability.md §2): the request's spans, and where it sits in its agent's loop. The agent's
        // traceparent only names the parent; nothing of it goes upstream (GatewayHttpClients).
        var trace = new RequestTrace();
        var (inboundTraceId, inboundSpanId) = TraceKeys.ParseTraceparent(ctx.Request.Headers["traceparent"].ToString());
        record.ParentSpanId = inboundSpanId;
        TraceLink? link = null;
        TraceSpanBuilder? responseSpan = null;
        var restorable = 0;
        var observer = new ResponseObserver
        {
            FirstToken = ttft =>
            {
                record.TtftMs = ttft;
                responseSpan?.Event("first_token");
                live.Update(record);
            },
            // thinking → writing → calling a tool: what the agent sees the model doing, live (never stored)
            StageChanged = stage =>
            {
                record.LiveStage = stage;
                // bounded: a long agentic answer can switch stage on every tool call
                if ((record.LivePhases?.Length ?? 0) < 2048)
                    record.LivePhases = $"{(record.LivePhases is null ? "" : record.LivePhases + ",")}{stage.Replace(",", "").Replace("@", "")}@{sw.ElapsedMilliseconds}";
                live.Update(record);
            },
        };
        EffectiveModel? effective = null;
        Provider? provider = null;
        IResponseDecoder? decoder = null;
        IResponseEncoder? encoder = null;
        AccountScheduler.Lease? accountLease = null;
        // Provider pools (issue #2): the request's plan (probe slots) and its in-flight hold on the member it went to.
        PoolPlan? poolPlan = null;
        ProviderPoolScheduler.Lease? poolLease = null;
        MiddlewareRun? middlewareRun = null;
        MiddlewareResponseStream? middlewareStream = null;
        // Pooled: returned when the request is over (a send still running keeps its own reference).
        RequestBody? requestBody = null;
        PooledBuffer? upstreamPayload = null;

        try
        {
            var routeSpan = trace.Start(TraceSpanNames.Route);
            var route = await router.ResolveAsync(ctx.Request, ct);
            provider = route.Provider;
            record.ClientKind = route.ClientKind;
            record.TokenId = route.Token.Id;
            record.TokenName = route.Token.Name;
            record.ProviderId = provider.Id;
            record.ProviderName = provider.Name;
            live.Update(record);

            // Token caps (plan: alerts-webhooks-token-limits §1.4): once per request, before anything goes upstream.
            if (limiter is not null) await limiter.CheckAsync(route.Token, ct);
            routeSpan.Set("astra.client.kind", route.ClientKind).Set("astra.token.id", route.Token.Id).Set("astra.provider.id", provider.Id).Ok().End();
            // Read once into a pooled UTF-8 buffer; from here on the body is these bytes and one tree over them.
            var bodySpan = trace.Start(TraceSpanNames.Body);
            requestBody = await RequestBody.ReadAsync(ctx.Request, _bodyLimits.MaxBytes, ct, _bodyLimits.ReadTimeout);
            bodySpan.Set("astra.body.bytes", requestBody.Utf8.Length).Ok().End();
            capture?.Set(BodyStore.ClientRequest, requestBody.Utf8);

            // Gateway middleware plugins (magpie-compatible; .plan/plugins.md) see the agent's body in its own
            // API first, before the privacy guard — which still applies to whatever they send on — and the
            // reply on its way back to the agent. A reject is answered in the agent's API's error shape.
            var middlewareStart = Stopwatch.GetTimestamp();
            middlewareRun = middleware is null ? null : await middleware.BeginAsync(() =>
                GatewayMiddleware.Info(inbound, requestBody.Utf8, ctx.Request.Path,
                    route.ClientKind is { } kind ? ClientKinds.AgentIdOf(kind) : null, StripModels(pathModel), pathStream), ct);
            if (middlewareRun is not null)
            {
                var middlewareSpan = trace.StartAt(TraceSpanNames.Middleware, middlewareStart);
                var bytesBefore = requestBody.Utf8.Length;
                var (changed, reject) = middlewareRun.Request(requestBody.Utf8.ToArray());
                if (reject is not null)
                {
                    middlewareSpan.Error("rejected").End();
                    record.Status = RequestStatus.Blocked;
                    throw GatewayMiddleware.Rejected(reject);
                }
                middlewareSpan.Set("astra.middleware.rewritten", !requestBody.Utf8.SequenceEqual(changed))
                    .Set("astra.body.bytes_before", bytesBefore).Set("astra.body.bytes", changed.Length).Ok().End();
                requestBody.Replace(changed);
                middlewareStream = MiddlewareResponseStream.Install(ctx, middlewareRun);
            }

            // Privacy guard (plan §6.7): runs on the client body before anything leaves the machine. A JSON object
            // is parsed once and redacted in place — the tree the rest of the pipeline reads. Anything else takes
            // the text path, which reports it (or blocks it) exactly as before.
            var parsed = requestBody.Parse();
            // Where this call sits in the agent's loop (.plan/observability.md §3), read from the client's own body
            // before the guard can rewrite it.
            link = parsed?.Object is { } clientTree
                ? LinkOf(record.Id, inbound, clientTree, ctx.Request.Headers, inboundTraceId)
                : linker.Link(record.Id, null, null, new LastMessage([], true), inboundTraceId);
            ApplyLink(record, link);
            if (parsed?.Object is { } asked)
            {
                var shape = TraceKeys.ShapeOf(inbound, asked);
                trace.Root.Set("astra.request.messages", shape.Messages).Set("astra.request.tools", shape.Tools)
                    .Set("astra.request.system_chars", shape.SystemChars).Set("gen_ai.request.max_tokens", shape.MaxTokens);
            }
            var privacySpan = trace.Start(TraceSpanNames.Privacy);
            // A host without a guard passes the body on as a switched-off guard does.
            var guard = privacy is null
                ? PrivacyGuardResult.PassThrough with { Body = parsed is null ? requestBody.Text() : "" }
                : parsed is (var root, var tree)
                    ? privacy.InspectJson(root, tree, route.ClientKind)
                    : privacy.Inspect(requestBody.Text(), route.ClientKind);
            restorable = guard.RestoreMap.Count;
            privacySpan.Set("astra.privacy.applied", guard.Applied).Set("astra.privacy.hits", guard.Hits.Sum(h => (long)h.Count))
                .Set("astra.privacy.categories", guard.Hits.Count == 0 ? null : string.Join(",", guard.Hits.Select(h => h.Category).Distinct()))
                .Set("astra.privacy.rewritten", guard.Rewritten).Set("astra.privacy.dry_run", guard.DryRun)
                .Set("astra.privacy.restorable", restorable);
            if (guard.Blocked) privacySpan.Error("blocked");
            else privacySpan.Ok();
            privacySpan.End();
            if (guard.Applied && guard.Hits.Count > 0)
                record.PrivacyJson = PrivacyGuardService.ReportJson(guard.DryRun, guard.Blocked, guard.Hits, guard.RestoreMap.Count);
            if (guard.Blocked)
            {
                record.Status = RequestStatus.Blocked;
                throw new GatewayException(400, "invalid_request_error", "请求中包含被 Astra 隐私护栏拦截的敏感信息，已拒绝发送到上游。详情见 Astra 请求日志。");
            }

            var body = parsed?.Object ?? ParseObject(guard.Body);
            // Whether the client's own bytes can go upstream as they are: the tree is theirs and nothing changed it.
            var bodyIsClientBytes = parsed is not null && !guard.Rewritten;
            // Log-only metadata (plan §6.5): what the client itself set, read before any codec derives
            // upstream budgets from it. Never touches the wire and never throws.
            RequestReasoning.Capture(record, inbound, body);
            var requestedModel = (inbound == ApiProtocol.Gemini ? StripModels(pathModel) : null) ?? Str(body, "model") ?? "";
            var isClaudeCode = inbound == ApiProtocol.Anthropic && ClaudeCodeDetector.IsClaudeCode(ctx.Request.Headers, body);
            // Read once from the client's own tree, before any attempt rewrites it: the conversation identity (the
            // rewrites touch model / stream_options / codex fields, none of which it reads), whether an OpenAI Chat client
            // asked for the usage chunk itself (a pass-through attempt injects include_usage) and its cache TTL option.
            var stickyKey = StickyKeys.Of(inbound, body, ctx.Request.Headers);
            var clientWantsChatUsage = inbound == ApiProtocol.OpenAIChat
                                       && body["stream_options"] is JsonObject so && so["include_usage"] is JsonValue iu && iu.TryGetValue<bool>(out var wants) && wants;
            var clientCacheTtl = ClientOptions.GetCacheTtl(route.Client?.ExtraJson);
            var clientCodec = codecs.Get(inbound);
            // Decoded from the client's tree the first time a translated attempt needs it; every encode starts from the
            // decoder's own warnings, so a retry reports only its own lossy mappings.
            UnifiedRequest? ir = null;
            var irDecodeWarnings = 0;

            // Where the request may go, in order. A client bound to several providers: the first one serving this model —
            // as an enabled model or through its model mapping — else the primary (ClientRouting). A token's provider pool
            // (issue #2): the members its strategy orders, the next one tried while an attempt fails before anything
            // reached the client. Decided before the client policy and capability discovery below.
            var resolveSpan = trace.Start(TraceSpanNames.Resolve);
            // Smart routing (plan §5): the virtual model becomes the model of the grade chosen for this request, before any
            // provider is picked — the route's ClientModelFor / UpstreamModelFor carry it on from here, the client's own body
            // stays untouched. The judge's time counts as part of resolving. Off, the virtual model is refused here.
            if (smartRouter.IsVirtual(requestedModel))
            {
                RoutingDecision decision;
                var caller = route;
                try
                {
                    decision = await smartRouter.DecideAsync(new RoutingInput(route.Token.Id, inbound, body, link!)
                    {
                        Caller = route.ClientKind ?? route.Token.Name,
                        // A grade this caller cannot use (its provider out of reach, or no longer serving the model) gives way.
                        ProblemOf = async (grade, token) => (await router.GradeUseAsync(caller, grade.ProviderId, grade.Model, isClaudeCode, token)).Problem,
                    }, ct);
                }
                catch (GatewayException)
                {
                    resolveSpan.Error("smart routing is off").End();
                    throw;
                }
                route = route with { Routed = new RoutedModel(requestedModel, decision.Model, decision.GradeId, decision.ProviderId) };
                // The grade's requests go to the provider it names — not to whichever of the caller's own providers serves
                // the same model id first. A grade nobody here can use keeps the caller's own routing below.
                if (decision.ProviderId is not null
                    && await router.GradeTargetAsync(caller, decision.ProviderId, decision.Model, isClaudeCode, ct) is { } target)
                    route = GatewayRouter.WithTarget(route, target);
                decision.Annotate(resolveSpan);
                // The request log shows how this request was routed — judged, reused, fallen back or moved — at a glance.
                record.RoutingJson = Json.Serialize(decision.Report());
            }
            List<GatewayRoute> candidates;
            var maxAttempts = 1;
            if (route.Pool is { } pool && pools is not null)
            {
                poolPlan = await pools.PlanAsync(route, route.ClientModelFor(requestedModel), isClaudeCode, stickyKey, commit: true, ct);
                candidates = poolPlan.Order.Select(t => route with { Provider = t.Provider, AccountId = t.AccountId }).ToList();
                maxAttempts = poolPlan.MaxAttempts;
                resolveSpan.Set("astra.pool.strategy", pool.Strategy).Set("astra.pool.candidates", candidates.Count)
                    .Set("astra.pool.max_attempts", maxAttempts);
            }
            else candidates = [await router.ForModelAsync(route, route.ClientModelFor(requestedModel), isClaudeCode, ct)];

            var attempts = new AttemptTrace();
            var sends = 0;
            var nextCandidate = 1;
            UpstreamPlan plan = null!;

            // One member's version of the request: its model and protocol, its endpoint, the tree and bytes it is sent.
            // Sets the record and `provider` / `effective` as it goes, so a refusal here is logged against this member.
            // While another attempt may follow (mayRetry), the client's tree is never rewritten: the next member gets it
            // as the privacy guard left it (re-reading the request bytes instead would undo a redaction).
            async Task<UpstreamPlan> PrepareAsync(GatewayRoute candidate, bool mayRetry, TraceSpanBuilder span)
            {
                provider = candidate.Provider;
                record.ProviderId = provider.Id;
                record.ProviderName = provider.Name;
                live.Update(record);
                // Enforce the client policy before capability discovery sends anything upstream.
                SubscriptionSupport.EnforceClientPolicy(provider, isClaudeCode);
                // 按实际发给上游的模型选择协议；Copilot 存量模型缺少能力时先自动补齐，
                // 不支持入站协议就经 IR 翻译，不能仅因提供商有 Responses 端点便直通。
                var resolvedModel = await models.ResolveProviderModelAsync(provider, candidate.UpstreamModelFor(requestedModel), ct);
                // Capability discovery may go to the network (a /models listing); timed on its own.
                var capabilitiesStart = Stopwatch.GetTimestamp();
                var supportedProtocols = await modelProtocols.ResolveAsync(provider, resolvedModel, candidate.AccountId, ct);
                span.Set("astra.resolve.capabilities_us", RequestTrace.Micros(capabilitiesStart, Stopwatch.GetTimestamp()));
                var endpoint = GatewayRouter.SelectEndpoint(provider, inbound, supportedProtocols);
                var upstreamProtocol = endpoint.Protocol;
                var passthrough = upstreamProtocol == inbound;
                record.UpstreamProtocol = upstreamProtocol.ToId();
                record.Passthrough = passthrough;
                span.Set("astra.provider.id", provider.Id).Set("astra.provider.name", provider.Name)
                    .Set("gen_ai.request.model", requestedModel).Set("astra.protocol.upstream", upstreamProtocol.ToId())
                    .Set("astra.passthrough", passthrough).Ok().End();

                // Claude subscription (plan §5.4): for Claude Code itself on the pass-through path, a faithful relay of
                // its own headers; other callers get the minimal OAuth compatibility headers — or, when the user enabled
                // subscription.mimic_claude_code, the full Claude Code identity (see ClaudeCodeMimicry), which
                // Anthropic's client check requires from non-Haiku callers.
                var claudeMode = !SubscriptionSupport.IsClaudeSubscription(provider) || upstreamProtocol != ApiProtocol.Anthropic
                    ? ClaudeHeaderMode.None
                    : passthrough && isClaudeCode ? ClaudeHeaderMode.Relay : ClaudeHeaderMode.Compat;
                var claudeMimic = claudeMode == ClaudeHeaderMode.Compat && SubscriptionSupport.MimicClaudeCodeOf(provider);

                // Legal per plan §6.2, but worth a line in the log: the provider prefers this protocol, has no address for
                // it, and the request is translated into a lower-preference protocol instead of passing through.
                if (!passthrough && GatewayRouter.MissingEndpointFor(provider, inbound) is { } missing)
                    logger.LogInformation("Request {Id}: {Provider} prefers {Preferred} but has no endpoint for it; translating to {Upstream}",
                        record.Id, provider.Name, missing.ToId(), upstreamProtocol.ToId());

                var encodeSpan = trace.Start(TraceSpanNames.Encode);
                var upstreamCodec = codecs.Get(upstreamProtocol);
                if (!passthrough && (clientCodec is null || upstreamCodec is null))
                    throw new GatewayException(501, "api_error", $"尚不支持 {inbound.ToId()} → {upstreamProtocol.ToId()} 的协议转换。");

                // What the client asked for.
                UnifiedRequest? attemptIr = null;
                string model;
                bool stream;
                if (passthrough)
                {
                    model = (inbound == ApiProtocol.Gemini ? StripModels(pathModel) : null) ?? Str(body, "model")
                            ?? throw new ProtocolException("请求缺少 model。");
                    stream = inbound == ApiProtocol.Gemini ? pathStream == true : body["stream"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                }
                else
                {
                    if (ir is null)
                    {
                        ir = clientCodec!.DecodeRequest(body, new RequestDecodeContext { PathModel = StripModels(pathModel), PathStream = pathStream });
                        irDecodeWarnings = ir.Warnings.Count;
                    }
                    else ir.Warnings.RemoveRange(irDecodeWarnings, ir.Warnings.Count - irDecodeWarnings);
                    attemptIr = ir;
                    model = ir.Model;
                    stream = ir.Stream;
                    if (string.IsNullOrEmpty(model)) throw new ProtocolException("请求缺少 model。");
                    if (ir.PreviousResponseId is not null && upstreamProtocol != ApiProtocol.OpenAIResponses)
                        throw new ProtocolException($"previous_response_id 依赖 OpenAI Responses 的服务端会话，而提供商 {provider.Name} 的上游协议是 {upstreamProtocol.ToId()}。请让客户端发送完整对话历史（例如关闭 store / 会话续接）。");
                }
                record.RequestedModel = model;
                var upstreamModel = candidate.UpstreamModelFor(model);
                record.UpstreamModel = upstreamModel;
                // ChatGPT 订阅（Codex）的 backend 把请求钉死成 codex-cli 的形状：input 必须是数组、
                // store 必须是 false、stream 必须是 true（客户端要非流式时我们带 stream:true 发上去，
                // 再把完整响应一次性回给客户端）。因此上游的流式与否在这里就定下来了。
                var forceResponsesStream = provider.SettingFlag("force_stream", false) && upstreamProtocol == ApiProtocol.OpenAIResponses;
                var clientWantsStream = stream;
                if (forceResponsesStream) stream = true;
                record.Stream = stream;

                // Capabilities may await the network. Merge current billing data once, after the final upstream model is known.
                EffectiveModel attemptEffective;
                (_, attemptEffective) = upstreamModel == resolvedModel.ModelId
                    ? (resolvedModel, await models.ResolveAsync(provider, resolvedModel, ct))
                    : await models.ResolveByModelIdAsync(provider, upstreamModel, ct);
                effective = attemptEffective;
                record.SystemModelId = attemptEffective.SystemModelId;
                live.Update(record);

                // What we send upstream: one tree, serialized once at the end — or the client's bytes when untouched.
                JsonObject upstreamTree;
                bool upstreamDirty;
                var injectedChatUsage = false;
                if (passthrough)
                {
                    // A pool attempt another one may follow rewrites a copy: the client's tree stays as it came.
                    var mayRewrite = (upstreamModel != model && inbound != ApiProtocol.Gemini)
                                     || (upstreamProtocol == ApiProtocol.OpenAIChat && stream && !clientWantsChatUsage)
                                     || forceResponsesStream || claudeMimic || claudeMode == ClaudeHeaderMode.Relay
                                     || (clientCacheTtl is not null && upstreamProtocol == ApiProtocol.Anthropic)
                                     || (upstreamProtocol == ApiProtocol.Anthropic && AnthropicToolSchemas.NeedsNormalizingTools(body));
                    upstreamTree = mayRetry && mayRewrite ? (JsonObject)body.DeepClone() : body;
                    var rewritten = false;
                    if (upstreamModel != model && inbound != ApiProtocol.Gemini)
                    {
                        upstreamTree["model"] = upstreamModel; // Gemini carries the model in the URL instead.
                        rewritten = true;
                    }
                    // Anthropic rejects a tool schema with oneOf/allOf/anyOf at the top level; only such bodies are touched.
                    if (upstreamProtocol == ApiProtocol.Anthropic && AnthropicToolSchemas.NormalizeTools(upstreamTree))
                        rewritten = true;
                    if (upstreamProtocol == ApiProtocol.OpenAIChat && stream && !clientWantsChatUsage)
                    {
                        // Plan §4.2: always ask for usage; the extra usage chunk is filtered out before it reaches the client.
                        var options = upstreamTree["stream_options"] as JsonObject ?? new JsonObject();
                        options["include_usage"] = true;
                        upstreamTree["stream_options"] = options;
                        injectedChatUsage = true;
                        rewritten = true;
                    }
                    upstreamDirty = rewritten || !bodyIsClientBytes;
                }
                else
                {
                    upstreamTree = upstreamCodec!.EncodeRequest(ir!, new RequestEncodeContext
                    {
                        UpstreamModel = upstreamModel,
                        DefaultMaxOutputTokens = attemptEffective.MaxOutputTokens is { } max && max <= int.MaxValue ? (int)max : null,
                        EffortBudgets = s.EffortBudgets,
                        AutoCacheControl = provider.SettingFlag("auto_cache_control", true),
                    });
                    upstreamDirty = true;
                    // Copilot's GPT models are Responses-only: a Chat client without its own key gets one per conversation,
                    // or the upstream prompt cache rarely hits (PromptCacheKeys).
                    if (upstreamProtocol == ApiProtocol.OpenAIResponses && CopilotModelIds.IsCopilot(provider)
                        && upstreamTree["prompt_cache_key"] is null && PromptCacheKeys.Derive(ir!) is { } cacheKey)
                        upstreamTree["prompt_cache_key"] = cacheKey;
                    if (ir!.Warnings.Count > 0) logger.LogInformation("Request {Id}: lossy mapping: {Warnings}", record.Id, string.Join("; ", ir.Warnings));
                }
                if (forceResponsesStream)
                {
                    // codex backend: input 必须是数组、store 必须是 false、stream 必须是 true；codex-cli 不发的
                    // 采样/身份参数（user、max_output_tokens、temperature…）一律回 400 "Unsupported parameter"。
                    // 直通与翻译两条路都在这里收口。
                    if (upstreamTree["input"] is JsonValue inputValue && inputValue.TryGetValue<string>(out var inputText))
                    {
                        upstreamTree["input"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "message", ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = inputText }),
                        });
                    }
                    upstreamTree["store"] = false;
                    upstreamTree["stream"] = true;
                    foreach (var name in CodexUnsupportedParams) upstreamTree.Remove(name);
                    upstreamDirty = true;
                }
                // 模拟 Claude Code（subscription.mimic_claude_code，见 ClaudeCodeMimicry）：把最终发往
                // 上游的请求体改写成 CLI 的形状（3 块 system + metadata.user_id）。直通与翻译两条路
                // 都在这里收口，改的是同一棵 upstreamTree。
                if (claudeMimic)
                    upstreamDirty |= ClaudeCodeMimicry.ApplyBody(upstreamTree, candidate.AccountId ?? provider.Id,
                        ctx.Request.Headers["x-claude-code-session-id"].ToString());
                // Relay（真正的 Claude Code）：经 ANTHROPIC_AUTH_TOKEN 指到 Astra 的 CC 处于 auth-token 模式，
                // 它的请求体与自己用订阅（OAuth）登录时发的不同——实测抓包（claude-cli/2.1.281 两种模式的原始请求）
                // 唯一的体差异是 cache_control 没有 ttl:"1h"（计费块、metadata 两种模式都有）。头里的 beta 同理要
                // 补 oauth + extended-cache-ttl（见 ClaudeOAuthHeaders.MergeRelayBeta）：头体必须自洽，
                // 只补头不补体，就是"声明订阅身份却带着 API-key 形态的体"，上游回 403 Request not allowed。
                // 客户端专属的 cacheTtl 选项（ClientOptions）优先于 Relay 的默认 1h；只给没有 ttl 的块补，不增删断点。
                else if (claudeMode == ClaudeHeaderMode.Relay)
                    upstreamDirty |= ClaudeOAuthHeaders.PromoteCacheControlTtl(upstreamTree, clientCacheTtl ?? "1h");
                else if (clientCacheTtl is not null && upstreamProtocol == ApiProtocol.Anthropic)
                    upstreamDirty |= ClaudeOAuthHeaders.PromoteCacheControlTtl(upstreamTree, clientCacheTtl);

                PooledBuffer payload;
                PooledBuffer? owned = null;
                if (upstreamDirty)
                {
                    // Straight to UTF-8 in a pooled buffer sized like the client's body (the usual outcome).
                    owned = payload = new PooledBuffer(requestBody!.Buffer.Length + 1024);
                    GatewayJson.WriteTo(upstreamTree, payload);
                }
                else
                {
                    payload = requestBody!.Buffer;
                }
                capture?.Set(BodyStore.UpstreamRequest, payload.Span);
                encodeSpan.Set("astra.protocol.inbound", inbound.ToId()).Set("astra.protocol.upstream", upstreamProtocol.ToId())
                    .Set("astra.passthrough", passthrough).Set("astra.encode.rewritten", upstreamDirty)
                    .Set("astra.encode.lossy", attemptIr?.Warnings.Count ?? 0)
                    .Set("astra.encode.warnings", attemptIr is { Warnings.Count: > 0 } ? Truncate(string.Join("; ", attemptIr.Warnings), 300) : null)
                    .Set("gen_ai.request.model", model).Set("astra.upstream.model", upstreamModel).Set("astra.stream", stream)
                    .Set("astra.claude.mode", claudeMode == ClaudeHeaderMode.None ? null
                        : claudeMimic ? "mimic" : claudeMode == ClaudeHeaderMode.Relay ? "relay" : "compat")
                    .Set("astra.body.bytes", payload.Length).Ok().End();

                var url = UpstreamUrls.For(endpoint, upstreamModel, stream);
                // Claude Code calls /v1/messages?beta=true; the relay keeps its query string.
                if (claudeMode == ClaudeHeaderMode.Relay) url = WithClientQuery(url, ctx.Request.QueryString);
                return new UpstreamPlan(candidate, endpoint, passthrough, claudeMode, upstreamCodec, attemptIr, model, upstreamModel, stream,
                    clientWantsStream, forceResponsesStream, injectedChatUsage, attemptEffective, upstreamTree, payload, owned, url);
            }

            // The next pool member that can take the request; null when none is left (the record then describes the
            // attempt at hand again, whose failure is the answer). A member this request cannot be written for (no
            // endpoint for the model's protocol, no codec, a Responses-only session) is skipped.
            async Task<UpstreamPlan?> TryPlanNextAsync()
            {
                while (nextCandidate < candidates.Count)
                {
                    var candidate = candidates[nextCandidate++];
                    var span = trace.Start(TraceSpanNames.Resolve).Set("astra.pool.attempt", sends + 1);
                    try
                    {
                        return await PrepareAsync(candidate, sends + 1 < maxAttempts && nextCandidate < candidates.Count, span);
                    }
                    catch (Exception e) when (e is GatewayException or ProtocolException)
                    {
                        span.Error(e.Message).End();
                        logger.LogInformation("Request {Id}: pool member {Provider} skipped: {Reason}", record.Id, candidate.Provider.Name, e.Message);
                    }
                }
                plan.ApplyTo(record);
                provider = plan.Route.Provider;
                effective = plan.Effective;
                live.Update(record);
                return null;
            }

            bool CanFailOver() => poolPlan is not null && sends < maxAttempts && nextCandidate < candidates.Count;

            // Health of API-key providers (ProviderHealth) is reported from every route and read by pools only; account-based
            // providers cool their accounts instead.
            void Report(Provider target, ProviderOutcome outcome, HttpResponseMessage? response)
            {
                if (AuthSchemes.UsesAccounts(target.AuthScheme)) return;
                health.Report(target.Id, outcome,
                    outcome == ProviderOutcome.RateLimited && response is not null ? RateLimitResetOf(response, DateTimeOffset.UtcNow) : null);
            }

            // Exception filter: reports the failure, never changes what happens to the exception on a single-provider route.
            bool Failed(Provider target, Exception e)
            {
                if (ProviderFailover.Classify(e, ct) is not { } verdict) return false;
                Report(target, verdict.Outcome, null);
                return verdict.FailOver && CanFailOver();
            }

            void Switch(UpstreamPlan next)
            {
                plan.OwnedPayload?.Release();
                plan = next;
                upstreamPayload = next.OwnedPayload;
            }

            plan = await PrepareAsync(candidates[0], maxAttempts > 1 && candidates.Count > 1, resolveSpan);
            upstreamPayload = plan.OwnedPayload;
            HttpResponseMessage answered;
            string? accountId;
            while (true)
            {
                if (sends > 0)
                {
                    // A failed attempt's numbers must not stick to the one that answers (the catch blocks only fill gaps).
                    record.HttpStatus = null;
                    record.AccountId = null;
                    record.UpstreamRequestId = null;
                }
                if (poolPlan is not null)
                {
                    poolLease?.Dispose();
                    poolLease = poolScheduler.Begin(plan.Route.Provider.Id);
                }
                sends++;
                (HttpResponseMessage Response, string? AccountId, AccountScheduler.Lease? Lease) sent;
                try
                {
                    sent = await SendAsync(plan.Route, plan.Endpoint, plan.Url, plan.Payload, plan.UpstreamTree, plan.Stream, plan.Passthrough,
                        plan.ClaudeMode, plan.UpstreamModel, stickyKey, ctx.Request, capture, trace, attempts, ct);
                }
                catch (Exception e) when (Failed(plan.Route.Provider, e))
                {
                    var failedSpan = attempts.Last;
                    if (await TryPlanNextAsync() is not { } next) throw;
                    failedSpan?.Set("astra.attempt.retry", ProviderFailoverRetry).Error(e.Message).End();
                    logger.LogInformation("Request {Id}: {From} failed ({Reason}); trying {To}", record.Id, plan.Route.Provider.Name, e.Message, next.Route.Provider.Name);
                    Switch(next);
                    continue;
                }
                var verdict = ProviderFailover.Classify((int)sent.Response.StatusCode);
                Report(plan.Route.Provider, verdict.Outcome, sent.Response);
                if (verdict.FailOver && CanFailOver())
                {
                    var failedSpan = attempts.Last;
                    if (await TryPlanNextAsync() is { } next)
                    {
                        failedSpan?.Set("astra.attempt.retry", ProviderFailoverRetry);
                        logger.LogInformation("Request {Id}: {From} answered {Status}; trying {To}", record.Id, plan.Route.Provider.Name,
                            (int)sent.Response.StatusCode, next.Route.Provider.Name);
                        sent.Response.Dispose();
                        sent.Lease?.Dispose();
                        Switch(next);
                        continue;
                    }
                }
                accountLease = sent.Lease;
                accountId = sent.AccountId;
                answered = sent.Response;
                break;
            }
            route = plan.Route;
            // Probe slots of members this request never reached go back now, not when the answer has been streamed.
            poolPlan?.Dispose();
            if (route.Pool is { } servedPool && answered.IsSuccessStatusCode)
                poolScheduler.Bind(route.Token.Id, servedPool, stickyKey, route.Provider.Id);

            using var response = answered;
            responseSpan = trace.Start(TraceSpanNames.Response);
            record.AccountId = accountId;
            record.TtfbMs = sw.ElapsedMilliseconds;
            record.HttpStatus = (int)response.StatusCode;
            record.UpstreamRequestId = FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray");
            if (plan.ClaudeMode == ClaudeHeaderMode.Relay) RelayResponseHeaders(response, ctx.Response);
            live.Update(record);

            if (!response.IsSuccessStatusCode)
            {
                await ForwardErrorAsync(ctx, response, record, inbound, plan.Passthrough, clientCodec, plan.UpstreamCodec, capture, ct);
                return;
            }

            var restore = guard.RestoreMap;
            // 被强制成流式的上游（codex backend）遇到"客户端要非流式"时：把 SSE 读完，
            // 再按客户端协议拼成一个完整 JSON 响应回去。
            var collapseStream = plan.ForceResponsesStream && !plan.ClientWantsStream;
            decoder = plan.UpstreamCodec?.CreateResponseDecoder(new ResponseDecodeContext { Stream = plan.Stream, Model = plan.UpstreamModel });
            encoder = plan.Passthrough
                ? null
                : clientCodec!.CreateResponseEncoder(new ResponseEncodeContext
                {
                    Stream = plan.ClientWantsStream,
                    Model = plan.Model,
                    ResponseId = record.Id,
                    Created = record.StartedAtUtc,
                    IncludeUsage = inbound != ApiProtocol.OpenAIChat || clientWantsChatUsage,
                    Request = plan.Ir,
                });

            if (plan.Stream && !collapseStream)
            {
                // Gemini :streamGenerateContent without ?alt=sse answers with a JSON array of chunks, not SSE.
                var asJsonArray = inbound == ApiProtocol.Gemini && pathAltSse == false;
                // GitHub Copilot re-encrypts Responses item ids per event; passed through, they are pinned (ResponsesItemIds).
                var itemIds = plan.Passthrough && inbound == ApiProtocol.OpenAIResponses && CopilotModelIds.IsCopilot(plan.Route.Provider)
                    ? new ResponsesItemIds() : null;
                await StreamAsync(ctx, response, decoder, encoder, observer, sw, restore, plan.InjectedChatUsage, itemIds, capture, s.StreamIdleTimeoutSec, asJsonArray, ct);
            }
            else if (collapseStream)
                await CollapseStreamAsync(ctx, response, decoder, encoder, observer, sw, restore, capture, s.StreamIdleTimeoutSec, ct);
            else
                await BufferAsync(ctx, response, decoder, encoder, observer, sw, restore, capture, s.StreamIdleTimeoutSec, ct);

            RecordUpstreamError(record, observer);
        }
        catch (GatewayException e)
        {
            if (record.Status != RequestStatus.Blocked) record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= e.Status;
            record.ErrorType = e.RecordErrorType ?? e.Type;
            record.ErrorMessage = e.Message;
            if (e.RetryAfterSeconds is { } retryAfter && !ctx.Response.HasStarted)
                ctx.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteErrorAsync(ctx, inbound, encoder, e.Status, e.Type, e.Message);
        }
        catch (ProtocolException e)
        {
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= e.Status;
            record.ErrorType = e.Type;
            record.ErrorMessage = e.Message;
            await WriteErrorAsync(ctx, inbound, encoder, e.Status, e.Type, e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Plan §6.5: the client went away; the upstream request is cancelled with it. Usage seen so far is billed.
            // Leaving once the whole answer was written is a finished request, not a cancelled one.
            if (observer.Delivered) RecordUpstreamError(record, observer);
            else record.Status = RequestStatus.ClientCancelled;
        }
        catch (UpstreamIdleTimeoutException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.ErrorType = "timeout";
            record.ErrorMessage = e.Message;
            await WriteErrorAsync(ctx, inbound, encoder, 504, "timeout", e.Message);
        }
        catch (HttpRequestException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.HttpStatus ??= 502;
            record.ErrorType = "upstream_unreachable";
            record.ErrorMessage = Truncate(e.Message, 2000);
            await WriteErrorAsync(ctx, inbound, encoder, 502, "api_error", $"无法连接上游 {provider?.Name}：{e.Message}");
        }
        catch (IOException) when (ct.IsCancellationRequested || ctx.Response.HasStarted)
        {
            if (observer.Delivered) RecordUpstreamError(record, observer);
            else record.Status = RequestStatus.ClientCancelled;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Gateway request {Id} failed", record.Id);
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= 500;
            record.ErrorType = e.GetType().Name;
            record.ErrorMessage = Truncate(e.Message, 2000);
            await WriteErrorAsync(ctx, inbound, encoder, 500, "api_error", $"Astra 网关内部错误：{e.Message}");
        }
        finally
        {
            if (middlewareStream is not null)
            {
                try
                {
                    await middlewareStream.CompleteAsync(CancellationToken.None);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // the client went away while the held reply was written
                }
            }
            middlewareRun?.Dispose();
            // Balanced mode: the account's in-flight slot is held until the whole response has been streamed back.
            accountLease?.Dispose();
            poolLease?.Dispose();
            poolPlan?.Dispose();
            record.ResponseModel = decoder?.ResponseModel;
            Finish(record, observer, sw, effective, provider, s.Locale);
            FinishTrace(record, trace, link, inboundTraceId, observer, responseSpan, middlewareStream is not null, restorable);
            if (capture is not null)
            {
                record.BodyRef = BodyStore.RefFor(record.Id, record.StartedAtUtc);
                await capture.WriteAsync(bodies!, record.BodyRef);
            }
            // Nothing reads the body tree past this point; a send still in flight holds its own buffer reference.
            upstreamPayload?.Release();
            requestBody?.Dispose();
            live.Finish(record);
            await usageWriter.EnqueueAsync(record);
        }
    }

    // ------------------------------------------------------------------ upstream

    /// <summary>The provider id the plugin knows a plugin provider by (its <c>settings.plugin.provider</c>).</summary>
    public static string PluginProviderOf(Provider provider) =>
        provider.Settings["plugin"]?["provider"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var id) ? id : provider.Id;

    private async Task<(HttpResponseMessage Response, string? AccountId, AccountScheduler.Lease? Lease)> SendAsync(
        GatewayRoute route, ProviderEndpoint endpoint, string url, PooledBuffer body, JsonObject bodyTree, bool stream, bool passthrough,
        ClaudeHeaderMode claudeMode, string upstreamModel, string? stickyKey, HttpRequest clientRequest, BodyCapture? capture, RequestTrace trace,
        AttemptTrace attempts, CancellationToken ct)
    {
        var provider = route.Provider;
        var plugin = provider.AuthScheme == AuthSchemes.Plugin;
        // A plugin provider's sign-ins are accounts too: chosen, failed over and balanced the same way.
        var subscription = AuthSchemes.UsesAccounts(provider.AuthScheme);
        // Plan §5.4: in failover mode a rate-limited or dead account hands the request to the next usable account
        // (which also becomes the provider's current account, so the following requests stay on it).
        // Balanced mode fails over the same way, but picks every account through the scheduler and leaves
        // the provider's current account alone.
        var switchMode = subscription ? SubscriptionSupport.SwitchModeOf(provider) : SwitchModes.Manual;
        var failover = switchMode is SwitchModes.Failover or SwitchModes.Balanced;
        // Balanced: spread new conversations over the usable accounts and keep a conversation (stickyKey) on its
        // account. An account pinned by the token / client binding is never rescheduled.
        var balanced = switchMode == SwitchModes.Balanced && route.AccountId is null;
        // 模拟 Claude Code（subscription.mimic_claude_code）：只作用于 compat 路径（调用方不是 Claude Code 本身）。
        var claudeMimic = claudeMode == ClaudeHeaderMode.Compat && SubscriptionSupport.MimicClaudeCodeOf(provider);
        var claudeSessionId = clientRequest.Headers["x-claude-code-session-id"].ToString();
        // 上游请求体压缩（settings.request_compression；codex-cli 发 chatgpt.com 用 zstd）：整个请求只压一次，
        // 各次重试共用；设置里的「上游请求压缩」可以换格式或关掉；插件提供商的请求不走 HTTP，不压。
        var compression = plugin ? null : UpstreamCompression.Of(provider, settings.Current.RequestCompression);
        var compressed = compression is null ? null : UpstreamCompression.Compress(body.Span, compression);
        var accountId = route.AccountId;
        var tried = new List<string>();
        AccountScheduler.Lease? lease = null;
        var handedOver = false;
        try
        {
            while (true)
            {
                // One upstream.attempt span per send (.plan/observability.md §2.3); a retry ends it and starts the next.
                var attempt = StartAttempt();
                if (balanced)
                {
                    // Pick (and hold an in-flight slot on) the account for this attempt; accounts that already failed
                    // this request are excluded. No usable account → the plain selection below reports it.
                    lease?.Dispose();
                    lease = scheduler.Pick(provider.Id, await auth.ListAccountsAsync(provider.Id, ct), stickyKey, tried);
                    if (lease is not null) accountId = lease.AccountId;
                }
                UpstreamAuth credentials;
                var credentialsStart = Stopwatch.GetTimestamp();
                try
                {
                    credentials = await auth.ResolveAsync(provider.Id, accountId, ct) ?? UpstreamAuth.None;
                }
                catch (SubscriptionAuthException e)
                {
                    var used = string.IsNullOrEmpty(e.AccountId) ? accountId : e.AccountId;
                    attempt.Set("astra.account.id", used).Error(e.Message);
                    // A direct token may already have expired before the first send; the remaining accounts still get a turn.
                    if (failover && used is not null && await FailOverAsync(used, null, e.Message) is { } next)
                    {
                        attempt.Set("astra.attempt.retry", "auth_failover").End();
                        accountId = next;
                        continue;
                    }
                    throw new GatewayException(401, "authentication_error", $"提供商 {provider.Name} 的订阅账号不可用：{e.Message}");
                }
                // A subscription token close to expiry is refreshed here, on the network.
                attempt.Set("astra.account.id", credentials.AccountId ?? accountId)
                    .Set("astra.credentials_us", RequestTrace.Micros(credentialsStart, Stopwatch.GetTimestamp()))
                    .Set("astra.transport", plugin ? "plugin" : null);

                // 账号自己的代理优先于提供商的（同一账号的 401 重试沿用这个出口）。
                var client = plugin ? _pluginClient ?? throw NoPluginProviders()
                    : credentials.Proxy is { Length: > 0 } accountProxy ? http.For(accountProxy) : http.For(provider);
                var request = Build(credentials);
                // 调试捕获（plan §6.5）：记录发给提供商的**最终请求头**（凭据脱敏）。排查
                // "Request not allowed" 这类身份判定问题需要看到实际出去的头，体捕获不包含它们。
                capture?.Set(BodyStore.UpstreamRequestHeaders, DescribeRequest(request));
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.UnsupportedMediaType && compressed is not null)
                {
                    // 上游不收压缩的请求体：本请求剩下的发送都改发原文。
                    attempt = Retry(attempt, response, "compression_rejected", credentials.AccountId ?? accountId);
                    response.Dispose();
                    compressed = null;
                    request = Build(credentials);
                    capture?.Set(BodyStore.UpstreamRequestHeaders, DescribeRequest(request));
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && subscription && credentials.CanRefresh)
                {
                    // Plan §5.4: renew a grant once before switching accounts. Direct tokens / API keys cannot be renewed.
                    var used = credentials.AccountId ?? accountId;
                    attempt = Retry(attempt, response, "auth_refresh", used);
                    response.Dispose();
                    try
                    {
                        if (plugin)
                        {
                            // the plugin renews its own sign-in: its loader is asked again, once
                            if (credentials.PluginKey is { } key)
                                await (pluginTransport ?? throw NoPluginProviders()).Providers.ReloadAsync(PluginProviderOf(provider), key, ct);
                        }
                        else
                            credentials = await auth.ResolveAfterUnauthorizedAsync(provider.Id, used, ct);
                    }
                    catch (SubscriptionAuthException e)
                    {
                        if (failover && used is not null && await FailOverAsync(used, null, e.Message) is { } next)
                        {
                            attempt.Set("astra.attempt.retry", "auth_failover").Error(e.Message).End();
                            accountId = next;
                            continue;
                        }
                        attempt.Error(e.Message);
                        throw new GatewayException(401, "authentication_error", $"提供商 {provider.Name} 的订阅账号需要重新登录：{e.Message}");
                    }
                    attempt.Set("astra.credentials.refreshed", true);
                    request = Build(credentials);
                    capture?.Set(BodyStore.UpstreamRequestHeaders, DescribeRequest(request));
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                if (failover && response.StatusCode is (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    && credentials.AccountId is { } rejected)
                {
                    // A new token can still be refused by this model/channel. Cool the account without revoking its grant,
                    // then move the conversation on; direct credentials reach this branch without any refresh attempt.
                    var until = DateTimeOffset.UtcNow + ProviderHealth.AuthCooldown;
                    if (await FailOverAsync(rejected, until, $"{(int)response.StatusCode} 认证被上游拒绝，冷却至 {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC") is { } next)
                    {
                        EndAttempt(attempt, response, "auth_failover");
                        response.Dispose();
                        accountId = next;
                        continue;
                    }
                }
                if (failover && response.StatusCode == HttpStatusCode.TooManyRequests && credentials.AccountId is { } limited)
                {
                    var until = RateLimitResetOf(response, DateTimeOffset.UtcNow);
                    if (await FailOverAsync(limited, until, $"429 限流，冷却至 {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC") is { } next)
                    {
                        EndAttempt(attempt, response, "rate_limited_failover");
                        response.Dispose();
                        accountId = next;
                        continue;
                    }
                }
                // 失败时的请求日志（用户要求的排查手段）：地址 + 最终出去的请求头（凭据脱敏）。
                // 只记已保存的账号；请求自带的凭据（relay）连脱敏后的末 4 位也不进日志。
                if (!response.IsSuccessStatusCode && subscription && credentials.AccountId is not null)
                    logger.LogWarning("Upstream {Status} for {Provider} → {Url}; sent: {Headers}",
                        (int)response.StatusCode, provider.Name, url, DescribeRequest(request));
                EndAttempt(attempt, response, null);
                handedOver = true;
                return (response, credentials.AccountId, lease);
            }
        }
        finally
        {
            // Failures must not leak an in-flight slot; on success the caller owns the lease.
            if (!handedOver) lease?.Dispose();
        }

        TraceSpanBuilder Retry(TraceSpanBuilder span, HttpResponseMessage response, string reason, string? account)
        {
            EndAttempt(span, response, reason);
            return StartAttempt().Set("astra.account.id", account).Set("astra.transport", plugin ? "plugin" : null);
        }

        // Each attempt names its provider: a pool request's attempts can go to several (.plan/observability.md §2.3).
        TraceSpanBuilder StartAttempt() => attempts.Last = trace.Start(TraceSpanNames.Attempt, TraceSpanKinds.Client)
            .Set("astra.provider.id", provider.Id).Set("astra.provider.name", provider.Name);

        // What the attempt's response said: status, upstream request id, rate-limit state, why it was retried.
        static void EndAttempt(TraceSpanBuilder span, HttpResponseMessage response, string? retry)
        {
            var status = (int)response.StatusCode;
            span.Set("http.response.status_code", status).Set("astra.attempt.retry", retry)
                .Set("astra.upstream.request_id", FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray"));
            foreach (var name in RateLimitHeaders)
                if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } value)
                    span.Set("astra.ratelimit." + name, Truncate(value, 100));
            if (response.Headers.TryGetValues("openai-processing-ms", out var processing)
                && long.TryParse(processing.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                span.Set("astra.upstream.processing_ms", ms);
            if (response.IsSuccessStatusCode) span.Ok();
            else span.Error(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            span.End();
        }

        async Task<string?> FailOverAsync(string from, DateTimeOffset? cooldownUntil, string reason)
        {
            tried.Add(from);
            var next = await auth.FailOverAsync(provider.Id, from, cooldownUntil, reason, tried, setCurrent: switchMode != SwitchModes.Balanced, ct: ct);
            if (next is not null)
                logger.LogInformation("Provider {Provider}: account {From} → {To} ({Reason})", provider.Name, from, next, reason);
            return next;
        }

        /// <summary>
        /// 调试视图：请求地址 + 最终出去的请求头。凭据头只留 scheme 与末 4 位——日志和请求详情
        /// 都可能被截图分享，令牌本体永远不落盘。
        /// </summary>
        static string DescribeRequest(HttpRequestMessage request)
        {
            string Redact(string value)
            {
                var space = value.IndexOf(' ');
                var tail = value.Length <= 8 ? "••••" : "••••" + value[^4..];
                return space > 0 ? value[..(space + 1)] + tail : tail;
            }

            var headers = new JsonObject();
            foreach (var (name, values) in request.Headers)
            {
                var value = string.Join(", ", values);
                headers[name] = name is "Authorization" or "X-Api-Key" or "Cookie" ? Redact(value) : value;
            }
            return new JsonObject
            {
                ["url"] = request.RequestUri?.ToString(),
                ["headers"] = headers,
            }.ToJsonString();
        }

        HttpRequestMessage Build(UpstreamAuth a)
        {
            var target = a.QueryName is not null && a.QueryValue is not null ? UpstreamUrls.WithQuery(url, a.QueryName, a.QueryValue) : url;
            var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = compressed is null ? new PooledContent(body) : UpstreamCompression.Content(compressed, compression!),
            };
            if (plugin)
            {
                // which of the plugin's accounts, its own proxy, and the conversation (the plugin's session)
                if (a.PluginKey is { } key) request.Options.Set(PluginFetchHandler.AccountOption, key);
                if (a.Proxy is { Length: > 0 } proxy) request.Options.Set(PluginFetchHandler.ProxyOption, proxy);
                if (stickyKey is { Length: > 0 }) request.Headers.TryAddWithoutValidation(PluginFetchHandler.ConversationHeader, stickyKey);
                WorkPlace.Tell(request, bodyTree);
            }
            if (claudeMode == ClaudeHeaderMode.Relay)
            {
                // Claude Code → Claude subscription: its own headers verbatim, only the credential and the OAuth beta change.
                ClaudeOAuthHeaders.ApplyRelay(request, clientRequest.Headers, upstreamModel, provider.ExtraHeaders, DefaultAnthropicVersion);
                a.Apply(request);
                return request;
            }
            request.Headers.TryAddWithoutValidation("Accept", stream ? "text/event-stream" : "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent",
                passthrough && clientRequest.Headers.UserAgent.Count > 0 ? clientRequest.Headers.UserAgent.ToString() : UserAgent);
            if (endpoint.Protocol == ApiProtocol.Anthropic)
            {
                var version = passthrough ? clientRequest.Headers["anthropic-version"].ToString() : "";
                request.Headers.TryAddWithoutValidation("anthropic-version", string.IsNullOrEmpty(version) ? DefaultAnthropicVersion : version);
                if (passthrough && clientRequest.Headers["anthropic-beta"].ToString() is { Length: > 0 } beta)
                    request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
            }
            if (passthrough && endpoint.Protocol is ApiProtocol.OpenAIChat or ApiProtocol.OpenAIResponses
                && clientRequest.Headers["openai-beta"].ToString() is { Length: > 0 } openaiBeta)
                request.Headers.TryAddWithoutValidation("OpenAI-Beta", openaiBeta);
            // codex-cli 每个聊天后端请求都带 session-id / thread-id；直通时原样带上
            // （chatgpt.com 用它做缓存亲和）。只转发现有值，绝不替客户端编造。
            if (passthrough && endpoint.Protocol == ApiProtocol.OpenAIResponses)
            {
                foreach (var name in (string[])["session-id", "thread-id", "x-client-request-id"])
                    if (clientRequest.Headers[name].ToString() is { Length: > 0 } forwarded)
                        request.Headers.TryAddWithoutValidation(name, forwarded);
            }
            foreach (var (name, value) in provider.ExtraHeaders)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
            ProviderClientHeaders.Apply(provider, request, clientRequest.Headers);
            // Non-Claude-Code callers of a Claude subscription: either the Claude Code identity
            // (subscription.mimic_claude_code — canonical headers only, nothing of the client's leaks
            // through) or just the betas OAuth tokens require.
            if (claudeMimic)
                ClaudeCodeMimicry.ApplyHeaders(request, accountId ?? provider.Id, claudeSessionId);
            else if (claudeMode == ClaudeHeaderMode.Compat) ClaudeOAuthHeaders.ApplyCompat(request, clientRequest.Headers);
            // 凭据 + 上游 CLI 固定头（Authorization / chatgpt-account-id）。
            a.Apply(request);
            return request;
        }
    }

    /// <summary>
    /// When a rate-limited account may be used again: Anthropic's <c>anthropic-ratelimit-unified-reset</c> (unix
    /// seconds), else <c>Retry-After</c>, else five minutes. Clamped to [1 minute, 7 days].
    /// </summary>
    public static DateTimeOffset RateLimitResetOf(HttpResponseMessage response, DateTimeOffset now)
    {
        DateTimeOffset? at = null;
        if (response.Headers.TryGetValues("anthropic-ratelimit-unified-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var unix))
            at = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (response.Headers.RetryAfter is { } retry)
            at = retry.Date ?? (retry.Delta is { } delta ? now + delta : null);
        var until = at ?? now + TimeSpan.FromMinutes(5);
        if (until < now + TimeSpan.FromMinutes(1)) until = now + TimeSpan.FromMinutes(1);
        if (until > now + TimeSpan.FromDays(7)) until = now + TimeSpan.FromDays(7);
        return until;
    }

    /// <summary>Appends the client's query string (e.g. Claude Code's <c>?beta=true</c>) to the upstream URL.</summary>
    public static string WithClientQuery(string url, QueryString query)
    {
        if (!query.HasValue || query.Value is not { Length: > 1 } q) return url;
        return url.Contains('?') ? url + "&" + q[1..] : url + q;
    }

    /// <summary>Relay path: hands Anthropic's rate-limit / request-id headers back to Claude Code.</summary>
    private static void RelayResponseHeaders(HttpResponseMessage response, HttpResponse target)
    {
        foreach (var (name, values) in response.Headers)
            if (ClaudeOAuthHeaders.IsRelayedResponseHeader(name))
                target.Headers[name] = string.Join(",", values);
    }

    /// <summary>The upstream attempt span being recorded, so a failover can mark it even when the send threw.</summary>
    private sealed class AttemptTrace
    {
        public TraceSpanBuilder? Last { get; set; }
    }

    /// <summary>
    /// One route's version of a request (provider pools, issue #2): where it goes, how it is written for that provider,
    /// and the bytes sent. <see cref="OwnedPayload"/> is the pooled buffer this plan allocated (null = the client's bytes).
    /// </summary>
    private sealed record UpstreamPlan(
        GatewayRoute Route, ProviderEndpoint Endpoint, bool Passthrough, ClaudeHeaderMode ClaudeMode, IProtocolCodec? UpstreamCodec,
        UnifiedRequest? Ir, string Model, string UpstreamModel, bool Stream, bool ClientWantsStream, bool ForceResponsesStream,
        bool InjectedChatUsage, EffectiveModel Effective, JsonObject UpstreamTree, PooledBuffer Payload, PooledBuffer? OwnedPayload, string Url)
    {
        /// <summary>Puts this plan's provider, protocol and models back on the record.</summary>
        public void ApplyTo(RequestRecord record)
        {
            record.ProviderId = Route.Provider.Id;
            record.ProviderName = Route.Provider.Name;
            record.UpstreamProtocol = Endpoint.Protocol.ToId();
            record.Passthrough = Passthrough;
            record.RequestedModel = Model;
            record.UpstreamModel = UpstreamModel;
            record.Stream = Stream;
            record.SystemModelId = Effective.SystemModelId;
        }
    }

    /// <summary>How outbound headers are built for a Claude subscription upstream.</summary>
    private enum ClaudeHeaderMode
    {
        /// <summary>Not a Claude subscription upstream.</summary>
        None,

        /// <summary>Claude Code itself on the pass-through path: relay its headers verbatim.</summary>
        Relay,

        /// <summary>Any other caller (policy lifted, or translated): minimal OAuth compatibility headers.</summary>
        Compat,
    }

    private async Task StreamAsync(
        HttpContext ctx, HttpResponseMessage response, IResponseDecoder? decoder, IResponseEncoder? encoder,
        ResponseObserver observer, Stopwatch sw, IReadOnlyDictionary<string, string> restore, bool dropChatUsageChunk,
        ResponsesItemIds? itemIds, BodyCapture? capture, int idleTimeoutSec, bool asJsonArray, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = asJsonArray ? "application/json" : "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var parser = new SseParser();
        var upstreamCapture = capture is null ? null : new MemoryStream();
        var clientCapture = capture is null ? null : new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new IdleTimeoutReader(stream, idleTimeoutSec, ct, observer);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var outText = new StringBuilder();
        var utf8 = Encoding.UTF8.GetEncoder();
        var output = ctx.Response.BodyWriter;
        var array = asJsonArray ? new JsonArrayBody() : null;
        var sawSseEvent = false;
        // Pass-through with nothing to change on the way (no placeholder to restore, no injected usage chunk to
        // drop, no item ids to pin, no capture, SSE to SSE): the upstream bytes are the answer. They are forwarded as
        // they arrive and only parsed to observe usage, first token and errors — no event is decoded to text and encoded back.
        var forwardBytes = encoder is null && restore.Count == 0 && !dropChatUsageChunk && itemIds is null && !asJsonArray && capture is null;
        // Upstream bytes until the first SSE event parses: a body that never becomes SSE (a JSON object /
        // array from an upstream ignoring alt=sse or the stream flag) is decoded or forwarded from here. One exception:
        // forwarded bytes of an upstream that declared SSE and opened with a comment line (a heartbeat while the model is
        // still thinking) are not held — no JSON body starts with ':' — so they reach the client at once, and a proxy in
        // between that times out idle connections keeps this one.
        PooledBuffer? rawPending = null;
        var forwardDeclaredSse = forwardBytes
            && response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        bool? opensWithComment = null; // decided by the stream's first non-whitespace byte

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read == 0) break;
                var chunk = buffer.AsMemory(0, read);
                var events = parser.Feed(chunk.Span);
                if (forwardDeclaredSse && opensWithComment is null && chunk.Span.TrimStart(" \t\r\n"u8) is { IsEmpty: false } start)
                    opensWithComment = start[0] == (byte)':';
                if (!sawSseEvent && events.Count == 0 && opensWithComment != true)
                {
                    (rawPending ??= new PooledBuffer(read)).Write(chunk.Span);
                    if (upstreamCapture is not null) AppendCapped(upstreamCapture, chunk.Span);
                    continue;
                }
                if (upstreamCapture is not null) AppendCapped(upstreamCapture, chunk.Span);
                if (forwardBytes)
                {
                    foreach (var sse in events) Observe(sse);
                    if (!sawSseEvent && rawPending is not null)
                    {
                        // the start of the stream, held while it could still have been a plain JSON body
                        await output.WriteAsync(rawPending.Memory, ct);
                        rawPending.Release();
                        rawPending = null;
                    }
                    sawSseEvent = true;
                    await output.WriteAsync(chunk, ct);
                    observer.Written();
                    continue;
                }
                sawSseEvent = true;
                rawPending?.Release();
                rawPending = null;
                foreach (var sse in events) Handle(sse);
                await FlushAsync();
                // A JSON array is not a whole answer until its closing bracket, written only at the upstream's end.
                if (array is null) observer.Written();
            }
            foreach (var sse in parser.Flush())
            {
                if (forwardBytes) Observe(sse);
                else Handle(sse);
            }
            if (!sawSseEvent && rawPending is not null) await HandleNonSseBodyAsync(Encoding.UTF8.GetString(rawPending.Span));
            if (decoder is not null) Emit(decoder.Complete());
            await FlushAsync();
            await CloseArrayAsync();
        }
        catch (UpstreamIdleTimeoutException e) when (encoder is not null)
        {
            // Conversion path: tell the client in its own protocol, then end the stream.
            Emit([new ErrorEvent(504, "timeout", e.Message)]);
            await FlushAsync();
            await CloseArrayAsync();
            throw;
        }
        finally
        {
            rawPending?.Release();
            ArrayPool<byte>.Shared.Return(buffer);
            if (capture is not null)
            {
                capture.Set(BodyStore.UpstreamResponse, upstreamCapture!);
                capture.Set(BodyStore.ClientResponse, clientCapture!);
            }
        }

        void Observe(SseEvent sse)
        {
            foreach (var e in decoder?.DecodeSse(sse) ?? []) observer.Observe(e, sw.ElapsedMilliseconds);
        }

        void Handle(SseEvent sse)
        {
            var events = decoder?.DecodeSse(sse) ?? [];
            if (encoder is null)
            {
                // Pass-through: observe, then forward the event unchanged (minus our injected usage chunk, with
                // Copilot's item ids pinned).
                foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds);
                if (dropChatUsageChunk && IsChatUsageOnlyChunk(sse.Data)) return;
                WriteFrame(itemIds is null ? sse : sse with { Data = itemIds.Pin(sse.Data) });
                return;
            }
            Emit(events);
        }

        /// <summary>The upstream never spoke SSE: decode (conversion) or forward (pass-through) the raw JSON body.</summary>
        async Task HandleNonSseBodyAsync(string raw)
        {
            JsonObject? body = null;
            JsonArray? arrayBody = null;
            try
            {
                switch (JsonNode.Parse(raw))
                {
                    case JsonObject o:
                        body = o;
                        break;
                    case JsonArray a:
                        arrayBody = a;
                        break;
                }
            }
            catch (JsonException)
            {
                // Not JSON (HTML error page with a 200, padding…): nothing to decode.
            }

            if (encoder is null)
            {
                // Pass-through: the client wanted a stream but got a body — hand the body over untouched.
                if (body is not null || arrayBody is not null)
                    Write(PrivacyGuardService.Restore(raw, restore));
                return;
            }
            if (decoder is null) return;
            if (body is not null)
            {
                Emit(decoder.DecodeJson(body), wholeBody: true);
            }
            else if (arrayBody is not null)
            {
                // Gemini's non-alt=sse shape: an array of GenerateContentResponse chunks.
                foreach (var element in arrayBody.OfType<JsonObject>())
                    Emit(decoder.DecodeSse(new SseEvent(null, element.ToJsonString(GatewayJson.Options))), wholeBody: true);
            }
        }

        void Emit(IEnumerable<UnifiedStreamEvent> events, bool wholeBody = false)
        {
            foreach (var e in events)
            {
                observer.Observe(e, sw.ElapsedMilliseconds, wholeBody);
                if (encoder is null) continue;
                foreach (var frame in encoder.OnEvent(e)) WriteFrame(frame);
            }
        }

        void WriteFrame(SseEvent frame)
        {
            if (asJsonArray) Write(PrivacyGuardService.Restore(frame.Data, restore));
            // Nothing to restore: the frame is formatted straight into the pending text.
            else if (restore.Count == 0) SseWriter.AppendTo(outText, frame);
            else Write(PrivacyGuardService.Restore(SseWriter.Format(frame), restore));
        }

        void Write(string text)
        {
            if (array is { } writer) writer.Add(text);
            else outText.Append(text);
        }

        async Task FlushAsync()
        {
            if (array is { })
            {
                var drained = array.Drain();
                if (drained is null) return;
                clientCapture?.Append(drained);
                await ctx.Response.WriteAsync(drained, ct);
                await ctx.Response.Body.FlushAsync(ct);
                return;
            }
            if (outText.Length == 0) return;
            if (clientCapture is not null && clientCapture.Length < BodyStore.MaxBytes) clientCapture.Append(outText);
            // Encoded chunk by chunk into the response pipe: the pending text never becomes one string.
            foreach (var part in outText.GetChunks()) utf8.Convert(part.Span, output, flush: false, out _, out _);
            utf8.Convert([], output, flush: true, out _, out _);
            outText.Clear();
            await output.FlushAsync(ct);
        }

        async Task CloseArrayAsync()
        {
            if (array?.Close() is not { } tail) return;
            clientCapture?.Append(tail);
            await ctx.Response.WriteAsync(tail, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    private static async Task BufferAsync(
        HttpContext ctx, HttpResponseMessage response, IResponseDecoder? decoder, IResponseEncoder? encoder,
        ResponseObserver observer, Stopwatch sw, IReadOnlyDictionary<string, string> restore, BodyCapture? capture,
        int idleTimeoutSec, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new IdleTimeoutReader(stream, idleTimeoutSec, ct, observer);
        // A declared length sizes the buffer once (+1: the read that sees the end needs room); a compressed or
        // chunked body (no length) grows by doubling inside the pool.
        var declared = response.Content.Headers.ContentLength;
        var known = declared is > 0 and < int.MaxValue;
        var body = new PooledBuffer(known ? (int)declared!.Value + 1 : 64 * 1024);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(body.GetMemory(known ? 1 : 16 * 1024))) > 0) body.Advance(read);

            JsonObject? json = null;
            try
            {
                // Parsed from its UTF-8 bytes: the body only becomes a string when something has to rewrite it.
                // Invalid UTF-8 (a binary error page, say) is not JSON, which is what the string path found too.
                if (Utf8.IsValid(body.Span)) json = JsonNode.Parse(body.Span) as JsonObject;
            }
            catch (JsonException)
            {
                // Not JSON: forwarded as-is on the pass-through path; the conversion path reports it below.
            }

            var events = json is null || decoder is null ? [] : decoder.DecodeJson(json).ToList();
            // One complete body: usage and errors are real, but there is no first-token moment to measure.
            foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds, wholeBody: true);

            if (encoder is null)
            {
                ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                ctx.Response.StatusCode = 200;
                if (capture is null && restore.Count == 0)
                {
                    // Nothing to rewrite (no capture, nothing redacted): hand the upstream bytes over untouched.
                    await ctx.Response.Body.WriteAsync(body.Memory, ct);
                    return;
                }
                var passthrough = PrivacyGuardService.Restore(Encoding.UTF8.GetString(body.Span), restore);
                capture?.Set(BodyStore.UpstreamResponse, passthrough);
                capture?.Set(BodyStore.ClientResponse, passthrough);
                await ctx.Response.WriteAsync(passthrough, ct);
                return;
            }

            if (json is null) throw new GatewayException(502, "api_error", "上游返回的不是 JSON：" + Truncate(Encoding.UTF8.GetString(body.Span[..Math.Min(body.Length, 1200)]), 300));
            capture?.Set(BodyStore.UpstreamResponse, body.Span);
            foreach (var e in events) encoder.OnEvent(e);
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = 200;
            await WriteJsonAsync(ctx, encoder.BuildJson(), restore, capture, ct);
        }
        finally
        {
            body.Release();
        }
    }

    /// <summary>
    /// Writes a whole JSON answer: straight from the tree into the response pipe as UTF-8, unless a placeholder
    /// has to be restored or the body captured — those need the text.
    /// </summary>
    private static async Task WriteJsonAsync(HttpContext ctx, JsonNode payload, IReadOnlyDictionary<string, string> restore,
        BodyCapture? capture, CancellationToken ct)
    {
        if (restore.Count == 0 && capture is null)
        {
            GatewayJson.WriteTo(payload, ctx.Response.BodyWriter);
            await ctx.Response.BodyWriter.FlushAsync(ct);
            return;
        }
        var output = PrivacyGuardService.Restore(payload.ToJsonString(GatewayJson.Options), restore);
        capture?.Set(BodyStore.ClientResponse, output);
        await ctx.Response.WriteAsync(output, ct);
    }

    /// <summary>
    /// 上游被强制成流式（codex backend 只接受 stream:true），但客户端要的是非流式：
    /// 把整个 SSE 读完喂给解码器，再用客户端的编码器拼出**一个**完整 JSON 响应。
    /// 直接回客户端协议的 JSON（与 BufferAsync 的非转换分支一致），不做二次转换。
    /// </summary>
    private async Task CollapseStreamAsync(
        HttpContext ctx, HttpResponseMessage response, IResponseDecoder? decoder, IResponseEncoder? encoder,
        ResponseObserver observer, Stopwatch sw, IReadOnlyDictionary<string, string> restore,
        BodyCapture? capture, int idleTimeoutSec, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new IdleTimeoutReader(stream, idleTimeoutSec, ct, observer);
        var parser = new SseParser();
        var upstreamCapture = capture is null ? null : new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        PooledBuffer? rawPending = null;
        var sawSseEvent = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read == 0) break;
                if (upstreamCapture is not null) AppendCapped(upstreamCapture, buffer.AsSpan(0, read));
                var events = parser.Feed(buffer.AsSpan(0, read));
                if (events.Count > 0)
                {
                    sawSseEvent = true;
                    rawPending?.Release();
                    rawPending = null;
                }
                else if (!sawSseEvent)
                {
                    (rawPending ??= new PooledBuffer(read)).Write(buffer.AsSpan(0, read));
                }
                foreach (var sse in events) Emit(decoder?.DecodeSse(sse) ?? []);
            }
            foreach (var sse in parser.Flush()) Emit(decoder?.DecodeSse(sse) ?? []);
            if (!sawSseEvent && rawPending is not null && JsonNode.Parse(rawPending.Span) is JsonObject whole)
                Emit(decoder?.DecodeJson(whole) ?? [], wholeBody: true);
        }
        finally
        {
            rawPending?.Release();
            ArrayPool<byte>.Shared.Return(buffer);
        }
        Emit(decoder?.Complete() ?? []);
        if (capture is not null) capture.Set(BodyStore.UpstreamResponse, upstreamCapture!);

        // 非转换路径没有客户端编码器：把上游的完整响应对象原样透出，只是去掉了 SSE 外壳。
        JsonObject payload;
        if (encoder is null)
            payload = decoder?.FinalResponse
                      ?? throw new GatewayException(502, "api_error", "上游强制流式但响应里没有完整结果。");
        else
            payload = encoder.BuildJson();
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await WriteJsonAsync(ctx, payload, restore, capture, ct);

        void Emit(IEnumerable<UnifiedStreamEvent> events, bool wholeBody = false)
        {
            foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds, wholeBody);
        }
    }

    // Debug capture keeps at most what BodyStore writes, as the upstream's own bytes (no UTF-16 copy); a long stream
    // must not grow it without bound.
    private static void AppendCapped(MemoryStream capture, ReadOnlySpan<byte> utf8)
    {
        var room = BodyStore.MaxBytes - (int)capture.Length;
        if (room > 0) capture.Write(utf8[..Math.Min(utf8.Length, room)]);
    }

    private async Task ForwardErrorAsync(
        HttpContext ctx, HttpResponseMessage response, RequestRecord record, ApiProtocol inbound, bool passthrough,
        IProtocolCodec? clientCodec, IProtocolCodec? upstreamCodec, BodyCapture? capture, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var text = await ReadLimitedAsync(response, MaxErrorBodyBytes, ct);
        capture?.Set(BodyStore.UpstreamResponse, text);
        var (type, message) = upstreamCodec?.DecodeError(status, text) ?? ("upstream_error", Truncate(text, 500));
        record.Status = RequestStatus.UpstreamError;
        record.ErrorType = type;
        record.ErrorMessage = Truncate(message, 2000);

        ctx.Response.StatusCode = status;
        if (passthrough)
        {
            // Same protocol: the upstream error body is already in the client's format.
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (response.Headers.RetryAfter is { } retry) ctx.Response.Headers.RetryAfter = retry.ToString();
            capture?.Set(BodyStore.ClientResponse, text);
            await ctx.Response.WriteAsync(text, ct);
            return;
        }
        var body = (clientCodec ?? codecs.Get(inbound))?.EncodeError(status, type, message) ?? GenericError(type, message);
        ctx.Response.ContentType = "application/json";
        if (response.Headers.RetryAfter is { } retryAfter) ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        var json = body.ToJsonString(GatewayJson.Options);
        capture?.Set(BodyStore.ClientResponse, json);
        await ctx.Response.WriteAsync(json, ct);
    }

    // ------------------------------------------------------------------ record

    /// <summary>An answer that ended in an upstream error event (written to the client in its own shape) is that error.</summary>
    private static void RecordUpstreamError(RequestRecord record, ResponseObserver observer)
    {
        if (observer.Error is not { } err) return;
        record.Status = RequestStatus.UpstreamError;
        record.ErrorType = err.Type;
        record.ErrorMessage = Truncate(err.Message, 2000);
    }

    private void Finish(RequestRecord record, ResponseObserver observer, Stopwatch sw, EffectiveModel? effective, Provider? provider, string locale)
    {
        record.TotalMs = sw.ElapsedMilliseconds;
        record.TtftMs = observer.TtftMs;
        if (observer.Usage is not null)
        {
            record.UsageSource = "reported";
            record.UsageRawJson = observer.RawUsage?.ToJsonString(GatewayJson.Options);
        }
        if (effective is not null)
        {
            try
            {
                RequestBilling.Apply(record, observer.Usage, effective.Pricing, locale,
                    subscriptionEquivalent: provider?.AuthScheme == AuthSchemes.OAuthSubscription);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Billing failed for request {Id}", record.Id);
            }
        }
        var generated = observer.Usage is null ? 0 : observer.Usage.Get(TokenTypes.Output) + observer.Usage.Get(TokenTypes.Reasoning);
        GenerationTiming.Apply(record, generated, observer.LastOutputMs);
    }

    // ------------------------------------------------------------------ tracing

    private TraceLink LinkOf(string requestId, ApiProtocol inbound, JsonObject body, IHeaderDictionary headers, string? inboundTraceId)
    {
        var chain = TraceKeys.ChainOf(inbound, body);
        return linker.Link(requestId, TraceKeys.SessionOf(inbound, body, headers, chain), chain, TraceKeys.LastMessageOf(inbound, body), inboundTraceId);
    }

    private static void ApplyLink(RequestRecord record, TraceLink link)
    {
        record.TraceId = link.TraceId;
        record.SessionKey = link.SessionKey;
        record.ChainKey = link.ChainKey;
        record.TurnId = link.TurnId;
        record.ParentRequestId = link.ParentRequestId;
        record.Step = link.Step;
        record.CallKind = link.CallKind;
        record.LinkSource = link.LinkSource;
    }

    /// <summary>Closes the request's spans into <see cref="RequestRecord.Spans"/> and remembers the tool calls it made.</summary>
    private void FinishTrace(RequestRecord record, RequestTrace trace, TraceLink? link, string? inboundTraceId, ResponseObserver observer,
        TraceSpanBuilder? responseSpan, bool middlewareResponse, int restorable)
    {
        record.FinishReason = observer.Finish is { } finish ? FinishReasonId(finish) : null;
        record.ToolCalls = observer.ToolCallIds.Count;
        if (responseSpan is not null)
        {
            responseSpan.Set("astra.protocol.client", record.InboundProtocol).Set("astra.response.reads", observer.Reads)
                .Set("astra.response.max_gap_ms", observer.MaxGapMs).Set("astra.response.events", observer.Events)
                .Set("astra.response.tool_calls", record.ToolCalls).Set("gen_ai.response.finish_reasons", record.FinishReason)
                .Set("astra.response.tool_names", observer.ToolNames.Count == 0 ? null : Truncate(string.Join(",", observer.ToolNames), 300))
                .Set("astra.restore.placeholders", restorable).Set("astra.middleware.response", middlewareResponse);
            // The model's own phases inside the response (.plan/observability.md §5): thinking, writing, tool calls.
            responseSpan.Set("astra.response.reasoning_chars", observer.ReasoningChars).Set("astra.response.text_chars", observer.TextChars);
            if (observer.ReasoningStart is { } reasoningStart) responseSpan.EventAt("reasoning_start", reasoningStart);
            if (observer.ReasoningEnd is { } reasoningEnd) responseSpan.EventAt("reasoning_end", reasoningEnd);
            if (observer.TextStart is { } textStart) responseSpan.EventAt("text_start", textStart);
            if (observer.TextEnd is { } textEnd) responseSpan.EventAt("text_end", textEnd);
            foreach (var (at, name) in observer.ToolStarts) responseSpan.EventAt("tool_call", at, new KeyValuePair<string, object>("name", name));
            if (observer.Error is { } error) responseSpan.Error(error.Message);
            else if (record.Status == RequestStatus.Success) responseSpan.Ok();
            responseSpan.End();
        }
        record.LiveStage = null;
        record.LivePhases = null;
        var root = trace.Root;
        root.Set("gen_ai.operation.name", "chat").Set("gen_ai.request.model", record.RequestedModel)
            .Set("gen_ai.response.model", record.ResponseModel).Set("gen_ai.usage.input_tokens", record.TotalInputTokens)
            .Set("gen_ai.usage.output_tokens", record.TotalOutputTokens).Set("astra.client.kind", record.ClientKind)
            .Set("astra.provider.id", record.ProviderId).Set("http.response.status_code", record.HttpStatus)
            .Set("astra.status", record.Status);
        if (record.Status == RequestStatus.Success) root.Ok();
        else root.Error(record.ErrorMessage is { Length: > 0 } message ? message : record.Status);
        // A request refused before its body was read still gets a trace: the agent's, else one of its own.
        record.TraceId ??= inboundTraceId ?? ActivityTraceId.CreateRandom().ToHexString();
        record.Spans = trace.Build(record.Id);
        if (link is not null) linker.Complete(link, observer.ToolCallIds);
    }

    private static string FinishReasonId(FinishReason reason) => reason switch
    {
        FinishReason.Stop => "stop",
        FinishReason.Length => "length",
        FinishReason.ToolCalls => "tool_calls",
        FinishReason.ContentFilter => "content_filter",
        FinishReason.Error => "error",
        _ => "other",
    };

    // ------------------------------------------------------------------ helpers

    private async Task WriteErrorAsync(HttpContext ctx, ApiProtocol inbound, IResponseEncoder? encoder, int status, string type, string message)
    {
        try
        {
            if (ctx.Response.HasStarted)
            {
                // Mid-stream: an error event in the client's protocol, when we are the ones encoding the stream.
                if (encoder is null) return;
                var text = string.Concat(encoder.OnEvent(new ErrorEvent(status, type, message)).Select(SseWriter.Format));
                if (text.Length > 0) await ctx.Response.WriteAsync(text, CancellationToken.None);
                return;
            }
            var body = codecs.Get(inbound)?.EncodeError(status, type, message) ?? GenericError(type, message);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(body.ToJsonString(GatewayJson.Options), CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }

    private static JsonObject GenericError(string type, string message) =>
        new() { ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };

    // Only plugin sync creates plugin providers, so a host without the plugin runtime never routes to one.
    private static InvalidOperationException NoPluginProviders() => new("This host runs no plugin providers.");

    private static JsonObject ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new ProtocolException("请求体必须是 JSON 对象。");
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"请求体不是有效的 JSON：{e.Message}");
        }
    }

    /// <summary>
    /// Reads the upstream body with an idle timeout per read. One token source serves the whole body: it is
    /// re-armed before each read (a timer that fired while the answer was being written to the client is not a
    /// timeout of the next read — that read gets a fresh source), instead of a linked source and timer per chunk.
    /// </summary>
    private sealed class IdleTimeoutReader(Stream stream, int idleTimeoutSec, CancellationToken ct, ResponseObserver? observer = null) : IDisposable
    {
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(5, idleTimeoutSec));
        private CancellationTokenSource _idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer)
        {
            if (!_idle.TryReset())
            {
                _idle.Dispose();
                _idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }
            _idle.CancelAfter(_timeout);
            try
            {
                var read = await stream.ReadAsync(buffer, _idle.Token);
                if (read > 0) observer?.OnRead();
                return read;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpstreamIdleTimeoutException($"上游超过 {idleTimeoutSec} 秒没有返回数据（流空闲超时，可在设置中调整）。");
            }
        }

        public void Dispose() => _idle.Dispose();
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while (memory.Length < limit && (read = await stream.ReadAsync(buffer, ct)) > 0) memory.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)Math.Min(memory.Length, limit));
    }

    /// <summary>The usage-only chunk produced by our injected stream_options.include_usage (choices empty, usage set).</summary>
    internal static bool IsChatUsageOnlyChunk(string data)
    {
        if (!data.Contains("\"usage\"", StringComparison.Ordinal)) return false;
        try
        {
            return JsonNode.Parse(data) is JsonObject o && o["usage"] is JsonObject && o["choices"] is JsonArray { Count: 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstHeader(HttpResponseMessage response, params string[] names)
    {
        foreach (var name in names)
        {
            if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } v) return Truncate(v, 200);
        }
        return null;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string? StripModels(string? model) =>
        string.IsNullOrEmpty(model) ? null : model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;

    /// <summary>
    /// The caller's address for the request log, when it isn't this machine: tunnel visitors arrive with Cloudflare's
    /// address already adopted by the security middleware; one it could not tell (no Cf-Connecting-IP) is left out.
    /// </summary>
    private static string? ClientIp(HttpContext ctx)
    {
        if (ctx.Connection.RemoteIpAddress is not { } raw) return null;
        var ip = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
        return System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.None) ? null : ip.ToString();
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];

    /// <summary>Tracks the IR events of one response: first-token time, usage, finish reason, errors.</summary>
    private sealed class ResponseObserver
    {
        public long? TtftMs { get; private set; }

        /// <summary>When the last output event of a stream arrived (the same events that set <see cref="TtftMs"/>).</summary>
        public long? LastOutputMs { get; private set; }
        public NormalizedUsage? Usage { get; private set; }
        public JsonObject? RawUsage { get; private set; }
        public FinishReason? Finish { get; private set; }
        public ErrorEvent? Error { get; private set; }

        /// <summary>
        /// The end of the answer (its <see cref="Finish"/> or <see cref="Error"/>) has been written to the client. A client
        /// that leaves after this — on its <c>[DONE]</c> / <c>response.completed</c>, while the upstream still holds its
        /// connection open (the ChatGPT backend does) — has its whole answer: that is not a cancellation.
        /// </summary>
        public bool Delivered { get; private set; }

        /// <summary>Called after everything observed so far has been written to the client.</summary>
        public void Written()
        {
            if (Finish is not null || Error is not null) Delivered = true;
        }

        /// <summary>Ids of the tool calls the response asked for (linking the agent's next call, .plan/observability.md §3.2).</summary>
        public List<string> ToolCallIds { get; } = [];

        /// <summary>Names of those tool calls (what the agent will run next), for the session view.</summary>
        public List<string> ToolNames { get; } = [];

        /// <summary>
        /// When the model thought, wrote and started each tool call (<see cref="Stopwatch.GetTimestamp"/> values; streams
        /// only — a whole body has no moments to tell apart), and how much it thought and wrote (.plan/observability.md §5).
        /// </summary>
        public long? ReasoningStart { get; private set; }
        public long? ReasoningEnd { get; private set; }
        public long? TextStart { get; private set; }
        public long? TextEnd { get; private set; }
        public List<(long At, string Name)> ToolStarts { get; } = [];
        public long ReasoningChars { get; private set; }
        public long TextChars { get; private set; }

        /// <summary>Called when what the model produces changes: <c>thinking</c>, <c>writing</c>, <c>tool:&lt;name&gt;</c>.</summary>
        public Action<string>? StageChanged { get; init; }

        private string? _stage;

        private void Stage(string stage)
        {
            if (stage == _stage) return;
            _stage = stage;
            StageChanged?.Invoke(stage);
        }

        /// <summary>IR events observed, upstream body reads, and the longest pause between two reads.</summary>
        public int Events { get; private set; }
        public int Reads { get; private set; }
        public long MaxGapMs { get; private set; }

        private long _lastRead;

        public void OnRead()
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastRead != 0 && (long)Stopwatch.GetElapsedTime(_lastRead, now).TotalMilliseconds is var gap && gap > MaxGapMs) MaxGapMs = gap;
            _lastRead = now;
            Reads++;
        }

        /// <summary>Called once, with the TTFT, when the first token arrives.</summary>
        public Action<long>? FirstToken { get; init; }

        /// <summary>
        /// Folds one observed event. <paramref name="wholeBody"/> marks events decoded from a complete response
        /// body (no stream): there is no first-token moment to measure there, so the TTFT — and with it
        /// GenerationMs / OutputTps — stays unset instead of being guessed from the decode time.
        /// </summary>
        public void Observe(UnifiedStreamEvent e, long elapsedMs, bool wholeBody = false)
        {
            Events++;
            if (e is BlockStartEvent { Kind: BlockKind.ToolCall } toolCall)
            {
                if (toolCall.ToolCallId is { Length: > 0 } toolCallId) ToolCallIds.Add(toolCallId);
                if (toolCall.ToolName is { Length: > 0 } toolName && ToolNames.Count < 32) ToolNames.Add(toolName);
                if (!wholeBody && ToolStarts.Count < 32) ToolStarts.Add((Stopwatch.GetTimestamp(), toolCall.ToolName ?? ""));
                if (!wholeBody) Stage("tool:" + (toolCall.ToolName ?? ""));
            }
            else if (e is ReasoningDeltaEvent reasoning && (reasoning.Text is { Length: > 0 } || reasoning.EncryptedContent is { Length: > 0 }))
            {
                ReasoningChars += reasoning.Text?.Length ?? 0;
                if (!wholeBody)
                {
                    var now = Stopwatch.GetTimestamp();
                    ReasoningStart ??= now;
                    ReasoningEnd = now;
                    Stage("thinking");
                }
            }
            else if (e is TextDeltaEvent { Text.Length: > 0 } text)
            {
                TextChars += text.Text.Length;
                if (!wholeBody)
                {
                    var now = Stopwatch.GetTimestamp();
                    TextStart ??= now;
                    TextEnd = now;
                    Stage("writing");
                }
            }
            switch (e)
            {
                case TextDeltaEvent { Text.Length: > 0 }:
                case ReasoningDeltaEvent { Text.Length: > 0 }:
                case ReasoningDeltaEvent { EncryptedContent.Length: > 0 }:
                case ToolArgsDeltaEvent:
                case BlockStartEvent { Kind: BlockKind.ToolCall }:
                case OutputActivityEvent:
                    if (TtftMs is null && !wholeBody)
                    {
                        TtftMs = elapsedMs;
                        FirstToken?.Invoke(elapsedMs);
                    }
                    if (!wholeBody) LastOutputMs = elapsedMs;
                    break;
                case UsageEvent u:
                    Usage = u.Usage;
                    RawUsage = u.Raw;
                    break;
                case MessageStopEvent m:
                    Finish = m.Reason;
                    break;
                case ErrorEvent err:
                    Error = err;
                    break;
            }
        }
    }

    /// <summary>Debug body capture (plan §6.5): four bodies per request, written after it finishes.</summary>
    private sealed class BodyCapture
    {
        private readonly Dictionary<string, object> _parts = new();

        public void Set(string part, string body) => _parts[part] = body;

        /// <summary>Bytes are copied (at most what is written): the source is a pooled buffer.</summary>
        public void Set(string part, ReadOnlySpan<byte> body) => _parts[part] = body[..Math.Min(body.Length, BodyStore.MaxBytes)].ToArray();

        /// <summary>A capped capture buffer is kept as it is (no copy): nothing writes to it after this.</summary>
        public void Set(string part, MemoryStream body) => _parts[part] = new ReadOnlyMemory<byte>(body.GetBuffer(), 0, (int)body.Length);

        /// <summary>Kept as a builder: BodyStore writes its chunks without making one string of them.</summary>
        public void Set(string part, StringBuilder body) => _parts[part] = body;

        public async Task WriteAsync(BodyStore store, string bodyRef)
        {
            foreach (var (part, body) in _parts)
            {
                try
                {
                    await (body switch
                    {
                        byte[] bytes => store.WriteAsync(bodyRef, part, bytes),
                        ReadOnlyMemory<byte> memory => store.WriteAsync(bodyRef, part, memory),
                        StringBuilder builder => store.WriteAsync(bodyRef, part, builder),
                        _ => store.WriteAsync(bodyRef, part, (string)body),
                    });
                }
                catch (IOException)
                {
                    // Debug capture is best effort.
                }
            }
        }
    }
}

/// <summary>The upstream stopped sending data for longer than the configured idle timeout.</summary>
public sealed class UpstreamIdleTimeoutException(string message) : Exception(message);
