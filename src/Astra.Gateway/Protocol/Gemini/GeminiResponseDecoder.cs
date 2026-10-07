using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Gemini;

/// <summary>
/// Turns Gemini GenerateContentResponse payloads — streamGenerateContent?alt=sse chunks or one
/// non-streaming body — into IR events. Gemini SSE has no terminal sentinel: the pipeline calls
/// <see cref="Complete"/> after the last chunk, which closes open blocks and emits the final usage and
/// MessageStop exactly once. usageMetadata is cumulative, so the last report wins.
/// </summary>
public sealed class GeminiResponseDecoder : IResponseDecoder
{
    private readonly ResponseDecodeContext _ctx;
    private bool _started;
    private bool _stopped;
    private bool _errored;
    private int _nextBlock;
    private int? _open;
    private BlockKind? _openKind;
    private bool _sawFunctionCall;
    private string? _rawFinish;
    private FinishReason? _finish; // set only by promptFeedback.blockReason (no finishReason involved)
    private JsonObject? _usage;

    public GeminiResponseDecoder(ResponseDecodeContext ctx) => _ctx = ctx;

    public string? ResponseModel { get; private set; }

    public IEnumerable<UnifiedStreamEvent> DecodeSse(SseEvent sse)
    {
        if (string.IsNullOrWhiteSpace(sse.Data) || sse.Data == "[DONE]") return []; // Gemini never sends [DONE].
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(sse.Data) as JsonObject;
        }
        catch (JsonException)
        {
            return []; // ignore keep-alives / non-JSON chunks
        }
        return body is null ? [] : DecodeObject(body);
    }

    public IEnumerable<UnifiedStreamEvent> DecodeJson(JsonObject body) => DecodeObject(body).Concat(Complete());

    public IEnumerable<UnifiedStreamEvent> Complete()
    {
        foreach (var e in CloseOpen()) yield return e;
        if (_errored) yield break; // an ErrorEvent already replaced the tail of this response
        if (_usage is not null)
        {
            yield return new UsageEvent(ApiProtocol.Gemini, (JsonObject)_usage.DeepClone(),
                UsageNormalizer.Normalize(ApiProtocol.Gemini, _usage) ?? new NormalizedUsage());
        }
        if (!_stopped)
        {
            _stopped = true;
            yield return new MessageStopEvent(FinalReason(), _rawFinish);
        }
    }

    private FinishReason FinalReason()
    {
        if (_finish is { } blocked) return blocked;
        if (_rawFinish is { } raw)
        {
            var reason = MapFinish(raw);
            // STOP with a function call in the response is a tool-call finish (plan §6.4).
            return reason == FinishReason.Stop && _sawFunctionCall ? FinishReason.ToolCalls : reason;
        }
        return FinishReason.Stop;
    }

    private IEnumerable<UnifiedStreamEvent> DecodeObject(JsonObject body)
    {
        if (_errored) return [];
        if (Fields.Str(body, "modelVersion", "model_version") is { } model && !string.IsNullOrWhiteSpace(model))
            ResponseModel = model;

        if (Fields.Node(body, "error") is JsonObject error)
        {
            _errored = true;
            var close = CloseOpen().ToList();
            var status = Fields.Int(error, "code") ?? 500;
            close.Add(new ErrorEvent(status,
                Fields.Str(error, "status") ?? GeminiCodec.StatusName(status),
                Fields.Str(error, "message") ?? "Upstream error"));
            return close;
        }

        return DecodeResponse(body);
    }

    private IEnumerable<UnifiedStreamEvent> DecodeResponse(JsonObject body)
    {
        if (!_started)
        {
            _started = true;
            yield return new MessageStartEvent(
                Fields.Str(body, "responseId", "response_id"),
                Fields.Str(body, "modelVersion", "model_version") ?? NullIfEmpty(_ctx.Model));
        }

        if (Fields.Node(body, "candidates") is JsonArray candidates && candidates.Count > 0)
        {
            // The IR streams one generation; candidates beyond index 0 (n>1 responses) are a lossy drop.
            var candidate = candidates.OfType<JsonObject>()
                .FirstOrDefault(c => (Fields.Int(c, "index") ?? 0) == 0) ?? candidates.OfType<JsonObject>().First();
            if (Fields.Node(candidate, "content") is JsonObject content && Fields.Node(content, "parts") is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    foreach (var e in DecodePart(part)) yield return e;
                }
            }
            if (Fields.Str(candidate, "finishReason", "finish_reason") is { Length: > 0 } finish)
                _rawFinish = finish;
        }
        else if (Fields.Node(body, "promptFeedback", "prompt_feedback") is JsonObject feedback &&
                 Fields.Str(feedback, "blockReason", "block_reason") is { Length: > 0 } blockReason)
        {
            // Prompt blocked before any candidate was produced.
            _finish = FinishReason.ContentFilter;
            _rawFinish = blockReason;
        }

        if (Fields.Node(body, "usageMetadata", "usage_metadata") is JsonObject usage)
            _usage = (JsonObject)usage.DeepClone(); // cumulative: the latest report wins
    }

    private IEnumerable<UnifiedStreamEvent> DecodePart(JsonObject part)
    {
        var signature = Fields.Str(part, "thoughtSignature", "thought_signature");
        var thought = Fields.Bool(part, "thought") == true;
        var text = Fields.Str(part, "text");

        if (signature is { Length: > 0 })
        {
            if (thought)
            {
                // The carrying part is itself reasoning: the signature rides in its reasoning block.
                foreach (var e in OpenBlock(BlockKind.Reasoning)) yield return e;
                yield return new ReasoningDeltaEvent(_open!.Value, null, signature, Origin: ApiProtocol.Gemini);
            }
            else
            {
                // Text or functionCall part: emit a standalone reasoning block for the signature first, so
                // it lands before the carrying part's own block (the position the encoder re-attaches from).
                foreach (var e in SignatureBlock(signature)) yield return e;
            }
        }

        if (text is { Length: > 0 })
        {
            var kind = thought ? BlockKind.Reasoning : BlockKind.Text;
            foreach (var e in OpenBlock(kind)) yield return e;
            yield return thought
                ? new ReasoningDeltaEvent(_open!.Value, text, Origin: ApiProtocol.Gemini)
                : new TextDeltaEvent(_open!.Value, text);
            yield break;
        }

        if (Fields.Node(part, "functionCall", "function_call") is JsonObject call)
        {
            foreach (var e in CloseOpen()) yield return e;
            var name = Fields.Str(call, "name") ?? "";
            var id = Fields.Str(call, "id") is { Length: > 0 } explicitId ? explicitId : "call_" + Ulid.NewUlid();
            var args = Fields.Node(call, "args") is JsonObject argsObject
                ? argsObject.ToJsonString(GatewayJson.Options)
                : "{}";
            var index = _nextBlock++;
            _sawFunctionCall = true;
            yield return new BlockStartEvent(index, BlockKind.ToolCall, id, name);
            yield return new ToolArgsDeltaEvent(index, args);
            yield return new BlockStopEvent(index);
        }
        // executableCode / codeExecutionResult and unknown part kinds carry no generation content.
    }

    private IEnumerable<UnifiedStreamEvent> SignatureBlock(string signature)
    {
        foreach (var e in CloseOpen()) yield return e;
        var index = _nextBlock++;
        yield return new BlockStartEvent(index, BlockKind.Reasoning);
        yield return new ReasoningDeltaEvent(index, null, signature, Origin: ApiProtocol.Gemini);
        yield return new BlockStopEvent(index);
    }

    private IEnumerable<UnifiedStreamEvent> OpenBlock(BlockKind kind)
    {
        if (_openKind == kind) yield break; // consecutive same-kind parts continue the open block
        foreach (var e in CloseOpen()) yield return e;
        var index = _nextBlock++;
        _open = index;
        _openKind = kind;
        yield return new BlockStartEvent(index, kind);
    }

    private IEnumerable<UnifiedStreamEvent> CloseOpen()
    {
        if (_open is { } index)
        {
            _open = null;
            _openKind = null;
            yield return new BlockStopEvent(index);
        }
    }

    private static FinishReason MapFinish(string finish) => finish switch
    {
        "STOP" => FinishReason.Stop,
        "MAX_TOKENS" => FinishReason.Length,
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => FinishReason.ContentFilter,
        _ => FinishReason.Other,
    };

    private string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
