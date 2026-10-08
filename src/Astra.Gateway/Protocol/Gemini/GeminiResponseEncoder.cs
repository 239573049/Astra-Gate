using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Gemini;

/// <summary>
/// Converts IR events into Gemini GenerateContentResponse frames (streaming) or one aggregated body
/// (BuildJson). Every stream frame is {"candidates":[{"content":{"role":"model","parts":[…]},"index":0]}}
/// plus modelVersion / responseId; there is no start frame — the first frame is the first content chunk.
/// A Gemini thoughtSignature is held until the next part is emitted and attached to it (decoders place the
/// signature before its carrying part); if the block ends first it goes out as {"text":"","thoughtSignature"}.
/// </summary>
public sealed class GeminiResponseEncoder : IResponseEncoder
{
    private readonly ResponseEncodeContext _ctx;
    private readonly List<JsonObject> _aggregated = [];               // wire parts, for BuildJson
    private readonly Dictionary<int, StringBuilder> _toolArgs = new(); // tool block index → buffered args
    private readonly Dictionary<int, (string? Id, string? Name)> _toolMeta = new();
    private string? _pendingSignature;
    private FinishReason? _finish;
    private NormalizedUsage? _usage;

    public GeminiResponseEncoder(ResponseEncodeContext ctx) => _ctx = ctx;

    public IReadOnlyList<SseEvent> OnEvent(UnifiedStreamEvent e)
    {
        switch (e)
        {
            case MessageStartEvent:
                return []; // Gemini streams carry no start frame

            case TextDeltaEvent text:
                return Frame(Track(new JsonObject { ["text"] = text.Text }));

            case ReasoningDeltaEvent reasoning:
                if (reasoning.Signature is { Length: > 0 } && reasoning.Origin == ApiProtocol.Gemini)
                    _pendingSignature = reasoning.Signature; // attached to the next emitted part
                if (reasoning.Text is { Length: > 0 } thoughtText)
                    return Frame(Track(ThoughtPart(thoughtText)));
                return [];

            case BlockStartEvent { Kind: BlockKind.ToolCall } start:
                _toolMeta[start.Index] = (start.ToolCallId, start.ToolName);
                _toolArgs[start.Index] = new StringBuilder();
                return [];

            case ToolArgsDeltaEvent args:
                if (_toolArgs.TryGetValue(args.Index, out var buffer)) buffer.Append(args.JsonFragment);
                return []; // Gemini functionCall parts are complete: buffered until BlockStop

            case BlockStopEvent stop:
                return OnBlockStop(stop.Index);

            case UsageEvent usage:
                _usage = usage.Usage; // latest report wins; sent with the final chunk
                return [];

            case MessageStopEvent messageStop:
                _finish = messageStop.Reason;
                return ChunkFrame(FinalChunk(MapFinish(messageStop.Reason)));

            case ErrorEvent error:
                return [ErrorFrame(error.Status, error.Message)];

            default:
                return []; // BlockStart(Text/Reasoning): content frames are emitted per delta
        }
    }

    public JsonObject BuildJson()
    {
        var parts = new JsonArray();
        foreach (var part in _aggregated) parts.AddNode(part.DeepClone());
        var candidate = new JsonObject
        {
            ["content"] = new JsonObject { ["role"] = "model", ["parts"] = parts },
            ["finishReason"] = MapFinish(_finish),
            ["index"] = 0,
        };
        var root = new JsonObject { ["candidates"] = new JsonArray(candidate) };
        if (_ctx.Model.Length > 0) root["modelVersion"] = _ctx.Model;
        if (_ctx.ResponseId.Length > 0) root["responseId"] = _ctx.ResponseId;
        if (UsageJson(_usage) is { } usage) root["usageMetadata"] = usage;
        return root;
    }

    // ---------------------------------------------------------------- frames

    private IReadOnlyList<SseEvent> OnBlockStop(int index)
    {
        if (_toolArgs.Remove(index, out var buffer) && _toolMeta.Remove(index, out var meta))
        {
            JsonObject callArgs;
            try
            {
                callArgs = JsonNode.Parse(buffer.ToString()) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                callArgs = [];
            }
            var call = new JsonObject { ["name"] = meta.Name ?? "", ["args"] = callArgs };
            if (meta.Id is { Length: > 0 } id) call["id"] = id;
            return Frame(Track(new JsonObject { ["functionCall"] = call }));
        }
        if (_pendingSignature is { } signature)
        {
            // The block ended with nothing after the signature: emit it as its own empty text part.
            _pendingSignature = null;
            return Frame(Track(SignaturePart(signature), signature));
        }
        return [];
    }

