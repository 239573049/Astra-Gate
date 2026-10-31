using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Protocol;
using Astra.Gateway.Tracing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Astra.Gateway.Pipeline;

/// <summary>An OpenAI Images API call the gateway forwards (issue #17).</summary>
public enum ImageOperation
{
    /// <summary><c>POST /v1/images/generations</c>: a JSON body.</summary>
    Generations,

    /// <summary><c>POST /v1/images/edits</c>: the images as a multipart/form-data upload, or referenced in a JSON body.</summary>
    Edits,
}

// The OpenAI Images API (issue #17). Nothing translates it: the call goes to an OpenAI-compatible provider as the client
// wrote it — only the model (the provider's model mapping) and what the privacy guard redacts are rewritten — and the answer
// comes back as the provider wrote it. The rest is the generation pipeline's own: the token and its caps, the provider the
// model routes to (the client's bindings, the token's pool), its credentials and accounts (SendAsync), the request log, the
// trace and the bill, priced from the answer's `usage`. Neither the images (b64_json, uploads) nor the prompt are logged,
// traced or captured.
public sealed partial class GatewayPipeline
{
    /// <summary>
    /// The request log's protocol of an Images API call. Not an <see cref="ApiProtocol"/>: no codec translates it, so no
    /// provider endpoint, client config or protocol choice has to know about it.
    /// </summary>
    public const string ImagesProtocol = "openai-images";

    /// <summary>Forwards one Images API call to the provider its model routes to, and logs and bills it.</summary>
    public async Task HandleImagesAsync(HttpContext ctx, ImageOperation operation)
    {
        var sw = Stopwatch.StartNew();
        var ct = ctx.RequestAborted;
        var s = settings.Current;
        var record = new RequestRecord
        {
            Id = Ulid.NewUlid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            InboundProtocol = ImagesProtocol,
            UpstreamProtocol = ImagesProtocol,
            Passthrough = true,
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 300),
            Remote = Http.RequestOrigin.IsRemote(ctx),
            ClientIp = ClientIp(ctx),
        };
        ctx.Response.Headers["x-astra-request-id"] = record.Id;
        live.Start(record);
        var trace = new RequestTrace();
        var (inboundTraceId, inboundSpanId) = TraceKeys.ParseTraceparent(ctx.Request.Headers["traceparent"].ToString());
        record.ParentSpanId = inboundSpanId;
        TraceSpanBuilder? responseSpan = null;
        var observer = new ResponseObserver();
        var restorable = 0;
        EffectiveModel? effective = null;
        Provider? provider = null;
        AccountScheduler.Lease? accountLease = null;
        PoolPlan? poolPlan = null;
        ProviderPoolScheduler.Lease? poolLease = null;
        RequestBody? requestBody = null;
        // Upstream bytes this request allocated: a rewritten body, or what was read of a form streamed on.
        PooledBuffer? owned = null;

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
            if (limiter is not null) await limiter.CheckAsync(route.Token, ct);
            routeSpan.Set("astra.client.kind", route.ClientKind).Set("astra.token.id", route.Token.Id).Set("astra.provider.id", provider.Id).Ok().End();

