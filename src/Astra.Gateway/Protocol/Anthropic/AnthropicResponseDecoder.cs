using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Anthropic;

/// <summary>
/// Converts Anthropic upstream output (SSE events or one non-streaming message object) into IR events.
/// One instance per response. Anthropic reports usage in message_start and message_delta: both are merged
/// into one cumulative raw object (later non-null fields win, cache_creation merges per key); a UsageEvent is
/// emitted per message_delta — and from Complete() as a safety net if only message_start carried usage.
/// Block kinds the IR cannot represent (server_tool_use, web_search_tool_result, …) are ignored completely:
/// they get no IR index, so their deltas are skipped too.
/// </summary>
public sealed class AnthropicResponseDecoder : IResponseDecoder
{
    private readonly ResponseDecodeContext _ctx;
    private readonly Dictionary<int, int> _indexes = new(); // upstream block index → IR block index
    private readonly List<int> _open = new();               // IR indexes of blocks not yet stopped
    private int _nextIndex;
    private bool _started;
    private JsonObject? _usage;                             // cumulative raw usage
    private bool _usageEmitted;
    private string? _stopReason;
    private bool _finished;
    private bool _errored;

    public AnthropicResponseDecoder(ResponseDecodeContext ctx) => _ctx = ctx;

    public string? ResponseModel { get; private set; }

    public IEnumerable<UnifiedStreamEvent> DecodeSse(SseEvent sse)
    {
        if (string.IsNullOrEmpty(sse.Data) || sse.Data == "[DONE]") return [];
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(sse.Data) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
        if (json is null) return [];
        var type = AnthropicRequestCodec.Str(json, "type") ?? sse.Event ?? "";
        return Handle(type, json);
    }

    private IEnumerable<UnifiedStreamEvent> Handle(string type, JsonObject json)
    {
        var events = new List<UnifiedStreamEvent>();
        switch (type)
        {
            case "message_start":
            {
                _started = true;
                var message = json["message"] as JsonObject;
                if (AnthropicRequestCodec.Str(message, "model") is { } reportedModel && !string.IsNullOrWhiteSpace(reportedModel))
                    ResponseModel = reportedModel;
                RememberUsage(message?["usage"] as JsonObject);
                events.Add(new MessageStartEvent(
                    AnthropicRequestCodec.Str(message, "id"),
                    AnthropicRequestCodec.Str(message, "model") is { Length: > 0 } model ? model : _ctx.Model));
                break;
            }
            case "content_block_start":
            {
                var index = Index(json);
                var block = json["content_block"] as JsonObject;
                switch (AnthropicRequestCodec.Str(block, "type"))
                {
                    case "text":
                        events.Add(new BlockStartEvent(Open(index), BlockKind.Text));
                        break;
                    case "thinking":
                        events.Add(new BlockStartEvent(Open(index), BlockKind.Reasoning));
                        break;
                    case "redacted_thinking":
                    {
                        var ir = Open(index);
                        events.Add(new BlockStartEvent(ir, BlockKind.Reasoning));
                        if (AnthropicRequestCodec.Str(block, "data") is { Length: > 0 } data)
                            events.Add(new ReasoningDeltaEvent(ir, null, EncryptedContent: data, Origin: ApiProtocol.Anthropic));
                        break;
                    }
                    case "tool_use":
                        var callId = AnthropicRequestCodec.Str(block, "id") ?? $"call_{Ulid.NewUlid()}";
                        events.Add(new BlockStartEvent(Open(index), BlockKind.ToolCall, callId, AnthropicRequestCodec.Str(block, "name")));
                        break;
                    default:
                        // server_tool_use / web_search_tool_result / unknown: no IR index → deltas ignored.
                        break;
                }
                break;
            }
            case "content_block_delta":
            {
                var index = Index(json);
                if (!_indexes.TryGetValue(index, out var ir)) break;
                var delta = json["delta"] as JsonObject;
                switch (AnthropicRequestCodec.Str(delta, "type"))
                {
                    case "text_delta":
                        events.Add(new TextDeltaEvent(ir, AnthropicRequestCodec.Str(delta, "text") ?? ""));
                        break;
                    case "thinking_delta":
                        events.Add(new ReasoningDeltaEvent(ir, AnthropicRequestCodec.Str(delta, "thinking"), Origin: ApiProtocol.Anthropic));
                        break;
                    case "signature_delta":
                        events.Add(new ReasoningDeltaEvent(ir, null, Signature: AnthropicRequestCodec.Str(delta, "signature"), Origin: ApiProtocol.Anthropic));
                        break;
                    case "input_json_delta":
                        events.Add(new ToolArgsDeltaEvent(ir, AnthropicRequestCodec.Str(delta, "partial_json") ?? ""));
                        break;
                    // citations_delta and unknown delta types are ignored.
                }
                break;
            }
            case "content_block_stop":
            {
                var index = Index(json);
                if (_indexes.TryGetValue(index, out var ir))
                    events.Add(Close(index, ir));
                break;
            }
            case "message_delta":
            {
                if (json["delta"]?["stop_reason"] is JsonValue reason && reason.TryGetValue<string>(out var stop) && stop.Length > 0)
                    _stopReason = stop;
                if (json["usage"] is JsonObject usage)
                {
                    MergeUsage(usage);
                    events.Add(EmitUsage());
                }
                break;
            }
            case "message_stop":
                _finished = true;
                events.Add(MessageStop());
                break;
            case "error":
            {
                var error = json["error"] as JsonObject;
                var errorType = AnthropicRequestCodec.Str(error, "type") ?? "api_error";
                _errored = true;
                events.Add(new ErrorEvent(ErrorStatus(errorType), errorType, AnthropicRequestCodec.Str(error, "message") ?? "upstream error"));
                break;
            }
            // "ping" and anything unknown are ignored.
        }
        return events;
    }

