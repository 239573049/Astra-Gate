using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tools;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Gateway.Tracing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Tools;

/// <summary>One search the bridge ran, handed to the pipeline of the next internal call so its trace shows it (<c>gateway.search</c> span).</summary>
public sealed record WebSearchRecord(string Source, int Pages, long ElapsedMs, string? Error, string? Query);

/// <summary>
/// Search on behalf of models that cannot search (magpie parity #8; the idea — a stand-in function tool the gateway answers itself,
/// then the reply said the way the client expects — is magpie's, MIT). A request that offers a web-search server tool to an upstream
/// that cannot serve it (<see cref="WebSearchSettings.OnBehalf"/>, off by default) is run through the ordinary pipeline as internal
/// calls (<see cref="InternalCall"/>: token caps, routing, failover, the request log and the bill are the HTTP route's own, one row
/// per round): the tool is replaced by a function tool, each call of it is answered by <see cref="ISearchService"/>, and the final
/// reply gets the server-tool blocks / <c>web_search_call</c> items / citations the protocol has. A client that asked for a stream gets
/// the finished reply as events: the rounds cannot be streamed, so the answer arrives whole.
/// The query is the model's text with the privacy guard applied to it first, so nothing the guard masks goes to a search API.
/// </summary>
public sealed class WebSearchBridge(GatewayPipeline pipeline, IGatewayRouter router, ISearchService search, ToolsSettingsService tools,
    ILogger<WebSearchBridge> logger, PrivacyGuardService? privacy = null)
{
    /// <summary>The request header that keeps one request out of it (<c>x-astra-web-search: off</c>).</summary>
    public const string OptOutHeader = "x-astra-web-search";

    /// <summary><see cref="HttpContext.Items"/> key of the searches an internal call's trace shows.</summary>
    public const string TraceItemKey = "astra.web_search.records";

    /// <summary>Most searches one request may run; a model that asks for more is told to answer with what it has.</summary>
    public const int MaxSearches = 6;

    private const int MaxRounds = MaxSearches + 2;
    private const int MaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>Whether the model's own provider serves the search tool (only the vendors' own hosts, or a provider marked <c>web_search</c>).</summary>
    private static readonly Dictionary<ApiProtocol, string[]> NativeHosts = new()
    {
        [ApiProtocol.Anthropic] = ["api.anthropic.com"],
        [ApiProtocol.OpenAIResponses] = ["api.openai.com", "chatgpt.com"],
        [ApiProtocol.OpenAIChat] = [],
        [ApiProtocol.Gemini] = ["generativelanguage.googleapis.com"],
    };

    public async Task HandleAsync(HttpContext ctx, ApiProtocol inbound, string? pathModel = null, bool? pathStream = null, bool? pathAltSse = null)
    {
        var cfg = tools.Current.Search;
        if (!cfg.OnBehalf || !search.HasSources || OptedOut(ctx.Request) || ctx.Request.Headers.ContentEncoding.Count > 0
            || !ctx.Request.Headers.ContentType.ToString().Contains("json", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.ContentLength is > MaxBodyBytes)
        {
            await pipeline.HandleAsync(ctx, inbound, pathModel, pathStream, pathAltSse);
            return;
        }
        ctx.Request.EnableBuffering(16 * 1024 * 1024, MaxBodyBytes);
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            bytes = buffer.ToArray();
        }
        ctx.Request.Body.Position = 0;
        JsonObject? body = null;
        var dialect = SearchDialect.For(inbound);
        if (Mentions(bytes))
        {
            try { body = JsonNode.Parse(bytes) as JsonObject; }
            catch (JsonException) { /* the pipeline answers a malformed body */ }
        }
        if (body is null || !dialect.Detect(body) || await ServedNativelyAsync(ctx, inbound, body, pathModel, cfg))
        {
            await pipeline.HandleAsync(ctx, inbound, pathModel, pathStream, pathAltSse);
            return;
        }
        var wantsStream = inbound == ApiProtocol.Gemini ? pathStream == true : body["stream"]?.GetValue<bool>() == true;
        await RunAsync(ctx, inbound, dialect, body, pathModel, wantsStream, pathAltSse == true, cfg);
    }

    private static bool OptedOut(HttpRequest request) =>
        string.Equals(request.Headers[OptOutHeader].ToString(), "off", StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(byte[] bytes)
    {
        var span = bytes.AsSpan();
        return span.IndexOf("web_search"u8) >= 0 || span.IndexOf("oogle_search"u8) >= 0 || span.IndexOf("googleSearch"u8) >= 0;
    }

    private async Task<bool> ServedNativelyAsync(HttpContext ctx, ApiProtocol inbound, JsonObject body, string? pathModel, WebSearchSettings cfg)
    {
        if (cfg.Mode == SearchModes.Always) return false;
        try
        {
            var model = inbound == ApiProtocol.Gemini ? pathModel ?? "" : body["model"]?.GetValue<string>() ?? "";
            var route = await router.ResolveAsync(ctx.Request, ctx.RequestAborted);
            route = await router.ForModelAsync(route, model, false, ctx.RequestAborted);
            return ServesItself(route.Provider, inbound, route.UpstreamModelFor(model));
        }
        catch (GatewayException)
        {
            // Unauthorized, an unknown model...: the pipeline answers it in the protocol's own error shape.
            return true;
        }
    }

    /// <summary>Whether <paramref name="provider"/> runs the search tool itself on <paramref name="inbound"/> (so Astra leaves the request alone).</summary>
    public static bool ServesItself(Provider provider, ApiProtocol inbound, string model)
    {
        if (provider.SettingFlag("web_search", false)) return true;
        var endpoint = provider.EndpointFor(inbound);
        if (endpoint is null) return false;
        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri)) return false;
        if (inbound == ApiProtocol.OpenAIChat) return uri.Host == "api.openai.com" && model.Contains("search", StringComparison.OrdinalIgnoreCase);
        return NativeHosts[inbound].Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunAsync(HttpContext ctx, ApiProtocol inbound, SearchDialect dialect, JsonObject original, string? pathModel, bool wantsStream, bool altSse,
        WebSearchSettings cfg)
    {
        var ct = ctx.RequestAborted;
        var body = (JsonObject)original.DeepClone();
        dialect.Rewrite(body);
        var rounds = new List<SearchRound>();
        var pending = new List<WebSearchRecord>();
        var searches = 0;
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(GatewayJson.Options));
                ctx.Items[TraceItemKey] = pending.ToList();
                pending.Clear();
                var call = InternalCall.Create(ctx, inbound == ApiProtocol.Gemini ? $"/v1beta/models/{pathModel}:generateContent" : dialect.Path, bytes, ct);
                await pipeline.HandleAsync(call.Context, inbound, pathModel, inbound == ApiProtocol.Gemini ? false : null, null);
                await call.FinishAsync();
                ctx.Items.Remove(TraceItemKey);
                if (ct.IsCancellationRequested) return;
                JsonObject? reply = null;
                if (call.Status / 100 == 2)
                {
                    try { reply = JsonNode.Parse(call.BodyText) as JsonObject; }
                    catch (JsonException) { /* relayed as it is below */ }
                }
                if (reply is null)
                {
                    await RelayAsync(ctx, call);
                    return;
                }
                var calls = dialect.Calls(reply);
                if (calls.Count == 0)
                {
                    dialect.Decorate(reply, rounds, stripCalls: false);
                    await WriteAsync(ctx, call, dialect, reply, original, wantsStream, altSse);
                    return;
                }
                var runs = new List<SearchRun>();
                foreach (var c in calls)
                {
                    searches++;
                    var run = searches > MaxSearches
                        ? new SearchRun(c.Id, c.Query, [], "the search limit for this request was reached; answer with what you already have")
                        : await RunSearchAsync(ctx, c, pending, ct);
                    runs.Add(run);
                }
                rounds.Add(new SearchRound(dialect.Kept(reply), runs));
                if (dialect.HasOtherCalls(reply))
                {
                    // The reply also calls the client's own tools: the searches are on record, the client's calls are its to answer.
                    // What this reply itself wrote stays in the reply, so the last round keeps none of it.
                    rounds[^1] = new SearchRound([], runs);
                    dialect.Decorate(reply, rounds, stripCalls: true);
                    await WriteAsync(ctx, call, dialect, reply, original, wantsStream, altSse);
                    return;
                }
                dialect.Continue(body, reply, runs);
            }
        }
        finally
        {
            ctx.Items.Remove(TraceItemKey);
        }
        await WriteErrorAsync(ctx, inbound, 502, "联网搜索没有在限定的轮数内结束。");
    }

    private static async Task WriteErrorAsync(HttpContext ctx, ApiProtocol inbound, int status, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        JsonObject error = inbound switch
        {
            ApiProtocol.Anthropic => new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message } },
            ApiProtocol.Gemini => new() { ["error"] = new JsonObject { ["code"] = status, ["message"] = message, ["status"] = "INTERNAL" } },
            _ => new() { ["error"] = new JsonObject { ["message"] = message, ["type"] = "api_error" } },
        };
        await ctx.Response.WriteAsync(error.ToJsonString(GatewayJson.Options), ctx.RequestAborted);
    }

    private async Task<SearchRun> RunSearchAsync(HttpContext ctx, SearchCall call, List<WebSearchRecord> pending, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var query = call.Query;
        try
        {
            // What goes to a search API is the guarded text: the model's query came back with the originals restored.
            query = Guard(ctx, query);
            if (query.Length == 0) throw new SearchFailedException("the query is empty");
            var outcome = await search.SearchAsync(query, null, null, ct);
            pending.Add(new WebSearchRecord(outcome.Source, outcome.Pages.Count, watch.ElapsedMilliseconds, null, query));
            return new SearchRun(call.Id, query, outcome.Pages, null);
        }
        catch (Exception ex) when (ex is SearchFailedException or GatewayException)
        {
            logger.LogDebug("联网搜索失败：{Message}", ex.Message);
            pending.Add(new WebSearchRecord("", 0, watch.ElapsedMilliseconds, ex.Message, null));
            return new SearchRun(call.Id, query, [], ex.Message);
        }
    }

    private string Guard(HttpContext ctx, string query)
    {
        query = query.Trim();
        if (privacy is null || query.Length == 0) return query;
        var result = privacy.Inspect(query, null);
        if (result.Blocked) throw new SearchFailedException("the query was blocked by the privacy policy");
        return result.Applied && !result.DryRun && result.Body.Length > 0 ? result.Body : query;
    }

    // An answer that is not a reply (an error in the protocol's own shape): the client gets it as the pipeline wrote it.
    private static async Task RelayAsync(HttpContext ctx, InternalCall call)
    {
        CopyHeaders(ctx, call);
        ctx.Response.StatusCode = call.Status;
        ctx.Response.ContentType = call.Headers.ContentType.ToString() is { Length: > 0 } t ? t : "application/json";
        await ctx.Response.WriteAsync(call.BodyText, ctx.RequestAborted);
    }

    private static void CopyHeaders(HttpContext ctx, InternalCall call)
    {
        foreach (var (name, values) in call.Headers)
            if (name.StartsWith("x-astra-", StringComparison.OrdinalIgnoreCase)) ctx.Response.Headers[name] = values;
    }

    private static async Task WriteAsync(HttpContext ctx, InternalCall call, SearchDialect dialect, JsonObject reply, JsonObject request, bool stream, bool altSse)
    {
        CopyHeaders(ctx, call);
        ctx.Response.StatusCode = 200;
        if (!stream)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(reply.ToJsonString(GatewayJson.Options), ctx.RequestAborted);
            return;
        }
        var json = dialect is GeminiSearchDialect && !altSse;
        ctx.Response.ContentType = json ? "application/json" : "text/event-stream";
        if (!json)
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
        }
        foreach (var chunk in dialect.Stream(reply, request, altSse))
        {
            await ctx.Response.WriteAsync(chunk, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }

    /// <summary>
    /// Puts the searches an internal call carried into its trace as <c>gateway.search</c> spans (the pipeline calls it before the spans
    /// are closed). The query is recorded only while <see cref="AppSettings.TraceContent"/> is on.
    /// </summary>
    public static void AddSpans(HttpContext ctx, RequestTrace trace, bool traceContent)
    {
        if (!ctx.Items.TryGetValue(TraceItemKey, out var value) || value is not List<WebSearchRecord> records) return;
        foreach (var r in records)
        {
            var span = trace.Start(TraceSpanNames.Search, TraceSpanKinds.Client)
                .Set("astra.search.source", r.Source.Length > 0 ? r.Source : null)
                .Set("astra.search.results", r.Pages)
                .Set("astra.search.duration_ms", r.ElapsedMs);
            if (traceContent) span.Set("astra.search.query", r.Query);
            if (r.Error is not null) span.Error("search failed");
            else span.Ok();
            span.End();
        }
    }
}
