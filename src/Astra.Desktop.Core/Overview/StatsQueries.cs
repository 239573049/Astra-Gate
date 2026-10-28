using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Overview;

/// <summary>The filter of a request list query (web <c>RequestQuery</c>), the part the overview and token pages use.</summary>
/// <param name="Client">A client kind, or null for every client.</param>
/// <param name="Token">A token id, or null for every token.</param>
/// <param name="From">Start of the window, ISO-8601 UTC, or null.</param>
/// <param name="Page">1-based page.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record RequestListQuery(string? Client, string? Token, string? From, int Page, int PageSize);

/// <summary>
/// The stats and request queries of the overview and a token's detail page (web <c>useStatsSummary</c>, <c>useTimeseries</c>,
/// <c>useTopModels</c>, <c>useProviderStats</c>, <c>useActivityHeatmap</c>, <c>useRequests</c>, <c>useToken</c> in
/// <c>web/src/api/hooks.ts</c>). Keys carry every parameter like the web's, so a filter change is a different cached query; the
/// "stats" and "requests" prefixes are what refresh invalidates. <see cref="TzOffsetMinutes"/> is the user's UTC offset (the
/// server buckets days in it).
/// </summary>
public sealed class StatsQueries
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the query factory over the app's client and cache.</summary>
    public StatsQueries(ApiClient api, QueryClient queries, int tzOffsetMinutes)
    {
        _api = api;
        _queries = queries;
        TzOffsetMinutes = tzOffsetMinutes;
    }

    /// <summary>Minutes east of UTC the server buckets days by.</summary>
    public int TzOffsetMinutes { get; }

    /// <summary>The totals of a range (GET /api/stats/summary).</summary>
    public Query<StatsSummaryDto> Summary(string range, string? client, string? token)
        => _queries.Query(new QueryKey("stats", "summary", range, client ?? "", token ?? ""), async ct =>
            await _api.GetAsync<StatsSummaryDto>("/api/stats/summary" + ApiClient.Qs(("range", range), ("client", client), ("token", token)), ct)
            ?? throw new ApiException(0, "empty stats response"));

    /// <summary>Per-bucket series of a range, grouped by model, provider, client or token (GET /api/stats/timeseries).</summary>
    public Query<List<TimeseriesPointDto>> Timeseries(string range, string groupBy, string by, string? client, string? token)
        => _queries.Query(new QueryKey("stats", "timeseries", range, groupBy, by, client ?? "", token ?? "", TzOffsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), async ct =>
            await _api.GetAsync<List<TimeseriesPointDto>>("/api/stats/timeseries" + ApiClient.Qs(
                ("range", range), ("groupBy", groupBy), ("by", by), ("client", client), ("token", token), ("tzOffset", TzOffsetMinutes)), ct) ?? []);

    /// <summary>The models with the most spend in a range (GET /api/stats/top-models).</summary>
    public Query<List<TopModelDto>> TopModels(string range, string? client, int limit, string? token)
        => _queries.Query(new QueryKey("stats", "top", range, client ?? "", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), token ?? ""), async ct =>
            await _api.GetAsync<List<TopModelDto>>("/api/stats/top-models" + ApiClient.Qs(("range", range), ("limit", limit), ("client", client), ("token", token)), ct) ?? []);

    /// <summary>Per provider: answered requests, success, cost, TTFT and pool failovers (GET /api/stats/providers).</summary>
    public Query<List<ProviderStatDto>> ProviderStats(string range, string? token, string? client = null)
        => _queries.Query(new QueryKey("stats", "providers", range, client ?? "", token ?? ""), async ct =>
            await _api.GetAsync<List<ProviderStatDto>>("/api/stats/providers" + ApiClient.Qs(("range", range), ("client", client), ("token", token)), ct) ?? []);

    /// <summary>Requests per local day for the heatmap (GET /api/stats/activity-heatmap).</summary>
    public Query<List<DailyActivityDto>> Activity(int days, string? client, string? token)
        => _queries.Query(new QueryKey("stats", "activity", days.ToString(System.Globalization.CultureInfo.InvariantCulture), client ?? "", token ?? "", TzOffsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), async ct =>
            await _api.GetAsync<List<DailyActivityDto>>("/api/stats/activity-heatmap" + ApiClient.Qs(("days", days), ("client", client), ("token", token), ("tzOffset", TzOffsetMinutes)), ct) ?? []);

    /// <summary>A page of the request log (GET /api/requests); key prefix "requests".</summary>
    public Query<RequestPageDto> Requests(RequestListQuery q)
        => _queries.Query(new QueryKey("requests", q.Client ?? "", q.Token ?? "", q.From ?? "", q.Page.ToString(System.Globalization.CultureInfo.InvariantCulture), q.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), async ct =>
            await _api.GetAsync<RequestPageDto>("/api/requests" + ApiClient.Qs(
                ("from", q.From), ("client", q.Client), ("token", q.Token), ("page", q.Page), ("pageSize", q.PageSize)), ct)
            ?? throw new ApiException(0, "empty requests response"));

    /// <summary>One token with its pool members' live health, refreshed every 10 s while observed (GET /api/tokens/{id}); key ['tokens', id].</summary>
    public Query<TokenDto> Token(string id)
        => _queries.Polling(new QueryKey("tokens", id), async ct =>
            await _api.GetAsync<TokenDto>("/api/tokens/" + ApiClient.Enc(id), ct) ?? throw new ApiException(0, "empty token response"),
            TimeSpan.FromSeconds(10));
}

/// <summary>
/// Typed reads of lists that the shared stores still hold as raw JSON (Providers, Clients — WP0 skeleton), under keys of their
/// own below the shared prefix so a refresh of "providers" / "clients" invalidates them too. When the stores are typed this can
/// shrink to a forward.
/// </summary>
public sealed class ListQueries
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the factory.</summary>
    public ListQueries(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
    }

    /// <summary>The providers (GET /api/providers).</summary>
    public Query<List<ProviderDto>> Providers()
        => _queries.Query(new QueryKey("providers", "typed"), async ct => await _api.GetAsync<List<ProviderDto>>("/api/providers", ct) ?? []);

    /// <summary>The clients (GET /api/clients).</summary>
    public Query<List<ClientInfoDto>> Clients()
        => _queries.Query(new QueryKey("clients", "typed"), async ct => await _api.GetAsync<List<ClientInfoDto>>("/api/clients", ct) ?? []);

    /// <summary>The accounts of a provider (GET /api/providers/{id}/accounts); key ['providers', id, 'accounts'].</summary>
    public Query<List<ProviderAccountDto>> Accounts(string providerId)
        => _queries.Query(new QueryKey("providers", providerId, "accounts"), async ct =>
            await _api.GetAsync<List<ProviderAccountDto>>("/api/providers/" + ApiClient.Enc(providerId) + "/accounts", ct) ?? []);

    /// <summary>The remote-access state, polled like the web's <c>useTunnel</c>: 1.5 s while busy, 15 s when on, 60 s when off (GET /api/tunnel).</summary>
    public Query<TunnelStatusDto> Tunnel()
        => _queries.Query(new QueryKey("tunnel"), async ct => await _api.GetAsync<TunnelStatusDto>("/api/tunnel", ct) ?? throw new ApiException(0, "empty tunnel response"),
            new QueryOptions<TunnelStatusDto>
            {
                RefetchInterval = s => s is null ? TimeSpan.FromSeconds(60)
                    : s.State is "downloading" or "starting" or "reconnecting" ? TimeSpan.FromSeconds(1.5) : s.Enabled ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60),
            });
}