    public IEnumerable<UnifiedStreamEvent> DecodeJson(JsonObject body)
    {
        _started = true;
        if (AnthropicRequestCodec.Str(body, "model") is { } reportedModel && !string.IsNullOrWhiteSpace(reportedModel))
            ResponseModel = reportedModel;
        _stopReason = AnthropicRequestCodec.Str(body, "stop_reason");
        var events = new List<UnifiedStreamEvent> { new MessageStartEvent(
            AnthropicRequestCodec.Str(body, "id"),
            AnthropicRequestCodec.Str(body, "model") is { Length: > 0 } model ? model : _ctx.Model) };
        if (body["usage"] is JsonObject usage)
            RememberUsage(usage);

        if (body["content"] is JsonArray content)
        {
            for (var index = 0; index < content.Count; index++)
            {
                if (content[index] is not JsonObject block) continue;
                switch (AnthropicRequestCodec.Str(block, "type"))
                {
                    case "text":
                    {
                        var ir = Open(index);
                        events.Add(new BlockStartEvent(ir, BlockKind.Text));
                        if (AnthropicRequestCodec.Str(block, "text") is { Length: > 0 } text)
                            events.Add(new TextDeltaEvent(ir, text));
                        events.Add(Close(index, ir));
                        break;
                    }
                    case "thinking":
                    {
                        var ir = Open(index);
                        events.Add(new BlockStartEvent(ir, BlockKind.Reasoning));
                        if (AnthropicRequestCodec.Str(block, "thinking") is { Length: > 0 } thinking)
                            events.Add(new ReasoningDeltaEvent(ir, thinking, Origin: ApiProtocol.Anthropic));
                        if (AnthropicRequestCodec.Str(block, "signature") is { Length: > 0 } signature)
                            events.Add(new ReasoningDeltaEvent(ir, null, Signature: signature, Origin: ApiProtocol.Anthropic));
                        events.Add(Close(index, ir));
                        break;
                    }
                    case "redacted_thinking":
                    {
                        var ir = Open(index);
                        events.Add(new BlockStartEvent(ir, BlockKind.Reasoning));
                        if (AnthropicRequestCodec.Str(block, "data") is { Length: > 0 } data)
                            events.Add(new ReasoningDeltaEvent(ir, null, EncryptedContent: data, Origin: ApiProtocol.Anthropic));
                        events.Add(Close(index, ir));
                        break;
                    }
                    case "tool_use":
                    {
                        var ir = Open(index);
                        var callId = AnthropicRequestCodec.Str(block, "id") ?? $"call_{Ulid.NewUlid()}";
                        events.Add(new BlockStartEvent(ir, BlockKind.ToolCall, callId, AnthropicRequestCodec.Str(block, "name")));
                        // The whole arguments object arrives at once as a single fragment.
                        var input = block["input"] as JsonObject ?? [];
                        events.Add(new ToolArgsDeltaEvent(ir, input.ToJsonString(GatewayJson.Options)));
                        events.Add(Close(index, ir));
                        break;
                    }
                    // server_tool_use / web_search_tool_result / others are ignored completely.
                }
            }
        }

        if (_usage is not null)
            events.Add(EmitUsage());
        _finished = true;
        events.Add(MessageStop());
        return events;
    }

