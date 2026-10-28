using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Pricing;

/// <summary>
/// The decimal handling of the pricing editor, as pure code. The web edits prices as JS numbers (<c>NumberInput</c> in
/// web/src/components/ui/controls.tsx) and the server stores them as <see cref="decimal"/> (<c>PriceSet</c>,
/// src/Astra.Core/Billing/Pricing.cs), so a price is never a <see cref="double"/> here: input text is parsed to a
/// <see cref="decimal"/>, written back as a canonical decimal JSON number (<c>0.000003</c>, never <c>3E-06</c> or
/// <c>3.0E-6</c>) and shown like the web's <c>formatUnitPrice</c>. AGENTS.md: money is never a float.
/// </summary>
public static class PriceNumber
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // What the browser's <input type=number> and JS Number() accept: a sign, digits, one point, an exponent. No grouping, no
    // currency, no hex (Number("0x10") is 16 in JS but a browser number input can never produce it).
    private const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    /// <summary>
    /// Parses what a person typed into a price box. Empty or white space is "no value" (true, <paramref name="value"/> null: the
    /// web's <c>onChange(null)</c>); anything that is not a finite decimal is rejected (false: the web ignores such an edit).
    /// Scientific notation is accepted (<c>1e-7</c>, <c>3E-6</c>) and is exact; a literal so small that it would round to zero
    /// (<c>1e-40</c>) is rejected instead of silently becoming a free price.
    /// </summary>
    public static bool TryParse(string? text, out decimal? value)
    {
        value = null;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        if (!decimal.TryParse(t, Styles, Inv, out var d)) return false;
        if (d == 0 && HasNonZeroDigit(t)) return false;
        value = Normalize(d);
        return true;
    }

    private static bool HasNonZeroDigit(string t)
    {
        foreach (var c in t)
        {
            if (c is 'e' or 'E') return false; // digits after the exponent mark do not count
            if (c is >= '1' and <= '9') return true;
        }
        return false;
    }

    /// <summary>Drops trailing zeros of the scale (<c>1.50</c> becomes <c>1.5</c>, <c>0.0</c> becomes <c>0</c>) without changing the value.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>The canonical text of a price: plain digits, no exponent, no trailing zeros (<c>0.000003</c>, <c>15</c>, <c>-2.5</c>).</summary>
    public static string ToText(decimal value) => Normalize(value).ToString(Inv);

    /// <summary>A price as a JSON number node, written in its canonical text; null for null.</summary>
    public static JsonNode? ToNode(decimal? value) => value is { } v ? JsonValue.Create(Normalize(v)) : null;

    /// <summary>
    /// Reads a price out of a JSON node: a number (any literal the server or a JS client wrote, exponent forms included) or a
    /// decimal string (the server's reader accepts <c>"0.000003"</c> too). Null, JSON null and anything else give null.
    /// </summary>
    public static decimal? FromNode(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return decimal.TryParse(v.ToJsonString(), Styles, Inv, out var n) ? Normalize(n) : null;
            case JsonValueKind.String:
                return v.TryGetValue<string>(out var s) && TryParse(s, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A decimal the way JavaScript's <c>String(Number(x))</c> prints it: plain digits from 1e-6 up to 1e21, otherwise
    /// <c>1e-7</c> / <c>1.5e+21</c>. Display only (the web's <c>formatUnitPrice</c> goes through a JS number); stored text is
    /// <see cref="ToText"/>.
    /// </summary>
    public static string ToJsText(decimal value)
    {
        value = Normalize(value);
        if (value == 0) return "0";
        var abs = Math.Abs(value);
        if (abs is >= 0.000001m and < 1e21m) return value.ToString(Inv);

        // digits and decimal exponent of abs: 1.5e-7 -> digits "15", exponent -7
        var fixedText = abs.ToString(Inv);
        var point = fixedText.IndexOf('.');
        var intPart = point < 0 ? fixedText : fixedText[..point];
        var fraction = point < 0 ? "" : fixedText[(point + 1)..];
        var all = (intPart + fraction).TrimStart('0');
        int exponent;
        if (intPart.Trim('0').Length > 0) exponent = intPart.TrimStart('0').Length - 1;
        else exponent = -(fraction.Length - fraction.TrimStart('0').Length + 1);
        var digits = all.TrimEnd('0');
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        return (value < 0 ? "-" : "") + mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(Inv);
    }
}
