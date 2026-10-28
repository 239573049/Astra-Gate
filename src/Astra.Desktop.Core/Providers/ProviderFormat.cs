using System.Globalization;

namespace Astra.Desktop.Core.Providers;

/// <summary>
/// The small formatters the providers page shares with the rest of the web UI (web/src/lib/format.ts: <c>formatTokens</c>,
/// <c>formatUsd</c>) plus the page's own helpers (<c>hostOf</c>, <c>lastDays</c> of ProvidersPage.tsx). Pure, culture-invariant
/// (the web pins en-US for these), so tests can compare strings.
/// </summary>
public static class ProviderFormat
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>1234 -> "1,234"; with <paramref name="compact"/> and at least 10,000: "12.3K", "8.1M" (Intl compact, one fraction digit).</summary>
    public static string FormatTokens(long? n, bool compact = false)
    {
        if (n is null) return "—";
        if (compact && Math.Abs(n.Value) >= 10_000) return Compact(n.Value);
        return n.Value.ToString("#,##0", En);
    }

    private static string Compact(double v)
    {
        string[] suffix = ["", "K", "M", "B", "T"];
        var i = 0;
        var a = Math.Abs(v);
        while (a >= 1000 && i < suffix.Length - 1) { a /= 1000; i++; }
        var r = Math.Round(a, 1, MidpointRounding.AwayFromZero);
        // Rounding can reach 1000 (999,950 -> 1000K): carry into the next unit like Intl does.
        if (r >= 1000 && i < suffix.Length - 1) { r = Math.Round(r / 1000, 1, MidpointRounding.AwayFromZero); i++; }
        return (v < 0 ? "-" : "") + r.ToString("0.#", En) + suffix[i];
    }

    /// <summary>"$12.34", "$0.0213", "$0.009" — three significant digits below $1 (web <c>formatUsd</c>).</summary>
    public static string FormatUsd(decimal? usd)
    {
        if (usd is null) return "—";
        var abs = Math.Abs(usd.Value);
        if (abs == 0) return "$0";
        if (abs >= 1) return "$" + usd.Value.ToString("#,##0.00", En);
        // maximumSignificantDigits 3: round to 3 significant digits, drop trailing zeros.
        var digits = (int)Math.Floor(Math.Log10((double)abs));
        var scale = 2 - digits;
        var rounded = Math.Round(usd.Value, Math.Clamp(scale, 0, 28), MidpointRounding.AwayFromZero);
        return "$" + rounded.ToString("0.##########", En);
    }

    /// <summary>The host of a URL, the text itself when it does not parse, null for none (web <c>hostOf</c>).</summary>
    public static string? HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}")
            : url;
    }

    /// <summary>The last <paramref name="n"/> local days as "yyyy-MM-dd", oldest first — the bucket format of /api/stats/timeseries.</summary>
    public static List<string> LastDays(DateTimeOffset now, int n)
    {
        var today = now.ToLocalTime().Date;
        return Enumerable.Range(0, n).Select(i => today.AddDays(-(n - 1 - i)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>The viewer's UTC offset in minutes (web <c>tzOffset</c>), so buckets follow local days.</summary>
    public static int TzOffsetMinutes(DateTimeOffset now) => (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes;
}
