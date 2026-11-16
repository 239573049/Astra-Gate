using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Tools;

/// <summary>
/// Astra's built-in tools over the local server's <c>/api/tools</c> (linked update 34; the web's <c>useToolsSettings</c> and friends): the
/// search APIs with their keys (masks only ever come back), the switches and limits, the tool list. Every change answers with the new settings,
/// so each command refreshes the two queries.
/// </summary>
public sealed class ToolsApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public ToolsApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Settings = queries.Query<ToolsSettingsDto>("tools-settings", async ct => await api.GetAsync<ToolsSettingsDto>("/api/tools/settings", ct) ?? throw new ApiException(0, "empty response"));
        Tools = queries.Query<List<ToolInfoDto>>("tools", async ct => await api.GetAsync<List<ToolInfoDto>>("/api/tools", ct) ?? [],
            new QueryOptions<List<ToolInfoDto>> { RefetchInterval = _ => TimeSpan.FromSeconds(20) });
    }

    /// <summary>['tools-settings']: the search APIs (keys as masks), the switches, the fetch limits, the image model.</summary>
    public Query<ToolsSettingsDto> Settings { get; }

    /// <summary>['tools']: the built-in tools and the plugins', with why one is not available.</summary>
    public Query<List<ToolInfoDto>> Tools { get; }

    private static string Vendor(string vendor) => Uri.EscapeDataString(vendor);

    private async Task<ToolsSettingsDto> Send(Func<Task<ToolsSettingsDto?>> call)
    {
        var result = await call() ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("tools-settings");
        _queries.Invalidate("tools");
        return result;
    }

    /// <summary>PUT /api/tools/settings: the sections present in <paramref name="body"/> (<c>search</c>, <c>fetch</c>, <c>image</c>).</summary>
    public Task<ToolsSettingsDto> UpdateAsync(JsonObject body, CancellationToken ct = default) =>
        Send(() => _api.PutAsync<JsonObject, ToolsSettingsDto>("/api/tools/settings", body, ct));

    /// <summary>POST /api/tools/search/apis: adds a search API (SearXNG needs its address).</summary>
    public Task<ToolsSettingsDto> AddApiAsync(string vendor, string? url, CancellationToken ct = default) =>
        Send(() => _api.PostAsync<JsonObject, ToolsSettingsDto>("/api/tools/search/apis", JsonPatch.Of(("vendor", vendor), ("url", string.IsNullOrWhiteSpace(url) ? null : url.Trim())), ct));

    /// <summary>PUT /api/tools/search/apis: the APIs in a new order.</summary>
    public Task<ToolsSettingsDto> ReorderApisAsync(IEnumerable<string> order, CancellationToken ct = default) =>
        Send(() => _api.PutAsync<JsonObject, ToolsSettingsDto>("/api/tools/search/apis", new JsonObject { ["order"] = Array(order) }, ct));

    /// <summary>PATCH /api/tools/search/apis/{vendor}: the fields present in <paramref name="body"/> (<c>enabled</c>, <c>pick</c>, <c>url</c>).</summary>
    public Task<ToolsSettingsDto> UpdateApiAsync(string vendor, JsonObject body, CancellationToken ct = default) =>
        Send(() => _api.PatchAsync<JsonObject, ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}", body, ct));

    /// <summary>DELETE /api/tools/search/apis/{vendor}, with its keys.</summary>
    public Task<ToolsSettingsDto> DeleteApiAsync(string vendor, CancellationToken ct = default) =>
        Send(() => _api.DeleteAsync<ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}", ct));

    /// <summary>POST …/keys: adds a key (the server stores it protected and never returns it).</summary>
    public Task<ToolsSettingsDto> AddKeyAsync(string vendor, string key, CancellationToken ct = default) =>
        Send(() => _api.PostAsync<JsonObject, ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}/keys", JsonPatch.Of(("key", key.Trim())), ct));

    /// <summary>PATCH …/keys/{id}: switches one key on or off.</summary>
    public Task<ToolsSettingsDto> SetKeyEnabledAsync(string vendor, string id, bool enabled, CancellationToken ct = default) =>
        Send(() => _api.PatchAsync<JsonObject, ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}/keys/{Uri.EscapeDataString(id)}", JsonPatch.Of(("enabled", enabled)), ct));

    /// <summary>DELETE …/keys/{id}.</summary>
    public Task<ToolsSettingsDto> DeleteKeyAsync(string vendor, string id, CancellationToken ct = default) =>
        Send(() => _api.DeleteAsync<ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}/keys/{Uri.EscapeDataString(id)}", ct));

    /// <summary>PUT …/keys: the keys in a new order.</summary>
    public Task<ToolsSettingsDto> ReorderKeysAsync(string vendor, IEnumerable<string> order, CancellationToken ct = default) =>
        Send(() => _api.PutAsync<JsonObject, ToolsSettingsDto>($"/api/tools/search/apis/{Vendor(vendor)}/keys", new JsonObject { ["order"] = Array(order) }, ct));

    /// <summary>POST …/test: asks one API alone (this machine only). A failure to search is a result (<c>Ok = false</c>), not an exception.</summary>
    public async Task<SearchTestDto> TestAsync(string vendor, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<JsonObject, SearchTestDto>($"/api/tools/search/apis/{Vendor(vendor)}/test", new JsonObject(), ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("tools-settings");
        return result;
    }

    private static JsonArray Array(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add((JsonNode?)JsonValue.Create(item));
        return array;
    }

    /// <summary>The ids with <paramref name="id"/> one step up (-1) or down (1); null when it cannot move.</summary>
    public static List<string>? Moved(IReadOnlyList<string> ids, string id, int by)
    {
        var i = ids.ToList().IndexOf(id);
        var j = i + by;
        if (i < 0 || j < 0 || j >= ids.Count) return null;
        var next = ids.ToList();
        (next[i], next[j]) = (next[j], next[i]);
        return next;
    }
}
