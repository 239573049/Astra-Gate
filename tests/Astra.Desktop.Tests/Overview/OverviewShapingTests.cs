using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

public sealed class OverviewShapingTests
{
    private static StatsSummaryDto Summary(long requests = 50) =>
        new("today", 6.36m, requests, 0.912, 1_201_000, 149_000, 714_000, 71_000, 17_880, 1180);

    [Fact]
    public void The_six_cards_of_the_overview()
    {
        var cards = OverviewShaping.Cards(Summary(), "RANGE", Fakes.T);
        Assert.Equal(6, cards.Count);
        Assert.Equal(new StatCard("overview.totalCost", "$6.36", "RANGE"), cards[0]);
        Assert.Equal(new StatCard("overview.totalRequests", "50", "RANGE"), cards[1]);
        Assert.Equal("1.4M", cards[2].Value);
        Assert.Equal("overview.tokensContext(input=1.2M,output=149K)", cards[2].Context);
        Assert.Equal("usage.hitRate", cards[3].Label);
        Assert.Equal("59.5%", cards[3].Value);                      // 714000 / 1201000
        Assert.Equal("usage.cacheAmounts(read=714K,write=71K)", cards[3].Context);
        Assert.Equal("91.2%", cards[4].Value);
        Assert.Equal("1180 ms", cards[5].Value);
        Assert.Equal("RANGE", cards[5].Context);
    }

    [Fact]
    public void A_range_without_requests_shows_dashes_and_no_data()
    {
        var empty = new StatsSummaryDto("today", 0m, 0, 0, 0, 0, 0, 0, 0, null);
        var cards = OverviewShaping.Cards(empty, "R", Fakes.T);
        Assert.Equal("$0", cards[0].Value);
        Assert.Equal("0%", cards[3].Value);          // no input: the web shows 0%, not a dash
        Assert.Equal("—", cards[4].Value);
        Assert.Equal("—", cards[5].Value);
        Assert.Equal("common.noData", cards[5].Context);
    }

    [Fact]
    public void The_token_detail_has_five_cards_with_the_ttft_under_the_success_rate()
    {
        var cards = OverviewShaping.TokenCards(Summary(), "R", Fakes.T);
        Assert.Equal(5, cards.Count);
        Assert.Equal("R", cards[3].Context);
        Assert.Equal("1180 ms", cards[4].Context);
    }

    [Fact]
    public void Trend_fills_the_range_scales_and_labels_the_bars()
    {
        var series = new[]
        {
            new TimeseriesPointDto("2026-10-26T01:00", "p", 0.5m, 4, 10), new TimeseriesPointDto("2026-10-26T01:00", "q", 0.25m, 2, 5),
        };
        var bars = OverviewShaping.Trend(series, TrendMeasure.Requests, StatsRange.Today, Fakes.Utc);
        Assert.Equal(24, bars.Count);
        Assert.Equal(new TrendBar("2026-10-26T01:00", "10/26 01:00", "01:00", 600), bars[1]);
        Assert.Equal(0, bars[0].Value);
        var cost = OverviewShaping.Trend(series, TrendMeasure.CostUsd, StatsRange.Today, Fakes.Utc);
        Assert.Equal(750_000, cost[1].Value);

        var plain = OverviewShaping.RequestsTrend(series, StatsRange.Days7, Fakes.Utc);
        Assert.Equal(7, plain.Count);
        Assert.All(plain, b => Assert.Equal(0, b.Value));
        Assert.Equal(6, OverviewShaping.RequestsTrend(series, StatsRange.Today, Fakes.Utc)[1].Value);
    }

    [Theory]
    [InlineData(TrendMeasure.Requests, 188, "1.88")]
    [InlineData(TrendMeasure.Requests, 46, "0.46")]
    [InlineData(TrendMeasure.Requests, 600, "6")]
    [InlineData(TrendMeasure.Tokens, 1_400_000, "1.4M")]
    [InlineData(TrendMeasure.Tokens, 950, "950")]
    [InlineData(TrendMeasure.CostUsd, 6_360_000, "$6.4")]
    [InlineData(TrendMeasure.CostUsd, 12_000, "$0.012")]
    [InlineData(TrendMeasure.CostUsd, 2_500_000_000, "$2.5K")]
    [InlineData(TrendMeasure.CostUsd, 0, "$0")]
    public void FormatMeasure_undoes_the_chart_scale(TrendMeasure measure, double scaled, string expected)
        => Assert.Equal(expected, OverviewShaping.FormatMeasure(scaled, measure));

