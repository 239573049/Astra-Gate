using System.Net;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

public sealed class StatsQueriesTests
{
    private static (StatsQueries Stats, StubHandler Server, QueryClient Queries) Make(int tz = 480)
    {
        var server = new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/stats/summary" => (HttpStatusCode.OK, """{"range":"today","costUsd":6.36,"requests":50,"successRate":0.912,"inputTokens":1,"outputTokens":2,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0,"avgTtftMs":null}"""),
            "/api/requests" => (HttpStatusCode.OK, """{"items":[],"total":0,"page":1,"pageSize":20,"totalCostNanoUsd":0}"""),
            _ => (HttpStatusCode.OK, "[]"),
        });
        var queries = new QueryClient();
        return (new StatsQueries(new ApiClient("http://fixture", server), queries, tz), server, queries);
    }

    [Fact]
    public async Task Summary_asks_for_the_range_and_only_the_filters_that_are_set()
    {
        var (stats, server, _) = Make();
        var q = stats.Summary("7d", null, null);
        await q.RefetchAsync();
        Assert.Equal(50, q.Data!.Requests);
        Assert.Equal(6.36m, q.Data.CostUsd);
        Assert.Equal("/api/stats/summary?range=7d", server.Seen[0].Url);

        await stats.Summary("today", "codex", "tok 1").RefetchAsync();
        Assert.Equal("/api/stats/summary?range=today&client=codex&token=tok%201", server.Seen[1].Url);
    }

    [Fact]
    public async Task Series_and_heatmap_carry_the_users_utc_offset()
    {
        var (stats, server, _) = Make(tz: -300);
        await stats.Timeseries("30d", "day", "provider", "codex", null).RefetchAsync();
        await stats.Activity(365, null, "t").RefetchAsync();
        Assert.Equal("/api/stats/timeseries?range=30d&groupBy=day&by=provider&client=codex&tzOffset=-300", server.Seen[0].Url);
        Assert.Equal("/api/stats/activity-heatmap?days=365&token=t&tzOffset=-300", server.Seen[1].Url);
    }

    [Fact]
    public async Task Top_models_providers_and_requests_urls()
    {
        var (stats, server, _) = Make();
        await stats.TopModels("today", null, 20, "t").RefetchAsync();
        await stats.ProviderStats("7d", "t").RefetchAsync();
        await stats.Requests(new RequestListQuery("codex", "t", "2026-10-26T00:00:00.000Z", 1, 20)).RefetchAsync();
        Assert.Equal("/api/stats/top-models?range=today&limit=20&token=t", server.Seen[0].Url);
        Assert.Equal("/api/stats/providers?range=7d&token=t", server.Seen[1].Url);
        Assert.Equal("/api/requests?from=2026-10-26T00%3A00%3A00.000Z&client=codex&token=t&page=1&pageSize=20", server.Seen[2].Url);
    }

    [Fact]
    public void The_same_filters_share_one_cached_query_and_another_filter_is_another_query()
    {
        var (stats, _, _) = Make();
        Assert.Same(stats.Summary("today", null, null), stats.Summary("today", null, null));
        Assert.NotSame(stats.Summary("today", null, null), stats.Summary("7d", null, null));
        Assert.NotSame(stats.Summary("today", null, null), stats.Summary("today", "codex", null));
        Assert.NotSame(stats.Summary("today", null, "a"), stats.Summary("today", null, "b"));
    }

    [Fact]
    public async Task Refresh_prefixes_make_every_stats_and_request_query_stale()
    {
        var (stats, _, queries) = Make();
        var summary = stats.Summary("today", null, null);
        var series = stats.Timeseries("today", "hour", "provider", null, null);
        var requests = stats.Requests(new RequestListQuery(null, null, null, 1, 20));
        await Task.WhenAll(summary.RefetchAsync(), series.RefetchAsync(), requests.RefetchAsync());
        Assert.False(summary.IsStale);
        queries.Invalidate("stats");
        queries.Invalidate("requests");
        Assert.True(summary.IsStale);
        Assert.True(series.IsStale);
        Assert.True(requests.IsStale);
    }

    [Fact]
    public async Task A_token_is_fetched_by_id_and_a_missing_one_is_an_error_with_the_servers_message()
    {
        var server = new StubHandler(_ => (HttpStatusCode.NotFound, """{"error":"令牌不存在"}"""));
        var queries = new QueryClient();
        var q = new StatsQueries(new ApiClient("http://fixture", server), queries, 0).Token("tok/1");
        await q.RefetchAsync();
        Assert.True(q.IsError);
        Assert.Equal("令牌不存在", q.Error!.Message);
        Assert.Equal("/api/tokens/tok%2F1", server.Seen[0].Url);
        // Shares the ['tokens'] prefix with the list, so a token mutation refreshes it.
        Assert.True(q.Key.StartsWith(QueryKeys.Tokens));
    }

    [Fact]
    public void Typed_list_queries_sit_below_the_shared_prefixes()
    {
        var (_, server, queries) = Make();
        var lists = new ListQueries(new ApiClient("http://fixture", server), queries);
        Assert.True(lists.Providers().Key.StartsWith(QueryKeys.Providers));
        Assert.True(lists.Clients().Key.StartsWith(QueryKeys.Clients));
        Assert.True(lists.Accounts("p1").Key.StartsWith(QueryKeys.Providers));
    }
}
