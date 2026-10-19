using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tracing;

/// <summary>What the last message of a request says about the agent loop (.plan/observability.md §3.2).</summary>
/// <param name="ToolResultIds">Ids of the tool calls this request answers (the trailing tool results), in order.</param>
/// <param name="UserTurn">The last message is the user's own input: a new turn, or a sub-agent / side call starting.</param>
public readonly record struct LastMessage(IReadOnlyList<string> ToolResultIds, bool UserTurn);

/// <summary>
/// Reads the agent-side identity of a request from the client's own body and headers, in its own API (before any
/// translation, so the pass-through and conversion paths agree; .plan/observability.md §3): the conversation (session
/// key), the call chain (chain key) and the tool calls the request answers. Never throws; unknown shapes give nothing.
/// </summary>
public static class TraceKeys
{
    /// <summary>Text taken into a chain fingerprint per part (system / first user message): enough to tell chains apart.</summary>
    private const int MaxFingerprintChars = 64 * 1024;

    /// <summary>Session ids kept verbatim are at most this long and use [A-Za-z0-9_-]; anything else is hashed.</summary>
    private const int MaxRawIdLength = 100;

    /// <summary>
    /// String values longer than this (raw JSON bytes) are read through a pooled buffer rather than as one string: an
    /// agent's system prompt can run to hundreds of KB, and each copy would land on the large object heap.
    /// </summary>
    private const int LargeValueBytes = 4096;

    /// <summary>The trailing tool results of the request and whether its last message is user input.</summary>
    public static LastMessage LastMessageOf(ApiProtocol inbound, JsonObject body)
    {
        try
        {
            return inbound switch
            {
                ApiProtocol.Anthropic => Anthropic(body),
                ApiProtocol.OpenAIChat => Chat(body),
                ApiProtocol.OpenAIResponses => Responses(body),
                ApiProtocol.Gemini => Gemini(body),
                _ => new([], false),
            };
        }
        catch (InvalidOperationException)
        {
            // A node of an unexpected kind: nothing to link by.
            return new([], false);
        }
    }

    /// <summary>
    /// The conversation key: Claude Code's session id (header, else <c>metadata.user_id</c>), Codex's <c>session-id</c>
    /// header, a hashed <c>prompt_cache_key</c>, else the chain fingerprint.
    /// </summary>
    public static string? SessionOf(ApiProtocol inbound, JsonObject body, IHeaderDictionary headers, string? chainKey)
    {
        if (inbound == ApiProtocol.Anthropic)
        {
            if (headers["x-claude-code-session-id"].ToString().Trim() is { Length: > 0 } header) return Keep("cc:", header);
            if (body["metadata"] is JsonObject meta && Str(meta, "user_id") is { } userId && StickyKeys.SessionIdOf(userId) is { Length: > 0 } session)
                return Keep("cc:", session);
        }
        if (inbound is ApiProtocol.OpenAIResponses or ApiProtocol.OpenAIChat)
        {
            if (headers["session-id"].ToString().Trim() is { Length: > 0 } codex) return Keep("codex:", codex);
            if (Str(body, "prompt_cache_key") is { } cacheKey) return Hash(cacheKey);
        }
        return chainKey;
    }

