using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Pricing;

/// <summary>
/// A pricing schedule in its storage format (snake_case, the <c>PricingSchedule</c> JSON of src/Astra.Core/Billing/Pricing.cs and
/// <c>PricingSchedule</c> in web/src/api/types.ts) as an immutable value with the edits of web/src/components/PricingEditor.tsx.
/// It wraps the raw JSON object instead of a typed copy so everything the editor does not know (<c>per_call</c> prices, fields a
/// newer server adds) survives an edit, exactly like the web spreading <c>{ ...p, ...patch }</c>. Every price is a
/// <see cref="decimal"/> written as a canonical JSON number (<see cref="PriceNumber"/>); nothing here uses <see cref="double"/>.
/// Every <c>With…</c> method returns a new document and compacts it like the web's <c>set()</c> (<c>compactSchedule</c>).
/// </summary>
public sealed class PricingDocument
{
    /// <summary>The token price columns of the editor, in the web's order (<c>TOKEN_TYPES</c>).</summary>
    public static readonly IReadOnlyList<string> TokenTypes =
    [
        "input", "cache_read", "cache_write_5m", "cache_write_30m", "cache_write_1h", "cache_write_12h", "output", "reasoning", "input_audio", "output_audio", "input_image", "output_image",
    ];

    /// <summary>Property name of the context tier list.</summary>
    public const string ContextTiersKey = "context_tiers";

    /// <summary>Property name of the time window list.</summary>
    public const string TimeWindowsKey = "time_windows";

    /// <summary>Property name of the service tier map.</summary>
    public const string ServiceTiersKey = "service_tiers";

    private readonly JsonObject _node;

    private PricingDocument(JsonObject node) => _node = node;

    /// <summary>A document with no prices yet (<c>emptySchedule()</c>): USD, per 1M tokens, base input and output unset.</summary>
    public static PricingDocument Empty() => new(new JsonObject
    {
        ["currency"] = "USD",
        ["unit"] = "per_1m_tokens",
        ["base"] = new JsonObject { ["input"] = null, ["output"] = null },
    });

    /// <summary>Copies a pricing JSON object (what the API carries in <c>pricing</c>); null for null or anything that is not an object.</summary>
    public static PricingDocument? FromNode(JsonNode? node) => node is JsonObject o ? new((JsonObject)o.DeepClone()) : null;

