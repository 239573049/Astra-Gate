using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Mcp;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Astra.Gateway.Http;

/// <summary>
/// The MCP gateway's front end (plan mcp-gateway §3.1): <c>POST /mcp</c> is the aggregate view of the servers bound to the caller's token,
/// <c>POST /mcp/{slug}</c> one server's own view (tool names unprefixed). Both eras of the protocol are served on the same address and
/// both without state — nothing here remembers a client between requests:
/// <list type="bullet">
/// <item>modern (2026-07-28): every request carries its protocol version and client capabilities in <c>params._meta</c>, mirrored in
/// <c>MCP-Protocol-Version</c> / <c>Mcp-Method</c> / <c>Mcp-Name</c> headers that are checked against the body;</item>
/// <item>legacy Streamable HTTP (2025-11-25 and earlier): <c>initialize</c> negotiates a version but issues no session id (the spec lets a
/// server omit it), and the requests that follow are served on their own.</item>
/// </list>
/// The deprecated HTTP+SSE transport is not served. Replies are always JSON, never an SSE stream (a server may choose either).
/// Authentication is the Astra token (<c>Authorization: Bearer</c> or <c>x-api-key</c>, never the query string); the endpoint answers 404
/// while <c>AppSettings.McpGateway.Enabled</c> is off.
/// </summary>
public static class McpGatewayEndpoints
{
    /// <summary>The stateless revision.</summary>
    public const string ModernVersion = "2026-07-28";

    /// <summary>Initialize-based revisions served over Streamable HTTP, newest first.</summary>
    public static readonly IReadOnlyList<string> LegacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    /// <summary>Every version offered, as <c>server/discover</c> and an unsupported-version error list them.</summary>
    public static IReadOnlyList<string> SupportedVersions { get; } = [ModernVersion, .. LegacyVersions];

    private const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
    private const string MetaCapabilities = "io.modelcontextprotocol/clientCapabilities";
    private const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

    // JSON-RPC 2.0 and MCP error codes (the MCP ones are reserved at -32020 .. -32099 by the 2026-07-28 revision).
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int HeaderMismatch = -32020;
    private const int UnsupportedVersion = -32022;

