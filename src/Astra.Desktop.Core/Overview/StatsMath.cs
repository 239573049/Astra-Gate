using System.Globalization;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>The stat ranges of the overview and token pages (web <c>Range</c>); the API takes the string.</summary>
public static class StatsRange
{
    /// <summary>Since local midnight.</summary>
    public const string Today = "today";

    /// <summary>The last 7 local days.</summary>
    public const string Days7 = "7d";

    /// <summary>The last 30 local days.</summary>
    public const string Days30 = "30d";

    /// <summary>The last 90 local days.</summary>
    public const string Days90 = "90d";

    /// <summary>The ranges the overview's selector offers, in order.</summary>
    public static readonly IReadOnlyList<string> Choices = [Today, Days7, Days30, Days90];

    /// <summary>The i18n key of a range's label (<c>range.&lt;range&gt;</c>).</summary>
    public static string LabelKey(string range) => "range." + range;

    /// <summary>The bucket unit of the timeseries for a range: hours for today, days otherwise.</summary>
    public static string GroupBy(string range) => range == Today ? "hour" : "day";

    internal static int? Days(string range) => range switch { Days7 => 7, Days30 => 30, Days90 => 90, _ => null };
}

/// <summary>The measure a trend chart plots (web <c>Measure</c>).</summary>
public enum TrendMeasure
{
    /// <summary>Spend in USD.</summary>
    CostUsd,

    /// <summary>Request count.</summary>
    Requests,

    /// <summary>Input plus output tokens.</summary>
    Tokens,
}

