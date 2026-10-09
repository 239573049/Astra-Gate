using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;
using Microsoft.AspNetCore.WebUtilities;

namespace Astra.Server.Api;

public sealed record RemoteModelDto(string Id, string? DisplayName, string? LinkedSystemModelId, bool AlreadyAdded,
    IReadOnlyList<ApiProtocol>? UpstreamProtocols = null);

/// <summary>Everything one test run needs, after validation. Every field is optional on the wire.</summary>
public sealed record ProviderTestOptions(string? ModelId, ApiProtocol? Protocol, bool Stream, string? Prompt, int MaxOutputTokens);

/// <summary>One event of a provider test stream, ready to be written as SSE.</summary>
public sealed record ProviderTestEvent(string Event, JsonNode Payload);

/// <summary>
/// State of one provider test run: the protocol and model it uses, the prompt it sends, and what the upstream
/// answered so far. The sink is a delegate, so the same run drives SSE or a plain list of events.
/// </summary>
public sealed class ProviderTestRun(ApiProtocol protocol, string modelId, Func<ProviderTestEvent, Task> emit)
{
    public ApiProtocol Protocol { get; } = protocol;
    public string ModelId { get; } = modelId;

    /// <summary>Prompt typed by the caller; empty means "use the default prompt".</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Milliseconds from sending the request to the first token of the answer.</summary>
    public long? FirstTokenMs { get; internal set; }
    /// <summary>Reasoning text reported by the upstream, concatenated.</summary>
    public string Reasoning { get; internal set; } = "";

    /// <summary>True once the codec surfaced anything at all (text, reasoning, tool call or usage).</summary>
    public bool HasEvents { get; internal set; }

    /// <summary>Upstream-reported usage, verbatim (stored on the request log); empty when none was reported.</summary>
    public JsonObject Usage { get; internal set; } = new();

    /// <summary>Where the run reports progress; bound by <see cref="ProviderProbe.RunTestAsync"/>.</summary>
    internal Func<ProviderTestEvent, Task> Emit { get; set; } = emit;

    internal async Task EmitAsync(string name, JsonNode payload) => await Emit(new ProviderTestEvent(name, payload));
}

