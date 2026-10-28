using System.Globalization;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Core.Overview;

/// <summary>
/// Display formatting of the overview, token and usage views: <c>web/src/lib/format.ts</c> ported. Money is a <see cref="decimal"/>
/// (the admin API's USD amounts are decimal, nanodollars are <see cref="long"/>) and never goes through a float; the web's
/// <c>Intl.NumberFormat('en-US', ...)</c> calls are reproduced by hand (invariant culture, half away from zero, like ICU) so
/// the text does not depend on the machine's globalization data. Where <see cref="TraceFormat"/> already matches the web helper
/// (<c>formatMs</c>, <c>formatPercent</c>) it is reused.
/// </summary>
public static class StatsFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The em dash for "no value".</summary>
    public const string Dash = TraceFormat.Dash;

    /// <summary>1 USD in nanodollars (web <c>NANOS_PER_USD</c>).</summary>
    public const long NanosPerUsd = 1_000_000_000;

    /// <summary>"$12.34", "$0.0213", "$0.009" — 3 significant digits below $1 so per-request costs stay visible (<c>formatUsd</c>).</summary>
    public static string Usd(decimal? usd)
    {
        if (usd is not { } v) return Dash;
        var abs = Math.Abs(v);
        if (abs == 0) return "$0";
        if (abs >= 1) return "$" + v.ToString("N2", Inv);
        return "$" + SignificantDigits(v, 3);
    }

    /// <summary>Short axis label: "$0.01", "$2.5", "$1.2K" (<c>formatUsdAxis</c>).</summary>
    public static string UsdAxis(decimal usd)
    {
        if (usd == 0) return "$0";
        if (Math.Abs(usd) >= 1000) return "$" + Compact(usd);
        return "$" + SignificantDigits(usd, 2);
    }

    /// <summary>Nanodollars as <see cref="Usd"/> (<c>formatNanos</c>); null is a dash.</summary>
    public static string Nanos(long? nanos) => nanos is { } n ? Usd(n / (decimal)NanosPerUsd) : Dash;

    /// <summary>
    /// 1234 becomes "1,234"; 8_100_000 becomes "8.1M" when <paramref name="compact"/> and the value reaches 10,000
    /// (<c>formatTokens</c>). Like <c>Intl</c> it keeps up to three fraction digits, so a scaled chart average reads "1.88".
    /// </summary>
    public static string Tokens(decimal? n, bool compact = false)
    {
        if (n is not { } v) return Dash;
        if (compact && Math.Abs(v) >= 10_000) return Compact(v);
        return Math.Round(v, 3, MidpointRounding.AwayFromZero).ToString("#,##0.###", Inv);
    }

    /// <summary>"420 ms", "12.3 s" (<c>formatMs</c>).</summary>
    public static string Ms(double? ms) => TraceFormat.Ms(ms);

    /// <summary>"62.4 tok/s"; null, NaN and negative values are a dash (<c>formatTps</c>).</summary>
    public static string Tps(double? tps)
    {
        if (tps is not { } v || !double.IsFinite(v) || v < 0) return Dash;
        return v.ToString("F1", Inv) + " tok/s";
    }

    /// <summary>A 0..1 ratio as "12.5%" (<c>formatPercent</c>).</summary>
    public static string Percent(double? ratio) => TraceFormat.Percent(ratio);

    /// <summary>
    /// The request log's time cell (<c>formatTime</c>): seconds precision for today ("11:57:00"), month/day and minutes otherwise
    /// ("10/25 11:57" in zh, "10/25, 11:57" in en), 24-hour, in the zone of <paramref name="zone"/>.
    /// </summary>
    public static string Time(DateTimeOffset at, DateTimeOffset now, TimeSpan zone, string language)
    {
        var d = at.ToOffset(zone);
        var today = now.ToOffset(zone);
        if (d.Date == today.Date) return d.ToString("HH:mm:ss", Inv);
        var stamp = d.ToString("MM/dd", Inv);
        return stamp + (language == "zh" ? " " : ", ") + d.ToString("HH:mm", Inv);
    }

    /// <summary>"2026/10/26 11:57" (zh) or "10/26/2026, 11:57" (en) (<c>formatDateTime</c>); null is a dash.</summary>
    public static string DateTime(DateTimeOffset? at, TimeSpan zone, string language)
    {
        if (at is not { } v) return Dash;
        var d = v.ToOffset(zone);
        return language == "zh"
            ? d.ToString("yyyy/MM/dd HH:mm", Inv)
            : d.ToString("MM/dd/yyyy", Inv) + ", " + d.ToString("HH:mm", Inv);
    }

    /// <summary>
    /// <c>maximumSignificantDigits</c> rendering of a decimal: rounds half away from zero to <paramref name="digits"/> significant
    /// digits (fraction digits capped at 10 like the web), without trailing zeros.
    /// </summary>
    public static string SignificantDigits(decimal v, int digits)
    {
        var abs = Math.Abs(v);
        if (abs == 0) return "0";
        var exponent = (int)Math.Floor(Math.Log10((double)abs));
        var places = digits - 1 - exponent;
        decimal rounded;
        if (places >= 0) rounded = Math.Round(v, Math.Min(places, 10), MidpointRounding.AwayFromZero);
        else
        {
            // Whole numbers above the digit budget: 123.4 with 2 digits is 120.
            var scale = (decimal)Math.Pow(10, -places);
            rounded = Math.Round(v / scale, 0, MidpointRounding.AwayFromZero) * scale;
        }
        return rounded.ToString("#,##0.##########", Inv);
    }

    /// <summary>
    /// <c>notation: 'compact', maximumFractionDigits: 1</c> in en-US: "1.2K", "8.1M", "3B", "2T"; 999,950 rounds up into "1M".
    /// </summary>
    public static string Compact(decimal v)
    {
        var sign = v < 0 ? "-" : "";
        var abs = Math.Abs(v);
        string[] units = ["", "K", "M", "B", "T"];
        var unit = 0;
        decimal scale = 1;
        while (unit < units.Length - 1 && abs >= scale * 1000)
        {
            scale *= 1000;
            unit++;
        }
        var r = Math.Round(abs / scale, 1, MidpointRounding.AwayFromZero);
        if (r >= 1000 && unit < units.Length - 1)
        {
            r = 1;
            unit++;
        }
        return sign + r.ToString("0.#", Inv) + units[unit];
    }
}