/// <summary>
/// Wall-clock "now" in the user's zone: what the web gets from <c>new Date()</c>. The page time is a <see cref="DateTimeOffset"/>
/// from the page's clock; the zone is the machine's for the real clock and the clock's own offset for a fixed one (the visual
/// tests freeze it in UTC like the web capture does).
/// </summary>
public readonly record struct LocalTime(DateTime Now, int OffsetMinutes)
{
    /// <summary>The wall clock at <paramref name="at"/> in the zone of <paramref name="zone"/>.</summary>
    public static LocalTime At(DateTimeOffset at, TimeSpan zone) => new(at.ToOffset(zone).DateTime, (int)zone.TotalMinutes);

    /// <summary>The zone to show times in for a clock: the machine's for the real one, the clock's offset otherwise.</summary>
    public static TimeSpan ZoneOf(Shell.IClock clock) =>
        clock is Shell.SystemClock ? TimeZoneInfo.Local.GetUtcOffset(clock.Now) : clock.Now.Offset;

    /// <summary>The local time of a page clock.</summary>
    public static LocalTime Of(Shell.IClock clock) => At(clock.Now, ZoneOf(clock));

    /// <summary>The local calendar day as the server's "yyyy-MM-dd" key.</summary>
    public string Day => DayKey(Now);

    internal static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One time bucket of a trend with the total of the plotted measure.</summary>
public sealed record BucketTotal(string Bucket, decimal Value);

/// <summary>One row of a breakdown table; <see cref="Share"/> is of cost, or of requests when nothing was billed (web <c>BreakdownRow</c>).</summary>
public sealed record BreakdownRow(string Key, long Requests, long Tokens, decimal CostUsd, double Share);

/// <summary>The calendar window of the activity heatmap, padded to whole Monday-to-Sunday weeks (web <c>ActivityGrid</c>).</summary>
public sealed record ActivityWindow(IReadOnlyList<string> Days, DateOnly First, DateOnly Last);

/// <summary>
/// The pure helpers behind the overview charts and tables: <c>web/src/lib/stats.ts</c> ported, plus the data shaping the page
/// does inline (chart points, the heatmap's thresholds).
/// </summary>
public static class StatsMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The chart plots small measures scaled up (cost in micro-USD, requests in hundredths) because the web chart rounds its
    /// average to whole units; the displayed value is divided back (<c>CHART_SCALE</c>).
    /// </summary>
    public static int ChartScale(TrendMeasure measure) => measure switch { TrendMeasure.CostUsd => 1_000_000, TrendMeasure.Requests => 100, _ => 1 };

    /// <summary>Sums every series per time bucket for one measure, oldest bucket first (<c>bucketTotals</c>).</summary>
    public static List<BucketTotal> BucketTotals(IEnumerable<TimeseriesPointDto> points, TrendMeasure measure)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var p in points)
        {
            var v = measure switch { TrendMeasure.CostUsd => p.CostUsd, TrendMeasure.Requests => p.Requests, _ => (decimal)p.Tokens };
            totals[p.Bucket] = totals.GetValueOrDefault(p.Bucket) + v;
        }
        return totals.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new BucketTotal(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Local midnight of a range's first day (the server's own range start); null for "all" (<c>rangeStart</c>).</summary>
    public static DateTime? RangeStart(string range, LocalTime now)
    {
        var today = now.Now.Date;
        if (range == StatsRange.Today) return today;
        return StatsRange.Days(range) is { } days ? today.AddDays(-(days - 1)) : null;
    }

    /// <summary>
    /// Every local bucket key of a range, oldest first, in the server's bucket format: the 24 hours of today
    /// ("yyyy-MM-ddTHH:00") or one "yyyy-MM-dd" per day. Empty for "all" (<c>rangeBuckets</c>).
    /// </summary>
    public static List<string> RangeBuckets(string range, LocalTime now)
    {
        if (range == StatsRange.Today)
        {
            var day = now.Day;
            return Enumerable.Range(0, 24).Select(h => $"{day}T{h:00}:00").ToList();
        }
        if (RangeStart(range, now) is not { } start || StatsRange.Days(range) is not { } days) return [];
        return Enumerable.Range(0, days).Select(i => LocalTime.DayKey(start.AddDays(i))).ToList();
    }

    /// <summary>Puts bucket totals on a continuous axis: every expected bucket in order, zero where nothing happened (<c>fillBuckets</c>).</summary>
    public static List<BucketTotal> FillBuckets(List<BucketTotal> totals, IReadOnlyList<string> buckets)
    {
        if (buckets.Count == 0) return totals;
        var by = totals.ToDictionary(t => t.Bucket, t => t.Value, StringComparer.Ordinal);
        return buckets.Select(b => new BucketTotal(b, by.GetValueOrDefault(b))).ToList();
    }

    /// <summary>Short label under a bar: "14:00" for an hour bucket, "10/07" for a day (<c>bucketAxisLabel</c>).</summary>
    public static string BucketAxisLabel(string bucket) => bucket.Contains('T') ? bucket[11..16] : MonthDay(bucket);

    /// <summary>Headline label while a bar is scrubbed: "10/07 14:00" or "10/07" (<c>bucketFullLabel</c>).</summary>
    public static string BucketFullLabel(string bucket) => bucket.Contains('T') ? MonthDay(bucket) + " " + bucket[11..16] : MonthDay(bucket);

    private static string MonthDay(string bucket) => bucket.Substring(5, 2) + "/" + bucket.Substring(8, 2);

    /// <summary>Sums rows per key; share is of cost, or of requests when nothing was billed (<c>breakdownRows</c>).</summary>
    public static List<BreakdownRow> BreakdownRows(IEnumerable<(string Key, long Requests, long Tokens, decimal CostUsd)> points)
    {
        var order = new List<string>();
        var sums = new Dictionary<string, (long Requests, long Tokens, decimal Cost)>(StringComparer.Ordinal);
        foreach (var (key, requests, tokens, cost) in points)
        {
            if (!sums.TryGetValue(key, out var row)) order.Add(key);
            sums[key] = (row.Requests + requests, row.Tokens + tokens, row.Cost + cost);
        }
        var totalCost = sums.Values.Sum(r => r.Cost);
        var totalRequests = sums.Values.Sum(r => r.Requests);
        return order.Select(key =>
        {
            var r = sums[key];
            var share = totalCost > 0 ? (double)(r.Cost / totalCost) : totalRequests > 0 ? (double)r.Requests / totalRequests : 0;
            return new BreakdownRow(key, r.Requests, r.Tokens, r.Cost, share);
        }).ToList();
    }

    /// <summary>The breakdown of a timeseries (per provider or per token).</summary>
    public static List<BreakdownRow> BreakdownRows(IEnumerable<TimeseriesPointDto> points)
        => BreakdownRows(points.Select(p => (p.Key, p.Requests, p.Tokens, p.CostUsd)));

    /// <summary>The breakdown of the top models.</summary>
    public static List<BreakdownRow> BreakdownRows(IEnumerable<TopModelDto> models)
        => BreakdownRows(models.Select(m => (m.Model, m.Requests, m.Tokens, m.CostUsd)));

    /// <summary>
    /// The last <paramref name="days"/> local days laid out as calendar weeks, padded at both ends to whole weeks that start on
    /// Monday (<c>activityGrid</c>): the heatmap's summary names its first and last cell.
    /// </summary>
    public static ActivityWindow ActivityGrid(int days, LocalTime now)
    {
        var today = now.Now.Date;
        var first = today.AddDays(-(days - 1));
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var last = today.AddDays(6 - (((int)today.DayOfWeek + 6) % 7));
        var keys = new List<string>();
        for (var d = start; d <= last; d = d.AddDays(1)) keys.Add(LocalTime.DayKey(d));
        return new ActivityWindow(keys, DateOnly.FromDateTime(start), DateOnly.FromDateTime(last));
    }

    /// <summary>
    /// A bucket total as the chart plots it: scaled by <see cref="ChartScale"/> and rounded to a whole number
    /// (<c>Math.round(p.value * CHART_SCALE[measure])</c>).
    /// </summary>
    public static double ChartValue(decimal value, TrendMeasure measure) => (double)Math.Round(value * ChartScale(measure), 0, MidpointRounding.AwayFromZero);

    /// <summary>"yyyy-MM-dd" to a date.</summary>
    public static DateOnly ParseDay(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", Inv);
}
