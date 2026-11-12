using System.Text.Json.Nodes;

namespace Astra.Plugins.Middleware;

/// <summary>
/// Where a reply's text deltas sit in each protocol's stream events, for v2's <c>onText</c> (design/plugin-v2 02 §3): the
/// gateway hands the plugin the text and writes back what it returns, so a plugin that only filters or rewrites text
/// needs to know nothing of the four event shapes. Reasoning, tool arguments and every other event pass untouched.
/// </summary>
internal static class TextDeltas
{
    /// <summary>The text delta in an event's JSON, or null when the event carries none.</summary>
    public static string? Get(string protocol, string eventName, JsonObject data) => Slot(protocol, eventName, data) is { } s ? s.Get() : null;

    /// <summary>Replaces the event's text delta; false when it carries none.</summary>
    public static bool Set(string protocol, string eventName, JsonObject data, string text)
    {
        if (Slot(protocol, eventName, data) is not { } s) return false;
        s.Put(text);
        return true;
    }

    /// <summary>Whether the event ends the reply's text (where text a hook held back must be written out).</summary>
    public static bool Ends(string protocol, string eventName, JsonObject data) => protocol switch
    {
        "anthropic" => eventName is "content_block_stop" or "message_delta" or "message_stop" || data["type"]?.GetValue<string>() is "content_block_stop" or "message_delta" or "message_stop",
        "chat" => data["choices"] is JsonArray c && c.Count > 0 && c[0]?["finish_reason"] is JsonValue fr && fr.GetValueKind() == System.Text.Json.JsonValueKind.String,
        "responses" => eventName is "response.output_text.done" or "response.completed" || data["type"]?.GetValue<string>() is "response.output_text.done" or "response.completed",
        "gemini" => data["candidates"] is JsonArray g && g.Count > 0 && g[0]?["finishReason"] is not null,
        _ => false,
    };

    private sealed record TextSlot(Func<string?> Get, Action<string> Put);

    private static TextSlot? Slot(string protocol, string eventName, JsonObject data)
    {
        switch (protocol)
        {
            case "anthropic":
                if ((eventName == "content_block_delta" || data["type"]?.GetValue<string>() == "content_block_delta")
                    && data["delta"] is JsonObject d && d["type"]?.GetValue<string>() == "text_delta" && d["text"] is JsonValue)
                    return new TextSlot(() => d["text"]?.GetValue<string>(), t => d["text"] = t);
                return null;
            case "chat":
                if (data["choices"] is JsonArray choices && choices.Count > 0 && choices[0]?["delta"] is JsonObject cd
                    && cd["content"] is JsonValue cv && cv.TryGetValue<string>(out _))
                    return new TextSlot(() => cd["content"]?.GetValue<string>(), t => cd["content"] = t);
                return null;
            case "responses":
                if ((eventName == "response.output_text.delta" || data["type"]?.GetValue<string>() == "response.output_text.delta")
                    && data["delta"] is JsonValue rv && rv.TryGetValue<string>(out _))
                    return new TextSlot(() => data["delta"]?.GetValue<string>(), t => data["delta"] = t);
                return null;
            case "gemini":
                if (data["candidates"] is JsonArray cands && cands.Count > 0 && cands[0]?["content"]?["parts"] is JsonArray parts)
                {
                    var textParts = parts.OfType<JsonObject>()
                        .Where(p => p["text"] is JsonValue && !(p["thought"] is JsonValue tv && tv.TryGetValue<bool>(out var thought) && thought)).ToList();
                    if (textParts.Count == 0) return null;
                    return new TextSlot(() => string.Concat(textParts.Select(p => p["text"]!.GetValue<string>())), t =>
                    {
                        textParts[0]["text"] = t;
                        foreach (var p in textParts.Skip(1)) p["text"] = "";
                    });
                }
                return null;
        }
        return null;
    }
}

/// <summary>
/// The protocol-free views a v2 hook set with <c>view: "ir"</c> gets (design/plugin-v2 02 §3). The gateway implements it
/// over its codecs; without one, ir hooks get the raw JSON.
/// </summary>
public interface IHookViews
{
    /// <summary>The request view (model, system, messages, tools, params) of a body in <paramref name="protocol"/>; null when it can't be read.</summary>
    byte[]? RequestView(byte[] body, string protocol);

    /// <summary>The body with a hook's changed view applied, in the same protocol; null when nothing changed or it can't be applied.</summary>
    byte[]? ApplyRequestView(byte[] body, string protocol, byte[] view, byte[] changed);

    /// <summary>A whole reply's view (content, finish reason, usage); null when it can't be read.</summary>
    byte[]? ResponseView(byte[] body, string protocol);

    /// <summary>The reply with a changed view's text written back; null when nothing changed.</summary>
    byte[]? ApplyResponseView(byte[] body, string protocol, byte[] view, byte[] changed);
}