    /// <summary>
    /// Parses and minimally validates pricing JSON typed into the editor's JSON mode (<c>parsePricing</c>): blank is "no document"
    /// (null, no error); otherwise it must be an object with an object <c>base</c>; <c>currency</c> and <c>unit</c> default to USD and per_1m_tokens.
    /// </summary>
    public static (PricingDocument? Value, string? Error) Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        JsonObject copy;
        try
        {
            // A repeated key is an error (JS would silently keep the last one; for money an explicit error is safer than a
            // price that is not the one on the first line, and JsonNode would only fail later, on first access).
            if (JsonNode.Parse(text, null, new JsonDocumentOptions { AllowDuplicateProperties = false }) is not JsonObject obj) return (null, "not an object");
            if (obj["base"] is not JsonObject) return (null, "\"base\" is required");
            copy = (JsonObject)obj.DeepClone();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return (null, e.Message);
        }
        // {...v, currency: v.currency ?? 'USD', unit: v.unit ?? 'per_1m_tokens'}: absent or null values are filled in place / appended
        if (copy["currency"] is null) copy["currency"] = "USD";
        if (copy["unit"] is null) copy["unit"] = "per_1m_tokens";
        return (new PricingDocument(copy), null);
    }

    /// <summary>A copy of the underlying JSON, for sending to the API or handing to a bound property.</summary>
    public JsonObject ToNode() => (JsonObject)_node.DeepClone();

    /// <summary>The JSON text the editor's JSON mode shows (<c>pretty</c>: two-space indent, no escaping of non-ASCII).</summary>
    public string Pretty() => PrettyJson(_node);

    /// <summary>The document as compact JSON text, written like <c>JSON.stringify</c> (no escaping of <c>&gt;</c> or non-ASCII).</summary>
    public string ToJson() => CompactJson(_node);

    /// <summary>A JSON node as compact text without HTML-safe escaping.</summary>
    public static string CompactJson(JsonNode? node)
    {
        if (node is null) return "null";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary><c>pretty(value)</c> of web/src/lib/format.ts for a JSON node: indented JSON, empty for null.</summary>
    public static string PrettyJson(JsonNode? node)
    {
        if (node is null) return "";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// A comparison key for a JSON value: compact JSON in which numbers are written canonically (<c>2</c>, <c>2.0</c> and <c>2e0</c> are
    /// one value), key order kept like the web's <c>JSON.stringify</c> comparison. Two nodes are "the same" when their keys are equal.
    /// </summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        AppendCanonical(sb, node);
        return sb.ToString();
    }

    private static void AppendCanonical(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonValue.Create(k)!.ToJsonString()).Append(':');
                    AppendCanonical(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendCanonical(sb, a[i]);
                }
                sb.Append(']');
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                sb.Append(decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? PriceNumber.ToText(d) : v.ToJsonString());
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }

    /// <summary>Whether two pricing values are the same document (null equals null).</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    // ---------- reading ----------

    /// <summary>The base price of <paramref name="type"/> (<c>input</c>, <c>cache_read</c>, ...); null when unset.</summary>
    public decimal? BasePrice(string type) => PriceNumber.FromNode(Base()[type]);

    /// <summary>The flat per-request price (<c>per_request</c>); null when unset.</summary>
    public decimal? PerRequest => PriceNumber.FromNode(_node["per_request"]);

    /// <summary>Free-text notes; empty when none.</summary>
    public string Notes => _node["notes"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private JsonObject Base() => _node["base"] as JsonObject ?? new JsonObject();

    /// <summary>How many rules a section holds (<see cref="ContextTiersKey"/>, <see cref="TimeWindowsKey"/> or <see cref="ServiceTiersKey"/>).</summary>
    public int Count(string section) => _node[section] switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ => 0,
    };

    /// <summary>A reader over the <paramref name="index"/>th rule of a list section (<see cref="ContextTiersKey"/> or <see cref="TimeWindowsKey"/>).</summary>
    public RuleReader Rule(string section, int index)
        => new(_node[section] is JsonArray a && index >= 0 && index < a.Count ? a[index] as JsonObject : null);

    /// <summary>The service tiers in their stored order, as (name, rule) pairs.</summary>
    public IReadOnlyList<ServiceTierView> ServiceTiers
    {
        get
        {
            if (_node[ServiceTiersKey] is not JsonObject o) return [];
            var list = new List<ServiceTierView>(o.Count);
            foreach (var (name, rule) in o)
            {
                var r = rule as JsonObject;
                list.Add(new ServiceTierView(name, PriceNumber.FromNode(r?["multiplier"]), r?["prices"] is JsonObject));
            }
            return list;
        }
    }

    /// <summary>Names of the service tiers (what the rule dialog checks a new name against).</summary>
    public IReadOnlyList<string> ServiceTierNames => _node[ServiceTiersKey] is JsonObject o ? o.Select(kv => kv.Key).ToList() : [];

    // ---------- editing ----------

    /// <summary>Applies <paramref name="edit"/> to a copy and compacts it (<c>set(patch)</c> of the web editor).</summary>
    public PricingDocument With(Action<JsonObject> edit)
    {
        var copy = (JsonObject)_node.DeepClone();
        edit(copy);
        Compact(copy);
        return new PricingDocument(copy);
    }

    /// <summary>Removes empty rule lists and unset optional fields so the stored JSON stays minimal (<c>compactSchedule</c>).</summary>
    public PricingDocument Compacted() => With(_ => { });

    private static void Compact(JsonObject o)
    {
        if (o["context_tiers"] is not JsonArray { Count: > 0 }) o.Remove("context_tiers");
        if (o["time_windows"] is not JsonArray { Count: > 0 }) o.Remove("time_windows");
        if (o["service_tiers"] is not JsonObject { Count: > 0 }) o.Remove("service_tiers");
        if (o["per_request"] is null) o.Remove("per_request");
        if (o["notes"] is not JsonValue n || !n.TryGetValue<string>(out var s) || s.Length == 0) o.Remove("notes");
    }

    /// <summary>Sets (or clears, with null) the base price of <paramref name="type"/>; a cleared price stays as an explicit null like the web's.</summary>
    public PricingDocument WithBasePrice(string type, decimal? price) => With(o =>
    {
        if (o["base"] is not JsonObject b) o["base"] = b = new JsonObject();
        b[type] = PriceNumber.ToNode(price);
    });

    /// <summary>Sets (null removes) the flat per-request price.</summary>
    public PricingDocument WithPerRequest(decimal? price) => With(o => o["per_request"] = PriceNumber.ToNode(price));

    /// <summary>Sets the notes; an empty text removes them.</summary>
    public PricingDocument WithNotes(string? notes) => With(o => o["notes"] = string.IsNullOrEmpty(notes) ? null : notes);

    /// <summary>Edits one rule of a list section in place (on the copy).</summary>
    public PricingDocument WithRule(string section, int index, Action<JsonObject> edit) => With(o =>
    {
        if (o[section] is JsonArray a && index >= 0 && index < a.Count && a[index] is JsonObject rule) edit(rule);
    });

    /// <summary>Removes one rule of a list section.</summary>
    public PricingDocument WithoutRule(string section, int index) => With(o =>
    {
        if (o[section] is JsonArray a && index >= 0 && index < a.Count) a.RemoveAt(index);
    });

    /// <summary>Appends a rule to a list section, creating the list when absent.</summary>
    public PricingDocument WithRuleAdded(string section, JsonObject rule) => With(o =>
    {
        if (o[section] is not JsonArray list) o[section] = list = [];
        list.Add((JsonNode?)rule);
    });

    /// <summary>Sets a service tier (adding it, or replacing a tier of the same name).</summary>
    public PricingDocument WithServiceTier(string name, JsonObject rule) => With(o =>
    {
        if (o[ServiceTiersKey] is not JsonObject tiers) o[ServiceTiersKey] = tiers = new JsonObject();
        tiers[name] = rule;
    });

    /// <summary>Renames the <paramref name="index"/>th service tier; if the new name equals another tier's, that tier's rule is replaced (JS <c>Object.fromEntries</c>: the later entry wins, at the earlier position).</summary>
    public PricingDocument WithServiceTierRenamed(int index, string name) => RebuildServiceTiers(entries =>
    {
        if (index >= 0 && index < entries.Count) entries[index] = (name, entries[index].Rule);
    });

    /// <summary>Sets (null clears) the multiplier of the <paramref name="index"/>th service tier; its own prices stay.</summary>
    public PricingDocument WithServiceTierMultiplier(int index, decimal? multiplier) => RebuildServiceTiers(entries =>
    {
        if (index < 0 || index >= entries.Count) return;
        var rule = (JsonObject)(entries[index].Rule?.DeepClone() ?? new JsonObject());
        rule["multiplier"] = PriceNumber.ToNode(multiplier);
        entries[index] = (entries[index].Name, rule);
    });

    /// <summary>Removes the <paramref name="index"/>th service tier.</summary>
    public PricingDocument WithoutServiceTier(int index) => RebuildServiceTiers(entries =>
    {
        if (index >= 0 && index < entries.Count) entries.RemoveAt(index);
    });

    private PricingDocument RebuildServiceTiers(Action<List<(string Name, JsonObject? Rule)>> change) => With(o =>
    {
        var entries = new List<(string Name, JsonObject? Rule)>();
        if (o[ServiceTiersKey] is JsonObject tiers)
            foreach (var (name, rule) in tiers) entries.Add((name, rule as JsonObject));
        change(entries);
        var rebuilt = new JsonObject();
        foreach (var (name, rule) in entries)
        {
            var value = (JsonObject?)rule?.DeepClone();
            // a later entry with the same key replaces the value but keeps the first position, like Object.fromEntries
            if (rebuilt.ContainsKey(name)) rebuilt[name] = value;
            else rebuilt.Add(name, value);
        }
        o[ServiceTiersKey] = rebuilt;
    });
}

