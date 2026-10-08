using System.Text.Json.Nodes;

namespace Astra.Core;

/// <summary>
/// AOT-safe helpers for mutating <see cref="JsonNode"/> trees.
///
/// <c>JsonArray.Add&lt;T&gt;</c> and <c>JsonNode.ReplaceWith&lt;T&gt;</c> are annotated
/// <c>RequiresDynamicCode</c>/<c>RequiresUnreferencedCode</c>, which is a hard compile error under
/// <c>PublishAot</c> with warnings-as-errors. For a node-derived value the annotated generic takes an
/// internal fast path and behaves identically, while the interface/explicit routes below are not
/// annotated — so these helpers keep the behaviour and drop the warning. See the callers for the
/// "value is JsonNode" argument.
/// </summary>
public static class JsonNodes
{
    /// <summary>Appends <paramref name="node"/> to <paramref name="array"/> without <c>Add&lt;T&gt;</c>'s AOT annotations.</summary>
    public static void AddNode(this JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    /// <summary>
    /// Replaces <paramref name="node"/> in its parent with a text node. Equivalent to
    /// <c>node.ReplaceWith(text)</c> for a node that has a parent; a parentless root node is left
    /// untouched (as <c>ReplaceWith</c> does today).
    /// </summary>
    public static void ReplaceTextWith(this JsonNode node, string text)
    {
        JsonNode replacement = JsonValue.Create(text);
        switch (node.Parent)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (!ReferenceEquals(property.Value, node)) continue;
                    obj[property.Key] = replacement;
                    return;
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (!ReferenceEquals(array[i], node)) continue;
                    array[i] = replacement;
                    return;
                }
                break;
        }
    }
}