    public IEnumerable<UnifiedStreamEvent> Complete()
    {
        // After an error event the response is over — the IR ends with Error, not with a synthetic MessageStop.
        if (_errored) return [];
        var events = new List<UnifiedStreamEvent>();
        if (!_started)
        {
            // Degenerate upstream stream (nothing usable arrived): clients still need the opening frame.
            _started = true;
            events.Add(new MessageStartEvent(null, _ctx.Model));
        }
        foreach (var ir in _open)
            events.Add(new BlockStopEvent(ir));
        _open.Clear();
        if (!_usageEmitted && _usage is not null)
            events.Add(EmitUsage());
        if (!_finished)
            events.Add(MessageStop());
        return events;
    }

    // ---------------------------------------------------------------- helpers

    private int Open(int index)
    {
        var ir = _nextIndex++;
        _indexes[index] = ir;
        _open.Add(ir);
        return ir;
    }

    private BlockStopEvent Close(int index, int ir)
    {
        _indexes.Remove(index);
        _open.Remove(ir);
        return new BlockStopEvent(ir);
    }

    private MessageStopEvent MessageStop() => new(MapStopReason(_stopReason), _stopReason);

    private UsageEvent EmitUsage()
    {
        var raw = (JsonObject)_usage!.DeepClone();
        _usageEmitted = true;
        return new UsageEvent(ApiProtocol.Anthropic, raw, UsageNormalizer.Normalize(ApiProtocol.Anthropic, raw)!);
    }

    private void RememberUsage(JsonObject? usage)
    {
        if (usage is null) return;
        _usage ??= [];
        MergeUsage(usage);
    }

    /// <summary>Cumulative merge: later non-null fields override; objects (cache_creation) merge per key.</summary>
    private void MergeUsage(JsonObject later)
    {
        _usage ??= [];
        foreach (var (key, value) in later)
        {
            if (value is null) continue;
            if (_usage[key] is JsonObject into && value is JsonObject from)
            {
                foreach (var (innerKey, innerValue) in from)
                    if (innerValue is not null)
                        into[innerKey] = innerValue.DeepClone();
            }
            else
            {
                _usage[key] = value.DeepClone();
            }
        }
    }

    private static int Index(JsonObject json) =>
        json["index"] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : -1;

    /// <summary>end_turn / stop_sequence → Stop; a missing stop_reason defaults to Stop (normal completion).</summary>
    private static FinishReason MapStopReason(string? reason) => reason switch
    {
        "max_tokens" => FinishReason.Length,
        "tool_use" => FinishReason.ToolCalls,
        "refusal" => FinishReason.ContentFilter,
        null or "" or "end_turn" or "stop_sequence" => FinishReason.Stop,
        _ => FinishReason.Other,
    };

    private static int ErrorStatus(string type) => type switch
    {
        "overloaded_error" => 529,
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "permission_error" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        _ => 500,
    };
}