    public static IEndpointRouteBuilder MapMcpGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        string[] methods = ["GET", "POST", "DELETE", "PUT", "PATCH"];
        app.MapMethods("/mcp", methods, (HttpContext ctx, AstraDatabase db, IGatewaySettings settings, McpAggregator aggregator) =>
            HandleAsync(ctx, null, db, settings, aggregator)).ExcludeFromDescription();
        app.MapMethods("/mcp/{slug}", methods, (HttpContext ctx, string slug, AstraDatabase db, IGatewaySettings settings, McpAggregator aggregator) =>
            HandleAsync(ctx, slug, db, settings, aggregator)).ExcludeFromDescription();
        return app;
    }

    private static async Task HandleAsync(HttpContext ctx, string? slug, AstraDatabase db, IGatewaySettings settings, McpAggregator aggregator)
    {
        var ct = ctx.RequestAborted;
        var config = settings.Current.McpGateway;
        if (!config.Enabled)
        {
            await Plain(ctx, StatusCodes.Status404NotFound, "Not found");
            return;
        }
        // DNS-rebinding defence of the MCP spec: a browser origin must be allowed explicitly; no Origin (every non-browser client) passes.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !config.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            await Plain(ctx, StatusCodes.Status403Forbidden, "Origin not allowed");
            return;
        }
        // No standalone event stream, no session to delete: the only door is POST.
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.Headers.Allow = "POST";
            await Plain(ctx, StatusCodes.Status405MethodNotAllowed, "Use POST");
            return;
        }
        if (await AuthenticateAsync(ctx, db, ct) is not { } token)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"astra\"";
            await Plain(ctx, StatusCodes.Status401Unauthorized, "Missing, invalid or disabled token");
            return;
        }
        if (ctx.Request.ContentType is null || !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Plain(ctx, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
            return;
        }
        if (!AcceptsJson(ctx.Request.Headers.Accept.ToString()))
        {
            await Plain(ctx, StatusCodes.Status406NotAcceptable, "Accept must allow application/json");
            return;
        }
        var view = await aggregator.ViewAsync(token, slug, ct);
        if (view is null)
        {
            await Plain(ctx, StatusCodes.Status404NotFound, "Unknown MCP server");
            return;
        }

        var body = await ReadBodyAsync(ctx, ct);
        if (body is null)
        {
            await Plain(ctx, StatusCodes.Status413PayloadTooLarge, $"Request body must be at most {Astra.Core.Mcp.McpLimits.MaxRequestBytes / (1024 * 1024)} MB");
            return;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            await RpcError(ctx, null, ParseError, "Parse error", status: StatusCodes.Status400BadRequest);
            return;
        }
        if (parsed is not JsonObject request)
        {
            await RpcError(ctx, null, InvalidRequest, "A single JSON-RPC request object is expected (batches are not supported)", status: StatusCodes.Status400BadRequest);
            return;
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null;
        if (method is null)
        {
            await RpcError(ctx, id, InvalidRequest, "method is required", status: StatusCodes.Status400BadRequest);
            return;
        }
        var parameters = request["params"] as JsonObject;
        var meta = parameters?["_meta"] as JsonObject;
        var metaVersion = meta?[MetaVersion] is JsonValue v && v.TryGetValue<string>(out var ver) ? ver : null;
        var headerVersion = ctx.Request.Headers["MCP-Protocol-Version"].ToString();
        // The era is the caller's, announced by how the request opens: modern per-request metadata, or (legacy) an initialize handshake.
        var modern = metaVersion is not null || headerVersion == ModernVersion;

        if (modern && await CheckModernAsync(ctx, id, method, parameters, meta, metaVersion, headerVersion) is false) return;

        var era = modern ? "modern" : "legacy";
        try
        {
            if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted; // a notification has no reply
                return;
            }
            switch (method)
            {
                case "initialize" when !modern:
                    await Result(ctx, id, Initialize(parameters), modern);
                    return;
                case "ping" when !modern:
                    await Result(ctx, id, new JsonObject(), modern);
                    return;
                case "server/discover" when modern:
                    await Result(ctx, id, ToNode(new DiscoverResult
                    {
                        SupportedVersions = [.. SupportedVersions],
                        Capabilities = Capabilities(),
                        TimeToLive = TimeSpan.Zero,
                        CacheScope = CacheScope.Private,
                    }), modern);
                    return;
                case "tools/list":
                    // Always private and never cacheable: the list is cut to the caller's token (the unprefixed single-server view too).
                    await Result(ctx, id, ToNode(new ListToolsResult
                    {
                        Tools = [.. await aggregator.ListToolsAsync(view, ct)],
                        TimeToLive = TimeSpan.Zero,
                        CacheScope = CacheScope.Private,
                    }), modern);
                    return;
                case "tools/call":
                    var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var toolName) ? toolName : null;
                    if (name is null)
                    {
                        await RpcError(ctx, id, InvalidParams, "params.name is required");
                        return;
                    }
                    var result = await aggregator.CallToolAsync(view, name, parameters?["arguments"] as JsonObject, era, ct);
                    await Result(ctx, id, ToNode(result), modern);
                    return;
                default:
                    await RpcError(ctx, id, MethodNotFound, $"Method not found: {method}");
                    return;
            }
        }
        catch (McpToolNotFoundException e)
        {
            await RpcError(ctx, id, InvalidParams, e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client closed the request: a modern client's way to cancel. Nothing to answer.
        }
    }

    // ---------------------------------------------------------------- modern validation

    /// <summary>
    /// The checks a modern request must pass before it is served (MCP 2026-07-28, streamable HTTP "server validation"): a version we speak,
    /// headers that agree with the body, and the client's capabilities. False = an error was written.
    /// </summary>
    private static async Task<bool> CheckModernAsync(HttpContext ctx, JsonNode? id, string method, JsonObject? parameters, JsonObject? meta,
        string? metaVersion, string headerVersion)
    {
        var version = metaVersion ?? headerVersion;
        if (version != ModernVersion)
        {
            await RpcError(ctx, id, UnsupportedVersion, "Unsupported protocol version", status: StatusCodes.Status400BadRequest,
                data: new JsonObject { ["supported"] = new JsonArray([.. SupportedVersions.Select(s => (JsonNode?)JsonValue.Create(s))]), ["requested"] = version });
            return false;
        }
        if (headerVersion != metaVersion)
        {
            await RpcError(ctx, id, HeaderMismatch, "MCP-Protocol-Version does not match params._meta", status: StatusCodes.Status400BadRequest);
            return false;
        }
        if (ctx.Request.Headers["Mcp-Method"].ToString() != method)
        {
            await RpcError(ctx, id, HeaderMismatch, "Mcp-Method does not match the request method", status: StatusCodes.Status400BadRequest);
            return false;
        }
        if (method is "tools/call" or "prompts/get")
        {
            var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var text) ? text : null;
            if (ctx.Request.Headers["Mcp-Name"].ToString() != name)
            {
                await RpcError(ctx, id, HeaderMismatch, "Mcp-Name does not match params.name", status: StatusCodes.Status400BadRequest);
                return false;
            }
        }
        if (meta?[MetaCapabilities] is not JsonObject)
        {
            await RpcError(ctx, id, InvalidParams, $"params._meta[\"{MetaCapabilities}\"] is required", status: StatusCodes.Status400BadRequest);
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- results

    private static ServerCapabilities Capabilities() => new() { Tools = new ToolsCapability { ListChanged = false } };

    private static JsonNode Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        // The version the client asked for when it is one we serve, else our newest legacy one (the client then decides whether it can live with it).
        var version = asked is not null && LegacyVersions.Contains(asked) ? asked : LegacyVersions[0];
        return ToNode(new InitializeResult
        {
            ProtocolVersion = version,
            Capabilities = Capabilities(),
            ServerInfo = ServerInfo(),
        });
    }

    private static Implementation ServerInfo() => new() { Name = "astra-gateway", Version = McpUpstreamPool.ServerVersion };

    private static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))) ?? new JsonObject();

    /// <summary>
    /// A result in the caller's era: modern results carry <c>resultType</c> and the server's identity in <c>_meta</c>; a legacy client gets
    /// neither, nor the cache hints (<c>ttlMs</c>, <c>cacheScope</c>) it has never heard of.
    /// </summary>
    private static Task Result(HttpContext ctx, JsonNode? id, JsonNode result, bool modern)
    {
        if (result is JsonObject obj)
        {
            if (modern)
            {
                obj["resultType"] ??= "complete";
                var meta = obj["_meta"] as JsonObject ?? new JsonObject();
                meta[MetaServerInfo] = new JsonObject { ["name"] = ServerInfo().Name, ["version"] = ServerInfo().Version };
                obj["_meta"] = meta;
            }
            else
            {
                obj.Remove("resultType");
                obj.Remove("ttlMs");
                obj.Remove("cacheScope");
            }
        }
        return Json(ctx, StatusCodes.Status200OK, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
    }

    private static Task RpcError(HttpContext ctx, JsonNode? id, int code, string message, int status = StatusCodes.Status200OK, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return Json(ctx, status, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error });
    }

    private static Task Json(HttpContext ctx, int status, JsonObject body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(body.ToJsonString(), ctx.RequestAborted);
    }

    private static Task Plain(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(new JsonObject { ["error"] = message }.ToJsonString(), ctx.RequestAborted);
    }

    // ---------------------------------------------------------------- request plumbing

    /// <summary>The Astra token of the request (header only — a token in a URL ends up in logs), or null.</summary>
    private static async Task<TokenRecord?> AuthenticateAsync(HttpContext ctx, AstraDatabase db, CancellationToken ct)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : ctx.Request.Headers["x-api-key"].ToString().Trim();
        if (string.IsNullOrEmpty(key) || !GatewayTokens.TryParse(key, out var secret, out _)) return null;
        var token = (await db.Tokens.FindByPrefixAsync(GatewayTokens.PrefixOf(secret), ct)).FirstOrDefault(t => GatewayTokens.Verify(secret, t.KeyHash));
        return token is { Enabled: true } ? token : null;
    }

    /// <summary>Absent, <c>*/*</c> and anything naming <c>application/json</c> are fine: the reply is always JSON.</summary>
    private static bool AcceptsJson(string accept) =>
        accept.Length == 0 || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) || accept.Contains("*/*", StringComparison.Ordinal)
        || accept.Contains("application/*", StringComparison.Ordinal);

    /// <summary>The request body, or null when it is larger than <see cref="Astra.Core.Mcp.McpLimits.MaxRequestBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        var limit = Astra.Core.Mcp.McpLimits.MaxRequestBytes;
        if (ctx.Request.ContentLength > limit) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
