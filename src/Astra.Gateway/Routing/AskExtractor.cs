using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Routing;

namespace Astra.Gateway.Routing;

/// <summary>What the judge reads of a request: the current ask, the user asks before it (oldest first) and the conversation's shape.</summary>
public sealed record Ask(string Request, IReadOnlyList<string> Earlier, JudgeShape Shape);

/// <summary>
/// Picks the user's own words out of a request body in the client's protocol (plan §5.5): the last real user message
/// and a few before it. Tool results, assistant turns, system prompts and images are not the user's task, so none of
/// them is read (an image only sets <see cref="JudgeShape.HasImages"/>), and the <c>&lt;system-reminder&gt;</c> blocks
/// Claude Code adds to user messages are removed. Reads backwards and stops early: a long agent conversation is not walked.
/// Reads the guarded body, so a redacted value stays redacted. Never throws; a body of an unknown shape gives null.
/// </summary>
public static class AskExtractor
{
    private const string ReminderOpen = "<system-reminder>";
    private const string ReminderClose = "</system-reminder>";
    private const string Elision = "\n…\n";

    private readonly record struct UserText(string Text, bool Images);

    /// <summary>The ask of <paramref name="body"/>, or null when it holds no user text at all.</summary>
    /// <param name="contextTurns">How many user asks before the current one to include.</param>
    /// <param name="maxChars">The most characters of text in all (the current ask keeps at least two thirds of them).</param>
    public static Ask? Extract(ApiProtocol inbound, JsonObject body, int contextTurns, int maxChars)
    {
        try
        {
            var tools = (body["tools"] as JsonArray)?.Count ?? 0;
            switch (inbound)
            {
                case ApiProtocol.Anthropic:
                    return Collect(body["messages"] as JsonArray, AnthropicUser, tools, contextTurns, maxChars);
                case ApiProtocol.OpenAIChat:
                    return Collect(body["messages"] as JsonArray, ChatUser, tools, contextTurns, maxChars);
                case ApiProtocol.OpenAIResponses:
                    if (body["input"] is JsonValue input && input.TryGetValue<string>(out var single))
                        return Collect(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = single }), ResponsesUser, tools, contextTurns, maxChars);
                    return Collect(body["input"] as JsonArray, ResponsesUser, tools, contextTurns, maxChars);
                case ApiProtocol.Gemini:
                    return Collect(body["contents"] as JsonArray, GeminiUser, tools, contextTurns, maxChars);
                default:
                    return null;
            }
        }
        catch (InvalidOperationException)
        {
            // A node of an unexpected kind: nothing to judge.
            return null;
        }
    }

    private static Ask? Collect(JsonArray? items, Func<JsonObject, UserText?> read, int tools, int contextTurns, int maxChars)
    {
        if (items is null) return null;
        var asks = new List<string>(); // newest first
        var images = false;
        for (var i = items.Count - 1; i >= 0 && asks.Count < 1 + contextTurns; i--)
        {
            if (items[i] is not JsonObject item || read(item) is not { } user) continue;
            if (asks.Count == 0) images = user.Images;
            asks.Add(user.Text);
        }
        if (asks.Count == 0) return null;

        var earlier = asks.Skip(1).Reverse().ToList();
        if (earlier.Count > 0)
        {
            var each = Math.Max(80, maxChars / 3 / earlier.Count);
            for (var i = 0; i < earlier.Count; i++) earlier[i] = Shorten(earlier[i], each);
        }
        var request = Shorten(asks[0], Math.Max(500, maxChars - earlier.Sum(e => e.Length)));
        return new Ask(request, earlier, new JudgeShape(items.Count, tools, images));
    }

    // ------------------------------------------------------------------ per protocol

    private static UserText? AnthropicUser(JsonObject message)
    {
        if (Str(message, "role") != "user") return null;
        var text = new StringBuilder();
        var images = false;
        switch (message["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                text.Append(s);
                break;
            case JsonArray blocks:
                foreach (var block in blocks.OfType<JsonObject>())
                    switch (Str(block, "type"))
                    {
                        case "text":
                            Append(text, Str(block, "text"));
                            break;
                        case "image" or "document":
                            images = true;
                            break;
                        // tool_result, tool_use, thinking …: not the user's words
                    }
                break;
        }
        return Finish(text, images);
    }

    private static UserText? ChatUser(JsonObject message)
    {
        if (Str(message, "role") != "user") return null;
        return Parts(message["content"], textTypes: ["text"], imageTypes: ["image_url", "input_image"]);
    }

    private static UserText? ResponsesUser(JsonObject item)
    {
        // function_call, function_call_output, reasoning … carry no user words.
        if (Str(item, "type") is not (null or "message") || Str(item, "role") != "user") return null;
        return Parts(item["content"], textTypes: ["input_text", "text"], imageTypes: ["input_image"]);
    }

    private static UserText? GeminiUser(JsonObject content)
    {
        if (Str(content, "role") is not (null or "user")) return null;
        var text = new StringBuilder();
        var images = false;
        foreach (var part in (content["parts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(part, "text") is { } t) Append(text, t);
            else if (part.ContainsKey("inlineData") || part.ContainsKey("inline_data") || part.ContainsKey("fileData") || part.ContainsKey("file_data"))
                images = true;
            // functionResponse: a tool result, not the user's words
        }
        return Finish(text, images);
    }

    /// <summary>A message whose content is a string or a list of typed parts (the OpenAI shapes).</summary>
    private static UserText? Parts(JsonNode? content, string[] textTypes, string[] imageTypes)
    {
        var text = new StringBuilder();
        var images = false;
        switch (content)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                text.Append(s);
                break;
            case JsonArray parts:
                foreach (var part in parts.OfType<JsonObject>())
                {
                    var type = Str(part, "type");
                    if (type is not null && textTypes.Contains(type)) Append(text, Str(part, "text"));
                    else if (type is not null && imageTypes.Contains(type)) images = true;
                }
                break;
        }
        return Finish(text, images);
    }

    // ------------------------------------------------------------------ text

    private static UserText? Finish(StringBuilder text, bool images)
    {
        var clean = StripReminders(text.ToString()).Trim();
        return clean.Length == 0 ? null : new UserText(clean, images);
    }

    private static void Append(StringBuilder text, string? part)
    {
        if (string.IsNullOrEmpty(part)) return;
        if (text.Length > 0) text.Append('\n');
        text.Append(part);
    }

    /// <summary>Removes every complete <c>&lt;system-reminder&gt;…&lt;/system-reminder&gt;</c> block; an unterminated one is kept as it is.</summary>
    private static string StripReminders(string text)
    {
        if (!text.Contains(ReminderOpen, StringComparison.Ordinal)) return text;
        var kept = new StringBuilder(text.Length);
        var at = 0;
        while (true)
        {
            var open = text.IndexOf(ReminderOpen, at, StringComparison.Ordinal);
            var close = open < 0 ? -1 : text.IndexOf(ReminderClose, open + ReminderOpen.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                kept.Append(text, at, text.Length - at);
                return kept.ToString();
            }
            kept.Append(text, at, open - at);
            at = close + ReminderClose.Length;
        }
    }

    /// <summary>Cuts the middle out of a text longer than <paramref name="max"/>: its start (two thirds) and its end stay.</summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        var room = Math.Max(0, max - Elision.Length);
        var head = room * 2 / 3;
        var tail = room - head;
        // never cut a surrogate pair in half
        if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        if (tail > 0 && char.IsLowSurrogate(text[^tail])) tail--;
        return text[..head] + Elision + text[^tail..];
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
