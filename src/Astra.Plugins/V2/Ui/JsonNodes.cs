using System.Text.Json.Nodes;

namespace Astra.Plugins.V2.Ui;

internal static class JsonNodes
{
    /// <summary>JsonArray.Add for a node: a JsonObject argument would bind the reflection-based Add&lt;T&gt; (not AOT-safe).</summary>
    public static void AddNode(this JsonArray array, JsonNode? node) => array.Add(node);
}
