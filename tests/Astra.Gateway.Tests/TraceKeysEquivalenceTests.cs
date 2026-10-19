using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Tracing;

namespace Astra.Gateway.Tests;

/// <summary>
/// The chain fingerprint is stored with every request (chain_key) and links calls across versions, so reading large
/// string values through pooled buffers must not change a single byte of it — nor the system-prompt length the request
/// shape reports. Compared here against the original string-based implementation, kept verbatim as the reference.
/// </summary>
public class TraceKeysEquivalenceTests
{
    private const int MaxFingerprintChars = 64 * 1024;

    public static TheoryData<string> Texts()
    {
        var emoji = "😀"; // a surrogate pair
        return new TheoryData<string>
        {
            "short",
            new string('a', 5000),
            string.Concat(Enumerable.Repeat("line \"quoted\"\n\ttab é 中文 " + emoji + " \\ /", 400)),
            string.Concat(Enumerable.Repeat("系统提示词，", 3000)),
            new string('x', MaxFingerprintChars - 1) + emoji + "tail",   // the cap cuts the pair in half
            new string('x', MaxFingerprintChars - 2) + emoji + "tail",
            new string('y', 70_000) + "\u0001\u001f",
            string.Concat(Enumerable.Repeat(emoji, 40_000)),
        };
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void Fingerprint_and_system_length_match_the_string_based_reading(string text)
    {
        foreach (var (protocol, json) in Bodies(text))
        {
            var body = (JsonObject)JsonObject.Create(JsonDocument.Parse(json).RootElement)!;
            var reference = (JsonObject)JsonObject.Create(JsonDocument.Parse(json).RootElement)!;
            Assert.Equal(ReferenceChainOf(protocol, reference), TraceKeys.ChainOf(protocol, body));
            Assert.Equal(ReferenceSystemChars(protocol, reference), TraceKeys.ShapeOf(protocol, body).SystemChars);
        }
    }

    private static IEnumerable<(ApiProtocol, string)> Bodies(string text)
    {
        var s = JsonSerializer.Serialize(text);
        yield return (ApiProtocol.Anthropic, $$"""{"system":{{s}},"messages":[{"role":"user","content":{{s}}}]}""");
        yield return (ApiProtocol.Anthropic, """{"system":[{"type":"text","text":""" + s + """},{"type":"text","text":"more"}],"messages":[{"role":"user","content":[{"type":"text","text":"""
            + s + """},{"type":"image","source":{}}]}]}""");
        yield return (ApiProtocol.OpenAIChat, $$"""{"messages":[{"role":"system","content":{{s}}},{"role":"developer","content":[{"type":"text","text":{{s}}}]},{"role":"user","content":{{s}}}]}""");
        yield return (ApiProtocol.OpenAIResponses, $$"""{"instructions":{{s}},"input":[{"role":"user","content":[{"type":"input_text","text":{{s}}}]}]}""");
        yield return (ApiProtocol.OpenAIResponses, $$"""{"instructions":{{s}},"input":{{s}}}""");
        yield return (ApiProtocol.Gemini, $$"""{"systemInstruction":{"parts":[{"text":{{s}}}]},"contents":[{"role":"user","parts":[{"text":{{s}}}]}]}""");
    }

    // ---------------------------------------------------------------- the original implementation (reference)

    private static string? ReferenceChainOf(ApiProtocol inbound, JsonObject body)
    {
        var system = new StringBuilder();
        var first = new StringBuilder();
        switch (inbound)
        {
            case ApiProtocol.Anthropic:
                CollectText(body["system"], system);
                if (body["messages"] is JsonArray messages)
                    CollectText(messages.OfType<JsonObject>().FirstOrDefault(m => Str(m, "role") == "user")?["content"], first);
                break;
            case ApiProtocol.OpenAIChat:
                if (body["messages"] is JsonArray chat)
                {
                    foreach (var m in chat.OfType<JsonObject>().Where(m => Str(m, "role") is "system" or "developer"))
                        CollectText(m["content"], system);
                    CollectText(chat.OfType<JsonObject>().FirstOrDefault(m => Str(m, "role") == "user")?["content"], first);
                }
                break;
            case ApiProtocol.OpenAIResponses:
                CollectText(body["instructions"], system);
                if (body["input"] is JsonValue inputText) CollectText(inputText, first);
                else if (body["input"] is JsonArray input)
                    CollectText(input.OfType<JsonObject>().FirstOrDefault(i => Str(i, "role") == "user")?["content"], first);
                break;
            case ApiProtocol.Gemini:
                CollectText(body["systemInstruction"] ?? body["system_instruction"], system);
                if (body["contents"] is JsonArray contents)
                    CollectText(contents.OfType<JsonObject>().FirstOrDefault(c => Str(c, "role") is null or "user")?["parts"], first);
                break;
        }
        if (system.Length == 0 && first.Length == 0) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(system.ToString()));
        hash.AppendData("\n\0\n"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(first.ToString()));
        return "fp:" + Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 12));
    }

    private static long? ReferenceSystemChars(ApiProtocol inbound, JsonObject body)
    {
        var system = inbound switch
        {
            ApiProtocol.Anthropic => TextLength(body["system"]),
            ApiProtocol.OpenAIResponses => TextLength(body["instructions"]),
            ApiProtocol.Gemini => TextLength(body["systemInstruction"] ?? body["system_instruction"]),
            _ => body["messages"] is JsonArray all
                ? all.OfType<JsonObject>().Where(m => Str(m, "role") is "system" or "developer").Sum(m => TextLength(m["content"]) ?? 0)
                : null,
        };
        return system is > 0 ? system : null;
    }

    private static void CollectText(JsonNode? node, StringBuilder into)
    {
        if (node is null || into.Length >= MaxFingerprintChars) return;
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                Append(into, s);
                break;
            case JsonArray a:
                foreach (var item in a) CollectText(item, into);
                break;
            case JsonObject o:
                if (Str(o, "type") is "tool_result" or "function_call_output" or "image" or "image_url" or "input_image" or "document") return;
                if (o["text"] is JsonValue t && t.TryGetValue<string>(out var text)) Append(into, text);
                else if (o["parts"] is JsonArray parts) CollectText(parts, into);
                else if (o["content"] is { } content) CollectText(content, into);
                break;
        }
    }

    private static void Append(StringBuilder into, string text)
    {
        var room = MaxFingerprintChars - into.Length;
        if (room <= 0) return;
        into.Append(text.Length <= room ? text : text[..room]).Append('\n');
    }

    private static long? TextLength(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
        JsonArray a => a.Sum(x => TextLength(x is JsonObject o ? o["text"] ?? o["parts"] : x) ?? 0),
        JsonObject o => TextLength(o["parts"] ?? o["text"]),
        _ => null,
    };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
