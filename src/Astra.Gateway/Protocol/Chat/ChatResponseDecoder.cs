using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Protocol.Chat;

/// <summary>
/// Converts upstream Chat Completions output (SSE chunks or one JSON body) into IR events.
/// Holds the per-response state: exactly one IR block is open at a time and a kind change
/// (text ↔ reasoning ↔ next tool call) closes it. Block indexes are 0,1,2… in order of appearance.
/// </summary>
public sealed class ChatResponseDecoder : IResponseDecoder
{
    private readonly ResponseDecodeContext _ctx;

    private bool _started;
    private bool _stopped;
    private bool _errored;
    private FinishReason _finish = FinishReason.Other;
    private string? _rawFinish;

    private int _nextBlock;
    private int _openBlock = -1;
    private BlockKind _openKind;
    private readonly Dictionary<int, int> _toolBlocks = new(); // upstream tool_calls index → IR block index

    public ChatResponseDecoder(ResponseDecodeContext ctx) => _ctx = ctx;

    public string? ResponseModel { get; private set; }

    public IEnumerable<UnifiedStreamEvent> DecodeSse(SseEvent sse)
    {
        if (sse.Data == "[DONE]") return Complete();
        JsonObject? chunk = null;
        try
        {
            chunk = JsonNode.Parse(sse.Data) as JsonObject;
        }
        catch (JsonException)
        {
            // Not JSON (keep-alive padding): ignore.
        }
        if (chunk is null) return [];
        if (Str(chunk, "model") is { } model && !string.IsNullOrWhiteSpace(model)) ResponseModel = model;
        if (chunk["error"] is JsonObject error)
        {
            _errored = true;
            return [ErrorEventOf(error, sse.Data)];
        }

        var events = new List<UnifiedStreamEvent>();
        if (!_started)
        {
            _started = true;
            events.Add(new MessageStartEvent(Str(chunk, "id"), Str(chunk, "model") ?? _ctx.Model));
        }
        if ((chunk["choices"] as JsonArray) is { Count: > 0 } choices && choices[0] is JsonObject choice)
        {
            DecodeDelta(choice["delta"] as JsonObject, events);
            var finish = Str(choice, "finish_reason");
            if (finish is not null)
            {
                _finish = MapFinish(finish);
                _rawFinish = finish;
                // finish_reason ends the message content: close the open block so Usage stays after all BlockStops.
                events.AddRange(CloseOpenBlock());
            }
        }
        // The usage-only chunk (choices: []) and any chunk that carries a cumulative usage report.
        if (chunk["usage"] is JsonObject usage)
        {
            var normalized = UsageNormalizer.Normalize(ApiProtocol.OpenAIChat, usage, Str(chunk, "service_tier"));
            if (normalized is not null) events.Add(new UsageEvent(ApiProtocol.OpenAIChat, usage, normalized));
        }
        return events;
    }

