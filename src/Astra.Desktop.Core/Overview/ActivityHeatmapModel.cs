using System.Globalization;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>Where a hovered cell's bubble sits (web <c>activity-heatmap.tsx</c> <c>Hover</c>).</summary>
public enum BubbleAlign
{
    /// <summary>The bubble's left edge is 8px inside the grid's left edge.</summary>
    Start,

    /// <summary>The bubble is centred on the cell.</summary>
    Center,

    /// <summary>The bubble's right edge is 8px inside the grid's right edge.</summary>
    End,
}

/// <summary>The bubble of one hovered cell: its anchor in plot coordinates, whether it hangs above the cell, and how it aligns.</summary>
public readonly record struct BubblePlacement(double Left, double Top, bool Above, BubbleAlign Align);

/// <summary>
/// Everything the activity heatmap draws, computed once per answer (web <c>components/arc/activity-heatmap/activity-heatmap.tsx</c> plus
/// <c>activityGrid</c> / <c>activityLevel</c> in <c>lib/stats.ts</c> and the Intl formatters of <c>lib/format.ts</c>): the padded calendar
/// (Monday-first weeks), one level 0-4 per day, the month header of every column and the per-cell bubble text.
/// </summary>
public sealed class ActivityHeatmapModel
{
    /// <summary>"yyyy-MM-dd" of every cell, column by column (7 per week, Monday first).</summary>
    public IReadOnlyList<string> Days { get; }

    /// <summary>Number of week columns.</summary>
    public int Weeks => Days.Count / 7;

    /// <summary>Requests per cell (0 for days without data).</summary>
    public IReadOnlyList<long> Counts { get; }

    /// <summary>Intensity 0-4 per cell.</summary>
    public IReadOnlyList<int> Levels { get; }

    /// <summary>Month header of every column, or null for a quiet one.</summary>
    public IReadOnlyList<string?> MonthLabels { get; }

    /// <summary>The busiest day's count.</summary>
    public long Peak { get; }

    private ActivityHeatmapModel(IReadOnlyList<string> days, IReadOnlyList<long> counts, IReadOnlyList<int> levels, IReadOnlyList<string?> months, long peak)
    {
        Days = days;
        Counts = counts;
        Levels = levels;
        MonthLabels = months;
        Peak = peak;
    }

    /// <summary>The model of <paramref name="days"/> days up to <paramref name="now"/>'s day, with <paramref name="language"/>'s month names.</summary>
    public static ActivityHeatmapModel Build(IReadOnlyList<DailyActivityDto> data, int days, LocalTime now, string language)
    {
        var window = StatsMath.ActivityGrid(days, now);
        var byDay = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var d in data) byDay[d.Day] = d.Requests;
        var peak = data.Count == 0 ? 0 : data.Max(d => d.Requests);
        var counts = window.Days.Select(d => byDay.GetValueOrDefault(d)).ToList();
        var levels = counts.Select(c => Level(c, peak)).ToList();

