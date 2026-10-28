using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Plugins;

/// <summary>
/// The Plugins page's reads and writes: the react-query hooks of web/src/api/hooks.ts (<c>usePlugins</c>, <c>usePluginHost</c>,
/// <c>usePluginMarket</c>, <c>usePluginSearch</c>, <c>usePluginJob</c>, <c>useMagpieImportPreview</c> and the plugin mutations)
/// over the shared <see cref="QueryClient"/>, with the same keys, polling and stale times. A mutation, like web
/// <c>usePluginMutation</c>, refreshes plugins, host, clients and providers once it succeeded.
/// </summary>
public sealed class PluginsApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the API over the shared client and cache.</summary>
    public PluginsApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Plugins = queries.Polling(PluginsKey, async ct => await api.GetAsync<PluginsDto>("/api/plugins", ct) ?? throw Empty("plugins"), TimeSpan.FromSeconds(10));
        Host = queries.Polling(HostKey, async ct => await api.GetAsync<PluginHostStatusDto>("/api/plugins/host", ct) ?? throw Empty("plugin host"), TimeSpan.FromSeconds(15));
        Market = queries.Query(MarketKey, async ct => await api.GetAsync<List<MarketListingDto>>("/api/plugins/market", ct) ?? [],
            new QueryOptions<List<MarketListingDto>> { StaleTime = TimeSpan.FromMinutes(10) });
        MagpiePreview = queries.Query(MagpieKey, async ct => await api.GetAsync<MagpieImportPreviewDto>("/api/plugins/import-magpie", ct) ?? throw Empty("magpie import"),
            new QueryOptions<MagpieImportPreviewDto> { StaleTime = TimeSpan.FromMinutes(1) });
    }

    /// <summary>['plugins']</summary>
    public static readonly QueryKey PluginsKey = "plugins";

    /// <summary>['plugin-host']</summary>
    public static readonly QueryKey HostKey = "plugin-host";

    /// <summary>['plugin-market']</summary>
    public static readonly QueryKey MarketKey = "plugin-market";

    /// <summary>['magpie-import']</summary>
    public static readonly QueryKey MagpieKey = "magpie-import";

    /// <summary>Installed plugins, their order, Bun's state; polled every 10 s while observed.</summary>
    public Query<PluginsDto> Plugins { get; }

    /// <summary>The plugin host; polled every 15 s while observed. Observe it only when something needs it (<see cref="Query{T}.Enabled"/>).</summary>
    public Query<PluginHostStatusDto> Host { get; }

    /// <summary>The market's registry; kept for ten minutes.</summary>
    public Query<List<MarketListingDto>> Market { get; }

    /// <summary>What importing from a magpie install on this machine would bring; kept for a minute.</summary>
    public Query<MagpieImportPreviewDto> MagpiePreview { get; }

    /// <summary>A search of the registry, npm and GitHub (key ['plugin-search', q]); kept ten minutes. Only ask for two or more characters.</summary>
    public Query<List<MarketSearchResultDto>> Search(string q) =>
        _queries.Query(new QueryKey("plugin-search", q), async ct => await _api.GetAsync<List<MarketSearchResultDto>>("/api/plugins/search" + ApiClient.Qs(("q", q)), ct) ?? [],
            new QueryOptions<List<MarketSearchResultDto>> { StaleTime = TimeSpan.FromMinutes(10) });

    /// <summary>A plugin job, polled each second while it runs (key ['plugin-job', id]).</summary>
    public Query<PluginJobDto> Job(string id) =>
        _queries.Query(new QueryKey("plugin-job", id), async ct => await _api.GetAsync<PluginJobDto>("/api/plugins/jobs/" + ApiClient.Enc(id), ct) ?? throw Empty("plugin job"),
            new QueryOptions<PluginJobDto> { StaleTime = TimeSpan.Zero, RefetchInterval = job => PluginJobTexts.IsRunning(job) ? PluginJobTexts.PollInterval : null });

    /// <summary>Installs a plugin (an npm package, <c>github:owner/repo</c> or a path). Returns the job to follow.</summary>
    public Task<PluginJobDto> AddAsync(string spec, string? source = null, CancellationToken ct = default)
        => Mutate(() => _api.PostAsync<PluginAddInput, PluginJobDto>("/api/plugins", new PluginAddInput(spec, source), ct), "add");

    /// <summary>Updates one plugin, or every one when <paramref name="spec"/> is null. Returns the job.</summary>
    public Task<PluginJobDto> UpdateAsync(string? spec, CancellationToken ct = default)
        => Mutate(() => _api.PostAsync<PluginUpdateInput, PluginJobDto>("/api/plugins/update", new PluginUpdateInput(spec), ct), "update");

    /// <summary>Removes a plugin. Returns the job.</summary>
    public Task<PluginJobDto> RemoveAsync(string spec, CancellationToken ct = default)
        => Mutate(() => _api.DeleteAsync<PluginJobDto>("/api/plugins" + ApiClient.Qs(("spec", spec)), ct), "remove");

    /// <summary>Switches a plugin on or off.</summary>
    public Task<PluginsDto> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default)
        => Patch(spec, PluginOptions.EnabledBody(enabled), ct);

    /// <summary>Sets a plugin's options; null clears them.</summary>
    public Task<PluginsDto> SetOptionsAsync(string spec, JsonObject? options, CancellationToken ct = default)
        => Patch(spec, PluginOptions.PatchBody(options), ct);

    /// <summary>Puts the plugins in the given order (the middleware chain's order).</summary>
    public Task<PluginsDto> ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default)
        => Mutate(() => _api.PutAsync<PluginOrderInput, PluginsDto>("/api/plugins/order", new PluginOrderInput([.. specs]), ct), "reorder");

    /// <summary>Has <paramref name="spec"/> serve <paramref name="provider"/>, which another plugin serves too.</summary>
    public Task<PluginsDto> PreferAsync(string spec, string provider, CancellationToken ct = default)
        => Mutate(() => _api.PostAsync<PluginPreferInput, PluginsDto>("/api/plugins/prefer", new PluginPreferInput(spec, provider), ct), "prefer");

    /// <summary>Downloads Bun. Returns the job.</summary>
    public Task<PluginJobDto> InstallBunAsync(CancellationToken ct = default)
        => Mutate(() => _api.PostAsync<PluginJobDto>("/api/plugins/runtime/install", ct), "install Bun");

    /// <summary>Imports magpie's plugins, and with <paramref name="signIns"/> its sign-ins. Returns the job.</summary>
    public Task<PluginJobDto> ImportMagpieAsync(bool signIns, CancellationToken ct = default)
        => Mutate(() => _api.PostAsync<MagpieImportInput, PluginJobDto>("/api/plugins/import-magpie", new MagpieImportInput(signIns), ct), "import");

    private Task<PluginsDto> Patch(string spec, JsonObject body, CancellationToken ct)
        => Mutate(() => _api.PatchAsync<JsonObject, PluginsDto>("/api/plugins" + ApiClient.Qs(("spec", spec)), body, ct), "patch");

    private async Task<T> Mutate<T>(Func<Task<T?>> call, string what)
    {
        var result = await call().ConfigureAwait(false) ?? throw Empty(what);
        // web usePluginMutation: a plugin change can add providers and clients, so those lists are read again too.
        _queries.Invalidate("plugins");
        _queries.Invalidate("plugin-host");
        _queries.Invalidate("clients");
        _queries.Invalidate("providers");
        return result;
    }

    private static ApiException Empty(string what) => new(0, "empty " + what + " response");
}
