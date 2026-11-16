using System.Text.Json.Nodes;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// The <c>reasoning</c> items of a Responses request's <c>input</c>. GitHub Copilot refuses encrypted reasoning issued
/// to an earlier session with 401 "input item does not belong to this connection"; removing <c>encrypted_content</c>
/// is what makes it accept the request again — removing the item ids is not (.agents/memory/copilot-responses-item-ids.md).
/// </summary>
public static class ResponsesReasoningItems
{
    /// <summary>
    /// A copy of <paramref name="body"/> whose reasoning input items carry no <c>encrypted_content</c>, or null when no
    /// item had any (then there is nothing to drop). The original tree is left as it is.
    /// </summary>
    public static JsonObject? WithoutEncryptedContent(JsonObject body)
    {
        if (body["input"] is not JsonArray input || !input.Any(HasEncryptedContent)) return null;
        var copy = (JsonObject)body.DeepClone();
        foreach (var item in (JsonArray)copy["input"]!)
            if (HasEncryptedContent(item)) ((JsonObject)item!).Remove("encrypted_content");
        return copy;
    }

    private static bool HasEncryptedContent(JsonNode? item) =>
        item is JsonObject o && o["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "reasoning"
        && o.ContainsKey("encrypted_content");
}
