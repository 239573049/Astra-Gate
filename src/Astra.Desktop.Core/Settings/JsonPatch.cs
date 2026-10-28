using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// Builds the partial-update bodies the admin API takes as <c>JsonObject</c> (PATCH /api/settings, /api/webhooks/{id},
/// /api/alerts/rules/{id}): only the keys a caller sets are sent, and an explicit null clears a value — the web's
/// <c>Partial&lt;Settings&gt;</c> semantic, which a typed DTO with nullable fields cannot express.
/// </summary>
public static class JsonPatch
{
    /// <summary>An object from key / value pairs; values may be string, bool, int, long, double, decimal, <see cref="JsonNode"/> or null.</summary>
    public static JsonObject Of(params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields) o[key] = Node(value);
        return o;
    }

    /// <summary>One value as a node (no reflection: only the primitives the API takes).</summary>
    public static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create(m),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => throw new ArgumentException($"unsupported patch value {value.GetType().Name}"),
    };

    /// <summary>A JSON array of strings (a webhook's rule ids, a rule's subjects).</summary>
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
}
