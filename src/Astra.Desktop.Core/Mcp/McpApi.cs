using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Mcp;

/// <summary>
/// The MCP gateway over the local server's <c>/api/mcp</c> (plan mcp-gateway; the web's <c>useMcp*</c> hooks): the gateway's settings, the
/// upstream servers, "test connection", OAuth authorization and the call log. Nothing secret passes through here: header values and OAuth
/// tokens are stored by the server and only their names and states come back.
/// </summary>
public sealed class McpApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public McpApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Settings = queries.Query<McpSettingsDto>("mcp-settings", async ct => await api.GetAsync<McpSettingsDto>("/api/mcp/settings", ct) ?? throw new ApiException(0, "empty response"));
        Servers = queries.Query<List<McpServerDto>>("mcp-servers", async ct => await api.GetAsync<List<McpServerDto>>("/api/mcp/servers", ct) ?? [],
            new QueryOptions<List<McpServerDto>> { RefetchInterval = _ => TimeSpan.FromSeconds(15) });
        Calls = queries.Query<List<McpCallDto>>("mcp-calls", async ct => await api.GetAsync<List<McpCallDto>>("/api/mcp/calls?limit=50", ct) ?? [],
            new QueryOptions<List<McpCallDto>> { RefetchInterval = _ => TimeSpan.FromSeconds(10) });
    }

    /// <summary>['mcp-settings']: whether the gateway answers, the browser origins it allows, the address clients use.</summary>
    public Query<McpSettingsDto> Settings { get; }

    /// <summary>['mcp-servers']: the upstream servers with their status (polled every 15 s while a page shows it).</summary>
    public Query<List<McpServerDto>> Servers { get; }

    /// <summary>['mcp-calls']: the newest tool calls (who, which tool, how long, how it ended; never the arguments).</summary>
    public Query<List<McpCallDto>> Calls { get; }

    /// <summary>PUT /api/mcp/settings: the switch and / or the allowed origins (null = leave as is).</summary>
    public async Task<McpSettingsDto> UpdateSettingsAsync(bool? enabled, IReadOnlyList<string>? allowedOrigins, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (enabled is { } e) body["enabled"] = e;
        if (allowedOrigins is not null)
        {
            var origins = new JsonArray();
            foreach (var o in allowedOrigins) origins.Add((JsonNode?)JsonValue.Create(o));
            body["allowedOrigins"] = origins;
        }
        var result = await _api.PutAsync<JsonObject, McpSettingsDto>("/api/mcp/settings", body, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("mcp-settings");
        return result;
    }

    /// <summary>POST /api/mcp/servers. 400 validation, 409 slug taken, 403 not from this machine.</summary>
    public async Task<McpServerDto> CreateAsync(JsonObject body, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<JsonObject, McpServerDto>("/api/mcp/servers", body, ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>PATCH /api/mcp/servers/{id}: the fields present in <paramref name="body"/>; a header sent as null keeps its stored value.</summary>
    public async Task<McpServerDto> UpdateAsync(string id, JsonObject body, CancellationToken ct = default)
    {
        var result = await _api.PatchAsync<JsonObject, McpServerDto>($"/api/mcp/servers/{Uri.EscapeDataString(id)}", body, ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>DELETE /api/mcp/servers/{id} (and, through the foreign key, its OAuth state).</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/mcp/servers/{Uri.EscapeDataString(id)}", ct);
        ServersChanged();
    }

    /// <summary>POST /api/mcp/servers/{id}/test: a fresh connection and a tool list. A failure to connect is a result (<c>Ok = false</c>), not an exception.</summary>
    public async Task<McpTestResultDto> TestAsync(string id, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<McpTestResultDto>($"/api/mcp/servers/{Uri.EscapeDataString(id)}/test", ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>POST /api/mcp/servers/{id}/oauth/start: where to send the browser (null address = nothing to do).</summary>
    public async Task<McpOAuthStartDto> StartOAuthAsync(string id, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<McpOAuthStartDto>($"/api/mcp/servers/{Uri.EscapeDataString(id)}/oauth/start", ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>DELETE /api/mcp/servers/{id}/oauth: forgets the tokens (the server needs authorizing again).</summary>
    public async Task<McpServerDto> ForgetOAuthAsync(string id, CancellationToken ct = default)
    {
        var result = await _api.DeleteAsync<McpServerDto>($"/api/mcp/servers/{Uri.EscapeDataString(id)}/oauth", ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    private void ServersChanged()
    {
        _queries.Invalidate("mcp-servers");
        _queries.Invalidate("tokens");
    }

    /// <summary>
    /// The body of a create / update from the sheet's fields. <paramref name="headers"/>: name → value, a null value keeps what the server
    /// stored; only sent for <c>header</c> authentication (every other kind carries none).
    /// </summary>
    public static JsonObject ServerBody(string name, string? slug, string url, string transport, string authType, bool enabled, int timeoutSeconds,
        IEnumerable<KeyValuePair<string, string?>>? headers)
    {
        var body = JsonPatch.Of(("name", name.Trim()), ("url", url.Trim()), ("transport", transport), ("authType", authType), ("enabled", enabled), ("timeoutSeconds", timeoutSeconds));
        if (!string.IsNullOrWhiteSpace(slug)) body["slug"] = slug.Trim();
        if (authType == "header")
        {
            var map = new JsonObject();
            foreach (var (header, value) in headers ?? [])
                if (header.Trim().Length > 0) map[header.Trim()] = value;
            body["headers"] = map;
        }
        return body;
    }

    /// <summary>The origins as typed, one per line, without blanks.</summary>
    public static List<string> ParseOrigins(string? text)
        => (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
