using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Requests;

namespace Astra.Gateway.Tracing;

/// <summary>
/// Estimates what a request's prompt is made of — system instructions, tool definitions, earlier messages, the latest turn — from the
/// client's own request body, for the request log's context-window view (the idea is magpie's "what is occupying the window", MIT).
/// It counts text characters, not tokens: the read side scales the shares to the input tokens the upstream reported, so only the
/// proportions matter. Inline images and files count a flat amount, so a base64 payload does not swamp the text.
/// </summary>
public static class ContextEstimator
{
    /// <summary>What an inline image or file weighs, in characters (a typical image is ~1,500 tokens).</summary>
    private const int MediaWeight = 4_000;

    /// <summary>The shares for a body of <paramref name="protocol"/>; null when there is nothing to measure.</summary>
    public static ContextShares? Estimate(ApiProtocol protocol, JsonObject body)
    {
        double system = 0, tools = 0, history = 0, latest = 0;
        JsonArray? turns = null;
        switch (protocol)
        {
            case ApiProtocol.Anthropic:
                system = Weight(body["system"]);
                tools = Raw(body["tools"]);
                turns = body["messages"] as JsonArray;
                break;
            case ApiProtocol.OpenAIChat:
                tools = Raw(body["tools"]) + Raw(body["functions"]);
                if (body["messages"] is JsonArray chat)
                {
                    // system / developer messages are instructions wherever they stand; the rest is the conversation.
                    var rest = new JsonArray();
                    foreach (var m in chat)
                    {
                        if (m is JsonObject o && IsInstruction(o)) system += Weight(o);
                        else rest.Add(m?.DeepClone());
                    }
                    turns = rest;
                }
                break;
            case ApiProtocol.OpenAIResponses:
                system = Weight(body["instructions"]);
                tools = Raw(body["tools"]);
                if (body["input"] is JsonArray input)
                {
                    var rest = new JsonArray();
                    foreach (var m in input)
                    {
                        if (m is JsonObject o && IsInstruction(o)) system += Weight(o);
                        else rest.Add(m?.DeepClone());
                    }
                    turns = rest;
                }
                else history = Weight(body["input"]);
                break;
            case ApiProtocol.Gemini:
                system = Weight(body["systemInstruction"] ?? body["system_instruction"]);
                tools = Raw(body["tools"]);
                turns = body["contents"] as JsonArray;
                break;
        }
        if (turns is { Count: > 0 })
        {
            // The latest turn is the last message — the user's new input, or the tool results answering the model's last calls.
            var last = turns.Count - 1;
            latest = Weight(turns[last]);
            for (var i = 0; i < last; i++) history += Weight(turns[i]);
        }
        var total = system + tools + history + latest;
        if (total <= 0) return null;
        return new ContextShares(Round(system / total), Round(tools / total), Round(history / total), Round(latest / total));
    }

    /// <summary>Tool definitions count by their JSON text: names, descriptions and parameter schemas all sit in the prompt.</summary>
    private static double Raw(JsonNode? node) => node?.ToJsonString().Length ?? 0;

    private static bool IsInstruction(JsonObject m) =>
        m["role"] is JsonValue r && r.TryGetValue<string>(out var role) && role is "system" or "developer";

    private static double Round(double v) => Math.Round(v, 4);

    /// <summary>Characters of text a JSON subtree carries: string values count in full, keys and structure do not, media a flat weight.</summary>
    private static double Weight(JsonNode? node)
    {
        switch (node)
        {
            case null: return 0;
            case JsonValue v:
                return v.TryGetValue<string>(out var s) ? s.Length : 0;
            case JsonArray a:
                double sum = 0;
                foreach (var item in a) sum += Weight(item);
                return sum;
            case JsonObject o:
                var type = o["type"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
                if (type is "image" or "image_url" or "input_image" or "file" or "input_file" or "document" || o.ContainsKey("inlineData")
                    || o.ContainsKey("inline_data") || o.ContainsKey("fileData") || o.ContainsKey("file_data"))
                    return MediaWeight;
                double total = 0;
                foreach (var (key, value) in o)
                {
                    // An opaque id or signature is not prompt text.
                    if (key is "role" or "type" or "media_type" or "mime_type" or "id" or "tool_use_id" or "tool_call_id" or "call_id" or "signature" or "encrypted_content" or "cache_control") continue;
                    total += Weight(value);
                }
                return total;
            default: return 0;
        }
    }
}
