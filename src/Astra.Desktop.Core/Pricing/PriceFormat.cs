using System.Globalization;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Pricing;

/// <summary>
/// Price and amount display, ported from web/src/lib/format.ts (<c>formatUnitPrice</c>, <c>formatUsd</c>, <c>formatNanos</c>,
/// <c>formatTokens</c>, <c>formatContext</c>, <c>priceSummary</c>). Unlike the web these work on <see cref="decimal"/>, so a
/// price read from the server never passes through a binary float; the printed text is what the web prints.
/// </summary>
public static class PriceFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The em dash the web shows for "no value".</summary>
    public const string Dash = "—";

    /// <summary>
    /// Per-1M-token unit price: "$3", "$0.075", "$1.25" (<c>formatUnitPrice</c>): at most 8 significant digits, no trailing zeros,
    /// JS number text (so below 1e-6 it is "$1e-7", as in the web).
    /// </summary>
    public static string UnitPrice(decimal? price)
    {
        if (price is not { } p) return Dash;
        return "$" + PriceNumber.ToJsText(RoundSignificant(p, 8));
    }

    /// <summary>The same for a pricing JSON value (a number or a decimal string); a string that is not a number is printed as written, like the web.</summary>
    public static string UnitPrice(JsonNode? price)
    {
        if (price is null) return Dash;
        if (price is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (s.Length == 0) return Dash;
            return PriceNumber.TryParse(s, out var parsed) && parsed is not null ? UnitPrice(parsed) : s;
        }
        return PriceNumber.FromNode(price) is { } d ? UnitPrice(d) : Dash;
    }

    /// <summary>Rounds to <paramref name="digits"/> significant digits, halves away from zero (<c>Number.prototype.toPrecision</c> on a decimal value).</summary>
    public static decimal RoundSignificant(decimal value, int digits)
    {
        if (value == 0) return 0;
        var abs = Math.Abs(value);
        // position of the first significant digit relative to the decimal point
        var magnitude = (int)Math.Floor(Math.Log10((double)abs));
        // Log10 of a double can be off by one at an exact power of ten; fix up from the decimal itself.
        if (abs >= Pow10(magnitude + 1)) magnitude++;
        else if (abs < Pow10(magnitude)) magnitude--;
        var decimals = digits - 1 - magnitude;
        if (decimals >= 0) return Math.Round(value, Math.Min(decimals, 28), MidpointRounding.AwayFromZero);
        var scale = Pow10(-decimals);
        return Math.Round(value / scale, 0, MidpointRounding.AwayFromZero) * scale;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        if (exponent >= 0) for (var i = 0; i < exponent; i++) result *= 10m;
        else for (var i = 0; i > exponent; i--) result /= 10m;
        return result;
    }

    /// <summary>
    /// "$12.34", "$0.0213", "$0.009": dollars with 3 significant digits below $1 so per-request costs stay visible
    /// (<c>formatUsd</c>, non-compact). Amounts of 1000 or more get grouping ("$1,234.50").
    /// </summary>
    public static string Usd(decimal? usd)
    {
        if (usd is not { } v) return Dash;
        var abs = Math.Abs(v);
        if (abs == 0) return "$0";
        var sign = v < 0 ? "-" : "";
        // the web prints '$' + Intl(usd), so a negative amount reads "$-3.46"
        if (abs >= 1) return "$" + sign + Math.Round(abs, 2, MidpointRounding.AwayFromZero).ToString("#,##0.00", Inv);
        var rounded = RoundSignificant(abs, 3);
        // plain digits without a limit on fraction digits (Intl: significant digits win over maximumFractionDigits)
        return "$" + sign + PriceNumber.ToText(rounded);
    }

    /// <summary>Integer nano-USD (1 USD = 1e9 nanos, AGENTS.md money) as dollars (<c>formatNanos</c>); null is a dash.</summary>
    public static string Nanos(long? nanos) => nanos is { } n ? Usd(n / 1_000_000_000m) : Dash;

    /// <summary>1234 becomes "1,234" (<c>formatTokens</c>, non-compact).</summary>
    public static string Tokens(long? n) => n is { } v ? v.ToString("#,##0", Inv) : Dash;

    /// <summary>A context window as "1.05M" / "200K" / "512" (<c>formatContext</c>); null and 0 are a dash.</summary>
    public static string Context(long? tokens)
    {
        if (tokens is not { } t || t == 0) return Dash;
        if (t >= 1_000_000)
        {
            // Number((t / 1e6).toFixed(2)): two decimals, then JS number text (trailing zeros dropped)
            var m = Math.Round(t / 1_000_000m, 2, MidpointRounding.AwayFromZero);
            return PriceNumber.ToText(m) + "M";
        }
        if (t >= 1000) return JsRound(t / 1000m).ToString(Inv) + "K";
        return t.ToString(Inv);
    }

    private static long JsRound(decimal v) => (long)Math.Floor(v + 0.5m);

    /// <summary>"$3 / $15": the base input and output prices of a pricing document (<c>priceSummary</c>); a dash when neither is set.</summary>
    public static string Summary(JsonNode? pricing)
    {
        if (pricing is not JsonObject doc || doc["base"] is not JsonObject b) return Dash;
        var input = b["input"];
        var output = b["output"];
        if (IsMissing(input) && IsMissing(output)) return Dash;
        return $"{UnitPrice(input)} / {UnitPrice(output)}";
    }

    private static bool IsMissing(JsonNode? n) => n is null;
}