    public IEnumerable<UnifiedStreamEvent> DecodeJson(JsonObject body)
    {
        if (Str(body, "model") is { } model && !string.IsNullOrWhiteSpace(model)) ResponseModel = model;
        var events = new List<UnifiedStreamEvent>();
        if (!_started)
        {
            _started = true;
            events.Add(new MessageStartEvent(Str(body, "id"), Str(body, "model") ?? _ctx.Model));
        }
        var choice = (body["choices"] as JsonArray) is { Count: > 0 } choices ? choices[0] as JsonObject : null;
        if (choice?["message"] is JsonObject message)
        {
            // Reasoning precedes the answer text (DeepSeek / Kimi reasoning_content, Kimi reasoning).
            var reasoning = Str(message, "reasoning_content") ?? Str(message, "reasoning");
            if (reasoning is { Length: > 0 })
            {
                events.AddRange(OpenBlock(BlockKind.Reasoning));
                events.Add(new ReasoningDeltaEvent(_openBlock, reasoning, Origin: ApiProtocol.OpenAIChat));
                events.AddRange(CloseOpenBlock());
            }
            var text = string.Concat(TextsOf(message["content"]));
            if (text.Length > 0)
            {
                events.AddRange(OpenBlock(BlockKind.Text));
                events.Add(new TextDeltaEvent(_openBlock, text));
                events.AddRange(CloseOpenBlock());
            }
            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var node in calls)
                {
                    if (node is not JsonObject call) continue;
                    var function = call["function"] as JsonObject ?? new JsonObject();
                    events.AddRange(CloseOpenBlock());
                    events.Add(new BlockStartEvent(_nextBlock, BlockKind.ToolCall,
                        Str(call, "id") ?? $"call_{Ulid.NewUlid()}", Str(function, "name")));
                    var arguments = Str(function, "arguments");
                    events.Add(new ToolArgsDeltaEvent(_nextBlock, string.IsNullOrEmpty(arguments) ? "{}" : arguments));
                    events.Add(new BlockStopEvent(_nextBlock));
                    _nextBlock++;
                }
            }
        }
        if (body["usage"] is JsonObject usage)
        {
            var normalized = UsageNormalizer.Normalize(ApiProtocol.OpenAIChat, usage, Str(body, "service_tier"));
            if (normalized is not null) events.Add(new UsageEvent(ApiProtocol.OpenAIChat, usage, normalized));
        }
        var finish = Str(choice, "finish_reason");
        if (finish is not null)
        {
            _finish = MapFinish(finish);
            _rawFinish = finish;
        }
        if (!_stopped)
        {
            _stopped = true;
            events.Add(new MessageStopEvent(_finish, _rawFinish));
        }
        return events;
    }

    public IEnumerable<UnifiedStreamEvent> Complete()
    {
        var events = new List<UnifiedStreamEvent>();
        if (!_started)
        {
            // The upstream never said anything; keep the event order contract intact.
            _started = true;
            events.Add(new MessageStartEvent(null, _ctx.Model));
        }
        events.AddRange(CloseOpenBlock());
        if (_errored) return events; // a response ends with MessageStop OR Error, never both
        if (!_stopped)
        {
            _stopped = true;
            events.Add(new MessageStopEvent(_finish, _rawFinish));
        }
        return events;
    }

    // ---------------------------------------------------------------- chunk internals

    private void DecodeDelta(JsonObject? delta, List<UnifiedStreamEvent> events)
    {
        if (delta is null) return;
        var reasoning = Str(delta, "reasoning_content") ?? Str(delta, "reasoning");
        if (reasoning is { Length: > 0 })
        {
            events.AddRange(OpenBlock(BlockKind.Reasoning));
            events.Add(new ReasoningDeltaEvent(_openBlock, reasoning, Origin: ApiProtocol.OpenAIChat));
        }
        if (Str(delta, "content") is { Length: > 0 } text)
        {
            events.AddRange(OpenBlock(BlockKind.Text));
            events.Add(new TextDeltaEvent(_openBlock, text));
        }
        if (delta["tool_calls"] is not JsonArray calls) return;
        foreach (var node in calls)
        {
            if (node is not JsonObject call) continue;
            var upstreamIndex = call["index"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : _toolBlocks.Count;
            var function = call["function"] as JsonObject;
            if (!_toolBlocks.TryGetValue(upstreamIndex, out var block))
            {
                // First sight of this tool call: close whatever is open and start its block.
                events.AddRange(CloseOpenBlock());
                block = _nextBlock++;
                _toolBlocks[upstreamIndex] = block;
                _openBlock = block;
                _openKind = BlockKind.ToolCall;
                events.Add(new BlockStartEvent(block, BlockKind.ToolCall,
                    Str(call, "id") ?? $"call_{Ulid.NewUlid()}", Str(function, "name")));
            }
            else if (_openBlock != block)
            {
                // Interleaved tool indexes: keep fragments attributable to their block.
                events.AddRange(CloseOpenBlock());
                _openBlock = block;
                _openKind = BlockKind.ToolCall;
            }
            if (Str(function, "arguments") is { Length: > 0 } fragment)
                events.Add(new ToolArgsDeltaEvent(block, fragment));
        }
    }

    /// <summary>Opens a text / reasoning block, closing the open one when the kind changes.</summary>
    private IEnumerable<UnifiedStreamEvent> OpenBlock(BlockKind kind)
    {
        if (_openBlock >= 0 && _openKind == kind) return []; // the same block continues
        var events = CloseOpenBlock().ToList();
        _openBlock = _nextBlock++;
        _openKind = kind;
        events.Add(new BlockStartEvent(_openBlock, kind));
        return events;
    }

    private IEnumerable<UnifiedStreamEvent> CloseOpenBlock()
    {
        if (_openBlock < 0) return [];
        var events = new List<UnifiedStreamEvent> { new BlockStopEvent(_openBlock) };
        _openBlock = -1;
        return events;
    }

    private static ErrorEvent ErrorEventOf(JsonObject error, string raw)
    {
        var message = Str(error, "message") ?? (raw.Length > 500 ? raw[..500] : raw);
        var type = Str(error, "type") ?? Str(error, "code") ?? "api_error";
        return new ErrorEvent(500, type, message);
    }

    internal static FinishReason MapFinish(string reason) => reason switch
    {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "tool_calls" or "function_call" => FinishReason.ToolCalls,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Other,
    };

    private static IEnumerable<string> TextsOf(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var text))
        {
            yield return text;
            yield break;
        }
        if (content is not JsonArray parts) yield break;
        foreach (var part in parts)
        {
            if (part is JsonObject o && Str(o, "type") == "text" && Str(o, "text") is { Length: > 0 } s)
                yield return s;
        }
    }

    internal static string? Str(JsonObject? o, string key) =>
        o is not null && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
