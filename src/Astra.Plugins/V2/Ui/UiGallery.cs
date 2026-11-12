using System.Text.Json.Nodes;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2.Ui;

/// <summary>
/// The component gallery (design/plugin-v2 06 §2): every scene of the UI scene set (tests/fixtures/plugin-ui, bundled as
/// scenes.json) normalised the way a plugin's tree is, so the shells render exactly what a plugin view would send — and the
/// same scenes the two shells are compared on (06 §5).
/// </summary>
public static class UiGallery
{
    private static readonly Lazy<JsonArray> All = new(Build);

    /// <summary>Each scene: id, component (null for a composite), state, the normalised tree and any warnings (none expected).</summary>
    public static JsonArray Scenes() => (JsonArray)All.Value.DeepClone();

    private static JsonArray Build()
    {
        var doc = JsonNode.Parse(V2Assets.ScenesJson)!.AsObject();
        var list = new JsonArray();
        foreach (var scene in doc["scenes"]!.AsArray().OfType<JsonObject>())
        {
            var warnings = new List<string>();
            var tree = UiTree.Normalize(scene["tree"], ActionsIn(scene["tree"]), UiSchema.Default, warnings);
            list.AddNode(new JsonObject
            {
                ["id"] = scene["id"]?.DeepClone(),
                ["component"] = scene["component"]?.DeepClone(),
                ["state"] = scene["state"]?.DeepClone(),
                ["tree"] = tree,
                ["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode?)JsonValue.Create(w))]),
            });
        }
        return list;
    }

    // the action names a scene's event props use, so normalising keeps them (a gallery view "has" every action it names)
    private static HashSet<string> ActionsIn(JsonNode? node)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o["action"] is JsonValue a && a.TryGetValue<string>(out var name)) set.Add(name);
                    foreach (var (key, value) in o)
                    {
                        if (key.Length > 2 && key.StartsWith("on", StringComparison.Ordinal) && char.IsUpper(key[2])
                            && value is JsonValue v && v.TryGetValue<string>(out var s)) set.Add(s);
                        Walk(value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr) Walk(item);
                    break;
            }
        }
        Walk(node);
        return set;
    }
}
