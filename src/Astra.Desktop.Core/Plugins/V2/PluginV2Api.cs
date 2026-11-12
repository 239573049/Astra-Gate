using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>
/// v2 plugins over the local server's <c>/api/plugins/v2</c> (the web's <c>usePluginsV2</c> and friends): the list with
/// what the running plugins contribute (pages, slots, menus), permissions, settings, commands, runtime permission
/// requests, notifications and the v1 → v2 update reminder.
/// </summary>
public sealed class PluginV2Api
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly Func<string> _locale;

    public PluginV2Api(ApiClient api, QueryClient queries, Func<string> locale)
    {
        _api = api;
        _queries = queries;
        _locale = locale;
        List = queries.Query<PluginV2ListDto>("plugins-v2", async ct =>
                await api.GetAsync<PluginV2ListDto>("/api/plugins/v2" + ApiClient.Qs(("locale", _locale())), ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<PluginV2ListDto> { RefetchInterval = _ => TimeSpan.FromSeconds(15) });
        Successors = queries.Query<List<PluginSuccessorDto>>("plugins-v2-successors", async ct =>
                await api.GetAsync<List<PluginSuccessorDto>>("/api/plugins/v2/successors" + ApiClient.Qs(("locale", _locale())), ct) ?? [],
            new QueryOptions<List<PluginSuccessorDto>> { RefetchInterval = _ => TimeSpan.FromHours(6) });
    }

    /// <summary>The underlying client (view sessions use it).</summary>
    public ApiClient Client => _api;

    /// <summary>['plugins-v2']: the v2 plugins, their contributions and the requests waiting (polled every 15 s while shown).</summary>
    public Query<PluginV2ListDto> List { get; }

    /// <summary>['plugins-v2-successors']: v1 plugins the market has a v2 version of.</summary>
    public Query<List<PluginSuccessorDto>> Successors { get; }

    public Task<PluginV2DetailDto?> DetailAsync(string id, CancellationToken ct = default) =>
        _api.GetAsync<PluginV2DetailDto>($"/api/plugins/v2/{ApiClient.Enc(id)}" + ApiClient.Qs(("locale", _locale())), ct);

    public async Task GrantAsync(string id, IReadOnlyList<string> permissions, bool revoke = false, CancellationToken ct = default)
    {
        var body = new JsonObject { ["permissions"] = new JsonArray([.. permissions.Select(p => (JsonNode?)JsonValue.Create(p))]) };
        await _api.PostAsync<JsonObject, PluginV2ListDto>($"/api/plugins/v2/{ApiClient.Enc(id)}/{(revoke ? "revoke" : "grant")}" + ApiClient.Qs(("locale", _locale())), body, ct);
        Changed();
    }

    public async Task SaveSettingsAsync(string id, JsonObject values, JsonObject secrets, CancellationToken ct = default)
    {
        await _api.PutAsync<JsonObject, PluginV2DetailDto>($"/api/plugins/v2/{ApiClient.Enc(id)}/settings" + ApiClient.Qs(("locale", _locale())),
            new JsonObject { ["values"] = values, ["secrets"] = secrets }, ct);
        Changed();
    }

    public async Task<JsonNode?> RunCommandAsync(string plugin, string command, JsonNode? args, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<JsonObject, PluginV2CommandResultDto>($"/api/plugins/v2/{ApiClient.Enc(plugin)}/commands/{ApiClient.Enc(command)}",
            new JsonObject { ["args"] = args?.DeepClone() }, ct);
        return r?.Result;
    }

    public async Task AnswerRequestAsync(string requestId, bool allow, CancellationToken ct = default)
    {
        await _api.PostAsync($"/api/plugins/v2/requests/{ApiClient.Enc(requestId)}", new JsonObject { ["allow"] = allow }, ct);
        Changed();
    }

    public async Task<string?> UpgradeAsync(string spec, IReadOnlyList<string> grant, CancellationToken ct = default)
    {
        var job = await _api.PostAsync<JsonObject, JsonObject>("/api/plugins/v2/upgrade",
            new JsonObject { ["spec"] = spec, ["grant"] = new JsonArray([.. grant.Select(p => (JsonNode?)JsonValue.Create(p))]) }, ct);
        Changed();
        return job?["id"]?.GetValue<string>();
    }

    /// <summary>The component gallery's scenes (GET /api/plugins/v2/ui/scenes): id, component, state, the normalised tree.</summary>
    public Task<JsonArray?> ScenesAsync(CancellationToken ct = default) => _api.GetAsync<JsonArray>("/api/plugins/v2/ui/scenes", ct);

    /// <summary>Goes back to the v1 plugin <paramref name="id"/> replaced (within 30 days of the upgrade); the job's id.</summary>
    public async Task<string?> RollbackAsync(string id, CancellationToken ct = default)
    {
        var job = await _api.PostAsync<JsonObject, JsonObject>($"/api/plugins/v2/{ApiClient.Enc(id)}/rollback", new JsonObject(), ct);
        Changed();
        return job?["id"]?.GetValue<string>();
    }

    public async Task DismissSuccessorAsync(string spec, string version, CancellationToken ct = default)
    {
        await _api.PostAsync("/api/plugins/v2/successors/dismiss", new JsonObject { ["spec"] = spec, ["version"] = version }, ct);
        _queries.Invalidate("plugins-v2-successors");
    }

    public Task<List<PluginV2NotificationDto>?> NotificationsAsync(long after, CancellationToken ct = default) =>
        _api.GetAsync<List<PluginV2NotificationDto>>("/api/plugins/v2/notifications" + ApiClient.Qs(("after", after)), ct);

    private void Changed()
    {
        _queries.Invalidate("plugins-v2");
        _queries.Invalidate("plugins");
    }
}
