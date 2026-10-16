using System.Text.Json.Nodes;

namespace Astra.Gateway.Protocol.Anthropic;

/// <summary>
/// Anthropic's validator rejects a custom tool whose <c>input_schema</c> has <c>oneOf</c> / <c>allOf</c> / <c>anyOf</c> at
/// the top level (nested inside <c>properties</c> or <c>$defs</c> is fine): "input_schema does not support oneOf, allOf,
/// or anyOf at the top level". Clients built for other backends send such schemas (a conditional <c>allOf</c> on a search
/// tool, a <c>oneOf</c> on an MCP forwarder), and some relays pass them on untouched. The top-level keywords are dropped
/// (lossy: the conditional constraints are gone, the properties stay) and <c>type: "object"</c> is guaranteed.
/// </summary>
public static class AnthropicToolSchemas
{
    private static readonly string[] TopLevelKeywords = ["oneOf", "allOf", "anyOf"];

    /// <summary>Whether <paramref name="schema"/> would be rejected, or fixed by <see cref="Normalize(JsonObject)"/>.</summary>
    public static bool NeedsNormalizing(JsonObject schema) =>
        TopLevelKeywords.Any(schema.ContainsKey);

    /// <summary>Strips the top-level combinators of one schema in place; true when it changed.</summary>
    public static bool Normalize(JsonObject schema)
    {
        var changed = false;
        foreach (var keyword in TopLevelKeywords)
            changed |= schema.Remove(keyword);
        if (changed && schema["type"]?.GetValueKind() != System.Text.Json.JsonValueKind.String)
            schema["type"] = "object";
        return changed;
    }

    /// <summary>Whether any custom tool in an Anthropic request body carries a schema that needs <see cref="Normalize(JsonObject)"/>.</summary>
    public static bool NeedsNormalizingTools(JsonObject body) => Schemas(body).Any(NeedsNormalizing);

    /// <summary>Normalizes every custom tool's schema of an Anthropic request body in place; true when any changed.</summary>
    public static bool NormalizeTools(JsonObject body)
    {
        var changed = false;
        foreach (var schema in Schemas(body).ToList())
            changed |= Normalize(schema);
        return changed;
    }

    private static IEnumerable<JsonObject> Schemas(JsonObject body)
    {
        if (body["tools"] is not JsonArray tools) yield break;
        foreach (var t in tools)
        {
            // Typed / server tools carry no input_schema of the client's making.
            if (t is JsonObject tool && IsCustom(tool) && tool["input_schema"] is JsonObject schema)
                yield return schema;
        }
    }

    private static bool IsCustom(JsonObject tool) =>
        tool["type"] is null || (tool["type"] is JsonValue v && v.TryGetValue<string>(out var type) && type == "custom");
}
