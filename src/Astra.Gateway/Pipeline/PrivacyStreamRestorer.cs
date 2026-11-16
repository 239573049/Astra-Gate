using System.Text.Json.Nodes;
using Astra.Core.Privacy;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Puts masked values back into a streamed response, frame by frame, in whichever of the four wire protocols the
/// frames are (Chat Completions, Responses, Anthropic Messages, Gemini): the originals the guard replaced with
/// placeholders on the way out.
///
/// Three things a plain string replace per frame gets wrong, and this class gets right:
///  - a placeholder is split across two frames (the model emits it a few tokens at a time): each stream of deltas
///    (one text block, one tool call's arguments…) is its own channel with a <see cref="ChunkRestorer"/>, which holds back
///    only a tail that is still the beginning of a placeholder, and releases it before the next frame that is not part of
///    that channel and when the stream ends (as a copy of the channel's last delta frame carrying the leftover);
///  - the frame is JSON: the original is put into the decoded string and the frame serialized again, so quotes and
///    line breaks in it (a PEM key) are escaped as they must be;
///  - a tool call's arguments are a JSON text delivered in fragments inside that string: the original goes back escaped
///    for the inside of a JSON string, so the arguments the client assembles still parse, wherever the placeholder was
///    cut — even through the fragments of a value that is itself JSON-escaped.
/// Frames without a placeholder in them are passed on as they came (not even re-serialized).
/// </summary>
public sealed class PrivacyStreamRestorer(IReadOnlyDictionary<string, string> map)
{
    private sealed class Channel(ChunkRestorer restorer)
    {
        public ChunkRestorer Restorer { get; } = restorer;
        public SseEvent? Last { get; set; }
    }

    /// <summary>One string of a frame that carries a delta of a stream: where it is, and which channel it belongs to.</summary>
    private readonly record struct Slot(string Key, bool Fragment, JsonObject Parent, string Prop);

    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    public bool IsEmpty => map.Count == 0;

