using System.Globalization;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// Translator the trace builders call: a web <c>traces.*</c> / <c>smartRouting.*</c> key plus named placeholders.
/// Pages pass <see cref="TraceTexts.FromLoc"/>; tests pass the same after pinning <c>Loc.Language</c>.
/// </summary>
public delegate string TraceText(string key, params (string Name, object? Value)[] args);

/// <summary>Ready-made <see cref="TraceText"/> sources.</summary>
public static class TraceTexts
{
    /// <summary>The desktop catalog (<see cref="Loc"/>), resolved at call time so a language switch applies.</summary>
    public static readonly TraceText FromLoc = (key, args) => Loc.T(key, args);
}

/// <summary>
/// Display formatting the trace views share. Ported from <c>web/src/lib/format.ts</c> (<c>formatMs</c>, <c>formatTokens</c>,
/// <c>formatUsd</c>, <c>formatPercent</c>) and <c>formatUs</c> in <c>traces/model.ts</c>; all of it is invariant-culture
/// and en-US-shaped on purpose, as on the web. JS rounds halves up (<c>Math.round</c>), which differs from
/// <see cref="Math.Round(double)"/>, hence <see cref="JsRound"/>.
/// </summary>
public static class TraceFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The em dash the web shows for "no value".</summary>
    public const string Dash = "—";

    /// <summary>JS <c>Math.round</c>: halves round toward +infinity.</summary>
    public static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>Microseconds as a short duration: 0.4 ms, 38 ms, 1.83 s (<c>formatUs</c>).</summary>
    public static string Us(double? us)
    {
        if (us is null) return Dash;
        var v = us.Value;
        if (v < 10_000) return (v / 1000).ToString("F1", Inv) + " ms";
        if (v < 1_000_000) return Whole(JsRound(v / 1000)) + " ms";
        return (v / 1_000_000).ToString("F2", Inv) + " s";
    }

    /// <summary>Milliseconds: "420 ms" below 10 s, "12.3 s" above (<c>formatMs</c>).</summary>
    public static string Ms(double? ms)
    {
        if (ms is null) return Dash;
        var v = ms.Value;
        if (v >= 10_000) return (v / 1000).ToString("F1", Inv) + " s";
        return Whole(JsRound(v)) + " ms";
    }

    /// <summary>1234 becomes "1,234"; with <paramref name="compact"/> 8_100_000 becomes "8.1M" (<c>formatTokens</c>).</summary>
    public static string Tokens(double? n, bool compact = false)
    {
        if (n is null) return Dash;
        var v = n.Value;
        if (compact && Math.Abs(v) >= 10_000)
        {
            var sign = v < 0 ? "-" : "";
            var abs = Math.Abs(v);
            string[] units = ["K", "M", "B", "T"];
            var scale = 1000.0;
            var i = 0;
            while (i < units.Length - 1 && abs >= scale * 1000) { scale *= 1000; i++; }
            var r = JsRound(abs / scale * 10) / 10;
            // 999,950 rounds to 1000K, which Intl writes as 1M.
            if (r >= 1000 && i < units.Length - 1) { r = 1; i++; }
            return sign + r.ToString("0.#", Inv) + units[i];
        }
        return JsRound(v).ToString("N0", Inv);
    }

    /// <summary>"$12.34", "$0.0213", "$0.009": three significant digits below $1 (<c>formatUsd</c>, non-compact).</summary>
    public static string Usd(double? usd)
    {
        if (usd is null || double.IsNaN(usd.Value)) return Dash;
        var v = usd.Value;
        var abs = Math.Abs(v);
        if (abs == 0) return "$0";
        if (abs >= 1) return "$" + v.ToString("N2", Inv);
        var digits = Math.Min(10, 2 - (int)Math.Floor(Math.Log10(abs)));
        var rounded = Math.Round(v, Math.Max(0, digits), MidpointRounding.AwayFromZero);
        return "$" + rounded.ToString("0.##########", Inv);
    }

    /// <summary>Nano-USD as <see cref="Usd"/> (the <c>usd</c> helper of the session and request views).</summary>
    public static string Nanos(double nanos) => Usd(nanos / 1e9);

    /// <summary>A 0..1 ratio as "12.5%" (<c>formatPercent</c>).</summary>
    public static string Percent(double? ratio)
    {
        if (ratio is null || double.IsNaN(ratio.Value)) return Dash;
        var r = ratio.Value;
        return (r * 100).ToString(r >= 0.999 || r == 0 ? "F0" : "F1", Inv) + "%";
    }

    /// <summary>"claude-sonnet-5-5" becomes "sonnet-5-5": the family tells calls apart at a glance (<c>shortModel</c>).</summary>
    public static string ShortModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return Dash;
        var m = model;
        if (m.StartsWith("claude-", StringComparison.Ordinal)) m = m["claude-".Length..];
        if (m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        return m.Length > 22 ? m[..21] + "…" : m;
    }

    /// <summary>"Read×2 · Grep" from tool names (<c>toolSummary</c>).</summary>
    public static string ToolSummary(IEnumerable<string> names)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            if (counts.TryGetValue(n, out var c)) counts[n] = c + 1;
            else { counts[n] = 1; order.Add(n); }
        }
        return string.Join(" · ", order.Select(n => counts[n] > 1 ? $"{n}×{counts[n]}" : n));
    }

    /// <summary>"12 s", "4 min", "2 h": how long the agent has been at it (<c>sinceText</c> in session.tsx).</summary>
    public static string Since(double ms) =>
        ms < 60_000 ? Whole(Math.Max(0, JsRound(ms / 1000))) + " s"
        : ms < 3_600_000 ? Whole(Math.Floor(ms / 60_000)) + " min"
        : Whole(Math.Floor(ms / 3_600_000)) + " h";

    /// <summary>A JS number as <c>String(n)</c> prints it for the values traces carry (integers, short decimals).</summary>
    public static string Num(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString(Inv) : v.ToString("R", Inv);

    private static string Whole(double v) => ((long)v).ToString(Inv);
}
