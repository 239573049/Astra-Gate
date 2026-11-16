using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Gateway.Protocol;
using Astra.Gateway.Tracing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Pipeline;

// Vendor-native passthrough: <METHOD> /v1/astra/native/{provider}/{path} reaches an API-key provider's own API as it is, for what
// the OpenAI shape does not carry (a vendor's video tasks, files, batches, fine-tunes). The idea is magpie's /v1/magpie/native
// (MIT; internal/gateway/native.go); the rules it keeps:
//  - the token and its reach: the provider must be one this caller can route to (the client's bindings, the token's pool or
//    direct provider); a token with a cost or token budget is refused, because a path Astra does not read cannot be priced;
//  - an API-key provider only (never a subscription's or a plugin's account);
//  - the address is the provider's own origin (its configured endpoint's scheme and host) plus the path — never an address
//    the request names: no "." / ".." segment (encoded too), no backslash, no leading or encoded slash, no scheme;
//  - the caller's own credentials (Authorization, x-api-key, x-goog-api-key, cookies, ?key=) never go on; the provider's key is
//    added the way that provider takes it; the provider's own headers and the passed-through client headers follow the
//    pipeline's rules;
//  - with the privacy guard on, a JSON body is masked (a body that is not JSON is refused) and placeholders are restored in a
//    JSON / text answer (which is then read whole);
//  - each call is a request log row (protocol "native") with the tokens its answer reports when it reports them.
public sealed partial class GatewayPipeline
{
    /// <summary>The request log's protocol of a vendor-native call (not an <see cref="ApiProtocol"/>: nothing translates it).</summary>
    public const string NativeProtocol = "native";

    /// <summary>The route prefix: <c>/v1/astra/native/{provider}/{path}</c>.</summary>
    public const string NativePrefix = "/v1/astra/native/";

    private const int NativeUsageSniffBytes = 1024 * 1024;