    [Fact]
    public void Empty_state_by_filter()
    {
        Assert.Equal(OverviewEmpty.None, OverviewShaping.EmptyOf(null, null, null));
        Assert.Equal(OverviewEmpty.None, OverviewShaping.EmptyOf(Summary(), "codex", null));
        var empty = new StatsSummaryDto("today", 0m, 0, 0, 0, 0, 0, 0, 0, null);
        Assert.Equal(OverviewEmpty.Everything, OverviewShaping.EmptyOf(empty, null, null));
        Assert.Equal(OverviewEmpty.Token, OverviewShaping.EmptyOf(empty, null, "tok"));
        Assert.Equal(OverviewEmpty.Client, OverviewShaping.EmptyOf(empty, "codex", "tok"));   // the client filter wins, like the web
    }

    [Fact]
    public void Activity_summary_names_the_window_the_active_days_and_the_peak()
    {
        var data = new[] { new DailyActivityDto("2026-10-01", 12), new DailyActivityDto("2026-10-02", 157) };
        var text = OverviewShaping.ActivitySummary(data, 365, Fakes.Utc, Fakes.T);
        Assert.Equal("overview.activitySummary(from=2025-10-27,to=2026-11-01,active=2,days=365) · overview.activityPeak(count=157)", text);
        Assert.Contains("count=0", OverviewShaping.ActivitySummary([], 365, Fakes.Utc, Fakes.T));
    }

    private static RequestSummaryDto Request(string id, string? client, string? token, string status = "success", string usage = "reported", long cost = 7_520_000)
        => new(id, Fakes.Noon, client, "prov", "OpenAI", "openai-chat", null, false, "gpt-5", null, null, null, false, status, 200, 100, 900, 62.4,
            1000, 200, 500, 0, 0, cost, usage, null, null, null, token, null, null, null, false, null);

    [Fact]
    public void Log_rows_label_the_client_and_hide_usage_that_was_not_reported()
    {
        var rows = OverviewShaping.LogRows([Request("a", "claude-code", null), Request("b", null, "tok"), Request("c", null, null), Request("d", "plugin-x", null)], Fakes.T);
        Assert.Equal(["Claude Code", "requests.direct", "—", "plugin-x"], rows.Select(r => r.Client));
        Assert.Equal(62.4, rows[0].Speed);
        Assert.Equal(7_520_000, rows[0].CostNanos);
        Assert.Equal("1.2K", OverviewShaping.UsageCell(rows[0].Request, "1.2K"));
        Assert.Equal("—", OverviewShaping.UsageCell(Request("e", null, null, usage: "missing"), "1.2K"));
        Assert.Equal(-1, OverviewShaping.LogRows([Request("f", null, null) with { OutputTps = null }], Fakes.T)[0].Speed);
    }

    [Fact]
    public void Failure_tone_of_a_status()
    {
        Assert.Null(OverviewShaping.FailureTone("success"));
        Assert.Equal("orange", OverviewShaping.FailureTone("client_cancelled"));
        Assert.Equal("red", OverviewShaping.FailureTone("upstream_error"));
    }

    [Fact]
    public void Model_rows_carry_the_cache_hit_ratio_or_minus_one_without_input()
    {
        var rows = OverviewShaping.ModelRows([new TopModelDto("gpt-5", 3m, 10, 1000, 1000, 250), new TopModelDto("none", 1m, 1, 5, 0, 0)]);
        Assert.Equal(0.25, rows[0].CacheHit);
        Assert.Equal(-1, rows[1].CacheHit);
        Assert.Equal(0.75, rows[0].Share, 10);
    }

    [Fact]
    public void Usage_math_matches_the_web()
    {
        Assert.Null(UsageMath.CacheHitRatio(5, 0));
        Assert.Null(UsageMath.CacheHitRatio(-1, 10));
        Assert.Equal(1, UsageMath.CacheHitRatio(50, 10));          // capped
        Assert.Equal(0.5, UsageMath.CacheHitRatio(5, 10));
        var split = UsageMath.Split(1000, 300, 600, 100, 50, "reported");
        Assert.Equal((300L, 250L, 0.6), (split.UncachedInput, split.RegularOutput, split.HitRatio ?? 0));
        Assert.Null(UsageMath.Split(1000, 300, 600, 100, 50, "estimated").HitRatio);
        Assert.False(UsageMath.Split(1, 1, 0, 0, 0, "missing").Reported);
        Assert.Equal(0, UsageMath.Split(100, 100, 400, 400, 900, "reported").UncachedInput);   // never negative
    }

    [Fact]
    public void Token_type_labels()
    {
        Assert.Equal("usage.cacheRead", UsageLabels.TokenTypeKey("cache_read"));
        Assert.Null(UsageLabels.TokenTypeKey("mystery"));
        Assert.Equal("green", UsageLabels.TokenTypeTone("cache_read"));
        Assert.Equal("orange", UsageLabels.TokenTypeTone("cache_write_1h"));
        Assert.Null(UsageLabels.TokenTypeTone("input"));
    }
}
