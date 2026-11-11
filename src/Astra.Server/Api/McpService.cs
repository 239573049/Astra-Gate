using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Mcp;
using Astra.Data;
using Astra.Gateway.Mcp;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>
/// Administration of the MCP gateway (plan mcp-gateway): the upstream servers, their credentials, "test connection", the gateway's
/// settings and the call log. Whoever may write here makes Astra connect to an address of their choosing, so creating, changing and testing a
/// server is limited to callers on this machine by the endpoints (like the tunnel's <c>cloudflaredPath</c>).
/// </summary>
public sealed partial class McpService(
    AstraDatabase db, ISecretProtector secrets, McpUpstreamPool pool, SettingsService settings, ServerOptions server)
{
    private const int MaxNameLength = 64;
    private const int MaxHeaders = 20;
    private const int MaxTestTools = 100;

    /// <summary>
    /// Headers an admin cannot set: they belong to the HTTP framing or to the protocol, and the gateway sets them itself.
    /// </summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "content-encoding", "transfer-encoding", "connection", "upgrade", "te", "trailer",
        "accept", "mcp-protocol-version", "mcp-session-id", "mcp-method", "mcp-name", "last-event-id",
    };

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial Regex HeaderName();

    // ---------------------------------------------------------------- settings

    public McpSettingsDto GetSettings() => ToSettingsDto(settings.Current.McpGateway);

    public async Task<McpSettingsDto> UpdateSettingsAsync(JsonObject body, CancellationToken ct)
    {
        bool? enabled = null;
        List<string>? origins = null;
        if (body.ContainsKey("enabled"))
            enabled = body["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new AdminApiException(400, "enabled must be a boolean");
        if (body.ContainsKey("allowedOrigins"))
            origins = ReadOrigins(body["allowedOrigins"]);
        var updated = await settings.UpdateAsync(s =>
        {
            if (enabled is { } e) s.McpGateway.Enabled = e;
            if (origins is not null) s.McpGateway.AllowedOrigins = origins;
        }, ct);
        return ToSettingsDto(updated.McpGateway);
    }

    private McpSettingsDto ToSettingsDto(McpGatewaySettings s) => new(s.Enabled, [.. s.AllowedOrigins], server.GatewayBaseUrl.TrimEnd('/') + "/mcp");

    /// <summary>Origins as the browser sends them (<c>scheme://host[:port]</c>, no path), de-duplicated.</summary>
    private static List<string> ReadOrigins(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array) throw new AdminApiException(400, "allowedOrigins must be an array of origins");
        var result = new List<string>();
        foreach (var item in array)
        {
            var text = item is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query.Length > 0)
                throw new AdminApiException(400, "Each allowed origin must look like https://example.com or http://localhost:3000");
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!result.Contains(origin, StringComparer.OrdinalIgnoreCase)) result.Add(origin);
        }
        return result;
    }

    // ---------------------------------------------------------------- servers

    public async Task<List<McpServerDto>> ListAsync(CancellationToken ct)
    {
        var servers = await db.Mcp.ListAsync(ct);
        var bound = await BoundCountsAsync(ct);
        var list = new List<McpServerDto>(servers.Count);
        foreach (var s in servers) list.Add(await ToDtoAsync(s, bound.GetValueOrDefault(s.Id), ct));
        return list;
    }

    public async Task<McpServerDto> GetAsync(string id, CancellationToken ct)
    {
        var s = await RequireAsync(id, ct);
        return await ToDtoAsync(s, (await BoundCountsAsync(ct)).GetValueOrDefault(id), ct);
    }

    private async Task<Dictionary<string, int>> BoundCountsAsync(CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in await db.Tokens.ListAsync(ct))
            foreach (var id in token.McpServerIds().Distinct(StringComparer.Ordinal))
                counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }

    private async Task<McpServerDto> ToDtoAsync(McpServerRecord s, int boundTokens, CancellationToken ct)
    {
        McpOAuthDto? oauth = null;
        if (s.AuthType == McpAuthTypes.OAuth)
        {
            var row = await db.Mcp.GetOAuthAsync(s.Id, ct);
            oauth = new McpOAuthDto(row is not null, row?.Issuer, row?.Scope, row?.UpdatedAt);
        }
        return new McpServerDto(s.Id, s.Slug, s.Name, s.Transport, s.Url, s.AuthType, HeaderNamesOf(s), s.Enabled, s.TimeoutSeconds, s.Status,
            s.StatusMessage, s.ProtocolVersion, s.CheckedAt, oauth, boundTokens, s.CreatedAt, s.UpdatedAt);
    }

    private List<string> HeaderNamesOf(McpServerRecord s) => [.. ReadHeaders(s).Keys.Order(StringComparer.OrdinalIgnoreCase)];

    private Dictionary<string, string> ReadHeaders(McpServerRecord s)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(s.HeadersEnc)) return map;
        try
        {
            if (JsonNode.Parse(secrets.Unprotect(s.HeadersEnc)) is JsonObject obj)
                foreach (var (name, value) in obj)
                    if ((string?)value is { } text) map[name] = text;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Headers that no longer decrypt are as good as none; the admin types them again.
        }
        return map;
    }

    /// <summary>
    /// Creates a server. Body: <c>name</c>, <c>url</c> (required); <c>slug</c> (derived from the name when absent), <c>transport</c> (<c>http</c>),
    /// <c>authType</c> (<c>none</c>), <c>headers</c> (name → value; for <c>header</c> auth), <c>enabled</c>, <c>timeoutSeconds</c>.
    /// </summary>
    public async Task<McpServerDto> CreateAsync(JsonObject body, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var s = new McpServerRecord { Id = Ulid.NewUlid(), CreatedAt = now, UpdatedAt = now };
        s.Name = RequireName(ReadString(body, "name"));
        s.Slug = ReadString(body, "slug") is { Length: > 0 } given ? given.Trim() : await FreeSlugAsync(McpNames.SlugFrom(s.Name), null, ct);
        await ApplyAsync(s, body, creating: true, ct);
        await db.Mcp.InsertAsync(s, ct);
        return await GetAsync(s.Id, ct);
    }

    /// <summary>
    /// Changes a server: any field of <see cref="CreateAsync"/> that is present. A server's connection is rebuilt, because its stamp moves.
    /// <c>headers</c> replaces the set; a name whose value is null keeps the value already stored.
    /// </summary>
    public async Task<McpServerDto> UpdateAsync(string id, JsonObject body, CancellationToken ct)
    {
        var s = await RequireAsync(id, ct);
        var previousUrl = s.Url;
        if (body.ContainsKey("name")) s.Name = RequireName(ReadString(body, "name"));
        if (body.ContainsKey("slug")) s.Slug = ReadString(body, "slug")?.Trim() ?? "";
        await ApplyAsync(s, body, creating: false, ct);
        // Tokens saved for one address are not sent to another.
        if (s.AuthType == McpAuthTypes.OAuth && !string.Equals(previousUrl, s.Url, StringComparison.Ordinal))
            await db.Mcp.DeleteOAuthAsync(s.Id, ct);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.Mcp.UpdateAsync(s, ct);
        pool.Invalidate(s.Id);
        return await GetAsync(s.Id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        if (!await db.Mcp.DeleteAsync(id, ct)) throw new AdminApiException(404, "MCP server not found");
        pool.Invalidate(id);
    }

    /// <summary>Forgets the OAuth tokens of a server (it needs authorizing again).</summary>
    public async Task<McpServerDto> ForgetOAuthAsync(string id, CancellationToken ct)
    {
        var s = await RequireAsync(id, ct);
        await db.Mcp.DeleteOAuthAsync(id, ct);
        await db.Mcp.UpdateStatusAsync(id, McpServerStatuses.NeedsAuth, "尚未授权", s.ProtocolVersion, DateTimeOffset.UtcNow, ct);
        pool.Invalidate(id);
        return await GetAsync(id, ct);
    }

    private async Task ApplyAsync(McpServerRecord s, JsonObject body, bool creating, CancellationToken ct)
    {
        if (body.ContainsKey("transport") || creating)
        {
            s.Transport = ReadString(body, "transport") ?? McpTransports.Http;
            if (!McpTransports.All.Contains(s.Transport)) throw new AdminApiException(400, $"transport must be one of: {string.Join(", ", McpTransports.All)}");
        }
        if (body.ContainsKey("url") || creating) s.Url = RequireUrl(ReadString(body, "url"));
        if (body.ContainsKey("authType") || creating)
        {
            s.AuthType = ReadString(body, "authType") ?? McpAuthTypes.None;
            if (!McpAuthTypes.All.Contains(s.AuthType)) throw new AdminApiException(400, $"authType must be one of: {string.Join(", ", McpAuthTypes.All)}");
        }
        if (body.ContainsKey("headers")) s.HeadersEnc = ProtectHeaders(body["headers"], ReadHeaders(s));
        if (s.AuthType != McpAuthTypes.Header) s.HeadersEnc = null;
        else if (s.HeadersEnc is null) throw new AdminApiException(400, "authType \"header\" needs at least one header");
        if (body.ContainsKey("enabled"))
            s.Enabled = body["enabled"] is JsonValue v && v.TryGetValue<bool>(out var enabled) ? enabled : throw new AdminApiException(400, "enabled must be a boolean");
        if (body.ContainsKey("timeoutSeconds"))
        {
            var seconds = body["timeoutSeconds"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : throw new AdminApiException(400, "timeoutSeconds must be a number");
            if (seconds is < McpLimits.MinTimeoutSeconds or > McpLimits.MaxTimeoutSeconds)
                throw new AdminApiException(400, $"timeoutSeconds must be between {McpLimits.MinTimeoutSeconds} and {McpLimits.MaxTimeoutSeconds}");
            s.TimeoutSeconds = seconds;
        }
        if (McpNames.ValidateSlug(s.Slug) is { } problem) throw new AdminApiException(400, problem);
        if (await db.Mcp.SlugTakenAsync(s.Slug, s.Id, ct)) throw new AdminApiException(409, $"slug \"{s.Slug}\" is already used by another MCP server");
    }

    private async Task<string> FreeSlugAsync(string wanted, string? exceptId, CancellationToken ct)
    {
        var slug = wanted;
        for (var i = 2; await db.Mcp.SlugTakenAsync(slug, exceptId, ct); i++)
        {
            var suffix = "-" + i;
            slug = wanted[..Math.Min(wanted.Length, McpNames.MaxSlugLength - suffix.Length)].TrimEnd('-') + suffix;
        }
        return slug;
    }

    private static string RequireName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new AdminApiException(400, "name is required");
        if (name.Length > MaxNameLength) throw new AdminApiException(400, $"name must be at most {MaxNameLength} characters");
        return name;
    }

    /// <summary>An absolute http(s) address without credentials of its own (a secret in a URL ends up in logs; use headers or OAuth).</summary>
    private static string RequireUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new AdminApiException(400, "url must be an absolute http:// or https:// address");
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new AdminApiException(400, "url must not carry credentials or a fragment; put credentials in headers or use OAuth");
        return uri.ToString();
    }

    /// <summary>The headers body as one protected value; <c>null</c> = none. A name whose value is null keeps what <paramref name="existing"/> holds.</summary>
    private string? ProtectHeaders(JsonNode? node, IReadOnlyDictionary<string, string> existing)
    {
        if (node is null) return null;
        if (node is not JsonObject obj) throw new AdminApiException(400, "headers must be an object of header name to value");
        if (obj.Count > MaxHeaders) throw new AdminApiException(400, $"At most {MaxHeaders} headers");
        var result = new JsonObject();
        foreach (var (name, value) in obj)
        {
            if (!HeaderName().IsMatch(name)) throw new AdminApiException(400, $"Invalid header name: {name}");
            if (ReservedHeaders.Contains(name)) throw new AdminApiException(400, $"Header {name} is set by the gateway itself");
            var text = value is null ? existing.GetValueOrDefault(name) : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AdminApiException(400, $"Header {name} must be a string");
            if (string.IsNullOrEmpty(text)) throw new AdminApiException(400, $"Header {name} needs a value");
            if (text.Contains('\r') || text.Contains('\n')) throw new AdminApiException(400, $"Header {name} must be a single line");
            result[name] = text;
        }
        return result.Count == 0 ? null : secrets.Protect(result.ToJsonString());
    }

    // ---------------------------------------------------------------- test & log

    /// <summary>
    /// "Test connection": a fresh connection (so what the upstream speaks is found out again), then the tool list. The outcome, whatever it is,
    /// is also the server's status; failures are reported in words that never name the upstream's address.
    /// </summary>
    public async Task<McpTestResultDto> TestAsync(string id, CancellationToken ct)
    {
        var s = await RequireAsync(id, ct);
        pool.Invalidate(id);
        try
        {
            var tools = await pool.ListToolsAsync(s, refresh: true, ct);
            var fresh = await db.Mcp.GetAsync(id, ct);
            return new McpTestResultDto(true, McpServerStatuses.Ok, null, fresh?.ProtocolVersion, tools.Count, [.. tools.Select(t => t.Name).Take(MaxTestTools)]);
        }
        catch (McpUpstreamException failure)
        {
            return new McpTestResultDto(false, failure.Status, failure.Message, null, 0, []);
        }
    }

    public async Task<List<McpCallDto>> CallsAsync(int? limit, string? tokenId, string? serverId, CancellationToken ct)
    {
        var calls = await db.Mcp.ListCallsAsync(Math.Clamp(limit ?? 50, 1, 500), string.IsNullOrWhiteSpace(tokenId) ? null : tokenId, string.IsNullOrWhiteSpace(serverId) ? null : serverId, ct);
        var names = (await db.Tokens.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        return [.. calls.Select(c => new McpCallDto(c.Id, c.At, c.TokenId, names.GetValueOrDefault(c.TokenId), c.ServerId, c.ServerSlug, c.Tool,
            c.DurationMs, c.Ok, c.ErrorCategory, c.FrontEra))];
    }

    // ---------------------------------------------------------------- plumbing

    internal async Task<McpServerRecord> RequireAsync(string id, CancellationToken ct) =>
        await db.Mcp.GetAsync(id, ct) ?? throw new AdminApiException(404, "MCP server not found");

    private static string? ReadString(JsonObject body, string key) => body[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new AdminApiException(400, $"{key} must be a string"),
    };
}