    // Never copied from the caller to the vendor: the connection's own, the caller's credentials and identity, proxy marks.
    private static readonly HashSet<string> NativeSkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Content-Length", "Content-Encoding", "Accept-Encoding", "Expect", "Authorization", "X-Api-Key", "X-Goog-Api-Key", "Cookie",
        "Origin", "Referer", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "X-Original-For",
        "Cf-Connecting-IP", "Cf-Ray", "Cf-Visitor", "Cf-IPCountry", "Cdn-Loop", "True-Client-IP",
    };

    // What of the vendor's answer reaches the caller: its type and caching, not its framing or cookies.
    private static readonly string[] NativeAnswerHeaders =
        ["Content-Type", "Content-Disposition", "Cache-Control", "ETag", "Last-Modified", "Location", "Retry-After", "X-Request-Id", "Request-Id"];

    /// <summary>Forwards one native call to the provider named in the path, and logs it.</summary>
    public async Task HandleNativeAsync(HttpContext ctx, string providerRef)
    {
        var sw = Stopwatch.StartNew();
        var ct = ctx.RequestAborted;
        var s = settings.Current;
        var record = new RequestRecord
        {
            Id = Ulid.NewUlid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            InboundProtocol = NativeProtocol,
            UpstreamProtocol = NativeProtocol,
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
        EffectiveModel? effective = null;
        Provider? provider = null;
        RequestBody? requestBody = null;
        PooledBuffer? owned = null;
        var restorable = 0;

        try
        {
            var routeSpan = trace.Start(TraceSpanNames.Route);
            var route = await router.ResolveAsync(ctx.Request, ct);
            record.ClientKind = route.ClientKind;
            record.TokenId = route.Token.Id;
            record.TokenName = route.Token.Name;
            live.Update(record);
            if (limiter is not null) await limiter.CheckAsync(route.Token, ct);
            // A path Astra does not read cannot be priced, so a token held to a cost or token budget is refused: such a token uses
            // the OpenAI-shaped routes.
            if (route.Token.Limits() is { } limits && limits.Budgets.Any(b => b.Metric != TokenLimitMetrics.Requests))
                throw new GatewayException(403, "permission_error",
                    $"令牌 {route.Token.Name} 设有费用或 token 额度，而原生透传的路径 Astra 无法计费，因此不可用。请改用 OpenAI 形态的接口，或换一个没有费用 / token 额度的令牌。");

            // The provider among the ones this caller reaches (never any provider by id alone), by id or by exact name.
            var matches = route.Targets.Where(t => string.Equals(t.Provider.Id, providerRef, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
                matches = route.Targets.Where(t => string.Equals(t.Provider.Name, providerRef, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new GatewayException(404, "not_found_error", $"这个令牌可用的提供商中没有「{providerRef}」（或名称重复，请改用提供商 id）。");
            provider = matches[0].Provider;
            record.ProviderId = provider.Id;
            record.ProviderName = provider.Name;
            if (AuthSchemes.UsesAccounts(provider.AuthScheme) || provider.AuthScheme == AuthSchemes.OAuthSubscription)
                throw new GatewayException(403, "permission_error", $"提供商 {provider.Name} 是订阅或插件账号，不支持原生透传（只支持用 API key 的提供商）。");
            routeSpan.Set("astra.client.kind", route.ClientKind).Set("astra.token.id", route.Token.Id).Set("astra.provider.id", provider.Id).Ok().End();

            // Where it goes: the provider's own origin and the path as the caller wrote it. Nothing the request carries picks a host.
            var resolveSpan = trace.Start(TraceSpanNames.Resolve);
            var endpoint = provider.EndpointFor(ApiProtocol.OpenAIChat) ?? provider.Endpoints.FirstOrDefault()
                           ?? throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 没有配置接口地址。");
            var (path, query) = NativeTarget(ctx, providerRef);
            var origin = NativeOrigin(endpoint, provider);
            var url = origin + "/" + path;
            var method = new HttpMethod(ctx.Request.Method);
            resolveSpan.Set("astra.provider.id", provider.Id).Set("astra.provider.name", provider.Name)
                .Set("astra.protocol.upstream", NativeProtocol).Set("astra.passthrough", true).Set("http.request.method", ctx.Request.Method).Ok().End();

            // The body, whole (a vendor's native call is small next to a model's context); the privacy guard masks a JSON one.
            var bodySpan = trace.Start(TraceSpanNames.Body);
            requestBody = await RequestBody.ReadAsync(ctx.Request, _bodyLimits.Effective(settings.Current), ct, _bodyLimits.ReadTimeout);
            bodySpan.Set("astra.body.bytes", requestBody.Utf8.Length).Ok().End();
            PooledBuffer? payload = requestBody.Utf8.Length == 0 ? null : requestBody.Buffer;
            var guard = PrivacyGuardResult.PassThrough;
            var privacySpan = trace.Start(TraceSpanNames.Privacy);
            string? model = null;
            if (requestBody.Parse() is (var root, var tree))
            {
                model = Str(tree, "model");
                if (privacy is not null)
                {
                    guard = privacy.InspectJson(root, tree, route.ClientKind);
                    if (guard.Rewritten)
                    {
                        owned = payload = new PooledBuffer(requestBody.Buffer.Length + 256);
                        GatewayJson.WriteTo(tree, payload);
                    }
                }
            }
            else if (privacy?.Settings.Enabled == true && requestBody.Utf8.Length > 0)
            {
                // Fails closed: a body the guard cannot read is not sent.
                throw new GatewayException(400, "invalid_request_error", "隐私护栏已开启，原生透传只接受 JSON 请求体（它无法检查其他格式的内容）。");
            }
            restorable = guard.RestoreMap.Count;
            privacySpan.Set("astra.privacy.applied", guard.Applied).Set("astra.privacy.hits", guard.Hits.Sum(h => (long)h.Count))
                .Set("astra.privacy.rewritten", guard.Rewritten).Set("astra.privacy.dry_run", guard.DryRun).Set("astra.privacy.restorable", restorable);
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

            if (model is not null)
            {
                record.RequestedModel = model;
                record.UpstreamModel = route.UpstreamModelFor(model);
                (_, effective) = await models.ResolveByModelIdAsync(provider, record.UpstreamModel, ct);
                record.SystemModelId = effective.SystemModelId;
            }
            live.Update(record);

            var credentials = await auth.ResolveAsync(provider.Id, route.AccountId, ct) ?? UpstreamAuth.None;
            using var request = new HttpRequestMessage(method, NativeUrl(url, query, credentials));
            if (payload is not null) request.Content = new PooledContent(payload);
            foreach (var (name, values) in ctx.Request.Headers)
            {
                if (NativeSkippedHeaders.Contains(name) || name.StartsWith("X-Astra-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Cf-", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Content is not null)
                    {
                        request.Content.Headers.ContentType = null;
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", values.ToArray());
                    }
                    continue;
                }
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
            foreach (var (name, value) in provider.ExtraHeaders)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
            ProviderClientHeaders.Apply(provider, request, ctx.Request.Headers);
            credentials.Apply(request);

            using var response = await http.For(provider).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ReportHealth(provider, ProviderFailover.Classify((int)response.StatusCode).Outcome, response);
            responseSpan = trace.Start(TraceSpanNames.Response);
            record.TtfbMs = sw.ElapsedMilliseconds;
            record.HttpStatus = (int)response.StatusCode;
            record.UpstreamRequestId = FirstHeader(response, "x-request-id", "request-id", "x-goog-request-id", "cf-ray");
            record.Stream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (!response.IsSuccessStatusCode)
            {
                record.Status = RequestStatus.UpstreamError;
                record.ErrorType = "upstream_error";
                record.ErrorMessage = $"{provider.Name} answered {(int)response.StatusCode}";
            }
            live.Update(record);

            // The answer as it came: status, its own headers, the body in the chunks it arrives in (or, with placeholders to put
            // back, read whole as text). A sample of a JSON body is kept for its usage.
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in NativeAnswerHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    ctx.Response.Headers[name] = values.ToArray();
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new IdleTimeoutReader(stream, s.StreamIdleTimeoutSec, ct, observer);
            var textual = IsNativeText(response.Content.Headers.ContentType?.MediaType);
            var sample = new MemoryStream();
            var sniff = textual && !record.Stream;
            if (guard.RestoreMap.Count > 0 && textual)
            {
                var whole = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int got;
                while ((got = await reader.ReadAsync(chunk)) > 0) whole.Write(chunk, 0, got);
                sample = whole;
                await ctx.Response.WriteAsync(PrivacyGuardService.Restore(Encoding.UTF8.GetString(whole.GetBuffer(), 0, (int)whole.Length), guard.RestoreMap), ct);
            }
            else
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32 * 1024);
                try
                {
                    int read;
                    while ((read = await reader.ReadAsync(buffer)) > 0)
                    {
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        if (sniff && sample.Length + read <= NativeUsageSniffBytes) sample.Write(buffer, 0, read);
                        else sniff = false;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            if (response.IsSuccessStatusCode && sample.Length > 0 && ImageUsageOf(sample.GetBuffer().AsSpan(0, (int)sample.Length)) is { } usage)
                observer.Observe(new UsageEvent(ApiProtocol.OpenAIResponses, usage, UsageNormalizer.FromForwarded(usage)), sw.ElapsedMilliseconds, wholeBody: true);
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
            record.Status = RequestStatus.ClientCancelled;
        }
        catch (UpstreamIdleTimeoutException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.ErrorType = "timeout";
            record.ErrorMessage = e.Message;
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 504, "timeout", e.Message);
        }
        catch (HttpRequestException e)
        {
            record.Status = RequestStatus.UpstreamError;
            record.HttpStatus ??= 502;
            record.ErrorType = "upstream_unreachable";
            var cause = UpstreamFailure.Describe(e);
            record.ErrorMessage = Truncate(cause, 2000);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 502, "api_error", $"无法连接上游 {provider?.Name}：{cause}");
        }
        catch (IOException) when (ctx.Response.HasStarted)
        {
            record.Status = RequestStatus.ClientCancelled;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Gateway native request {Id} failed", record.Id);
            record.Status = RequestStatus.GatewayError;
            record.HttpStatus ??= 500;
            record.ErrorType = e.GetType().Name;
            record.ErrorMessage = Truncate(e.Message, 2000);
            await WriteErrorAsync(ctx, ApiProtocol.OpenAIChat, null, 500, "api_error", $"Astra 网关内部错误：{e.Message}");
        }
        finally
        {
            Finish(record, observer, sw, effective, provider, s.Locale);
            FinishTrace(record, trace, null, inboundTraceId, observer, responseSpan, false, restorable, "native");
            owned?.Release();
            requestBody?.Dispose();
            live.Finish(record);
            await usageWriter.EnqueueAsync(record);
        }
    }

    private static bool IsNativeText(string? mediaType) =>
        mediaType is not null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path and query of a native call, as the caller wrote them, checked to stay on the provider's host: read from the raw request
    /// target, because the router has already decoded the path (an encoded slash or dot segment would look ordinary there).
    /// </summary>
    /// <exception cref="GatewayException">An address that could leave the provider's host or climb out of the path.</exception>
    public static (string Path, string Query) NativeTarget(HttpContext ctx, string providerRef)
    {
        // Kestrel hands over the target as the client wrote it; a host that does not (an in-memory test server) gets the decoded path re-encoded.
        var raw = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } target && target.StartsWith('/')
            ? target
            : ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString;
        var queryAt = raw.IndexOf('?', StringComparison.Ordinal);
        var rawPath = queryAt < 0 ? raw : raw[..queryAt];
        var query = queryAt < 0 ? "" : raw[(queryAt + 1)..];
        return (NativePath(rawPath, providerRef), NativeQuery(query));
    }

    /// <summary>The path to forward from a raw request path (<c>/v1/astra/native/{provider}/…</c>), or the refusal.</summary>
    public static string NativePath(string rawPath, string providerRef)
    {
        if (!rawPath.StartsWith(NativePrefix, StringComparison.Ordinal))
            throw NativeRefused("路径不在 /v1/astra/native/ 之下。");
        var rest = rawPath[NativePrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash == rest.Length - 1) throw NativeRefused("缺少要转发的路径。");
        var path = rest[(slash + 1)..];
        if (path.StartsWith('/') || path.Contains('\\') || path.Any(c => c <= ' ' || c == '#' || c == 127))
            throw NativeRefused("路径含有不允许的字符（前导斜杠、反斜杠或控制字符）。");
        var segments = path.Split('/');
        if (segments[0].Contains(':', StringComparison.Ordinal)) throw NativeRefused("路径不能带协议（scheme）。");
        foreach (var segment in segments)
        {
            // Decoded as far as it goes: %2e%2e, %252e%252e, an encoded slash or backslash are all the same attempt.
            var decoded = segment;
            for (var i = 0; i < 3 && decoded.Contains('%', StringComparison.Ordinal); i++) decoded = Uri.UnescapeDataString(decoded);
            if (decoded is "." or ".." || decoded.Contains('/', StringComparison.Ordinal) || decoded.Contains('\\', StringComparison.Ordinal)
                || decoded.Any(c => c < ' ' || c == 127))
                throw NativeRefused("路径含有 . / .. 段、编码过的斜杠或控制字符。");
        }
        return path;
    }

    // The gateway's own ?key= is a caller credential: it never goes on.
    public static string NativeQuery(string query) =>
        string.Join('&', query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("key=", StringComparison.OrdinalIgnoreCase) && !p.Equals("key", StringComparison.OrdinalIgnoreCase)));

    private static GatewayException NativeRefused(string why) =>
        new(400, "invalid_request_error", $"原生透传的路径被拒绝：{why}");

    /// <summary>The provider's own origin (scheme and host of its configured endpoint) — the only place a native call goes.</summary>
    private static string NativeOrigin(ProviderEndpoint endpoint, Provider provider)
    {
        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 的接口地址无效。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string NativeUrl(string url, string query, UpstreamAuth credentials)
    {
        var withQuery = query.Length > 0 ? url + "?" + query : url;
        return credentials.QueryName is not null && credentials.QueryValue is not null
            ? UpstreamUrls.WithQuery(withQuery, credentials.QueryName, credentials.QueryValue)
            : withQuery;
    }
}
