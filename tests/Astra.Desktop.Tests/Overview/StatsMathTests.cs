using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

public sealed class StatsMathTests
{
    private static TimeseriesPointDto Point(string bucket, string key, decimal cost, long requests, long tokens) => new(bucket, key, cost, requests, tokens);

    [Fact]
    public void BucketTotals_sums_every_series_per_bucket_oldest_first()
    {
        var points = new[]
        {
            Point("2026-10-26T11:00", "a", 0.5m, 3, 100), Point("2026-10-26T10:00", "a", 0.25m, 1, 10), Point("2026-10-26T11:00", "b", 0.5m, 2, 50),
        };
        var requests = StatsMath.BucketTotals(points, TrendMeasure.Requests);
        Assert.Equal(["2026-10-26T10:00", "2026-10-26T11:00"], requests.Select(t => t.Bucket));
        Assert.Equal([1m, 5m], requests.Select(t => t.Value));
        Assert.Equal([0.25m, 1.0m], StatsMath.BucketTotals(points, TrendMeasure.CostUsd).Select(t => t.Value));
        Assert.Equal([10m, 150m], StatsMath.BucketTotals(points, TrendMeasure.Tokens).Select(t => t.Value));
    }

    [Fact]
    public void Cost_totals_stay_exact_decimals()
        => Assert.Equal(0.3m, StatsMath.BucketTotals([Point("d", "a", 0.1m, 0, 0), Point("d", "b", 0.2m, 0, 0)], TrendMeasure.CostUsd)[0].Value);

    [Fact]
    public void RangeBuckets_today_is_the_24_local_hours()
    {
        var buckets = StatsMath.RangeBuckets(StatsRange.Today, Fakes.Utc);
        Assert.Equal(24, buckets.Count);
        Assert.Equal("2026-10-26T00:00", buckets[0]);
        Assert.Equal("2026-10-26T23:00", buckets[23]);
    }

    [Fact]
    public void RangeBuckets_of_days_end_today_and_all_is_open_ended()
    {
        var week = StatsMath.RangeBuckets(StatsRange.Days7, Fakes.Utc);
        Assert.Equal(7, week.Count);
        Assert.Equal("2026-10-20", week[0]);
        Assert.Equal("2026-10-26", week[6]);
        Assert.Equal(90, StatsMath.RangeBuckets(StatsRange.Days90, Fakes.Utc).Count);
        Assert.Empty(StatsMath.RangeBuckets("all", Fakes.Utc));
        Assert.Null(StatsMath.RangeStart("all", Fakes.Utc));
        Assert.Equal(new DateTime(2026, 10, 26), StatsMath.RangeStart(StatsRange.Today, Fakes.Utc));
        Assert.Equal(new DateTime(2026, 9, 27), StatsMath.RangeStart(StatsRange.Days30, Fakes.Utc));
    }

    [Fact]
    public void The_wall_clock_follows_the_zone()
    {
        // 23:30 UTC is already the next day in UTC+8.
        var late = new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.Zero);
        var local = LocalTime.At(late, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-27", local.Day);
        Assert.Equal(480, local.OffsetMinutes);
        Assert.Equal("2026-10-27T00:00", StatsMath.RangeBuckets(StatsRange.Today, local)[0]);
    }

    [Fact]
    public void FillBuckets_puts_totals_on_a_continuous_axis()
    {
        var filled = StatsMath.FillBuckets([new("2026-10-21", 4m)], ["2026-10-20", "2026-10-21", "2026-10-22"]);
        Assert.Equal([0m, 4m, 0m], filled.Select(t => t.Value));
        // No expected buckets (range "all"): the totals as they are.
        var totals = new List<BucketTotal> { new("x", 1m) };
        Assert.Same(totals, StatsMath.FillBuckets(totals, []));
    }

    [Fact]
    public void Bucket_labels()
    {
        Assert.Equal("14:00", StatsMath.BucketAxisLabel("2026-10-07T14:00"));
        Assert.Equal("10/07", StatsMath.BucketAxisLabel("2026-10-07"));
        Assert.Equal("10/07 14:00", StatsMath.BucketFullLabel("2026-10-07T14:00"));
        Assert.Equal("10/07", StatsMath.BucketFullLabel("2026-10-07"));
    }

    [Fact]
    public void Breakdown_shares_are_of_cost_or_of_requests_when_nothing_was_billed()
    {
        var billed = StatsMath.BreakdownRows([Point("b1", "openai", 3m, 10, 100), Point("b2", "anthropic", 1m, 90, 400), Point("b3", "openai", 0m, 5, 5)]);
        Assert.Equal(["openai", "anthropic"], billed.Select(r => r.Key));
        Assert.Equal(15, billed[0].Requests);
        Assert.Equal(105, billed[0].Tokens);
        Assert.Equal(0.75, billed[0].Share, 10);
        Assert.Equal(0.25, billed[1].Share, 10);

        var free = StatsMath.BreakdownRows([Point("b", "x", 0m, 1, 0), Point("b", "y", 0m, 3, 0)]);
        Assert.Equal([0.25, 0.75], free.Select(r => r.Share));

        Assert.Empty(StatsMath.BreakdownRows(Array.Empty<TimeseriesPointDto>()));
        Assert.Equal(0, StatsMath.BreakdownRows([Point("b", "x", 0m, 0, 0)])[0].Share);
    }

    [Fact]
    public void ActivityGrid_pads_to_whole_monday_to_sunday_weeks()
    {
        // The window of the web's overview screenshot: today is Monday 2026-10-26, so the first day is Monday 2025-10-27 and the
        // grid runs to the Sunday of the current week.
        var grid = StatsMath.ActivityGrid(365, Fakes.Utc);
        Assert.Equal("2025-10-27", grid.Days[0]);
        Assert.Equal("2026-11-01", grid.Days[^1]);
        Assert.Equal(0, grid.Days.Count % 7);
        Assert.Equal(new DateOnly(2026, 11, 1), grid.Last);

        // A window that opens mid-week backs up to Monday.
        var shifted = StatsMath.ActivityGrid(10, Fakes.Utc);   // first = 2026-10-17 (Saturday)
        Assert.Equal("2026-10-12", shifted.Days[0]);
        Assert.Equal(DayOfWeek.Monday, DateOnly.ParseExact(shifted.Days[0], "yyyy-MM-dd").DayOfWeek);
    }

    [Fact]
    public void ChartValue_scales_small_measures_and_rounds()
    {
        Assert.Equal(188d, StatsMath.ChartValue(1.875m, TrendMeasure.Requests));
        Assert.Equal(6_360_000d, StatsMath.ChartValue(6.36m, TrendMeasure.CostUsd));
        Assert.Equal(1_400_000d, StatsMath.ChartValue(1_400_000m, TrendMeasure.Tokens));
    }
}
