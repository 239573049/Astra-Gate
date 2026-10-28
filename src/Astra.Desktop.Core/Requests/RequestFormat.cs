using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// Display formatting of the request log that <see cref="TraceFormat"/> does not already cover (ports of
/// <c>web/src/lib/format.ts</c>: <c>formatTps</c>, <c>formatBytes</c>, <c>formatTime</c>, <c>formatDateTime</c>,
/// <c>formatUnitPrice</c>, <c>pretty</c>). <c>formatMs</c> / <c>formatTokens</c> / <c>formatNanos</c> / <c>formatPercent</c> are
/// <see cref="TraceFormat.Ms"/> / <see cref="TraceFormat.Tokens"/> / <see cref="TraceFormat.Nanos"/> / <see cref="TraceFormat.Percent"/>.
/// </summary>
public static class RequestFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"62.9 tok/s"; a dash for no value or a nonsense one (<c>formatTps</c>).</summary>
    public static string Tps(double? tps)
        => tps is not { } v || !double.IsFinite(v) || v < 0 ? TraceFormat.Dash : v.ToString("F1", Inv) + " tok/s";

    /// <summary>A byte count as B / KB / MB, 1024-based (<c>formatBytes</c>).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(Inv) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "F1" : "F0", Inv) + " KB";
        return (bytes / (1024.0 * 1024)).ToString("F1", Inv) + " MB";
    }

    /// <summary>
    /// The list's time cell (<c>formatTime</c>): seconds precision for today ("11:57:00"), month/day and minutes otherwise
    /// ("10/25 11:57"; en-US writes "10/25, 11:57"). 24-hour clock, local time zone; "today" is relative to <paramref name="now"/>.
    /// </summary>
    public static string Time(DateTimeOffset at, string locale, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var d = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        if (d.Date == today.Date) return d.ToString("HH:mm:ss", Inv);
        return locale == "zh" ? d.ToString("MM/dd HH:mm", Inv) : d.ToString("MM/dd", Inv) + ", " + d.ToString("HH:mm", Inv);
    }

    /// <summary>"2026/10/26 11:57" (zh) or "10/26/2026, 11:57" (en); a dash for no value (<c>formatDateTime</c>).</summary>
    public static string DateTime(DateTimeOffset? at, string locale, TimeZoneInfo? zone = null)
    {
        if (at is null) return TraceFormat.Dash;
        var d = TimeZoneInfo.ConvertTime(at.Value, zone ?? TimeZoneInfo.Local);
        return locale == "zh" ? d.ToString("yyyy/MM/dd HH:mm", Inv) : d.ToString("MM/dd/yyyy", Inv) + ", " + d.ToString("HH:mm", Inv);
    }

    /// <summary>A per-1M-token unit price: "$3", "$0.075", "$1.25" (<c>formatUnitPrice</c>: 8 significant digits, trailing zeros dropped).</summary>
    public static string UnitPrice(string? price)
    {
        if (string.IsNullOrEmpty(price)) return TraceFormat.Dash;
        if (!double.TryParse(price, NumberStyles.Float, Inv, out var n)) return price;
        var g8 = n.ToString("G8", Inv);
        // JS prints plain decimals down to 1e-6 and exponents below; "G8" would switch to exponents sooner.
        var rounded = double.Parse(g8, NumberStyles.Float, Inv);
        var text = Math.Abs(rounded) is > 0 and < 1e-6 ? rounded.ToString("R", Inv) : decimal.Parse(g8, NumberStyles.Float, Inv).ToString("0.############################", Inv);
        return "$" + text;
    }

    /// <summary>
    /// A body or JSON value as indented text (<c>pretty</c>): a JSON string is re-indented, other text stays as it is, an object is
    /// written indented, null is empty. Non-ASCII stays readable (as <c>JSON.stringify</c> does).
    /// </summary>
    public static string Pretty(JsonNode? value)
    {
        if (value is null) return "";
        if (value is JsonValue v && v.TryGetValue<string>(out var text)) return Pretty(text);
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options)) value.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>See <see cref="Pretty(JsonNode?)"/>: text that is JSON is re-indented, anything else is returned unchanged.</summary>
    public static string Pretty(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, options)) doc.RootElement.WriteTo(writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>The compact token count of a rate cell: 61.8K, 3.4 (<c>rateText</c> in <c>LiveRate</c>).</summary>
    public static string RateText(double n) => n >= 100 ? TraceFormat.Tokens(Math.Round(n, MidpointRounding.AwayFromZero)) : n.ToString("F1", Inv);
}
