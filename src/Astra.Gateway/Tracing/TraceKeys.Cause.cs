using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Requests;

namespace Astra.Gateway.Tracing;

public static partial class TraceKeys
{
    /// <summary>The most of the user's prompt kept when <see cref="AppSettings.TraceContent"/> is on.</summary>
    public const int MaxPromptChars = 300;

    /// <summary>Tool results described per request; a parallel batch beyond this is cut.</summary>
    private const int MaxToolResults = 32;

    /// <summary>
    /// Why the request was sent (<see cref="RequestCause"/>): the user's own input, or the results of tool calls — each with
    /// its tool's name (from the call it answers, found in the history the request re-sends), its error mark and its size.
    /// <paramref name="withPrompt"/> keeps the start of the user's message (callers pass the guarded body, so the privacy
    /// guard's redactions apply). Null when the last message is neither (an assistant prefill, an unknown shape). Never throws.
    /// </summary>
    public static RequestCause? CauseOf(ApiProtocol inbound, JsonObject body, bool withPrompt)
    {
        try
        {
            return inbound switch
            {
                ApiProtocol.Anthropic => AnthropicCause(body, withPrompt),
                ApiProtocol.OpenAIChat => ChatCause(body, withPrompt),
                ApiProtocol.OpenAIResponses => ResponsesCause(body, withPrompt),
                ApiProtocol.Gemini => GeminiCause(body, withPrompt),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static RequestCause? AnthropicCause(JsonObject body, bool withPrompt)
    {
        if (body["messages"] is not JsonArray messages) return null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            var role = Str(m, "role");
            if (role == "system") continue;
            if (role != "user") return null;
            var results = new List<ToolResultInfo>();
            if (m["content"] is JsonArray blocks)
            {
                Dictionary<string, string>? names = null;
                foreach (var b in blocks.OfType<JsonObject>())
                {
                    if (Str(b, "type") != "tool_result" || results.Count >= MaxToolResults) continue;
                    names ??= AnthropicToolNames(messages, i);
                    var id = Str(b, "tool_use_id");
                    results.Add(new ToolResultInfo(id is not null && names.TryGetValue(id, out var name) ? name : null,
                        b["is_error"] is JsonValue e && e.TryGetValue<bool>(out var error) ? error : false, TextLength(b["content"]) ?? 0));
                }
            }
            if (results.Count > 0) return new RequestCause(RequestCauseKinds.ToolResult, results, null);
            return new RequestCause(RequestCauseKinds.User, [], withPrompt ? PromptOf(m["content"]) : null);
        }
        return null;
    }

    /// <summary>tool_use id → name of the assistant message before message <paramref name="before"/>.</summary>
    private static Dictionary<string, string> AnthropicToolNames(JsonArray messages, int before)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = before - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m || Str(m, "role") != "assistant") continue;
            if (m["content"] is JsonArray blocks)
                foreach (var b in blocks.OfType<JsonObject>())
                    if (Str(b, "type") is "tool_use" or "server_tool_use" && Str(b, "id") is { } id && Str(b, "name") is { } name) names[id] = name;
            break;
        }
        return names;
    }

    private static RequestCause? ChatCause(JsonObject body, bool withPrompt)
    {
        if (body["messages"] is not JsonArray messages) return null;
        var results = new List<(string? Id, long Chars)>();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            var role = Str(m, "role");
            if (role is "system" or "developer") continue;
            if (role == "tool")
            {
                if (results.Count < MaxToolResults) results.Insert(0, (Str(m, "tool_call_id"), TextLength(m["content"]) ?? 0));
                continue;
            }
            if (results.Count > 0)
            {
                // The assistant message these results answer: its tool_calls name them.
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                if (role == "assistant" && m["tool_calls"] is JsonArray calls)
                    foreach (var c in calls.OfType<JsonObject>())
                        if (Str(c, "id") is { } id && (c["function"] as JsonObject is { } f ? Str(f, "name") : Str(c, "name")) is { } name) names[id] = name;
                return new RequestCause(RequestCauseKinds.ToolResult,
                    results.Select(r => new ToolResultInfo(r.Id is not null && names.TryGetValue(r.Id, out var n) ? n : null, null, r.Chars)).ToList(), null);
            }
            return role == "user" ? new RequestCause(RequestCauseKinds.User, [], withPrompt ? PromptOf(m["content"]) : null) : null;
        }
        return null;
    }

    private static RequestCause? ResponsesCause(JsonObject body, bool withPrompt)
    {
        if (body["input"] is JsonValue text)
            return new RequestCause(RequestCauseKinds.User, [], withPrompt ? PromptOf(text) : null);
        if (body["input"] is not JsonArray input) return null;
        var results = new List<(string? Id, long Chars)>();
        var i = input.Count - 1;
        for (; i >= 0; i--)
        {
            if (input[i] is not JsonObject item) continue;
            var type = Str(item, "type");
            if (type is "function_call_output" or "custom_tool_call_output")
            {
                if (results.Count < MaxToolResults) results.Insert(0, (Str(item, "call_id"), TextLength(item["output"]) ?? 0));
                continue;
            }
            if (type is "message" || (type is null && item["role"] is not null))
            {
                if (results.Count > 0) break;
                return Str(item, "role") == "user" ? new RequestCause(RequestCauseKinds.User, [], withPrompt ? PromptOf(item["content"]) : null) : null;
            }
        }
        if (results.Count == 0) return null;
        // Parallel calls interleave calls and outputs: every call item of the input names its id.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in input.OfType<JsonObject>())
            if (Str(item, "type") is "function_call" or "custom_tool_call" && Str(item, "call_id") is { } id && Str(item, "name") is { } name) names[id] = name;
        return new RequestCause(RequestCauseKinds.ToolResult,
            results.Select(r => new ToolResultInfo(r.Id is not null && names.TryGetValue(r.Id, out var n) ? n : null, null, r.Chars)).ToList(), null);
    }

    private static RequestCause? GeminiCause(JsonObject body, bool withPrompt)
    {
        if (body["contents"] is not JsonArray contents || contents.Count == 0 || contents[^1] is not JsonObject last) return null;
        if (Str(last, "role") is "model" or "assistant") return null;
        var results = new List<ToolResultInfo>();
        if (last["parts"] is JsonArray parts)
            foreach (var part in parts.OfType<JsonObject>())
            {
                if ((part["functionResponse"] ?? part["function_response"]) is not JsonObject r || results.Count >= MaxToolResults) continue;
                var response = r["response"] as JsonObject;
                results.Add(new ToolResultInfo(Str(r, "name"), response?["error"] is not null, AllTextLength(response, 0)));
            }
        if (results.Count > 0) return new RequestCause(RequestCauseKinds.ToolResult, results, null);
        return new RequestCause(RequestCauseKinds.User, [], withPrompt ? PromptOf(last["parts"]) : null);
    }

    /// <summary>
    /// The start of a user message's text, at most <see cref="MaxPromptChars"/>. Claude Code puts &lt;system-reminder&gt;
    /// blocks before the prompt; they are skipped when the message has other text.
    /// </summary>
    private static string? PromptOf(JsonNode? content)
    {
        var texts = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    if (s.Length > 0) texts.Add(s);
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
                case JsonObject o:
                    if (Str(o, "type") is "tool_result" or "image" or "image_url" or "input_image" or "document" or "input_file") return;
                    if (o["text"] is JsonValue t) Walk(t);
                    else if (o["parts"] is { } p) Walk(p);
                    else if (o["content"] is { } c) Walk(c);
                    break;
            }
        }
        Walk(content);
        var kept = texts.Where(t => !t.TrimStart().StartsWith("<system-reminder>", StringComparison.Ordinal)).ToList();
        var sb = new StringBuilder();
        foreach (var t in kept.Count > 0 ? kept : texts)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t.Trim());
            if (sb.Length >= MaxPromptChars) break;
        }
        if (sb.Length == 0) return null;
        return sb.Length > MaxPromptChars ? sb.ToString(0, MaxPromptChars) + "…" : sb.ToString();
    }

    /// <summary>Characters of every string under <paramref name="node"/> (a Gemini function response is free-form JSON).</summary>
    private static long AllTextLength(JsonNode? node, int depth) => depth > 32 ? 0 : node switch
    {
        JsonValue v => StringLength(v) ?? 0,
        JsonArray a => a.Sum(x => AllTextLength(x, depth + 1)),
        JsonObject o => o.Sum(kv => AllTextLength(kv.Value, depth + 1)),
        _ => 0,
    };
}