        // A column is named by the month that starts in it; the first column, when no month starts there, names the month and year it opens inside of.
        var months = new List<string?>();
        for (var column = 0; column < window.Days.Count / 7; column++)
        {
            string? label = null;
            for (var row = 0; row < 7; row++)
            {
                var key = window.Days[column * 7 + row];
                if (key.EndsWith("-01", StringComparison.Ordinal)) { label = MonthName(Month(key), language); break; }
            }
            if (label is null && column == 0) label = MonthStartName(Month(window.Days[0]), Year(window.Days[0]), language);
            months.Add(label);
        }
        return new ActivityHeatmapModel(window.Days, counts, levels, months, peak);
    }

    /// <summary>Intensity of one cell from its count against the window's busiest day (<c>activityLevel</c>).</summary>
    public static int Level(long count, long peak)
    {
        if (count <= 0) return 0;
        if (peak <= 0) return 1;
        return (int)Math.Min(4, Math.Max(1, Math.Ceiling((double)count / peak * 4)));
    }

    /// <summary>
    /// The bubble of the cell whose centre is <paramref name="cellCenterX"/> and top <paramref name="cellTop"/> in a plot
    /// <paramref name="plotWidth"/> wide: edge-aligned within 100px of either side, hanging below when the cell is within 44px of the top.
    /// </summary>
    public static BubblePlacement Place(double cellCenterX, double cellTop, double plotWidth)
    {
        const double room = 44, edge = 100;
        var align = cellCenterX < edge ? BubbleAlign.Start : cellCenterX > plotWidth - edge ? BubbleAlign.End : BubbleAlign.Center;
        var left = align switch { BubbleAlign.Start => 8, BubbleAlign.End => plotWidth - 8, _ => cellCenterX };
        return new BubblePlacement(left, cellTop, cellTop > room, align);
    }

    private static readonly string[] EnMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] EnDays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] ZhDays = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>Short month name, 1-12 (<c>monthName</c>: Intl zh-CN "10月", en-US "Oct").</summary>
    public static string MonthName(int month, string language) => language == "zh" ? $"{month}月" : EnMonths[month - 1];

    /// <summary>The window's first month with its year (<c>monthStartName</c>: "2025年10月", "Oct 2025").</summary>
    public static string MonthStartName(int month, int year, string language)
        => language == "zh" ? $"{year}年{month}月" : $"{EnMonths[month - 1]} {year}";

    /// <summary>Short weekday name of row 0-6, Monday first (<c>weekdayName</c>: "周一", "Mon").</summary>
    public static string WeekdayName(int row, string language) => (language == "zh" ? ZhDays : EnDays)[row];

    /// <summary>A cell's bubble (<c>formatDayCount</c>): "2026年10月8日周四 · 4 次请求" / "Thu, Oct 8, 2026 · no activity".</summary>
    public static string DayCount(string day, long count, string language, string unit, string empty)
    {
        var date = StatsMath.ParseDay(day);
        var row = ((int)date.DayOfWeek + 6) % 7;
        var label = language == "zh"
            ? $"{date.Year}年{date.Month}月{date.Day}日{ZhDays[row]}"
            : $"{EnDays[row]}, {EnMonths[date.Month - 1]} {date.Day}, {date.Year}";
        return $"{label} · {(count > 0 ? $"{count.ToString(CultureInfo.InvariantCulture)} {unit}" : empty)}";
    }

    private static int Month(string key) => int.Parse(key.AsSpan(5, 2), CultureInfo.InvariantCulture);

    private static int Year(string key) => int.Parse(key.AsSpan(0, 4), CultureInfo.InvariantCulture);
}

/// <summary>
/// CSS <c>color-mix(in oklab, a p%, b)</c> with premultiplied alpha, as browsers compute it: the heatmap's level colours mix the accent
/// into the translucent <c>--surface</c>. Channels are 0-255 bytes, alpha 0-1.
/// </summary>
public static class OklabMix
{
    /// <summary>Mixes <paramref name="p"/> (0-1) of colour a into colour b; returns (r, g, b, alpha).</summary>
    public static (byte R, byte G, byte B, double A) Mix((byte R, byte G, byte B, double A) a, double p, (byte R, byte G, byte B, double A) b)
    {
        var la = ToOklab(a.R, a.G, a.B);
        var lb = ToOklab(b.R, b.G, b.B);
        var alpha = a.A * p + b.A * (1 - p);
        if (alpha <= 0) return (0, 0, 0, 0);
        double Channel(double x, double y) => (x * a.A * p + y * b.A * (1 - p)) / alpha;
        var (r, g, bl) = FromOklab(Channel(la.L, lb.L), Channel(la.A, lb.A), Channel(la.B, lb.B));
        return (r, g, bl, alpha);
    }

    private static double ToLinear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte FromLinear(double v)
    {
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }

    private static (double L, double A, double B) ToOklab(byte r8, byte g8, byte b8)
    {
        double r = ToLinear(r8), g = ToLinear(g8), b = ToLinear(b8);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (byte, byte, byte) FromOklab(double L, double a, double b)
    {
        var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }
}
