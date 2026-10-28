using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Traces;

/// <summary>One event on a span (<c>{ name, offset_us, attributes }</c>, e.g. <c>first_token</c>).</summary>
public sealed record SpanEvent(string Name, double OffsetUs, JsonObject? Attributes);

/// <summary>
/// Typed reads of a span's flat attribute bag and event list: the <c>attr</c> / <c>attrNum</c> / <c>attrStr</c> helpers of
/// <c>traces/model.ts</c>. The keys are the contract of AGENTS.md linked update 15 (also matched by SQL in
/// <c>TraceRepository</c>), so the constants live here once. A value of the wrong JSON kind reads as absent, as in TS
/// (<c>typeof v === 'number'</c>).
/// </summary>
public static class SpanAttrs
{
    /// <summary>Raw value of an attribute, or null.</summary>
    public static JsonNode? Attr(SpanDto? span, string key) =>
        span?.Attributes is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>A numeric attribute; a string "5" is not a number here.</summary>
    public static double? Num(SpanDto? span, string key) => AsNumber(Attr(span, key));

    /// <summary>A string attribute.</summary>
    public static string? Str(SpanDto? span, string key) =>
        Attr(span, key) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>True only for a JSON <c>true</c> (<c>attr(...) === true</c>).</summary>
    public static bool IsTrue(SpanDto? span, string key) =>
        Attr(span, key) is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>JS truthiness of an attribute (<c>attr(...) ? a : b</c>): non-empty string, non-zero number, true.</summary>
    public static bool Truthy(SpanDto? span, string key) => Attr(span, key) switch
    {
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => AsNumber(v) is { } n && n != 0 && !double.IsNaN(n),
            _ => false,
        },
        JsonNode => true,
        _ => false,
    };

    /// <summary>A JSON scalar as JS <c>String(v)</c> prints it.</summary>
    public static string JsString(JsonNode? node)
    {
        if (node is not JsonValue v) return node is null ? "" : node.ToJsonString();
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => AsNumber(v) is { } n ? TraceFormat.Num(n) : "",
            _ => "",
        };
    }

    /// <summary>All attributes as (key, <see cref="JsString"/>) pairs, in the order the server wrote them.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Entries(SpanDto? span) =>
        span?.Attributes is JsonObject o
            ? o.Select(kv => new KeyValuePair<string, string>(kv.Key, JsString(kv.Value)))
            : [];

    /// <summary>The span's events; entries without a name are dropped.</summary>
    public static IReadOnlyList<SpanEvent> Events(SpanDto? span)
    {
        if (span?.Events is not JsonArray arr) return [];
        var list = new List<SpanEvent>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject e) continue;
            var name = e.TryGetPropertyValue("name", out var n) && n is JsonValue nv && nv.GetValueKind() == JsonValueKind.String
                ? nv.GetValue<string>() : null;
            if (name is null) continue;
            var offset = e.TryGetPropertyValue("offset_us", out var o) ? AsNumber(o) ?? 0 : 0;
            list.Add(new SpanEvent(name, offset, e.TryGetPropertyValue("attributes", out var a) ? a as JsonObject : null));
        }
        return list;
    }

    /// <summary>Offset of the first event with this name, in microseconds.</summary>
    public static double? EventOffsetUs(SpanDto? span, string name)
    {
        foreach (var e in Events(span)) if (e.Name == name) return e.OffsetUs;
        return null;
    }

    private static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        // A tree built in code holds the CLR number it was given; one parsed from the API holds a JsonElement.
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        return null;
    }
}