/// <summary>Reads the fields of one pricing rule (a context tier or a time window) without exposing the JSON.</summary>
public readonly struct RuleReader
{
    private readonly JsonObject? _rule;

    internal RuleReader(JsonObject? rule) => _rule = rule;

    /// <summary>A string field ("" when missing).</summary>
    public string Text(string field) => _rule?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>A number field, or null.</summary>
    public decimal? Number(string field) => PriceNumber.FromNode(_rule?[field]);

    /// <summary>A whole-number field (a token threshold), 0 when missing or fractional.</summary>
    public long Whole(string field) => Number(field) is { } d && d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue ? (long)d : 0;

    /// <summary>The price of a token type inside the rule's <c>prices</c> object; null when unset.</summary>
    public decimal? Price(string type) => PriceNumber.FromNode((_rule?["prices"] as JsonObject)?[type]);

    /// <summary>ISO weekdays of a time window (1 = Monday ... 7 = Sunday); all seven when the window has no <c>days</c> list.</summary>
    public IReadOnlyList<int> Days()
    {
        if (_rule?["days"] is not JsonArray a) return [1, 2, 3, 4, 5, 6, 7];
        var days = new List<int>();
        foreach (var n in a)
            if (PriceNumber.FromNode(n) is { } d && d == decimal.Truncate(d)) days.Add((int)d);
        return days;
    }

    /// <summary>Dates a time window skips (<c>yyyy-MM-dd</c>).</summary>
    public IReadOnlyList<string> ExcludeDates()
        => _rule?["exclude_dates"] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToList() : [];
}

/// <summary>A service tier as the editor lists it.</summary>
/// <param name="Name">The tier name (the <c>service_tier</c> a request carries).</param>
/// <param name="Multiplier">Price multiplier, null when the tier only has its own prices.</param>
/// <param name="HasOwnPrices">The rule also carries a <c>prices</c> object (the editor only notes it).</param>
public sealed record ServiceTierView(string Name, decimal? Multiplier, bool HasOwnPrices);