/// <summary>
/// Authenticated connection tests and remote model discovery. Never forwards a local client key. A test is a real
/// request: it applies the privacy guard, is recorded in the request log and billed, and is never retried.
/// </summary>
public sealed class ProviderProbe(AstraDatabase db, IHttpClientFactory http, UpstreamAuthResolver auth,
    EffectiveModelResolver models, SettingsService settings, UsageWriter writer, CodecRegistry codecs,
    PrivacyGuardService privacy, BodyStore bodies, ILogger<ProviderProbe> logger) : IModelProtocolResolver
{
    public const string HttpClientName = "astra-provider-probe";

    private readonly ConcurrentDictionary<(string ProviderId, string? AccountId), SemaphoreSlim> _modelProtocolGates = new();
    private readonly ConcurrentDictionary<(string ProviderId, string? AccountId), ModelProtocolSnapshot> _modelProtocolCache = new();
    private sealed record ModelProtocolSnapshot(DateTimeOffset ExpiresAtUtc, IReadOnlyList<RemoteModelDto> Models);

    /// <summary>Used when the caller sends no prompt.</summary>
    public const string DefaultTestPrompt = "Reply with OK.";
    public const int DefaultMaxOutputTokens = 256;
    public const int MaxTestPromptChars = 4000;

    private const int TestTimeoutSec = 120;
    private const int ModelListTimeoutSec = 30;
    private const int StreamReadTimeoutSec = 30;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxErrorBytes = 1024 * 1024;
    private const int MaxModels = 2000;
    private const int MaxPages = 20;

    /// <summary>Raw upstream text kept in memory and sent back as the "raw" view.</summary>
    private const int MaxRawChars = 256 * 1024;
    private const string TruncatedNote = "\n…[truncated]";

    /// <summary>Response headers echoed in the result; values longer than this are cut.</summary>
    private const int MaxHeaderValueChars = 1024;
    private const int MaxHeaderCount = 100;

    /// <summary>The upstream protocols this provider can be tested with (one endpoint each).</summary>
    public static IReadOnlyList<ApiProtocol> ProtocolsFor(Provider p) =>
        ApiProtocols.All.Where(protocol => p.EndpointFor(protocol) is not null).ToList();

    /// <summary>
    /// Validates a test request and resolves everything it needs. Throws <see cref="AdminApiException"/> while the
    /// response has not started yet, so these failures are still plain JSON errors.
    /// </summary>
    public async Task<ProviderTestRun> PrepareTestAsync(string providerId, ProviderTestOptions options, CancellationToken ct)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        var modelId = options.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = (await db.Providers.ListModelsAsync(p.Id, ct)).FirstOrDefault(m => m.Enabled)?.ModelId;
        if (string.IsNullOrWhiteSpace(modelId)) throw new AdminApiException(400, "Add a model or provide modelId first");
        modelId = modelId.Trim();
        if (modelId.Length > 512 || modelId.Any(char.IsControl)) throw new AdminApiException(400, "Invalid model ID");
        var protocol = options.Protocol ?? PreferredEndpoint(p).Protocol;
        if (p.EndpointFor(protocol) is null) throw new AdminApiException(400, $"Provider has no endpoint for protocol {protocol.ToId()}");
        var prompt = options.Prompt?.Trim();
        if (prompt is { Length: > MaxTestPromptChars }) throw new AdminApiException(400, $"Test prompt must be at most {MaxTestPromptChars} characters");
        return new ProviderTestRun(protocol, modelId, _ => Task.CompletedTask) { Prompt = prompt ?? "" };
    }

    /// <summary>
    /// Sends one real test request, streaming progress into <paramref name="run"/>, and records it like a gateway
    /// request (usage, cost, privacy report, optional debug bodies). Everything except the caller's own cancellation
    /// is reported as a "done" event, because by then the HTTP response has already started.
    /// </summary>
    public async Task RunTestAsync(string providerId, ProviderTestOptions options, ProviderTestRun run,
        Func<ProviderTestEvent, Task> emit, CancellationToken ct)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        var protocol = p.EndpointFor(run.Protocol) is { } endpoint ? run.Protocol : PreferredEndpoint(p).Protocol;
        endpoint = p.EndpointFor(protocol)!;
        var prompt = string.IsNullOrWhiteSpace(run.Prompt) ? DefaultTestPrompt : run.Prompt;
        var (_, effective) = await models.ResolveByModelIdAsync(p, run.ModelId, ct);
        run.Emit = emit;

        var record = new RequestRecord
        {
            Id = Ulid.NewUlid(), StartedAtUtc = DateTimeOffset.UtcNow, ProviderId = p.Id, ProviderName = p.Name,
            InboundProtocol = protocol.ToId(), UpstreamProtocol = protocol.ToId(), Passthrough = true,
            RequestedModel = run.ModelId, UpstreamModel = run.ModelId, SystemModelId = effective.SystemModelId,
            Stream = options.Stream, UserAgent = "Astra connection test", Status = RequestStatus.GatewayError,
        };
        var capture = settings.Current.DebugBodies ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
        var sw = Stopwatch.StartNew();
        long? httpMs = null;
        NormalizedUsage? usage = null;
        JsonObject? usageRaw = null;
        JsonObject? decodedBody = null;
        string? error = null;
        string? contentType = null;
        var headers = new JsonObject();
        var ok = false;
        var text = new StringBuilder();
        var raw = new StringBuilder();
        var rawTruncated = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSec));

        try
        {
            var bodyText = TestBody(run.ModelId, prompt, options.MaxOutputTokens, options.Stream, protocol)
                .ToJsonString(GatewayJson.Options);
            // Privacy guard (plan §6.7): a test prompt is client content, so the same policy and the same request-log
            // report apply as for a gateway request.
            var guard = privacy.Inspect(bodyText, null);
            var restore = guard.RestoreMap;
            if (guard.Applied && guard.Hits.Count > 0)
                record.PrivacyJson = PrivacyGuardService.ReportJson(guard.DryRun, guard.Blocked, guard.Hits, restore.Count);
            if (guard.Blocked)
            {
                // Nothing leaves the machine; report it the way a gateway request would (the run is still logged).
                record.Status = RequestStatus.Blocked;
                record.ErrorType = "privacy_blocked";
                error = "Blocked by the privacy guard; see the request log for the matched rules";
                record.ErrorMessage = error;
            }
            else
            {
                using var client = CreateClient(p);
                using var request = await RequestAsync(p, HttpMethod.Post, UpstreamUrls.For(endpoint, run.ModelId, options.Stream),
                    protocol, options.Stream, deadline.Token);
                // 模拟 Claude Code（subscription.mimic_claude_code）：测试请求和网关请求同形，
                // 否则"测试配置"对非 Haiku 模型的结论和真实请求不一致。
                var outbound = protocol == ApiProtocol.Anthropic && SubscriptionSupport.MimicClaudeCodeOf(p)
                    ? ClaudeCodeMimicry.ApplyBody(guard.Body, p.Id, null)
                    : guard.Body;
                request.Content = new StringContent(outbound, Encoding.UTF8, "application/json");
                if (capture is not null) capture[BodyStore.UpstreamRequest] = Redact(outbound, p, request);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                httpMs = sw.ElapsedMilliseconds;
                record.HttpStatus = (int)response.StatusCode;
                record.UpstreamRequestId = FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray");
                contentType = response.Content.Headers.ContentType?.ToString();
                headers = ResponseHeaders(response, p, request);
                await run.EmitAsync("headers", new JsonObject
                {
                    ["httpStatus"] = record.HttpStatus, ["httpMs"] = httpMs, ["contentType"] = contentType,
                    ["headers"] = headers.DeepClone(),
                });

                // The redacted copy of what the upstream sent, accumulated for the "raw" view.
                var rawView = new StringBuilder();
                var rawViewTruncated = false;
                string Redacted(string body)
                {
                    var safe = Redact(body, p, request);
                    AppendBounded(rawView, safe, ref rawViewTruncated);
                    return safe;
                }

                // No retry on 401/403, not even for subscription accounts: plan §5.4 covers the gateway path only.
                if (!response.IsSuccessStatusCode)
                {
                    var safe = Redacted(await ReadLimitedAsync(response, MaxErrorBytes, deadline.Token));
                    if (capture is not null) capture[BodyStore.UpstreamResponse] = safe;
                    error = ErrorMessage(TryObject(safe), response.StatusCode, p, request, safe);
                    record.Status = RequestStatus.UpstreamError;
                    record.ErrorType = "upstream_error";
                    record.ErrorMessage = error;
                }
                else
                {
                    var decoder = codecs.Require(protocol)
                        .CreateResponseDecoder(new ResponseDecodeContext { Stream = options.Stream, Model = run.ModelId });
                    var events = new List<ProviderTestEvent>();
                    var sink = new EventSink(guard, text, events);
                    decodedBody = options.Stream
                        ? await ReadStreamAsync(response, decoder, sw, sink, Redacted, deadline.Token)
                        : await ReadBodyAsync(response, decoder, sw, sink, Redacted, deadline.Token);
                    ok = sink.Ok && decodedBody is not null;
                    error = sink.Error;
                    if (!ok)
                    {
                        // A 2xx body the codec could not read (or an error object inside it) still came from the upstream.
                        record.Status = RequestStatus.UpstreamError;
                        record.ErrorType = "upstream_error";
                        record.ErrorMessage = error ?? "Upstream returned an unusable response";
                    }
                    run.FirstTokenMs = sink.FirstTokenMs;
                    run.Reasoning = sink.Reasoning.ToString();
                    run.HasEvents = sink.HasEvents;
                    run.Usage = (JsonObject)sink.Usage.DeepClone();
                    foreach (var e in events) await run.EmitAsync(e.Event, e.Payload);
                    record.ResponseModel = decoder.ResponseModel ?? ReportedModel(protocol, decodedBody);
                    usageRaw = run.Usage;
                    if (usageRaw.Count > 0) usage = Normalize(protocol, usageRaw);
                    if (capture is not null) capture[BodyStore.UpstreamResponse] = rawView.ToString();
                }
                // The raw view shows what the gateway actually sent; with redactions in place the originals are put back.
                raw = restore.Count == 0 ? rawView : new StringBuilder(PrivacyGuardService.Restore(rawView.ToString(), restore));
                rawTruncated = rawViewTruncated;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away: stop, report nothing more, and log it like any cancelled request.
            record.Status = RequestStatus.ClientCancelled;
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SubscriptionAuthException
                                       or CryptographicException or AdminApiException or IOException)
        {
            (record.Status, record.ErrorType) = ex switch
            {
                HttpRequestException or IOException => (record.HttpStatus is null ? RequestStatus.GatewayError : RequestStatus.UpstreamError, "upstream_unreachable"),
                SubscriptionAuthException or CryptographicException => (RequestStatus.GatewayError, ex.GetType().Name),
                OperationCanceledException => (RequestStatus.GatewayError, "timeout"),
                _ => (record.HttpStatus is null ? RequestStatus.GatewayError : RequestStatus.UpstreamError, "upstream_error"),
            };
            if (ex is HttpRequestException or IOException) record.HttpStatus ??= 502;
            error = ex switch
            {
                AdminApiException api => api.Message,
                SubscriptionAuthException => "Subscription account is missing or unusable; sign in again",
                CryptographicException => "Stored API key could not be decrypted; replace it",
                OperationCanceledException => "Connection test timed out",
                _ => "Could not connect to the upstream provider",
            };
            record.ErrorMessage = error;
        }
        finally
        {
            if (ok) record.Status = RequestStatus.Success;
            record.TtfbMs = httpMs;
            record.TtftMs = run.FirstTokenMs;
            record.TotalMs = sw.ElapsedMilliseconds;
            if (run.FirstTokenMs is { } ttft && record.TotalMs > ttft)
            {
                record.GenerationMs = record.TotalMs - ttft;
                var generated = usage is null ? 0 : usage.Get(TokenTypes.Output) + usage.Get(TokenTypes.Reasoning);
                if (generated > 0 && record.GenerationMs > 0)
                    record.OutputTps = Math.Round(generated / (record.GenerationMs.Value / 1000.0), 2);
            }
            if (usageRaw is not null)
            {
                record.UsageSource = usage is null ? "missing" : "reported";
                if (usage is not null) record.UsageRawJson = usageRaw.ToJsonString(GatewayJson.Options);
            }
            try
            {
                RequestBilling.Apply(record, usage, effective.Pricing, settings.Current.Locale,
                    subscriptionEquivalent: p.AuthScheme == AuthSchemes.OAuthSubscription);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Billing must never swallow the test result.
            }
            if (capture is not null && record.Status != RequestStatus.ClientCancelled)
            {
                record.BodyRef = BodyStore.RefFor(record.Id, record.StartedAtUtc);
                foreach (var (part, body) in capture)
                {
                    try { await bodies.WriteAsync(record.BodyRef, part, body); }
                    catch (IOException) { }
                }
            }
            writer.Enqueue(record);
        }

        await run.EmitAsync("done", new JsonObject
        {
            ["ok"] = ok,
            ["error"] = string.IsNullOrEmpty(error) ? null : error,
            ["httpStatus"] = record.HttpStatus,
            ["httpMs"] = httpMs,
            ["ttftMs"] = run.FirstTokenMs,
            ["totalMs"] = record.TotalMs,
            ["text"] = text.ToString(),
            ["reasoning"] = run.Reasoning,
            ["raw"] = raw.ToString(),
            ["rawTruncated"] = rawTruncated,
            ["contentType"] = contentType,
            ["headers"] = headers.DeepClone(),
            ["responseModel"] = record.ResponseModel,
            ["requestId"] = record.Id,
            ["usage"] = usage is null ? null : new JsonObject
            {
                ["inputTokens"] = usage.TotalInput,
                ["outputTokens"] = usage.TotalOutput,
                ["raw"] = usageRaw?.DeepClone(),
            },
        });
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Streaming read: SSE frames go through the codec; a body that never became SSE is decoded whole.</summary>
    private async Task<JsonObject?> ReadStreamAsync(HttpResponseMessage response, IResponseDecoder decoder,
        Stopwatch sw, EventSink sink, Func<string, string> collect, CancellationToken ct)
    {
        var parser = new SseParser();
        var pending = new StringBuilder();
        var sawEvent = false;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(stream, buffer, ct);
            if (read == 0) break;
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
            collect(chunk);
            var frames = parser.Feed(buffer.AsSpan(0, read));
            if (frames.Count > 0)
            {
                sawEvent = true;
                pending.Clear();
            }
            else if (!sawEvent) pending.Append(chunk);
            foreach (var frame in frames) await FeedAsync(sink, decoder.DecodeSse(frame), sw);
        }
        foreach (var frame in parser.Flush()) await FeedAsync(sink, decoder.DecodeSse(frame), sw);
        // The upstream ignored the stream flag (or answered with an error and a 200): decode that body as one document.
        if (!sawEvent && pending.Length > 0)
        {
            var body = TryObject(pending.ToString());
            if (body is null)
            {
                sink.Failed("Upstream returned invalid JSON");
                return null;
            }
            await FeedAsync(sink, decoder.DecodeJson(body), sw);
            await FeedAsync(sink, decoder.Complete(), sw);
            return body;
        }
        await FeedAsync(sink, decoder.Complete(), sw);
        return new JsonObject();
    }

    /// <summary>Non-streaming read: the whole body is decoded once.</summary>
    private async Task<JsonObject?> ReadBodyAsync(HttpResponseMessage response, IResponseDecoder decoder,
        Stopwatch sw, EventSink sink, Func<string, string> collect, CancellationToken ct)
    {
        var body = TryObject(collect(await ReadResponseAsync(response, ct)));
        if (body is null)
        {
            sink.Failed("Upstream returned invalid JSON");
            return null;
        }
        await FeedAsync(sink, decoder.DecodeJson(body), sw);
        await FeedAsync(sink, decoder.Complete(), sw);
        return body;
    }

    /// <summary>Folds one codec batch into the preview, the usage and the errors.</summary>
    private async Task FeedAsync(EventSink sink, IEnumerable<UnifiedStreamEvent> events, Stopwatch sw)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case TextDeltaEvent { Text.Length: > 0 } delta:
                    sink.FirstToken(sw.ElapsedMilliseconds);
                    await sink.TextAsync(delta.Text);
                    break;
                case ReasoningDeltaEvent { Text.Length: > 0 } thinking:
                    sink.FirstToken(sw.ElapsedMilliseconds);
                    await sink.ReasoningAsync(thinking.Text);
                    break;
                case UsageEvent usage:
                    sink.SawUsage();
                    if (usage.Raw is not null) sink.Usage = usage.Raw;
                    break;
                case ErrorEvent err:
                    sink.Failed(err.Message);
                    break;
            }
        }
    }

    /// <summary>
    /// Collects one test response: the preview text (already redaction-restored), the reasoning text, the events to
    /// emit, and the pass/fail verdict. Kept separate from the run so a half-read stream never leaks state outward.
    /// </summary>
    private sealed class EventSink(PrivacyGuardResult guard, StringBuilder text, List<ProviderTestEvent> events)
    {
        public string Error { get; private set; } = "";
        public bool Ok { get; private set; } = true;
        public bool HasEvents { get; private set; }
        public long? FirstTokenMs { get; private set; }
        public StringBuilder Reasoning { get; } = new();

        /// <summary>Usage reported by the upstream, verbatim; empty when it reported none.</summary>
        public JsonObject Usage { get; set; } = new();

        public Task TextAsync(string delta)
        {
            HasEvents = true;
            var value = PrivacyGuardService.Restore(delta, guard.RestoreMap);
            text.Append(value);
            return Emit(new JsonObject { ["kind"] = "text", ["text"] = value });
        }

        public Task ReasoningAsync(string delta)
        {
            HasEvents = true;
            var value = PrivacyGuardService.Restore(delta, guard.RestoreMap);
            Reasoning.Append(value);
            return Emit(new JsonObject { ["kind"] = "reasoning", ["text"] = value });
        }

        public void FirstToken(long elapsedMs)
        {
            HasEvents = true;
            FirstTokenMs ??= elapsedMs;
        }

        public void SawUsage() => HasEvents = true;

        public void Failed(string message)
        {
            HasEvents = true;
            Ok = false;
            if (Error.Length == 0) Error = message;
        }

        private Task Emit(JsonNode payload)
        {
            events.Add(new ProviderTestEvent("delta", payload));
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ upstream

    private async Task<HttpRequestMessage> RequestAsync(Provider p, HttpMethod method, string url, ApiProtocol protocol,
        bool stream, CancellationToken ct, string? accountId = null)
    {
        var credentials = await auth.ResolveAsync(p.Id, accountId, ct) ?? UpstreamAuth.None;
        if (credentials.QueryName is { } q && credentials.QueryValue is { } value) url = WithQuery(url, q, value);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        foreach (var (key, val) in p.ExtraHeaders) request.Headers.TryAddWithoutValidation(key, val);
        if (protocol == ApiProtocol.Anthropic && !request.Headers.Contains("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        // Claude subscription OAuth tokens are rejected without the claude-code / oauth betas (same as the gateway's compat path).
        // With subscription.mimic_claude_code the probe presents the full Claude Code identity, like the gateway does.
        if (protocol == ApiProtocol.Anthropic && SubscriptionSupport.IsClaudeSubscription(p))
        {
            if (SubscriptionSupport.MimicClaudeCodeOf(p))
                ClaudeCodeMimicry.ApplyHeaders(request, accountId ?? p.Id, null);
            else
                ClaudeOAuthHeaders.ApplyCompat(request, new HeaderDictionary());
        }
        if (credentials.HeaderName is { } header)
        {
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, credentials.HeaderValue);
        }
        return request;
    }

    private HttpClient CreateClient(Provider provider)
    {
        if (provider.HttpProxy is null) return http.CreateClient(HttpClientName);
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(provider.HttpProxy), UseProxy = true, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All,
        }, false) { Timeout = TimeSpan.FromSeconds(TestTimeoutSec + 10) };
    }

    /// <summary>
    /// Learns missing Copilot model capabilities before a generation request. Cached per provider/account to avoid
    /// probing on every call, including for models not saved locally; existing model rows are backfilled separately
    /// from their editable settings. Other providers retain their ordinary endpoint selection.
    /// </summary>
    public async Task<IReadOnlyList<ApiProtocol>> ResolveAsync(Provider provider, ProviderModel model, string? accountId, CancellationToken ct)
    {
        if (model.UpstreamProtocols.Count > 0 || string.IsNullOrEmpty(model.ModelId)
            || SubscriptionSupport.ProviderKeyOf(provider) != "github-copilot-subscription")
            return model.UpstreamProtocols;

        var key = (provider.Id, accountId);
        if (_modelProtocolCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached.Models.FirstOrDefault(m => m.Id == model.ModelId)?.UpstreamProtocols ?? model.UpstreamProtocols;
        var gate = _modelProtocolGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // A concurrent login or request may already have filled this row while we waited.
            var stored = await db.Providers.GetModelAsync(provider.Id, model.ModelId, ct);
            if (stored?.UpstreamProtocols is { Count: > 0 } known) return known;
            if (!_modelProtocolCache.TryGetValue(key, out cached) || cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                IReadOnlyList<RemoteModelDto> remote;
                var lifetime = TimeSpan.FromMinutes(5);
                try
                {
                    remote = await ModelsAsync(provider.Id, ct, accountId);
                    await RefreshModelProtocolsAsync(provider.Id, remote, ct);
                }
                catch (AdminApiException e)
                {
                    // Best effort, like login-time sync; retry unavailable discovery sooner than a successful list.
                    logger.LogWarning("Provider {Provider}: model protocol discovery failed (HTTP {Status}); using configured endpoints until retry",
                        provider.Name, e.StatusCode);
                    remote = [];
                    lifetime = TimeSpan.FromMinutes(1);
                }
                cached = new ModelProtocolSnapshot(DateTimeOffset.UtcNow + lifetime, remote);
                _modelProtocolCache[key] = cached;
            }
            return cached.Models.FirstOrDefault(m => m.Id == model.ModelId)?.UpstreamProtocols ?? model.UpstreamProtocols;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteModelDto>> ModelsAsync(string providerId, CancellationToken ct, string? accountId = null)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        var snapshot = Json.Deserialize<ProviderTemplate>(p.TemplateSnapshotJson);
        var spec = snapshot?.ModelListEndpoint;
        var endpoint = spec is not null && p.EndpointFor(spec.Protocol) is { FullUrl: false } declared ? declared :
            p.PreferredUpstreamProtocols.Select(p.EndpointFor).FirstOrDefault(e => e is { FullUrl: false }) ?? p.Endpoints.FirstOrDefault(e => !e.FullUrl);
        if (endpoint is null) throw new AdminApiException(400, "A full request URL has no discoverable models URL; add a base URL or model IDs manually");
        var suffix = spec?.Protocol == endpoint.Protocol ? spec.Path :
            endpoint.Protocol == ApiProtocol.Anthropic && !new Uri(endpoint.BaseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "/v1/models" : "/models";
        var url = AppendPath(endpoint.BaseUrl, suffix);
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        var foundProtocols = new Dictionary<string, List<ApiProtocol>>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(ModelListTimeoutSec));
        try
        {
            using var client = CreateClient(p);
            for (var page = 0; page < MaxPages; page++)
            {
                using var request = await RequestAsync(p, HttpMethod.Get, url, endpoint.Protocol, false, deadline.Token, accountId);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var body = TryObject(await ReadResponseAsync(response, deadline.Token));
                if (!response.IsSuccessStatusCode) throw new AdminApiException(502, ErrorMessage(body, response.StatusCode, p, request), new { httpStatus = (int)response.StatusCode });
                if (body is null) throw InvalidList();
                var rows = body["data"] as JsonArray ?? body["models"] as JsonArray ?? throw InvalidList();
                foreach (var row in rows)
                {
                    if (row is not JsonObject item) throw InvalidList();
                    var id = Text(item["id"]) ?? Text(item["name"]);
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) throw InvalidList();
                    if (endpoint.Protocol == ApiProtocol.Gemini && id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                    if (id.Length == 0) throw InvalidList();
                    found[id] = Text(item["displayName"]) ?? Text(item["display_name"]);
                    // Copilot advertises per-model endpoints (Claude models are Messages-only); the same parsing
                    // is harmless for upstreams that never send it.
                    if (SupportedProtocols(item) is { Count: > 0 } protocols) foundProtocols[id] = protocols;
                    if (found.Count > MaxModels) throw new AdminApiException(502, "Upstream model list exceeds the supported limit");
                }
                var next = Text(body["nextPageToken"]);
                var hasMore = body["has_more"] is JsonValue more && more.TryGetValue<bool>(out var b) && b;
                if (body["nextPageToken"] is not null && next is null) throw InvalidList();
                if (body["has_more"] is not null && (body["has_more"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))) throw InvalidList();
                var cursor = !string.IsNullOrEmpty(next) ? next : hasMore ? Text(body["last_id"]) ?? Text((rows.LastOrDefault() as JsonObject)?["id"]) : null;
                if (hasMore && string.IsNullOrEmpty(cursor)) throw InvalidList();
                if (string.IsNullOrEmpty(cursor)) break;
                if (!cursors.Add(cursor)) throw new AdminApiException(502, "Upstream model pagination did not advance");
                if (page == MaxPages - 1) throw new AdminApiException(502, "Upstream model list has too many pages");
                url = WithQuery(url, !string.IsNullOrEmpty(next) ? "pageToken" : endpoint.Protocol == ApiProtocol.Anthropic ? "after_id" : "after", cursor);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AdminApiException(504, "Upstream model list request timed out"); }
        catch (HttpRequestException)
        { throw new AdminApiException(502, "Could not connect to the upstream provider"); }
        catch (IOException)
        { throw new AdminApiException(502, "Could not read the upstream model list"); }
        catch (SubscriptionAuthException)
        { throw new AdminApiException(409, "Subscription account is missing or unusable; sign in again"); }
        catch (CryptographicException)
        { throw new AdminApiException(409, "Stored API key could not be decrypted; replace it"); }
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var key = p.PriceKey ?? p.TemplateId;
        var index = key is null ? null : await db.Models.GetUpstreamIdIndexAsync(key, ct);
        var existing = (await db.Providers.ListModelsAsync(p.Id, ct)).Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        return found.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new RemoteModelDto(x.Key, x.Value,
            ModelIdMatcher.Match(x.Key, candidates, index), existing.Contains(x.Key),
            foundProtocols.TryGetValue(x.Key, out var protocols) ? protocols : null)).ToList();
    }

    /// <summary>
    /// Server-syncs a provider's model list from its upstream <c>/models</c> (best effort).
    /// Subscription providers whose catalog is server-driven rather than a fixed list — GitHub
    /// Copilot is the case in point — get the real list right after a successful login instead of
    /// shipping a hard-coded guess in the template.
    /// </summary>
    public async Task<int> SyncModelsFromUpstreamAsync(string providerId, CancellationToken ct = default)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        IReadOnlyList<RemoteModelDto> remote;
        try
        {
            remote = await ModelsAsync(providerId, ct);
        }
        catch (AdminApiException) // upstream list unavailable: the login itself already succeeded
        {
            return 0;
        }
        // Template defaults and pre-existing rows need the same capabilities, even when nothing new is added.
        await RefreshModelProtocolsAsync(providerId, remote, ct);
        var toAdd = remote.Where(m => !m.AlreadyAdded).ToList();
        if (toAdd.Count == 0) return 0;
        var added = await PlanModels(p, toAdd.Select(m => m.Id).ToList(), ct);
        // 把上游给出的 per-model 协议约束一并存下来（例如 Copilot 的 Claude 只支持 Messages）。
        var constraints = toAdd.Where(m => m.UpstreamProtocols is { Count: > 0 })
            .ToDictionary(m => m.Id, m => m.UpstreamProtocols!, StringComparer.Ordinal);
        foreach (var model in added)
            if (constraints.TryGetValue(model.ModelId, out var protocols)) model.UpstreamProtocols = [.. protocols];
        await db.Providers.InsertModelsAsync(added, ct);
        return added.Count;
    }

    private async Task RefreshModelProtocolsAsync(string providerId, IReadOnlyList<RemoteModelDto> remote, CancellationToken ct)
    {
        var constraints = remote.Where(m => m.UpstreamProtocols is { Count: > 0 })
            .ToDictionary(m => m.Id, m => m.UpstreamProtocols!, StringComparer.Ordinal);
        if (constraints.Count == 0) return;
        foreach (var model in await db.Providers.ListModelsAsync(providerId, ct))
            if (constraints.TryGetValue(model.ModelId, out var protocols) && !model.UpstreamProtocols.SequenceEqual(protocols))
                await db.Providers.UpdateModelProtocolsAsync(model.Id, protocols, ct);
    }

    /// <summary>Persists planned provider models for a set of upstream ids (shared by the sync paths).</summary>
    private async Task<List<ProviderModel>> PlanModels(Provider p, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var existing = (await db.Providers.ListModelsAsync(p.Id, ct)).ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        var order = existing.Values.Select(m => m.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var key = p.PriceKey ?? p.TemplateId;
        var index = key is null ? null : await db.Models.GetUpstreamIdIndexAsync(key, ct);
        var result = new List<ProviderModel>();
        foreach (var id in ids.Select(i => i.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (existing.ContainsKey(id) || id.Length is 0 or > 512 || id.Any(char.IsControl)) continue;
            var model = new ProviderModel
            {
                ProviderId = p.Id,
                ModelId = id,
                SystemModelId = ModelIdMatcher.Match(id, candidates, index),
                SortOrder = order++,
            };
            existing[id] = model;
            result.Add(model);
        }
        return result;
    }

    internal static ProviderEndpoint PreferredEndpoint(Provider p) =>
        p.PreferredUpstreamProtocols.Select(p.EndpointFor).FirstOrDefault(e => e is not null)
        ?? p.Endpoints.FirstOrDefault() ?? throw new AdminApiException(400, "Provider has no endpoint");


    /// <summary>
    /// Maps an upstream model row's <c>supported_endpoints</c> (GitHub Copilot: "/chat/completions",
    /// "/responses", "ws:/responses", "/v1/messages") to Astra protocols. Null when the row does not
    /// constrain endpoints, or when nothing recognisable is listed.
    /// </summary>
    private static List<ApiProtocol>? SupportedProtocols(JsonObject item)
    {
        if (item["supported_endpoints"] is not JsonArray raw) return null;
        var protocols = new List<ApiProtocol>();
        foreach (var entry in raw)
        {
            var text = Text(entry);
            if (text is null) continue;
            var lower = text.ToLowerInvariant();
            ApiProtocol? protocol = lower switch
            {
                "/responses" or "ws:/responses" or "ws://responses" => ApiProtocol.OpenAIResponses,
                "/chat/completions" => ApiProtocol.OpenAIChat,
                "/v1/messages" or "/messages" => ApiProtocol.Anthropic,
                _ => null,
            };
            if (protocol is { } known && !protocols.Contains(known)) protocols.Add(known);
        }
        return protocols.Count > 0 ? protocols : null;
    }

    /// <summary>The request body of one test, in the endpoint's own protocol (no IR conversion).</summary>
    internal static JsonObject TestBody(string model, string prompt, int maxOutputTokens, bool stream, ApiProtocol protocol)
    {
        switch (protocol)
        {
            case ApiProtocol.OpenAIResponses:
                // ChatGPT 订阅（Codex）的 backend 只接受数组形态的 input：字符串会被
                // "Input must be a list" 直接拒掉（400）。用单个 user 文本项。
                return new JsonObject
                {
                    ["model"] = model,
                    ["input"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt }),
                    }),
                    ["max_output_tokens"] = maxOutputTokens,
                    ["store"] = false,
                    ["stream"] = stream,
                };
            case ApiProtocol.OpenAIChat:
                var chat = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
                    ["stream"] = stream,
                };
                // Reasoning-first models reject max_tokens and want max_completion_tokens.
                chat[model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("o1", StringComparison.Ordinal) ||
                     model.StartsWith("o3", StringComparison.Ordinal) || model.StartsWith("o4", StringComparison.Ordinal)
                    ? "max_completion_tokens" : "max_tokens"] = maxOutputTokens;
                // A streamed Chat response only reports usage when asked; the test always wants the numbers.
                if (stream) chat["stream_options"] = new JsonObject { ["include_usage"] = true };
                return chat;
            case ApiProtocol.Gemini:
                return new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
                    }),
                    ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = maxOutputTokens },
                };
            default:
                return new JsonObject
                {
                    ["model"] = model,
                    ["max_tokens"] = maxOutputTokens,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
                    ["stream"] = stream,
                };
        }
    }

    private static string AppendPath(string baseUrl, string path)
    {
        var builder = new UriBuilder(baseUrl) { Path = new Uri(baseUrl).AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/') };
        return builder.Uri.AbsoluteUri;
    }

    private static string WithQuery(string url, string key, string value)
    {
        var builder = new UriBuilder(url);
        var query = QueryHelpers.ParseQuery(builder.Query).Where(x => x.Key != key).ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
        query[key] = value;
        builder.Query = QueryHelpers.AddQueryString("", query).TrimStart('?');
        return builder.Uri.AbsoluteUri;
    }

    private static void AppendBounded(StringBuilder target, string text, ref bool truncated)
    {
        if (target.Length >= MaxRawChars)
        {
            if (!truncated) target.Append(TruncatedNote);
            truncated = true;
            return;
        }
        var room = MaxRawChars - target.Length;
        if (text.Length > room)
        {
            target.Append(text, 0, room).Append(TruncatedNote);
            truncated = true;
            return;
        }
        target.Append(text);
    }

    private static string? ReportedModel(ApiProtocol protocol, JsonObject? body) =>
        protocol == ApiProtocol.Gemini ? Text(body?["modelVersion"]) ?? Text(body?["model_version"]) : Text(body?["model"]);

    private static NormalizedUsage Normalize(ApiProtocol protocol, JsonObject raw) => protocol switch
    {
        ApiProtocol.OpenAIChat => UsageNormalizer.FromOpenAIChat(raw),
        ApiProtocol.OpenAIResponses => UsageNormalizer.FromOpenAIResponses(raw),
        ApiProtocol.Anthropic => UsageNormalizer.FromAnthropic(raw),
        _ => UsageNormalizer.FromGemini(raw),
    };

    private static JsonObject? TryObject(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new AdminApiException(502, "Upstream response exceeds 4 MiB");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new AdminApiException(502, "Upstream response exceeds 4 MiB");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(StreamReadTimeoutSec));
        try
        {
            return await stream.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdminApiException(504, "Upstream stopped sending data during the connection test");
        }
    }

    private static string ErrorMessage(JsonObject? body, HttpStatusCode status, Provider provider, HttpRequestMessage request, string? rawBody = null)
    {
        var message = Text((body?["error"] as JsonObject)?["message"]) ?? Text(body?["error"]) ?? Text(body?["message"])
                      // ChatGPT subscription (codex) backend and FastAPI-style upstreams answer {"detail": "..."} (string, object or array).
                      ?? Text(body?["detail"]) ?? (body?["detail"] is { } detail ? detail.ToJsonString(GatewayJson.Options) : null)
                      // Unknown shape or not JSON at all: keep the upstream's own words rather than just the status code.
                      ?? (string.IsNullOrWhiteSpace(rawBody) ? null : rawBody.Trim());
        var result = $"Upstream returned HTTP {(int)status}" + (message is null ? "" : ": " + message);
        return Truncate(Redact(result, provider, request), 1000);
    }

    /// <summary>
    /// Removes every credential we sent (auth header, extra headers, query values) from text that can reach the API
    /// response or a debug body: some upstreams echo credentials back inside error messages and bodies.
    /// </summary>
    private static string Redact(string text, Provider provider, HttpRequestMessage request) =>
        Redact(text, Secrets(provider, request));

    private static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Every credential value this provider sends: extra headers, the auth header and query-string keys.</summary>
    private static List<string> Secrets(Provider provider, HttpRequestMessage? request)
    {
        var secrets = provider.ExtraHeaders.Values.ToList();
        if (request is null) return secrets;
        foreach (var header in request.Headers.Where(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in header.Value)
            {
                secrets.Add(value);
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.IndexOf(' ') is var space && space >= 0)
                    secrets.Add(value[(space + 1)..]);
            }
        }
        foreach (var query in QueryHelpers.ParseQuery(request.RequestUri?.Query))
            if (query.Key.Contains("key", StringComparison.OrdinalIgnoreCase) || query.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
                foreach (var value in query.Value) if (value is not null) secrets.Add(value);
        return secrets;
    }

    /// <summary>
    /// The upstream response headers, in arrival order, for the "headers" view of a test. Every value that matches a
    /// credential we sent is redacted; long values are cut and the list is capped.
    /// </summary>
    private static JsonObject ResponseHeaders(HttpResponseMessage response, Provider provider, HttpRequestMessage request)
    {
        var secrets = Secrets(provider, request);
        var result = new JsonObject();
        var count = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (count++ >= MaxHeaderCount)
            {
                result["x-astra-truncated"] = $"more than {MaxHeaderCount} headers";
                break;
            }
            var value = Redact(string.Join(", ", header.Value), secrets);
            result[header.Key] = value.Length <= MaxHeaderValueChars ? value : value[..MaxHeaderValueChars];
        }
        return result;
    }

    private static string? FirstHeader(HttpResponseMessage response, params string[] names)
    {
        foreach (var name in names)
        {
            if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } v) return Truncate(v, 200);
        }
        return null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static AdminApiException InvalidList() => new(502, "Upstream returned an invalid model list");
}
