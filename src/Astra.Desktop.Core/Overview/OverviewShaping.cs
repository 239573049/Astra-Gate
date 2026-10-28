using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>A translator: an i18n key plus named placeholders (the web's <c>t</c>). Pages pass <c>Loc.T</c>; tests pass a fake.</summary>
public delegate string Translate(string key, params (string Name, object? Value)[] args);

/// <summary>One number card (web <c>Stat</c>): a label, a big value and a small context line.</summary>
public sealed record StatCard(string Label, string Value, string Context);

/// <summary>One bar of the usage-trend chart (the object the web hands to <c>BarChart</c>).</summary>
public sealed record TrendBar(string Key, string Label, string AxisLabel, double Value);

/// <summary>Which empty state the overview shows when the summary has no requests.</summary>
public enum OverviewEmpty
{
    /// <summary>There is data.</summary>
    None,

    /// <summary>No requests at all: the gateway address hint.</summary>
    Everything,

    /// <summary>The selected client made no requests in the range.</summary>
    Client,

    /// <summary>The selected token made no requests in the range.</summary>
    Token,
}

/// <summary>One row of the overview's recent-requests table (web <c>LogRow</c>).</summary>
public sealed record LogRow(
    string Id, DateTimeOffset Time, string Client, string Provider, string Model, long Input, long Output, long Cache, long CostNanos,
    double Speed, RequestSummaryDto Request);

/// <summary>A model breakdown row: the share columns plus the cache hit ratio (-1 = "—", sorts below a real 0%).</summary>
public sealed record ModelBreakdownRow(string Key, long Requests, long Tokens, decimal CostUsd, double Share, double CacheHit);

/// <summary>What <c>OverviewPage.tsx</c> computes between the queries and the markup, as pure functions.</summary>
public static class OverviewShaping
{
    /// <summary>The six number cards of the overview (web <c>OverviewPage</c> stats grid).</summary>
    public static List<StatCard> Cards(StatsSummaryDto s, string rangeLabel, Translate t) =>
    [
        new(t("overview.totalCost"), StatsFormat.Usd(s.CostUsd), rangeLabel),
        new(t("overview.totalRequests"), StatsFormat.Tokens(s.Requests), rangeLabel),
        new(t("overview.tokens"), StatsFormat.Tokens(s.InputTokens + s.OutputTokens, true),
            t("overview.tokensContext", ("input", StatsFormat.Tokens(s.InputTokens, true)), ("output", StatsFormat.Tokens(s.OutputTokens, true)))),
        new(t("usage.hitRate"), StatsFormat.Percent(UsageMath.CacheHitRatio(s.CacheReadTokens, s.InputTokens) ?? 0),
            t("usage.cacheAmounts", ("read", StatsFormat.Tokens(s.CacheReadTokens, true)), ("write", StatsFormat.Tokens(s.CacheWriteTokens, true)))),
        new(t("overview.successRate"), s.Requests > 0 ? StatsFormat.Percent(s.SuccessRate) : StatsFormat.Dash, rangeLabel),
        new(t("overview.avgTtft"), StatsFormat.Ms(s.AvgTtftMs), s.AvgTtftMs is null ? t("common.noData") : rangeLabel),
    ];

    /// <summary>The five cards of a token's detail page (web <c>TokenDetailPage</c>): no token-count line, TTFT under the success rate.</summary>
    public static List<StatCard> TokenCards(StatsSummaryDto s, string rangeLabel, Translate t) =>
    [
        new(t("overview.totalCost"), StatsFormat.Usd(s.CostUsd), rangeLabel),
        new(t("overview.totalRequests"), StatsFormat.Tokens(s.Requests), rangeLabel),
        new(t("overview.tokens"), StatsFormat.Tokens(s.InputTokens + s.OutputTokens, true),
            t("overview.tokensContext", ("input", StatsFormat.Tokens(s.InputTokens, true)), ("output", StatsFormat.Tokens(s.OutputTokens, true)))),
        new(t("usage.hitRate"), StatsFormat.Percent(UsageMath.CacheHitRatio(s.CacheReadTokens, s.InputTokens) ?? 0), rangeLabel),
        new(t("overview.successRate"), s.Requests > 0 ? StatsFormat.Percent(s.SuccessRate) : StatsFormat.Dash, StatsFormat.Ms(s.AvgTtftMs)),
    ];

    /// <summary>The trend bars of one measure over a range: totals per bucket on a continuous axis, scaled like the web chart.</summary>
    public static List<TrendBar> Trend(IEnumerable<TimeseriesPointDto> series, TrendMeasure measure, string range, LocalTime now) =>
        StatsMath.FillBuckets(StatsMath.BucketTotals(series, measure), StatsMath.RangeBuckets(range, now))
            .Select(p => new TrendBar(p.Bucket, StatsMath.BucketFullLabel(p.Bucket), StatsMath.BucketAxisLabel(p.Bucket), StatsMath.ChartValue(p.Value, measure)))
            .ToList();

    /// <summary>The request-count trend of a token's detail page: whole-number counts per bucket, no scaling (<c>Math.round(p.value)</c>).</summary>
    public static List<TrendBar> RequestsTrend(IEnumerable<TimeseriesPointDto> series, string range, LocalTime now) =>
        StatsMath.FillBuckets(StatsMath.BucketTotals(series, TrendMeasure.Requests), StatsMath.RangeBuckets(range, now))
            .Select(p => new TrendBar(p.Bucket, StatsMath.BucketFullLabel(p.Bucket), StatsMath.BucketAxisLabel(p.Bucket), (double)Math.Round(p.Value, 0, MidpointRounding.AwayFromZero)))
            .ToList();

    /// <summary>A chart value (already scaled) as the chart shows it: cost as a short USD amount, counts as numbers (<c>formatMeasure</c>).</summary>
    public static string FormatMeasure(double scaled, TrendMeasure measure)
    {
        var n = (decimal)scaled / StatsMath.ChartScale(measure);
        return measure == TrendMeasure.CostUsd
            ? StatsFormat.UsdAxis(n)
            // Number(n.toFixed(2)): whole units stay whole, a sub-1 hourly average reads "0.46".
            : StatsFormat.Tokens(Math.Round(n, 2, MidpointRounding.AwayFromZero), measure == TrendMeasure.Tokens);
    }

    /// <summary>The i18n key naming a measure (<c>overview.cost</c> / <c>overview.requests</c> / <c>overview.tokens</c>).</summary>
    public static string MeasureKey(TrendMeasure measure) => measure switch { TrendMeasure.CostUsd => "overview.cost", TrendMeasure.Requests => "overview.requests", _ => "overview.tokens" };

    /// <summary>Which empty state applies: a summary with zero requests, by the filter that produced it.</summary>
    public static OverviewEmpty EmptyOf(StatsSummaryDto? summary, string? clientKind, string? tokenId)
    {
        if (summary is null || summary.Requests != 0) return OverviewEmpty.None;
        return clientKind is not null ? OverviewEmpty.Client : tokenId is not null ? OverviewEmpty.Token : OverviewEmpty.Everything;
    }

    /// <summary>The heatmap line under the grid: the window, how many days were active, the busiest day (<c>ActivitySection</c> <c>summary</c>).</summary>
    public static string ActivitySummary(IReadOnlyList<DailyActivityDto> data, int days, LocalTime now, Translate t)
    {
        var window = StatsMath.ActivityGrid(days, now);
        var peak = data.Count == 0 ? 0 : data.Max(d => d.Requests);
        return t("overview.activitySummary", ("from", window.Days[0]), ("to", window.Days[^1]), ("active", data.Count), ("days", days))
            + " · " + t("overview.activityPeak", ("count", StatsFormat.Tokens(peak)));
    }

    /// <summary>The recent-requests rows (web <c>logRows</c>).</summary>
    public static List<LogRow> LogRows(IEnumerable<RequestSummaryDto> items, Translate t) =>
        items.Select(r => new LogRow(
            r.Id, r.StartedAtUtc,
            // CLIENT_META[kind]?.name ?? kind: an agent plugin shows its raw kind here (clientName() would strip the prefix).
            r.ClientKind is not null ? (ClientCatalog.Known(r.ClientKind) ? ClientCatalog.Name(r.ClientKind) : r.ClientKind) : r.TokenId is not null ? t("requests.direct") : StatsFormat.Dash,
            r.ProviderName ?? "", r.RequestedModel ?? "", r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CostNanoUsd,
            r.OutputTps ?? -1, r)).ToList();

    /// <summary>Token counts are meaningless when the upstream reported no usage (web <c>usageCell</c>).</summary>
    public static string UsageCell(RequestSummaryDto r, string text) => r.UsageSource == "missing" ? StatsFormat.Dash : text;

    /// <summary>The model breakdown with each model's cache hit ratio; -1 when it had no input to hit.</summary>
    public static List<ModelBreakdownRow> ModelRows(IReadOnlyList<TopModelDto> top)
    {
        var hit = top.ToDictionary(m => m.Model, m => UsageMath.CacheHitRatio(m.CacheReadTokens, m.InputTokens) ?? -1, StringComparer.Ordinal);
        return StatsMath.BreakdownRows(top).Select(r => new ModelBreakdownRow(r.Key, r.Requests, r.Tokens, r.CostUsd, r.Share, hit.GetValueOrDefault(r.Key, -1))).ToList();
    }

    /// <summary>Whether the request row's status gets a badge, and its tone name: cancelled is orange, other failures red (<c>status !== 'success'</c>).</summary>
    public static string? FailureTone(string status) => status == "success" ? null : status == "client_cancelled" ? "orange" : "red";
}
