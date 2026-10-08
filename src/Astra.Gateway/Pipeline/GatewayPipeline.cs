using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Protocol;
using Astra.Providers.Subscription;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The gateway request pipeline (plan §6): authenticate the local client key, pick the upstream endpoint,
/// forward (pass-through fast path) or translate through the IR, stream the answer back, and record timing,
/// usage and cost for every request.
/// </summary>
public sealed class GatewayPipeline(
    GatewayRouter router,
    CodecRegistry codecs,
    GatewayHttpClients http,
    UpstreamAuthResolver auth,
    EffectiveModelResolver models,
    SettingsService settings,
    PrivacyGuardService privacy,
    BodyStore bodies,
    UsageWriter usageWriter,
    ILogger<GatewayPipeline> logger)
{
    private const long MaxRequestBytes = 64L * 1024 * 1024;
    private const int MaxErrorBodyBytes = 1024 * 1024;
    private const string DefaultAnthropicVersion = "2023-06-01";
    private static readonly string UserAgent = $"Astra/{typeof(GatewayPipeline).Assembly.GetName().Version?.ToString(3) ?? "0"}";

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
        };
        ctx.Response.Headers["x-astra-request-id"] = record.Id;
        var capture = s.DebugBodies ? new BodyCapture() : null;
        var observer = new ResponseObserver();
        EffectiveModel? effective = null;
        Provider? provider = null;
        IResponseDecoder? decoder = null;
        IResponseEncoder? encoder = null;

        try
        {
            var route = await router.ResolveAsync(ctx.Request, ct);
            provider = route.Provider;
            record.ClientKind = route.ClientKind;
            record.TokenId = route.Token.Id;
            record.TokenName = route.Token.Name;
            record.ProviderId = provider.Id;
            record.ProviderName = provider.Name;

            var rawBody = await ReadBodyAsync(ctx.Request, ct);
            capture?.Set(BodyStore.ClientRequest, rawBody);

            // Privacy guard (plan §6.7): runs on the client body before anything leaves the machine.
            var guard = privacy.Inspect(rawBody, route.ClientKind);
            if (guard.Applied && guard.Hits.Count > 0)
                record.PrivacyJson = PrivacyGuardService.ReportJson(guard.DryRun, guard.Blocked, guard.Hits, guard.RestoreMap.Count);
            if (guard.Blocked)
            {
                record.Status = RequestStatus.Blocked;
                throw new GatewayException(400, "invalid_request_error", "请求中包含被 Astra 隐私护栏拦截的敏感信息，已拒绝发送到上游。详情见 Astra 请求日志。");
            }

            var body = ParseObject(guard.Body);
            // Log-only metadata (plan §6.5): what the client itself set, read before any codec derives
            // upstream budgets from it. Never touches the wire and never throws.
            RequestReasoning.Capture(record, inbound, body);
            var requestedModel = (inbound == ApiProtocol.Gemini ? StripModels(pathModel) : null) ?? Str(body, "model") ?? "";
            // 有时上游按模型区分协议（GitHub Copilot 的 Claude 只支持 Messages），
            // 先解析出这条 provider model 的约束，再据它选上游端点：
            // 不支持直通时就翻译，否则 Responses 端点会回 model_not_supported。
            var (resolvedModel, _) = await models.ResolveByModelIdAsync(provider, requestedModel, ct);
            var endpoint = GatewayRouter.SelectEndpoint(provider, inbound, resolvedModel.UpstreamProtocols);
            var upstreamProtocol = endpoint.Protocol;
            var passthrough = upstreamProtocol == inbound;
            record.UpstreamProtocol = upstreamProtocol.ToId();
            record.Passthrough = passthrough;

            // Claude subscription (plan §5.4): the client policy ("Claude Code only" by default) and, for Claude Code
            // itself on the pass-through path, a faithful relay of its own headers; other callers get the minimal
            // OAuth compatibility headers.
            var isClaudeCode = inbound == ApiProtocol.Anthropic && ClaudeCodeDetector.IsClaudeCode(ctx.Request.Headers, body);
            SubscriptionSupport.EnforceClientPolicy(provider, isClaudeCode);
            var claudeMode = !SubscriptionSupport.IsClaudeSubscription(provider) || upstreamProtocol != ApiProtocol.Anthropic
                ? ClaudeHeaderMode.None
                : passthrough && isClaudeCode ? ClaudeHeaderMode.Relay : ClaudeHeaderMode.Compat;

            // Legal per plan §6.2, but worth a line in the log: the provider prefers this protocol, has no address for
            // it, and the request is translated into a lower-preference protocol instead of passing through.
            if (!passthrough && GatewayRouter.MissingEndpointFor(provider, inbound) is { } missing)
                logger.LogInformation("Request {Id}: {Provider} prefers {Preferred} but has no endpoint for it; translating to {Upstream}",
                    record.Id, provider.Name, missing.ToId(), upstreamProtocol.ToId());

            var clientCodec = codecs.Get(inbound);
            var upstreamCodec = codecs.Get(upstreamProtocol);
            if (!passthrough && (clientCodec is null || upstreamCodec is null))
                throw new GatewayException(501, "api_error", $"尚不支持 {inbound.ToId()} → {upstreamProtocol.ToId()} 的协议转换。");

            // What the client asked for.
            UnifiedRequest? ir = null;
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
                ir = clientCodec!.DecodeRequest(body, new RequestDecodeContext { PathModel = StripModels(pathModel), PathStream = pathStream });
                model = ir.Model;
                stream = ir.Stream;
                if (string.IsNullOrEmpty(model)) throw new ProtocolException("请求缺少 model。");
                if (ir.PreviousResponseId is not null && upstreamProtocol != ApiProtocol.OpenAIResponses)
                    throw new ProtocolException($"previous_response_id 依赖 OpenAI Responses 的服务端会话，而提供商 {provider.Name} 的上游协议是 {upstreamProtocol.ToId()}。请让客户端发送完整对话历史（例如关闭 store / 会话续接）。");
            }
            record.RequestedModel = model;
            var upstreamModel = route.UpstreamModelFor(model);
            record.UpstreamModel = upstreamModel;
            // ChatGPT 订阅（Codex）的 backend 把请求钉死成 codex-cli 的形状：input 必须是数组、
            // store 必须是 false、stream 必须是 true（客户端要非流式时我们带 stream:true 发上去，
            // 再把完整响应一次性回给客户端）。因此上游的流式与否在这里就定下来了。
            var forceResponsesStream = provider.SettingFlag("force_stream", false) && upstreamProtocol == ApiProtocol.OpenAIResponses;
            var clientWantsStream = stream;
            if (forceResponsesStream) stream = true;
            record.Stream = stream;
            var clientWantsChatUsage = inbound == ApiProtocol.OpenAIChat
                                       && body["stream_options"] is JsonObject so && so["include_usage"] is JsonValue iu && iu.TryGetValue<bool>(out var wants) && wants;

            // 上游模型名可能与请求里的不同（provider model 映射），按上游名再解析一次取有效模型。
            (_, effective) = upstreamModel == requestedModel
                ? (resolvedModel, await models.ResolveAsync(provider, resolvedModel, ct))
                : await models.ResolveByModelIdAsync(provider, upstreamModel, ct);
            record.SystemModelId = effective.SystemModelId;

            // What we send upstream.
            string upstreamText;
            var injectedChatUsage = false;
            if (passthrough)
            {
                var rewritten = false;
                if (upstreamModel != model && inbound != ApiProtocol.Gemini)
                {
                    body["model"] = upstreamModel; // Gemini carries the model in the URL instead.
                    rewritten = true;
                }
                if (forceResponsesStream)
                {
                    // codex backend: input 必须是数组、store 必须是 false、stream 必须是 true。
                    if (body["input"] is JsonValue inputValue && inputValue.TryGetValue<string>(out var inputText))
                    {
                        body["input"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "message", ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = inputText }),
                        });
                        rewritten = true;
                    }
                    body["store"] = false;
                    body["stream"] = true;
                    rewritten = true;
                }
                if (upstreamProtocol == ApiProtocol.OpenAIChat && stream && !clientWantsChatUsage)
                {
                    // Plan §4.2: always ask for usage; the extra usage chunk is filtered out before it reaches the client.
                    var options = body["stream_options"] as JsonObject ?? new JsonObject();
                    options["include_usage"] = true;
                    body["stream_options"] = options;
                    injectedChatUsage = true;
                    rewritten = true;
                }
                upstreamText = rewritten ? body.ToJsonString(GatewayJson.Options) : guard.Body;
            }
            else
            {
                var encoded = upstreamCodec!.EncodeRequest(ir!, new RequestEncodeContext
                {
                    UpstreamModel = upstreamModel,
                    DefaultMaxOutputTokens = effective.MaxOutputTokens is { } max && max <= int.MaxValue ? (int)max : null,
                    EffortBudgets = s.EffortBudgets,
                    AutoCacheControl = provider.SettingFlag("auto_cache_control", true),
                });
                upstreamText = encoded.ToJsonString(GatewayJson.Options);
                if (ir!.Warnings.Count > 0) logger.LogInformation("Request {Id}: lossy mapping: {Warnings}", record.Id, string.Join("; ", ir.Warnings));
            }
            capture?.Set(BodyStore.UpstreamRequest, upstreamText);

            var url = UpstreamUrls.For(endpoint, upstreamModel, stream);
            // Claude Code calls /v1/messages?beta=true; the relay keeps its query string.
            if (claudeMode == ClaudeHeaderMode.Relay) url = WithClientQuery(url, ctx.Request.QueryString);
            var (sent, accountId) = await SendAsync(route, endpoint, url, upstreamText, stream, passthrough, claudeMode, upstreamModel, ctx.Request, ct);
            using var response = sent;
            record.AccountId = accountId;
            record.TtfbMs = sw.ElapsedMilliseconds;
            record.HttpStatus = (int)response.StatusCode;
            record.UpstreamRequestId = FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray");
            if (claudeMode == ClaudeHeaderMode.Relay) RelayResponseHeaders(response, ctx.Response);

            if (!response.IsSuccessStatusCode)
            {
                await ForwardErrorAsync(ctx, response, record, inbound, passthrough, clientCodec, upstreamCodec, capture, ct);
                return;
            }

            var restore = guard.RestoreMap;
            // 被强制成流式的上游（codex backend）遇到"客户端要非流式"时：把 SSE 读完，
            // 再按客户端协议拼成一个完整 JSON 响应回去。
            var collapseStream = forceResponsesStream && !clientWantsStream;
            decoder = upstreamCodec?.CreateResponseDecoder(new ResponseDecodeContext { Stream = stream, Model = upstreamModel });
            encoder = passthrough
                ? null
                : clientCodec!.CreateResponseEncoder(new ResponseEncodeContext
                {
                    Stream = clientWantsStream,
                    Model = model,
                    ResponseId = record.Id,
                    Created = record.StartedAtUtc,
                    IncludeUsage = inbound != ApiProtocol.OpenAIChat || clientWantsChatUsage,
                    Request = ir,
                });

            if (stream && !collapseStream)
            {
                // Gemini :streamGenerateContent without ?alt=sse answers with a JSON array of chunks, not SSE.
                var asJsonArray = inbound == ApiProtocol.Gemini && pathAltSse == false;
                await StreamAsync(ctx, response, decoder, encoder, observer, sw, restore, injectedChatUsage, capture, s.StreamIdleTimeoutSec, asJsonArray, ct);
            }
            else if (collapseStream)
                await CollapseStreamAsync(ctx, response, decoder, encoder, observer, sw, restore, capture, s.StreamIdleTimeoutSec, ct);
            else
                await BufferAsync(ctx, response, decoder, encoder, observer, sw, restore, capture, s.StreamIdleTimeoutSec, ct);

            if (observer.Error is { } err)
            {
                record.Status = RequestStatus.UpstreamError;
                record.ErrorType = err.Type;
                record.ErrorMessage = Truncate(err.Message, 2000);
            }
        }
        catch (GatewayException e)
        {
            if (record.Status != RequestStatus.Blocked) record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= e.Status;
            record.ErrorType = e.Type;
            record.ErrorMessage = e.Message;
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
            record.Status = RequestStatus.ClientCancelled;
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
            record.Status = RequestStatus.ClientCancelled;
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
            record.ResponseModel = decoder?.ResponseModel;
            Finish(record, observer, sw, effective, provider, s.Locale);
            if (capture is not null)
            {
                record.BodyRef = BodyStore.RefFor(record.Id, record.StartedAtUtc);
                await capture.WriteAsync(bodies, record.BodyRef);
            }
            usageWriter.Enqueue(record);
        }
    }

    // ------------------------------------------------------------------ upstream

    private async Task<(HttpResponseMessage Response, string? AccountId)> SendAsync(
        GatewayRoute route, ProviderEndpoint endpoint, string url, string body, bool stream, bool passthrough,
        ClaudeHeaderMode claudeMode, string upstreamModel, HttpRequest clientRequest, CancellationToken ct)
    {
        var provider = route.Provider;
        var subscription = provider.AuthScheme == AuthSchemes.OAuthSubscription;
        // Plan §5.4: in failover mode a rate-limited or dead account hands the request to the next usable account
        // (which also becomes the provider's current account, so the following requests stay on it).
        var failover = subscription && SubscriptionSupport.SwitchModeOf(provider) == SwitchModes.Failover;
        var client = http.For(provider);
        var accountId = route.AccountId;
        var tried = new List<string>();
        while (true)
        {
            UpstreamAuth credentials;
            try
            {
                credentials = await auth.ResolveAsync(provider.Id, accountId, ct) ?? UpstreamAuth.None;
            }
            catch (SubscriptionAuthException e)
            {
                throw new GatewayException(401, "authentication_error", $"提供商 {provider.Name} 的订阅账号不可用：{e.Message}");
            }

            var response = await client.SendAsync(Build(credentials), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && subscription)
            {
                // Plan §5.4: refresh the subscription token once and retry before reporting the 401.
                response.Dispose();
                var used = credentials.AccountId ?? accountId;
                try
                {
                    credentials = await auth.ResolveAfterUnauthorizedAsync(provider.Id, used, ct);
                }
                catch (SubscriptionAuthException e)
                {
                    if (failover && used is not null && await FailOverAsync(used, null, e.Message) is { } next)
                    {
                        accountId = next;
                        continue;
                    }
                    throw new GatewayException(401, "authentication_error", $"提供商 {provider.Name} 的订阅账号需要重新登录：{e.Message}");
                }
                response = await client.SendAsync(Build(credentials), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            if (failover && response.StatusCode == HttpStatusCode.TooManyRequests && credentials.AccountId is { } limited)
            {
                var until = RateLimitResetOf(response, DateTimeOffset.UtcNow);
                if (await FailOverAsync(limited, until, $"429 限流，冷却至 {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC") is { } next)
                {
                    response.Dispose();
                    accountId = next;
                    continue;
                }
            }
            return (response, credentials.AccountId);
        }

        async Task<string?> FailOverAsync(string from, DateTimeOffset? cooldownUntil, string reason)
        {
            tried.Add(from);
            var next = await auth.FailOverAsync(provider.Id, from, cooldownUntil, reason, tried, ct);
            if (next is not null)
                logger.LogInformation("Provider {Provider}: account {From} → {To} ({Reason})", provider.Name, from, next, reason);
            return next;
        }

        HttpRequestMessage Build(UpstreamAuth a)
        {
            var target = a.QueryName is not null && a.QueryValue is not null ? UpstreamUrls.WithQuery(url, a.QueryName, a.QueryValue) : url;
            var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
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
            // Non-Claude-Code callers of a Claude subscription (policy lifted): the betas OAuth tokens require.
            if (claudeMode == ClaudeHeaderMode.Compat) ClaudeOAuthHeaders.ApplyCompat(request, clientRequest.Headers);
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
        BodyCapture? capture, int idleTimeoutSec, bool asJsonArray, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = asJsonArray ? "application/json" : "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var parser = new SseParser();
        var upstreamCapture = capture is null ? null : new StringBuilder();
        var clientCapture = capture is null ? null : new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var outText = new StringBuilder();
        var array = asJsonArray ? new JsonArrayBody() : null;
        var sawSseEvent = false;
        // Upstream bytes until the first SSE event parses: a body that never becomes SSE (a JSON object /
        // array from an upstream ignoring alt=sse or the stream flag) is decoded or forwarded from here.
        var rawPending = new StringBuilder();

        try
        {
            while (true)
            {
                var read = await ReadWithIdleTimeoutAsync(stream, buffer, idleTimeoutSec, ct);
                if (read == 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                upstreamCapture?.Append(text);
                var events = parser.Feed(buffer.AsSpan(0, read));
                if (events.Count > 0)
                {
                    sawSseEvent = true;
                    rawPending.Clear();
                }
                else if (!sawSseEvent)
                {
                    rawPending.Append(text);
                }
                foreach (var sse in events) Handle(sse);
                await FlushAsync();
            }
            foreach (var sse in parser.Flush()) Handle(sse);
            if (!sawSseEvent && rawPending.Length > 0) await HandleNonSseBodyAsync(rawPending.ToString());
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
            if (capture is not null)
            {
                capture.Set(BodyStore.UpstreamResponse, upstreamCapture!.ToString());
                capture.Set(BodyStore.ClientResponse, clientCapture!.ToString());
            }
        }

        void Handle(SseEvent sse)
        {
            var events = decoder?.DecodeSse(sse) ?? [];
            if (encoder is null)
            {
                // Pass-through: observe, then forward the event unchanged (minus our injected usage chunk).
                foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds);
                if (dropChatUsageChunk && IsChatUsageOnlyChunk(sse.Data)) return;
                Write(asJsonArray
                    ? PrivacyGuardService.Restore(sse.Data, restore)
                    : PrivacyGuardService.Restore(SseWriter.Format(sse), restore));
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
                Emit(decoder.DecodeJson(body));
            }
            else if (arrayBody is not null)
            {
                // Gemini's non-alt=sse shape: an array of GenerateContentResponse chunks.
                foreach (var element in arrayBody.OfType<JsonObject>())
                    Emit(decoder.DecodeSse(new SseEvent(null, element.ToJsonString(GatewayJson.Options))));
            }
        }

        void Emit(IEnumerable<UnifiedStreamEvent> events)
        {
            foreach (var e in events)
            {
                observer.Observe(e, sw.ElapsedMilliseconds);
                if (encoder is null) continue;
                foreach (var frame in encoder.OnEvent(e))
                    Write(asJsonArray
                        ? PrivacyGuardService.Restore(frame.Data, restore)
                        : PrivacyGuardService.Restore(SseWriter.Format(frame), restore));
            }
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
            var text = outText.ToString();
            outText.Clear();
            clientCapture?.Append(text);
            await ctx.Response.WriteAsync(text, ct);
            await ctx.Response.Body.FlushAsync(ct);
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
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await ReadWithIdleTimeoutAsync(stream, buffer, idleTimeoutSec, ct)) > 0) memory.Write(buffer, 0, read);
        var text = Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        capture?.Set(BodyStore.UpstreamResponse, text);

        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // Not JSON: forwarded as-is on the pass-through path; the conversion path reports it below.
        }

        var events = json is null || decoder is null ? [] : decoder.DecodeJson(json).ToList();
        foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds);

        string output;
        if (encoder is null)
        {
            output = text;
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        }
        else
        {
            if (json is null) throw new GatewayException(502, "api_error", "上游返回的不是 JSON：" + Truncate(text, 300));
            foreach (var e in events) encoder.OnEvent(e);
            output = encoder.BuildJson().ToJsonString(GatewayJson.Options);
            ctx.Response.ContentType = "application/json";
        }
        output = PrivacyGuardService.Restore(output, restore);
        capture?.Set(BodyStore.ClientResponse, output);
        ctx.Response.StatusCode = 200;
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
        var parser = new SseParser();
        var upstreamCapture = capture is null ? null : new StringBuilder();
        var buffer = new byte[16 * 1024];
        var rawPending = new StringBuilder();
        var sawSseEvent = false;
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(stream, buffer, idleTimeoutSec, ct);
            if (read == 0) break;
            upstreamCapture?.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var events = parser.Feed(buffer.AsSpan(0, read));
            if (events.Count > 0)
            {
                sawSseEvent = true;
                rawPending.Clear();
            }
            else if (!sawSseEvent)
            {
                rawPending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            foreach (var sse in events) Emit(decoder?.DecodeSse(sse) ?? []);
        }
        foreach (var sse in parser.Flush()) Emit(decoder?.DecodeSse(sse) ?? []);
        if (!sawSseEvent && rawPending.Length > 0 && JsonNode.Parse(rawPending.ToString()) is JsonObject whole)
            Emit(decoder?.DecodeJson(whole) ?? []);
        Emit(decoder?.Complete() ?? []);
        capture?.Set(BodyStore.UpstreamResponse, upstreamCapture?.ToString() ?? "");

        // 非转换路径没有客户端编码器：把上游的完整响应对象原样透出，只是去掉了 SSE 外壳。
        JsonObject payload;
        if (encoder is null)
            payload = decoder?.FinalResponse
                      ?? throw new GatewayException(502, "api_error", "上游强制流式但响应里没有完整结果。");
        else
            payload = encoder.BuildJson();
        var json = payload.ToJsonString(GatewayJson.Options);
        json = PrivacyGuardService.Restore(json, restore);
        capture?.Set(BodyStore.ClientResponse, json);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(json, ct);

        void Emit(IEnumerable<UnifiedStreamEvent> events)
        {
            foreach (var e in events) observer.Observe(e, sw.ElapsedMilliseconds);
        }
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
        if (record.TtftMs is { } ttft && record.TotalMs > ttft)
        {
            record.GenerationMs = record.TotalMs - ttft;
            var generated = observer.Usage is null ? 0 : observer.Usage.Get(TokenTypes.Output) + observer.Usage.Get(TokenTypes.Reasoning);
            if (generated > 0 && record.GenerationMs > 0) record.OutputTps = Math.Round(generated / (record.GenerationMs.Value / 1000.0), 2);
        }
    }

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

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequestBytes) throw new GatewayException(413, "request_too_large", "请求体超过 64 MiB。");
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Length > MaxRequestBytes) throw new GatewayException(413, "request_too_large", "请求体超过 64 MiB。");
        return text;
    }

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

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream stream, byte[] buffer, int idleTimeoutSec, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, idleTimeoutSec)));
        try
        {
            return await stream.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpstreamIdleTimeoutException($"上游超过 {idleTimeoutSec} 秒没有返回数据（流空闲超时，可在设置中调整）。");
        }
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

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];

    /// <summary>Tracks the IR events of one response: first-token time, usage, finish reason, errors.</summary>
    private sealed class ResponseObserver
    {
        public long? TtftMs { get; private set; }
        public NormalizedUsage? Usage { get; private set; }
        public JsonObject? RawUsage { get; private set; }
        public FinishReason? Finish { get; private set; }
        public ErrorEvent? Error { get; private set; }

        public void Observe(UnifiedStreamEvent e, long elapsedMs)
        {
            switch (e)
            {
                case TextDeltaEvent { Text.Length: > 0 }:
                case ReasoningDeltaEvent { Text.Length: > 0 }:
                case ToolArgsDeltaEvent:
                case BlockStartEvent { Kind: BlockKind.ToolCall }:
                    TtftMs ??= elapsedMs;
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
        private readonly Dictionary<string, string> _parts = new();

        public void Set(string part, string body) => _parts[part] = body;

        public async Task WriteAsync(BodyStore store, string bodyRef)
        {
            foreach (var (part, body) in _parts)
            {
                try
                {
                    await store.WriteAsync(bodyRef, part, body);
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