    /// <summary>One content part in its own chunk frame.</summary>
    private IReadOnlyList<SseEvent> Frame(JsonObject part) =>
        [new SseEvent(null, Chunk(new JsonArray(part), null, null).ToJsonString(GatewayJson.Options))];

    /// <summary>A fully built chunk (the final frame) written out directly.</summary>
    private IReadOnlyList<SseEvent> ChunkFrame(JsonObject chunk) =>
        [new SseEvent(null, chunk.ToJsonString(GatewayJson.Options))];

    private JsonObject FinalChunk(string finish)
    {
        var parts = new JsonArray();
        if (_pendingSignature is { } signature)
        {
            _pendingSignature = null;
            Track(SignaturePart(signature), signature);
            parts.AddNode(SignaturePart(signature));
        }
        return Chunk(parts, finish, UsageJson(_usage));
    }

    private SseEvent ErrorFrame(int status, string message) => new(null,
        new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = status,
                ["message"] = message,
                ["status"] = GeminiCodec.StatusName(status),
            },
        }.ToJsonString(GatewayJson.Options));

    private JsonObject Chunk(JsonArray parts, string? finish, JsonObject? usage)
    {
        var candidate = new JsonObject
        {
            ["content"] = new JsonObject { ["role"] = "model", ["parts"] = parts },
            ["index"] = 0,
        };
        if (finish is not null) candidate["finishReason"] = finish;
        var chunk = new JsonObject { ["candidates"] = new JsonArray(candidate) };
        if (_ctx.Model.Length > 0) chunk["modelVersion"] = _ctx.Model;
        if (_ctx.ResponseId.Length > 0) chunk["responseId"] = _ctx.ResponseId;
        if (usage is not null) chunk["usageMetadata"] = usage;
        return chunk;
    }

    // ---------------------------------------------------------------- parts & usage

    private JsonObject ThoughtPart(string text, string? signature = null)
    {
        var part = new JsonObject { ["text"] = text, ["thought"] = true };
        if (signature is not null) part["thoughtSignature"] = signature;
        return part;
    }

    /// <summary>A bare signature part for when a block ends with nothing after the signature.</summary>
    private static JsonObject SignaturePart(string signature) => new() { ["text"] = "", ["thoughtSignature"] = signature };

    /// <summary>Records the wire part for BuildJson (merging consecutive plain text) and attaches any pending signature.</summary>
    private JsonObject Track(JsonObject part, string? signature = null)
    {
        var sig = signature ?? _pendingSignature;
        if (sig is not null)
        {
            part["thoughtSignature"] = sig;
            _pendingSignature = null;
        }
        if (_aggregated.LastOrDefault() is { } last && last.Count == 1 && last.ContainsKey("text")
            && part.Count == 1 && part.ContainsKey("text"))
        {
            last["text"] = last["text"]!.GetValue<string>() + part["text"]!.GetValue<string>();
        }
        else
        {
            _aggregated.Add((JsonObject)part.DeepClone());
        }
        return part;
    }

    /// <summary>usageMetadata in the Gemini client shape, from the normalized usage (plan §4.2).</summary>
    private static JsonObject? UsageJson(NormalizedUsage? usage)
    {
        if (usage is null) return null;
        var prompt = usage.TotalInput;
        var json = new JsonObject
        {
            ["promptTokenCount"] = prompt,
            ["candidatesTokenCount"] = usage.Get(TokenTypes.Output) + usage.Get(TokenTypes.OutputAudio) + usage.Get(TokenTypes.OutputImage),
        };
        var cached = usage.Get(TokenTypes.CacheRead);
        if (cached > 0) json["cachedContentTokenCount"] = cached;
        var thoughts = usage.Get(TokenTypes.Reasoning);
        if (thoughts > 0) json["thoughtsTokenCount"] = thoughts;
        json["totalTokenCount"] = prompt + usage.TotalOutput;
        return json;
    }

    private static string MapFinish(FinishReason? reason) => reason switch
    {
        null => "STOP", // no MessageStop seen: a clean end
        FinishReason.Stop or FinishReason.ToolCalls => "STOP",
        FinishReason.Length => "MAX_TOKENS",
        FinishReason.ContentFilter => "SAFETY",
        _ => "OTHER",
    };
}