    /// <summary>The frames to send in place of <paramref name="frame"/> (any held leftovers first).</summary>
    public List<SseEvent> Process(SseEvent frame)
    {
        var output = new List<SseEvent>(1);
        if (map.Count == 0)
        {
            output.Add(frame);
            return output;
        }

        JsonNode? root = null;
        // [DONE], a comment, a payload that is no JSON: not a delta of anything.
        if (frame.Data.AsSpan().TrimStart() is ['{', ..])
        {
            try
            {
                root = JsonNode.Parse(frame.Data);
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        var slots = root is JsonObject obj ? FindSlots(obj) : [];
        if (slots.Count == 0)
        {
            FlushInto(output);
            output.Add(root is null ? PlainRestored(frame) : RestoredFull(frame, root));
            return output;
        }

        var changed = false;
        foreach (var slot in slots)
        {
            if (slot.Parent[slot.Prop] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            if (!_channels.TryGetValue(slot.Key, out var channel))
                _channels[slot.Key] = channel = new Channel(new ChunkRestorer(map, jsonEscape: slot.Fragment));
            channel.Last = frame;
            var emit = channel.Restorer.Push(text);
            if (!ReferenceEquals(emit, text) && emit != text)
            {
                slot.Parent[slot.Prop] = emit;
                changed = true;
            }
        }
        // What else the frame carries (Gemini's whole function-call args, Chat's other fields) is not a delta: whole
        // placeholders only.
        RestoreTree(root!, map, out var treeChanged);
        changed |= treeChanged;
        output.Add(changed ? frame with { Data = root!.ToJsonString(GatewayJson.Options) } : frame);
        return output;
    }

    /// <summary>At the end of the stream: whatever was still held back, as delta frames.</summary>
    public List<SseEvent> Flush()
    {
        var output = new List<SseEvent>();
        FlushInto(output);
        return output;
    }

    private void FlushInto(List<SseEvent> output)
    {
        foreach (var (key, channel) in _channels)
        {
            var rest = channel.Restorer.Flush();
            if (rest.Length == 0 || channel.Last is not { } last) continue;
            if (Synthesize(last, key, rest) is { } synthetic) output.Add(synthetic);
        }
        _channels.Clear();
    }

    // A frame like the channel's last delta frame, carrying the held text.
    private static SseEvent? Synthesize(SseEvent last, string key, string text)
    {
        if (JsonNode.Parse(last.Data) is not JsonObject root) return null;
        var slot = FindSlots(root).FirstOrDefault(s => s.Key == key);
        if (slot.Parent is null) return null;
        slot.Parent[slot.Prop] = text;
        // It is a chunk of text, not the end of the turn: no finish reason, no usage, no other parts.
        root.Remove("usage");
        root.Remove("usageMetadata");
        if (root["choices"] is JsonArray choices)
            foreach (var choice in choices.OfType<JsonObject>())
                if (choice.ContainsKey("finish_reason")) choice["finish_reason"] = null;
        if (root["candidates"] is JsonArray candidates)
            foreach (var candidate in candidates.OfType<JsonObject>())
                candidate.Remove("finishReason");
        return last with { Data = root.ToJsonString(GatewayJson.Options) };
    }

    private SseEvent PlainRestored(SseEvent frame) =>
        PrivacyGuardService.ContainsAny(frame.Data, map) ? frame with { Data = PrivacyGuardService.Restore(frame.Data, map) } : frame;

    private SseEvent RestoredFull(SseEvent frame, JsonNode root)
    {
        if (!PrivacyGuardService.ContainsAny(frame.Data, map)) return frame;
        RestoreTree(root, map, out var changed);
        return changed ? frame with { Data = root.ToJsonString(GatewayJson.Options) } : frame;
    }

    /// <summary>The strings of a frame that are one stream's next piece, by protocol.</summary>
    private static List<Slot> FindSlots(JsonObject root)
    {
        var slots = new List<Slot>();

        // Chat Completions: choices[].delta.{content, reasoning_content, tool_calls[].function.arguments}
        if (root["choices"] is JsonArray choices)
        {
            var ci = 0;
            foreach (var choice in choices)
            {
                var c = choice is JsonObject co && co["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx) ? idx : ci;
                ci++;
                if (choice is not JsonObject { } chosen || chosen["delta"] is not JsonObject delta) continue;
                AddIfString(slots, $"c{c}.content", false, delta, "content");
                AddIfString(slots, $"c{c}.reasoning_content", false, delta, "reasoning_content");
                AddIfString(slots, $"c{c}.reasoning", false, delta, "reasoning");
                AddIfString(slots, $"c{c}.refusal", false, delta, "refusal");
                if (delta["function_call"] is JsonObject legacy) AddIfString(slots, $"c{c}.function_call", true, legacy, "arguments");
                if (delta["tool_calls"] is JsonArray calls)
                {
                    var ti = 0;
                    foreach (var call in calls.OfType<JsonObject>())
                    {
                        var t = call["index"] is JsonValue tv && tv.TryGetValue<int>(out var tidx) ? tidx : ti;
                        ti++;
                        if (call["function"] is JsonObject function) AddIfString(slots, $"c{c}.tool{t}", true, function, "arguments");
                    }
                }
            }
            return slots;
        }

        // Gemini: candidates[].content.parts[].text (function-call args arrive whole, in the tree pass)
        if (root["candidates"] is JsonArray candidates)
        {
            var ci = 0;
            foreach (var candidate in candidates.OfType<JsonObject>())
            {
                var c = ci++;
                if (candidate["content"] is not JsonObject { } content || content["parts"] is not JsonArray parts) continue;
                foreach (var part in parts.OfType<JsonObject>())
                {
                    var thought = part["thought"] is JsonValue tv && tv.TryGetValue<bool>(out var isThought) && isThought;
                    AddIfString(slots, $"g{c}{(thought ? ".thought" : "")}", false, part, "text");
                }
            }
            return slots;
        }

        var type = root["type"] is JsonValue t0 && t0.TryGetValue<string>(out var typeName) ? typeName : null;
        if (type is null) return slots;

        // Anthropic Messages: content_block_delta {index, delta: text_delta | input_json_delta | thinking_delta}
        if (type == "content_block_delta" && root["delta"] is JsonObject block)
        {
            var index = root["index"] is JsonValue iv2 && iv2.TryGetValue<int>(out var bi) ? bi : 0;
            var deltaType = block["type"] is JsonValue dt && dt.TryGetValue<string>(out var dtn) ? dtn : null;
            switch (deltaType)
            {
                case "text_delta": AddIfString(slots, $"a{index}", false, block, "text"); break;
                case "input_json_delta": AddIfString(slots, $"a{index}", true, block, "partial_json"); break;
                case "thinking_delta": AddIfString(slots, $"a{index}.thinking", false, block, "thinking"); break;
            }
            return slots;
        }

        // Responses: response.<what>.delta {item_id, output_index, delta}
        if (type.EndsWith(".delta", StringComparison.Ordinal) && !type.Contains("audio.delta", StringComparison.Ordinal))
        {
            var item = root["item_id"] is JsonValue iv3 && iv3.TryGetValue<string>(out var itemId) ? itemId
                : root["output_index"]?.ToString() ?? "";
            var content = root["content_index"]?.ToString() ?? "";
            var summary = root["summary_index"]?.ToString() ?? "";
            var fragment = type.Contains("arguments", StringComparison.Ordinal) || type.Contains("_input", StringComparison.Ordinal);
            AddIfString(slots, $"r:{type}:{item}:{content}:{summary}", fragment, root, "delta");
        }
        return slots;
    }

    private static void AddIfString(List<Slot> slots, string key, bool fragment, JsonObject parent, string prop)
    {
        if (parent[prop] is JsonValue value && value.TryGetValue<string>(out _)) slots.Add(new Slot(key, fragment, parent, prop));
    }

    /// <summary>
    /// Restores the whole placeholders in every string of a document, in place (the strings called <c>arguments</c> hold
    /// a JSON text, so the original goes in escaped for it); returns the tree.
    /// </summary>
    internal static JsonNode RestoreTree(JsonNode root, IReadOnlyDictionary<string, string> map) =>
        RestoreTree(root, map, out _) ?? root;

    private static JsonNode? RestoreTree(JsonNode root, IReadOnlyDictionary<string, string> map, out bool changed)
    {
        changed = false;
        Walk(root, map, null, ref changed);
        return root;
    }

    private static void Walk(JsonNode? node, IReadOnlyDictionary<string, string> map, string? name, ref bool changed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (Replace(s, map, escape: key == "arguments") is { } replaced)
                        {
                            obj[key] = replaced;
                            changed = true;
                        }
                    }
                    else Walk(child, map, key, ref changed);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (Replace(s, map, escape: false) is { } replaced)
                        {
                            array[i] = replaced;
                            changed = true;
                        }
                    }
                    else Walk(array[i], map, name, ref changed);
                }
                break;
        }
    }

    // The string with its placeholders restored, or null when it holds none.
    private static string? Replace(string text, IReadOnlyDictionary<string, string> map, bool escape)
    {
        string? result = null;
        foreach (var (placeholder, original) in map)
        {
            var current = result ?? text;
            if (!current.Contains(placeholder, StringComparison.Ordinal)) continue;
            result = current.Replace(placeholder, escape ? ChunkRestorer.EscapeJson(original) : original, StringComparison.Ordinal);
        }
        return result;
    }
}