            // The body: JSON (generations, and edits that reference their images), or a form upload (edits). A form is read
            // only up to its model and then streamed on — unless the privacy guard has to see its text fields first (the
            // prompt usually follows the images), it arrived compressed, or the host gives the body a deadline (the relay,
            // whose callers must not hold a slot with a slow upload): then it is read whole, like a JSON body.
            var bodySpan = trace.Start(TraceSpanNames.Body);
            var contentType = ctx.Request.ContentType ?? "";
            var boundary = MultipartForm.BoundaryOf(contentType);
            JsonObject? json = null;
            JsonElement jsonRoot = default;
            List<MultipartForm.Part>? parts = null;
            MultipartForm.Part? modelPart = null;
            PooledBuffer? head = null;
            var wantsStream = false;
            string requestedModel;
            if (boundary is null)
            {
                requestBody = await RequestBody.ReadAsync(ctx.Request, _bodyLimits.MaxBytes, ct, _bodyLimits.ReadTimeout);
                bodySpan.Set("astra.body.bytes", requestBody.Utf8.Length).Ok().End();
                if (requestBody.Parse() is not (var root, var tree))
                    throw new ProtocolException("图像接口的请求体必须是 JSON 对象或 multipart/form-data 表单。");
                (jsonRoot, json) = (root, tree);
                requestedModel = Str(json, "model") ?? "";
                wantsStream = json["stream"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            }
            else if (privacy?.Settings.Enabled == true || _bodyLimits.ReadTimeout is not null
                     || RequestBody.ContentEncodings(ctx.Request.Headers.ContentEncoding.ToString()).Count > 0)
            {
                requestBody = await RequestBody.ReadAsync(ctx.Request, _bodyLimits.MaxBytes, ct, _bodyLimits.ReadTimeout);
                bodySpan.Set("astra.body.bytes", requestBody.Utf8.Length).Ok().End();
                parts = MultipartForm.Parse(requestBody.Utf8, boundary);
                modelPart = TextField(parts, "model");
                requestedModel = modelPart?.Text(requestBody.Utf8) ?? "";
                wantsStream = TextField(parts, "stream")?.Text(requestBody.Utf8) == "true";
            }
            else
            {
                (head, modelPart) = await MultipartForm.ReadUntilFieldAsync(ctx.Request.Body, boundary, "model", _bodyLimits.MaxBytes, ct);
                owned = head;
                bodySpan.Set("astra.body.bytes", head.Length).Set("astra.body.streamed", true).Ok().End();
                requestedModel = modelPart?.Text(head.Span) ?? "";
            }

            // Privacy guard (plan §6.7), before anything leaves the machine: the JSON body in place, a form's text fields
            // through one JSON probe (MultipartForm splices what it redacts). A form streamed on had no guard switched on.
            var privacySpan = trace.Start(TraceSpanNames.Privacy);
            var guard = PrivacyGuardResult.PassThrough;
            List<(int Start, int End, string Value)> redactions = [];
            if (privacy is not null && json is not null) guard = privacy.InspectJson(jsonRoot, json, route.ClientKind);
            else if (privacy is not null && parts is not null) (guard, redactions) = InspectForm(privacy, requestBody!.Utf8, parts, route.ClientKind);
            restorable = guard.RestoreMap.Count;
            privacySpan.Set("astra.privacy.applied", guard.Applied).Set("astra.privacy.hits", guard.Hits.Sum(h => (long)h.Count))
                .Set("astra.privacy.categories", guard.Hits.Count == 0 ? null : string.Join(",", guard.Hits.Select(h => h.Category).Distinct()))
                .Set("astra.privacy.rewritten", guard.Rewritten || redactions.Count > 0).Set("astra.privacy.dry_run", guard.DryRun)
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

            if (requestedModel.Length == 0)
                throw new ProtocolException("请求缺少 model。Astra 按模型选择提供商，图像接口需要指定模型（例如 gpt-image-1）。");
            record.RequestedModel = requestedModel;
            if (smartRouter.IsVirtual(requestedModel))
                throw new GatewayException(400, "invalid_request_error", $"智能路由的虚拟模型 {requestedModel} 只用于对话接口；图像接口请指定图像模型（例如 gpt-image-1）。");

            // Where it goes: the provider serving the model among the client's bindings, or the member the token's pool
            // picks (its rotation advances; an image is not failed over to the next member), as for a generation request.
            var resolveSpan = trace.Start(TraceSpanNames.Resolve);
            var clientModel = route.ClientModelFor(requestedModel);
            if (route.Pool is not null && pools is not null)
            {
                poolPlan = await pools.PlanAsync(route, clientModel, false, null, commit: true, ct);
                route = route with { Provider = poolPlan.Order[0].Provider, AccountId = poolPlan.Order[0].AccountId };
            }
            else route = await router.ForModelAsync(route, clientModel, false, ct);
            provider = route.Provider;
            record.ProviderId = provider.Id;
            record.ProviderName = provider.Name;
            live.Update(record);
            SubscriptionSupport.EnforceClientPolicy(provider, false);
            var endpoint = ImagesEndpoint(provider);
            var url = UpstreamUrls.Images(endpoint, operation == ImageOperation.Generations ? "images/generations" : "images/edits")
                      ?? throw new GatewayException(501, "api_error",
                          $"提供商 {provider.Name} 的 OpenAI 接口填的是完整地址，无法由它得出图像接口的地址。请改填基础地址（例如 https://api.openai.com/v1）。");
            var upstreamModel = route.UpstreamModelFor(requestedModel);
            record.UpstreamModel = upstreamModel;
            (_, effective) = await models.ResolveByModelIdAsync(provider, upstreamModel, ct);
            record.SystemModelId = effective.SystemModelId;
            live.Update(record);
            resolveSpan.Set("astra.provider.id", provider.Id).Set("astra.provider.name", provider.Name)
                .Set("gen_ai.request.model", requestedModel).Set("astra.protocol.upstream", ImagesProtocol).Set("astra.passthrough", true).Ok().End();

            // What goes upstream: the client's bytes, unless the model mapping or the guard changed a value.
            var encodeSpan = trace.Start(TraceSpanNames.Encode);
            var mapped = upstreamModel != requestedModel;
            PooledBuffer payload;
            Func<HttpContent>? content = null;
            var resendable = true;
            var rewritten = false;
            if (json is not null)
            {
                if (mapped) json["model"] = upstreamModel;
                rewritten = mapped || guard.Rewritten;
                if (rewritten)
                {
                    owned = payload = new PooledBuffer(requestBody!.Buffer.Length + 256);
                    GatewayJson.WriteTo(json, payload);
                }
                else payload = requestBody!.Buffer;
            }
            else
            {
                var read = head ?? requestBody!.Buffer;
                var readLength = read.Length;
                if (mapped) redactions.Add((modelPart!.Value.ValueStart, modelPart.Value.ValueEnd, upstreamModel));
                rewritten = redactions.Count > 0;
                if (rewritten)
                {
                    payload = MultipartForm.Splice(read.Span, redactions);
                    head?.Release();
                    owned = payload;
                }
                else payload = read;
                var form = payload;
                if (head is not null && ctx.Request.ContentLength != readLength)
                {
                    // The rest of the upload goes out as it arrives — once: a rejected attempt is not renewed or retried.
                    long? length = ctx.Request.ContentLength is { } total ? total - readLength + form.Length : null;
                    content = () => new StreamedFormContent(form, ctx.Request.Body, length, contentType);
                    resendable = false;
                }
                else content = () =>
                {
                    var whole = new PooledContent(form);
                    whole.Headers.ContentType = null;
                    whole.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
                    return whole;
                };
            }
            encodeSpan.Set("astra.protocol.inbound", ImagesProtocol).Set("astra.protocol.upstream", ImagesProtocol).Set("astra.passthrough", true)
                .Set("astra.encode.rewritten", rewritten).Set("gen_ai.request.model", requestedModel).Set("astra.upstream.model", upstreamModel)
                .Set("astra.stream", wantsStream).Set("astra.body.bytes", payload.Length).Ok().End();

            if (poolPlan is not null) poolLease = poolScheduler.Begin(provider.Id);
            var attempts = new AttemptTrace
            {
                Changed = () =>
                {
                    if (!live.IsActive) return;
                    record.LiveAttempts = trace.Attempts();
                    live.Update(record);
                },
            };
            (HttpResponseMessage Response, string? AccountId, AccountScheduler.Lease? Lease) sent;
            try
            {
                sent = await SendAsync(route, endpoint, url, payload, json ?? new JsonObject(), wantsStream, passthrough: true, ClaudeHeaderMode.None,
                    upstreamModel, null, ctx.Request, null, trace, attempts, ct, content, resendable);
            }
            catch (Exception e) when (ReportFailure(provider, e, ct))
            {
                throw; // never reached: the filter only reports
            }
            accountLease = sent.Lease;
            using var response = sent.Response;
            ReportHealth(provider, ProviderFailover.Classify((int)response.StatusCode).Outcome, response);
            poolPlan?.Dispose();
            responseSpan = trace.Start(TraceSpanNames.Response);
            record.AccountId = sent.AccountId;
            record.TtfbMs = sw.ElapsedMilliseconds;
            record.HttpStatus = (int)response.StatusCode;
            record.UpstreamRequestId = FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray");
            live.Update(record);

            // An OpenAI-compatible provider's error is already in the client's shape: passed on as it is.
            if (!response.IsSuccessStatusCode)
            {
                await ForwardErrorAsync(ctx, response, record, ApiProtocol.OpenAIChat, passthrough: true, null, codecs.Get(ApiProtocol.OpenAIChat), null, ct);
                return;
            }
            await ForwardImagesAsync(ctx, response, record, observer, sw, guard.RestoreMap, s.StreamIdleTimeoutSec, ct);
        }
        catch (GatewayException e)
        {
            if (record.Status != RequestStatus.Blocked) record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= e.Status;
            record.ErrorType = e.RecordErrorType ?? e.Type;
            record.ErrorMessage = e.Message;
            if (e.RetryAfterSeconds is { } retryAfter && !ctx.Response.HasStarted)
                ctx.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, e.Status, e.Type, e.Message);
        }
        catch (ProtocolException e)
        {
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= e.Status;
            record.ErrorType = e.Type;
            record.ErrorMessage = e.Message;
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, e.Status, e.Type, e.Message);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException && ct.IsCancellationRequested)
        {
            // The client went away — maybe in the middle of an upload that was being streamed on.
            record.Status = RequestStatus.ClientCancelled;
        }
        catch (UpstreamIdleTimeoutException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.ErrorType = "timeout";
            record.ErrorMessage = e.Message;
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 504, "timeout", e.Message);
        }
        catch (HttpRequestException e) when (e.InnerException is BadHttpRequestException bad)
        {
            // The client's upload broke off, or outgrew the server's limit, while it was streamed on.
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= bad.StatusCode;
            record.ErrorType = "invalid_request_error";
            record.ErrorMessage = Truncate(bad.Message, 2000);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, bad.StatusCode, "invalid_request_error", "请求体在转发途中中断或超过了大小限制。");
        }
        catch (HttpRequestException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.HttpStatus ??= 502;
            record.ErrorType = "upstream_unreachable";
            record.ErrorMessage = Truncate(e.Message, 2000);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 502, "api_error", $"无法连接上游 {provider?.Name}：{e.Message}");
        }
        catch (IOException) when (ctx.Response.HasStarted)
        {
            record.Status = RequestStatus.ClientCancelled;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Gateway images request {Id} failed", record.Id);
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= 500;
            record.ErrorType = e.GetType().Name;
            record.ErrorMessage = Truncate(e.Message, 2000);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 500, "api_error", $"Astra 网关内部错误：{e.Message}");
        }
        finally
        {
            accountLease?.Dispose();
            poolLease?.Dispose();
            poolPlan?.Dispose();
            Finish(record, observer, sw, effective, provider, s.Locale);
            FinishTrace(record, trace, null, inboundTraceId, observer, responseSpan, false, restorable,
                operation == ImageOperation.Generations ? "image_generation" : "image_edit");
            // A send still in flight holds its own reference to the bytes.
            owned?.Release();
            requestBody?.Dispose();
            live.Finish(record);
            await usageWriter.EnqueueAsync(record);
        }
    }

    /// <summary>The OpenAI endpoint an Images API call goes beside; a provider without one cannot serve it.</summary>
    private static ProviderEndpoint ImagesEndpoint(Provider provider)
    {
        if (provider.AuthScheme == AuthSchemes.Plugin)
            throw new GatewayException(501, "api_error", $"提供商 {provider.Name} 由插件提供，不支持 OpenAI 图像接口。");
        return provider.EndpointFor(ApiProtocol.OpenAIChat) ?? provider.EndpointFor(ApiProtocol.OpenAIResponses)
               ?? throw new GatewayException(501, "api_error",
                   $"提供商 {provider.Name} 没有 OpenAI 兼容的接口（openai-chat / openai-responses），无法转发 OpenAI 图像接口。请让这个模型路由到 OpenAI 兼容的提供商。");
    }

    private static MultipartForm.Part? TextField(List<MultipartForm.Part> parts, string name)
    {
        foreach (var part in parts)
            if (part.IsText && part.Name == name) return part;
        return null;
    }

    /// <summary>
    /// The privacy guard over a form's text fields: their values as one JSON array, inspected like a JSON body, and the
    /// fields whose value it redacted (where they lie, and their new value).
    /// </summary>
    private static (PrivacyGuardResult Guard, List<(int Start, int End, string Value)> Redactions) InspectForm(
        PrivacyGuardService privacy, ReadOnlySpan<byte> body, List<MultipartForm.Part> parts, string? clientKind)
    {
        var texts = new List<MultipartForm.Part>();
        var values = new List<string>();
        var probe = new JsonArray();
        foreach (var part in parts)
        {
            if (!part.IsText) continue;
            var value = part.Text(body);
            texts.Add(part);
            values.Add(value);
            probe.Add((JsonNode)JsonValue.Create(value));
        }
        var guard = privacy.Inspect(new JsonObject { ["form"] = probe }.ToJsonString(GatewayJson.Options), clientKind);
        var redactions = new List<(int Start, int End, string Value)>();
        if (guard is { Applied: true, DryRun: false, Blocked: false, Hits.Count: > 0 } && JsonNode.Parse(guard.Body)?["form"] is JsonArray redacted)
        {
            for (var i = 0; i < texts.Count && i < redacted.Count; i++)
                if (redacted[i]?.GetValue<string>() is { } value && value != values[i])
                    redactions.Add((texts[i].ValueStart, texts[i].ValueEnd, value));
        }
        return (guard, redactions);
    }

    /// <summary>Health of an API-key provider (pools read it), as the generation pipeline reports it.</summary>
    private void ReportHealth(Provider target, ProviderOutcome outcome, HttpResponseMessage? response)
    {
        if (AuthSchemes.UsesAccounts(target.AuthScheme)) return;
        health.Report(target.Id, outcome,
            outcome == ProviderOutcome.RateLimited && response is not null ? RateLimitResetOf(response, DateTimeOffset.UtcNow) : null);
    }

    /// <summary>Exception filter: reports a send that failed on the provider's side; never catches.</summary>
    private bool ReportFailure(Provider target, Exception e, CancellationToken ct)
    {
        if (e is HttpRequestException { InnerException: BadHttpRequestException }) return false; // the client's upload, not the provider
        if (ProviderFailover.Classify(e, ct) is { } verdict) ReportHealth(target, verdict.Outcome, null);
        return false;
    }

    /// <summary>
    /// The provider's answer as it wrote it: a JSON body (read whole, then written — placeholders the guard put into the
    /// prompt restored), or an event stream (partial images, written as they arrive). Only <c>usage</c> is read from it —
    /// from the body, or from the event that completes the image — never the images.
    /// </summary>
    private static async Task ForwardImagesAsync(HttpContext ctx, HttpResponseMessage response, RequestRecord record, ResponseObserver observer,
        Stopwatch sw, IReadOnlyDictionary<string, string> restore, int idleTimeoutSec, CancellationToken ct)
    {
        record.Stream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new IdleTimeoutReader(stream, idleTimeoutSec, ct, observer);
        if (record.Stream)
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var parser = new SseParser();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    foreach (var sse in parser.Feed(buffer.AsSpan(0, read))) ObserveEvent(sse.Data);
                }
                foreach (var sse in parser.Flush()) ObserveEvent(sse.Data);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return;
        }

        var declared = response.Content.Headers.ContentLength;
        var known = declared is > 0 and < int.MaxValue;
        var body = new PooledBuffer(known ? (int)declared!.Value + 1 : 64 * 1024);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(body.GetMemory(known ? 1 : 16 * 1024))) > 0) body.Advance(read);
            Observe(ImageUsageOf(body.Span));
            if (restore.Count == 0) await ctx.Response.Body.WriteAsync(body.Memory, ct);
            else await ctx.Response.WriteAsync(PrivacyGuardService.Restore(Encoding.UTF8.GetString(body.Span), restore), ct);
        }
        finally
        {
            body.Release();
        }

        void ObserveEvent(string data)
        {
            if (data.Contains("\"usage\"", StringComparison.Ordinal)) Observe(ImageUsageOf(Encoding.UTF8.GetBytes(data)));
        }

        void Observe(JsonObject? usage)
        {
            if (usage is not null)
                observer.Observe(new UsageEvent(ApiProtocol.OpenAIResponses, usage, UsageNormalizer.FromOpenAIImages(usage)), sw.ElapsedMilliseconds, wholeBody: true);
        }
    }

    /// <summary>The top-level <c>usage</c> object of an Images API body or event, read without materializing the images.</summary>
    public static JsonObject? ImageUsageOf(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUsage = reader.ValueTextEquals("usage"u8);
                if (!reader.Read()) return null;
                if (isUsage) return JsonNode.Parse(ref reader) as JsonObject;
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's page with a 200): nothing to bill.
        }
        return null;
    }
}
