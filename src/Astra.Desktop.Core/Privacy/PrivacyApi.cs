using Astra.Api.Contracts;
using Astra.Core.Privacy;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Privacy;

/// <summary>
/// The queries and writes of the Privacy page (web/src/api/hooks.ts: <c>usePrivacy</c>, <c>useUpdatePrivacy</c>, <c>usePrivacyDryRun</c>,
/// <c>usePrivacyEvents</c>, <c>usePrivacyEventStats</c>), over the shared cache with the web's keys.
/// </summary>
public sealed class PrivacyApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public PrivacyApi(ApiClient api, QueryClient queries, ClientsStore clients)
    {
        _api = api;
        _queries = queries;
        Policy = queries.Query<PrivacyPolicyDto>("privacy", async ct => await api.GetAsync<PrivacyPolicyDto>("/api/privacy", ct)
            ?? throw new ApiException(0, "empty response"));
        Clients = clients.Clients;
    }

    /// <summary>['privacy']: the policy and the built-in rules.</summary>
    public Query<PrivacyPolicyDto> Policy { get; }

    /// <summary>['clients'], raw: the log's client filter lists them.</summary>
    public Query<System.Text.Json.Nodes.JsonArray> Clients { get; }

    /// <summary>['privacy-event-stats', range], refreshed every 15 s.</summary>
    public Query<PrivacyEventStatsDto> Stats(string range)
        => _queries.Query<PrivacyEventStatsDto>(QueryKey.Of("privacy-event-stats", range),
            async ct => await _api.GetAsync<PrivacyEventStatsDto>("/api/privacy/events/stats" + ApiClient.Qs(("range", range)), ct)
                ?? throw new ApiException(0, "empty response"),
            new QueryOptions<PrivacyEventStatsDto> { RefetchInterval = _ => TimeSpan.FromSeconds(15) });

    /// <summary>The interception log page for a filter state; the first page also refreshes every 5 s.</summary>
    public Query<PageDto<PrivacyEventDto>> Events(PrivacyEventFilters filters)
    {
        var path = filters.Path;
        var live = filters.Live;
        return _queries.Query<PageDto<PrivacyEventDto>>(filters.Key,
            async ct => await _api.GetAsync<PageDto<PrivacyEventDto>>(path, ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<PageDto<PrivacyEventDto>> { RefetchInterval = _ => live ? TimeSpan.FromSeconds(5) : null });
    }

    /// <summary>PUT /api/privacy with the whole policy; the saved settings replace the cached ones (the rules stay).</summary>
    public async Task<PrivacySettings> UpdateAsync(PrivacySettings settings, CancellationToken ct = default)
    {
        var saved = await _api.PutAsync<PrivacySettings, PrivacySettings>("/api/privacy", settings, ct) ?? throw new ApiException(0, "empty response");
        if (Policy.Data is { } current) Policy.SetData(current with { Settings = saved });
        return saved;
    }

    /// <summary>POST /api/privacy/dry-run: runs a text through the current policy as if the guard were on; touches no real request.</summary>
    public async Task<PrivacyDryRunResultDto> DryRunAsync(string text, CancellationToken ct = default)
        => await _api.PostAsync<DryRunRequest, PrivacyDryRunResultDto>("/api/privacy/dry-run", new DryRunRequest(text, null, null), ct)
            ?? throw new ApiException(0, "empty response");
}
