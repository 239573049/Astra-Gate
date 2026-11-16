using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// <c>JsonArray.Add(x)</c> with a <see cref="JsonObject"/> or a string binds to the generic overload, which the AOT analyzers refuse;
/// these bind to the <see cref="JsonNode"/> one.
/// </summary>
internal static class JsonArrays
{
    public static void Push(this JsonArray array, JsonNode? node) => array.Add(node);

    public static void Push(this JsonArray array, string value) => array.Add((JsonNode?)JsonValue.Create(value));
}