    /// <summary>
    /// Fingerprint of the call chain — system prompt plus first user message — which stays the same for every call of
    /// one agent's loop (the history is re-sent each time) and differs for a sub-agent or side call. Null when both are empty.
    /// </summary>
    public static string? ChainOf(ApiProtocol inbound, JsonObject body)
    {
        var system = new StringBuilder();
        var first = new StringBuilder();
        try
        {
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
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (system.Length == 0 && first.Length == 0) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, system);
        hash.AppendData("\n\0\n"u8);
        AppendUtf8(hash, first);
        return "fp:" + Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 12));
    }

    /// <summary>The span id of a W3C <c>traceparent</c> header and its trace id; nulls when absent or malformed.</summary>
    public static (string? TraceId, string? SpanId) ParseTraceparent(string? header)
    {
        // version "-" trace-id(32) "-" parent-id(16) "-" flags(2)
        if (header is not { Length: >= 55 }) return (null, null);
        var parts = header.Trim().Split('-');
        if (parts.Length < 4 || parts[0].Length != 2 || parts[1].Length != 32 || parts[2].Length != 16) return (null, null);
        if (!IsLowerHex(parts[1]) || !IsLowerHex(parts[2]) || parts[1].All(c => c == '0') || parts[2].All(c => c == '0')) return (null, null);
        return (parts[1], parts[2]);
    }

    // ------------------------------------------------------------------ per protocol

    private static LastMessage Anthropic(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return new([], false);
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            var role = Str(m, "role");
            if (role == "system") continue; // the decoder hoists these into the system prompt
            if (role != "user") return new([], false); // an assistant prefill
            var ids = new List<string>();
            if (m["content"] is JsonArray blocks)
                foreach (var b in blocks.OfType<JsonObject>())
                    if (Str(b, "type") == "tool_result" && Str(b, "tool_use_id") is { } id) ids.Add(id);
            return new(ids, ids.Count == 0);
        }
        return new([], false);
    }

    private static LastMessage Chat(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return new([], false);
        var ids = new List<string>();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            var role = Str(m, "role");
            if (role is "system" or "developer") continue;
            if (role == "tool")
            {
                if (Str(m, "tool_call_id") is { } id) ids.Insert(0, id);
                continue;
            }
            if (ids.Count > 0) return new(ids, false);
            return new([], role == "user");
        }
        return new(ids, false);
    }

    private static LastMessage Responses(JsonObject body)
    {
        if (body["input"] is JsonValue) return new([], true); // a string input is the user's message
        if (body["input"] is not JsonArray input) return new([], false);
        // Everything after the last message item: parallel calls may interleave calls and their outputs.
        var ids = new List<string>();
        for (var i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is not JsonObject item) continue;
            var type = Str(item, "type");
            if (type is "function_call_output" or "custom_tool_call_output")
            {
                if (Str(item, "call_id") is { } id) ids.Insert(0, id);
                continue;
            }
            if (type is "message" || (type is null && item["role"] is not null))
                return ids.Count > 0 ? new(ids, false) : new([], Str(item, "role") == "user");
        }
        return new(ids, false);
    }

    private static LastMessage Gemini(JsonObject body)
    {
        if (body["contents"] is not JsonArray contents || contents.Count == 0 || contents[^1] is not JsonObject last) return new([], false);
        if (Str(last, "role") is "model" or "assistant") return new([], false);
        var ids = new List<string>();
        var answers = false;
        if (last["parts"] is JsonArray parts)
        {
            foreach (var part in parts.OfType<JsonObject>())
            {
                if ((part["functionResponse"] ?? part["function_response"]) is not JsonObject response) continue;
                answers = true;
                // Gemini ids are optional; without one the call is linked by session instead.
                if (Str(response, "id") is { } id) ids.Add(id);
            }
        }
        return new(ids, !answers);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Appends the text found in a content node (a string, text blocks, parts), skipping tool results and media.</summary>
    private static void CollectText(JsonNode? node, StringBuilder into)
    {
        if (node is null || into.Length >= MaxFingerprintChars) return;
        switch (node)
        {
            case JsonValue v:
                AppendString(into, v);
                break;
            case JsonArray a:
                foreach (var item in a) CollectText(item, into);
                break;
            case JsonObject o:
                if (Str(o, "type") is "tool_result" or "function_call_output" or "image" or "image_url" or "input_image" or "document") return;
                if (o["text"] is JsonValue t && AppendString(into, t)) break;
                if (o["parts"] is JsonArray parts) CollectText(parts, into);
                else if (o["content"] is { } content) CollectText(content, into);
                break;
        }
    }

    /// <summary>Appends a string value's text (see <see cref="Append"/>); false, appending nothing, when it is no string.</summary>
    private static bool AppendString(StringBuilder into, JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            if (into.Length >= MaxFingerprintChars) return true;
            var raw = JsonMarshal.GetRawUtf8Value(element);
            if (raw.Length > LargeValueBytes)
            {
                var buffer = ArrayPool<char>.Shared.Rent(raw.Length);
                try
                {
                    Append(into, buffer.AsSpan(0, Unescape(raw, buffer)));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
                return true;
            }
        }
        if (!value.TryGetValue<string>(out var s)) return false;
        Append(into, s);
        return true;
    }

    /// <summary>A string value's length in UTF-16 code units (what <see cref="string.Length"/> gives); null when it is no string.</summary>
    private static long? StringLength(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind != JsonValueKind.String) return null;
            var raw = JsonMarshal.GetRawUtf8Value(element);
            if (raw.Length > LargeValueBytes)
            {
                if (!raw.Contains((byte)'\\')) return Encoding.UTF8.GetCharCount(raw[1..^1]);
                var buffer = ArrayPool<char>.Shared.Rent(raw.Length);
                try
                {
                    return Unescape(raw, buffer);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
            }
        }
        return value.TryGetValue<string>(out var s) ? s.Length : null;
    }

    /// <summary>Unescapes a raw JSON string (quotes included) into <paramref name="into"/>, which must hold its byte length.</summary>
    private static int Unescape(ReadOnlySpan<byte> rawString, Span<char> into)
    {
        var reader = new Utf8JsonReader(rawString);
        reader.Read();
        return reader.CopyString(into);
    }

    private static void Append(StringBuilder into, ReadOnlySpan<char> text)
    {
        var room = MaxFingerprintChars - into.Length;
        if (room <= 0) return;
        into.Append(text.Length <= room ? text : text[..room]).Append('\n');
    }

    /// <summary>
    /// Hashes the UTF-8 of <paramref name="text"/> chunk by chunk — the same bytes as
    /// <c>Encoding.UTF8.GetBytes(text.ToString())</c> (the stateful encoder carries a surrogate pair across chunks and
    /// replaces a lone one), without the string and byte copies.
    /// </summary>
    private static void AppendUtf8(IncrementalHash hash, StringBuilder text)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        var bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            foreach (var chunk in text.GetChunks())
            {
                var chars = chunk.Span;
                while (!chars.IsEmpty)
                {
                    encoder.Convert(chars, bytes, flush: false, out var used, out var written, out _);
                    hash.AppendData(bytes, 0, written);
                    chars = chars[used..];
                }
            }
            encoder.Convert([], bytes, flush: true, out _, out var tail, out _);
            hash.AppendData(bytes, 0, tail);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static string Keep(string prefix, string id) =>
        id.Length <= MaxRawIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? prefix + id : Hash(prefix + id);

    private static string Hash(string value) =>
        "h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 12));

    private static bool IsLowerHex(string s) => s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// The shape of a request without its content (.plan/observability.md §5): how many messages and tool definitions it
    /// carries, how long its system prompt is (characters) and the output cap it asks for. Null where the API has none.
    /// </summary>
    public static RequestShape ShapeOf(ApiProtocol inbound, JsonObject body)
    {
        var messages = inbound switch
        {
            ApiProtocol.OpenAIResponses => body["input"] switch { JsonArray input => input.Count, JsonValue => 1, _ => (int?)null },
            ApiProtocol.Gemini => (body["contents"] as JsonArray)?.Count,
            _ => (body["messages"] as JsonArray)?.Count,
        };
        int? tools = body["tools"] is JsonArray list
            ? inbound == ApiProtocol.Gemini
                ? list.Sum(t => ((t as JsonObject)?["functionDeclarations"] ?? (t as JsonObject)?["function_declarations"]) is JsonArray d ? d.Count : 1)
                : list.Count
            : null;
        var system = inbound switch
        {
            ApiProtocol.Anthropic => TextLength(body["system"]),
            ApiProtocol.OpenAIResponses => TextLength(body["instructions"]),
            ApiProtocol.Gemini => TextLength(body["systemInstruction"] ?? body["system_instruction"]),
            _ => body["messages"] is JsonArray all
                ? all.OfType<JsonObject>().Where(m => Str(m, "role") is "system" or "developer").Sum(m => TextLength(m["content"]) ?? 0)
                : null,
        };
        var max = inbound switch
        {
            ApiProtocol.Anthropic => Num(body["max_tokens"]),
            ApiProtocol.OpenAIResponses => Num(body["max_output_tokens"]),
            ApiProtocol.Gemini => Num((body["generationConfig"] as JsonObject)?["maxOutputTokens"] ?? (body["generation_config"] as JsonObject)?["max_output_tokens"]),
            _ => Num(body["max_completion_tokens"]) ?? Num(body["max_tokens"]),
        };
        return new RequestShape(messages, tools, system is > 0 ? system : null, max);
    }

    /// <summary>Characters of text in a string, a list of blocks / parts, or an object with <c>parts</c>.</summary>
    private static long? TextLength(JsonNode? node) => node switch
    {
        JsonValue v => StringLength(v),
        JsonArray a => a.Sum(x => TextLength(x is JsonObject o ? o["text"] ?? o["parts"] : x) ?? 0),
        JsonObject o => TextLength(o["parts"] ?? o["text"]),
        _ => null,
    };

    private static long? Num(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<long>(out var n) || (v.TryGetValue<double>(out var d) && (n = (long)d) == d)) ? n : null;
}

/// <summary>What a request asks for, without its content: messages, tool definitions, system prompt characters, output cap.</summary>
public readonly record struct RequestShape(int? Messages, int? Tools, long? SystemChars, long? MaxTokens);
