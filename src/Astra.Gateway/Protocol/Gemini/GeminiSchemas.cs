using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Gateway.Protocol.Gemini;

/// <summary>
/// Gemini states a function's arguments (<c>parameters</c>) and a structured-output shape (<c>responseSchema</c>) in an
/// OpenAPI-subset <c>Schema</c>: its type names are an upper-case enum (<c>OBJECT</c>, <c>STRING</c>, …), a value that may be
/// null says <c>nullable: true</c>, and <c>propertyOrdering</c> hints at the order of the properties. The IR holds JSON Schema,
/// which the other protocols' encoders forward as it is and which spells its types in lower case, so a Schema has to be read as
/// one before it leaves the Gemini decoder: <c>OBJECT</c> is not a JSON Schema type, and GitHub Copilot's Gemini models refuse a
/// request that carries one with 400 "invalid request body". <c>parametersJsonSchema</c> and <c>responseJsonSchema</c> already are
/// JSON Schema and are not converted.
/// Only those three things change (lossy: the ordering hint is dropped). The rest of a Schema (<c>description</c>,
/// <c>enum</c>, <c>required</c>, <c>format</c>, bounds, <c>example</c>) is valid JSON Schema or harmless to it, and goes through as written.
/// </summary>
internal static class GeminiSchemas
{
    /// <summary>A Gemini <c>Schema</c> read as JSON Schema: a converted copy, <paramref name="schema"/> is not changed.</summary>
    public static JsonNode ToJsonSchema(JsonNode schema) => schema is JsonObject node ? Convert(node) : schema.DeepClone();

    private static JsonObject Convert(JsonObject schema)
    {
        // Built key by key, so the properties and keywords keep the order the client wrote them in.
        var result = new JsonObject();
        foreach (var (key, value) in schema)
        {
            switch (key)
            {
                case "nullable" or "propertyOrdering":
                    break; // JSON Schema has neither: nullable becomes part of the type below, the ordering hint is dropped
                case "type" when value is JsonValue typeNode && typeNode.TryGetValue<string>(out var type):
                    if (!type.Equals("TYPE_UNSPECIFIED", StringComparison.OrdinalIgnoreCase)) result[key] = type.ToLowerInvariant();
                    break;
                case "properties" when value is JsonObject properties:
                    var converted = new JsonObject();
                    foreach (var (name, property) in properties)
                        converted[name] = property is JsonObject child ? Convert(child) : property?.DeepClone();
                    result[key] = converted;
                    break;
                case "items" when value is JsonObject items:
                    result[key] = Convert(items);
                    break;
                case "anyOf" when value is JsonArray variants:
                    var alternatives = new JsonArray();
                    foreach (var variant in variants)
                        alternatives.AddNode(variant is JsonObject option ? Convert(option) : variant?.DeepClone());
                    result[key] = alternatives;
                    break;
                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }
        if (schema["nullable"] is JsonValue flag && flag.TryGetValue<bool>(out var nullable) && nullable) AllowNull(result);
        return result;
    }

    /// <summary><c>nullable: true</c>, said the JSON Schema way: in the type, or as one more alternative, and in the enum.</summary>
    private static void AllowNull(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeNode && typeNode.TryGetValue<string>(out var type))
        {
            if (type != "null") schema["type"] = new JsonArray(type, "null");
        }
        else if (schema["anyOf"] is JsonArray alternatives && !alternatives.Any(IsNullType))
        {
            alternatives.AddNode(new JsonObject { ["type"] = "null" });
        }
        if (schema["enum"] is JsonArray values && !values.Any(value => value is null)) values.AddNode(null);
    }

    private static bool IsNullType(JsonNode? schema) =>
        schema is JsonObject option && option["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "null";
}
