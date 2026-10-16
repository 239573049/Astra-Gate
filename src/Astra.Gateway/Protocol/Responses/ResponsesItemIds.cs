using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// Keeps each output item's id the same across a pass-through OpenAI Responses stream. GitHub Copilot's Responses API
/// re-encrypts item ids per event: one item arrives with one id on <c>response.output_item.added</c>, others on its
/// deltas and yet another on <c>response.output_item.done</c>. Codex starts an assistant message under the added id and
/// completes it under the done id, so its app shows the text twice. The id is pinned per <c>output_index</c> (stable,
/// like <c>call_id</c>) to the one <c>output_item.added</c> carried — made up when it carried none — and written into
/// <c>output_item.done</c>'s <c>item.id</c> and every other event's <c>item_id</c>. Response ids and the terminal
/// response's <c>output</c> stay as they came: Codex reads neither for item identity. One instance per stream.
/// <para>
/// Most events are deltas, which only need their top-level <c>item_id</c> replaced: those are spliced in place after a
/// token scan of the event, without building a JSON tree. Added and done events, and anything the scan cannot handle
/// exactly (escaped ids, duplicate keys, malformed JSON), take the tree path.
/// </para>
/// </summary>
public sealed class ResponsesItemIds
{
    private const string AddedType = "response.output_item.added";
    private const string DoneType = "response.output_item.done";

    private readonly Dictionary<int, string> _ids = new();

    /// <summary>The event's data with its item id pinned; <paramref name="data"/> itself when nothing changed.</summary>
    public string Pin(string data)
    {
        // Response-level events (created, in_progress, completed …) name no output item: nothing to parse.
        if (!data.Contains("\"output_index\"", StringComparison.Ordinal)) return data;
        if (!TryScan(data, out var scan)) return PinTree(data);
        if (scan.Index is not { } index) return data;
        if (scan.Type is AddedType or DoneType) return PinTree(data);
        if (!_ids.TryGetValue(index, out var pinned) || scan.ItemIdStart < 0) return data;
        if (data.AsSpan(scan.ItemIdStart, scan.ItemIdLength).SequenceEqual(pinned)) return data;
        // The pinned id goes in raw: an id that needs escaping takes the tree path, which escapes it.
        if (NeedsEscape(pinned)) return PinTree(data);
        return string.Concat(data.AsSpan(0, scan.ItemIdStart), pinned, data.AsSpan(scan.ItemIdStart + scan.ItemIdLength));
    }

    /// <summary>Pins through a parsed tree: the only path that records added ids and rewrites done items.</summary>
    private string PinTree(string data)
    {
        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return data;
        }
        if (evt is null || ResponsesCodec.Int(evt, "output_index") is not { } index) return data;

        var type = ResponsesCodec.Str(evt, "type");
        if (type == AddedType)
        {
            if (evt["item"] is not JsonObject added) return data;
            if (ResponsesCodec.Str(added, "id") is { Length: > 0 } id)
            {
                _ids[index] = id;
                return data;
            }
            added["id"] = _ids[index] = $"oi_{index}_{Guid.NewGuid():N}";
        }
        else if (!_ids.TryGetValue(index, out var pinned))
        {
            return data;
        }
        else if (type == DoneType)
        {
            if (evt["item"] is not JsonObject done || ResponsesCodec.Str(done, "id") == pinned) return data;
            done["id"] = pinned;
        }
        else
        {
            if (!evt.ContainsKey("item_id") || ResponsesCodec.Str(evt, "item_id") == pinned) return data;
            evt["item_id"] = pinned;
        }
        return evt.ToJsonString(GatewayJson.Options);
    }

    /// <summary>What the scan read from one event's top level.</summary>
    private struct Scan
    {
        public string? Type;
        public int? Index;
        /// <summary>Where the top-level <c>item_id</c> value sits in the string (its quotes excluded); -1 when absent.</summary>
        public int ItemIdStart;
        public int ItemIdLength;
    }

    /// <summary>
    /// Reads the top-level <c>type</c>, <c>output_index</c> and <c>item_id</c> without building a tree. False when the
    /// event is not plain JSON this exact path can answer for: malformed, an escaped string where an id or type is read,
    /// a repeated key, or a non-integer index. The caller then takes the tree path.
    /// </summary>
    private static bool TryScan(string data, out Scan scan)
    {
        scan = new Scan { ItemIdStart = -1 };
        var utf8 = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(data.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(data, utf8);
            var reader = new Utf8JsonReader(utf8.AsSpan(0, length));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            var seenType = false;
            var seenIndex = false;
            var seenItemId = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return true;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                if (reader.ValueTextEquals("type"u8))
                {
                    if (seenType || !reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped) return false;
                    scan.Type = reader.GetString();
                    seenType = true;
                }
                else if (reader.ValueTextEquals("output_index"u8))
                {
                    if (seenIndex || !reader.Read() || !reader.TryGetInt32(out var index)) return false;
                    scan.Index = index;
                    seenIndex = true;
                }
                else if (reader.ValueTextEquals("item_id"u8))
                {
                    if (seenItemId || !reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped) return false;
                    // The value starts after its opening quote; both offsets are converted to string offsets below.
                    var byteStart = (int)reader.TokenStartIndex + 1;
                    var byteLength = reader.ValueSpan.Length;
                    scan.ItemIdStart = Encoding.UTF8.GetCharCount(utf8.AsSpan(0, byteStart));
                    scan.ItemIdLength = Encoding.UTF8.GetCharCount(utf8.AsSpan(byteStart, byteLength));
                    seenItemId = true;
                }
                else
                {
                    // Any other field, nested or not, is skipped whole so only top-level keys are read.
                    if (!reader.Read() || !reader.TrySkip()) return false;
                }
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    private static bool NeedsEscape(string value)
    {
        foreach (var c in value)
            if (c < ' ' || c == '"' || c == '\\') return true;
        return false;
    }
}
