using System.Text.Json.Nodes;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Http;

/// <summary>
/// Local token estimate for providers that cannot count (GitHub Copilot has no countTokens). It counts what the model
/// reads — text, tool declarations, call arguments — rather than the JSON envelope, so field names and braces do not
/// inflate the number the way a byte count does.
/// </summary>
internal static class TokenEstimator
{
    /// <summary>Gemini bills an image or file part as a fixed 258 tokens (one tile).</summary>
    private const int MediaTokens = 258;

    /// <summary>Per-turn framing a chat template adds around each content.</summary>
    private const int TurnOverhead = 4;

    /// <summary>
    /// Gemini <c>countTokens</c> body: either <c>{contents}</c> or <c>{generateContentRequest:{contents, systemInstruction,
    /// tools}}</c> (what the SDKs send when a system prompt or tools are involved).
    /// </summary>
    public static int Gemini(JsonObject body)
    {
        var root = Fields.Node(body, "generateContentRequest", "generate_content_request") as JsonObject ?? body;
        var total = 0;
        if (Fields.Node(root, "systemInstruction", "system_instruction") is { } system) total += Content(system);
        if (Fields.Node(root, "contents") is JsonArray contents)
            foreach (var content in contents) total += TurnOverhead + Content(content);
        if (Fields.Node(root, "tools") is { } tools) total += Text(tools.ToJsonString());
        return Math.Max(1, total);
    }

    private static int Content(JsonNode? node)
    {
        if (node is not JsonObject content) return 0;
        var total = 0;
        if (Fields.Node(content, "parts") is not JsonArray parts) return 0;
        foreach (var part in parts)
        {
            if (part is not JsonObject p) continue;
            if (Fields.Str(p, "text") is { } text) total += Text(text);
            else if (Fields.Node(p, "inlineData", "inline_data", "fileData", "file_data") is not null) total += MediaTokens;
            else if (Fields.Node(p, "functionCall", "function_call", "functionResponse", "function_response") is { } call)
                total += Text(call.ToJsonString());
        }
        return total;
    }

    /// <summary>
    /// CJK and other wide scripts are about one token per character; Latin text runs near four characters per token.
    /// </summary>
    internal static int Text(string text)
    {
        double tokens = 0;
        foreach (var c in text) tokens += c >= 0x2E80 ? 1.0 : 0.25;
        return (int)Math.Ceiling(tokens);
    }
}
