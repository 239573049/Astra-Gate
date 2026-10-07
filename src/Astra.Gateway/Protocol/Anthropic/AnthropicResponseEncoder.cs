using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Anthropic;

/// <summary>
/// Converts IR events into the Anthropic Messages client format: streaming frames whose data "type" equals the
/// "event:" name (what Claude Code parses), or one aggregated message object for non-streaming requests.
/// Reasoning blocks are opened lazily — only the first delta tells a normal thinking block from a
/// redacted_thinking blob, and content_block_start must already carry the right type. A reasoning block whose
/// deltas carry nothing emit-able (no text, no Anthropic signature) is never emitted at all.
/// </summary>
public sealed class AnthropicResponseEncoder : IResponseEncoder
{
    private readonly ResponseEncodeContext _ctx;
    private readonly Dictionary<int, OutBlock> _blocks = new(); // IR block index → emitted block
    private readonly List<OutBlock> _order = new();             // emitted block order, for BuildJson content
    private FinishReason? _finish;
    private NormalizedUsage? _usage;

    private sealed class OutBlock
    {
        public required BlockKind Kind { get; init; }
        public int Index { get; init; }
        public bool Opened { get; set; }                 // content_block_start written
        public bool Redacted { get; set; }
        public bool Skipped { get; set; }                // closed with nothing emit-able; left out of the output
        public StringBuilder Text { get; } = new();
        public string? Signature { get; set; }
        public string? Encrypted { get; set; }
        public string? ToolCallId { get; init; }
        public string? ToolName { get; init; }
        public StringBuilder Args { get; } = new();
    }

    public AnthropicResponseEncoder(ResponseEncodeContext ctx) => _ctx = ctx;

    public IReadOnlyList<SseEvent> OnEvent(UnifiedStreamEvent e)
    {
        switch (e)
        {
            case MessageStartEvent:
                return [Frame("message_start", new JsonObject { ["message"] = MessageJson() })];

            case BlockStartEvent b:
            {
                var block = new OutBlock { Kind = b.Kind, Index = b.Index, ToolCallId = b.ToolCallId, ToolName = b.ToolName };
                _blocks[b.Index] = block;
                _order.Add(block);
                return b.Kind == BlockKind.Reasoning ? [] : [StartFrame(block)];
            }

            case TextDeltaEvent t when _blocks.TryGetValue(t.Index, out var textBlock):
                textBlock.Text.Append(t.Text);
                return [Delta(textBlock, new JsonObject { ["type"] = "text_delta", ["text"] = t.Text })];

            case TextDeltaEvent:
                return [];

            case ReasoningDeltaEvent r when _blocks.TryGetValue(r.Index, out var reasoning):
            {
                var frames = new List<SseEvent>();
                if (!reasoning.Opened)
                {
                    // First delta decides the block type: an Anthropic-origin encrypted payload with no text
                    // is a redacted_thinking blob, everything else opens a normal thinking block. A delta with
                    // nothing emit-able (e.g. only a foreign thoughtSignature) keeps the block closed instead
                    // of starting an empty thinking block.
                    reasoning.Redacted = r.Text is null && r.Signature is null
                                         && r.Origin == ApiProtocol.Anthropic && r.EncryptedContent is { Length: > 0 };
                    if (reasoning.Redacted) reasoning.Encrypted = r.EncryptedContent;
                    var emit = reasoning.Redacted
                               || r.Text is { Length: > 0 }
                               || (r.Signature is { Length: > 0 } && r.Origin == ApiProtocol.Anthropic);
                    if (!emit) return [];
                    frames.Add(StartFrame(reasoning));
                }
                if (reasoning.Redacted) return frames;
                if (r.Text is { Length: > 0 } text)
                {
                    reasoning.Text.Append(text);
                    frames.Add(Delta(reasoning, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = text }));
                }
                if (r.Signature is { Length: > 0 } signature)
                {
                    // Signatures (like encrypted blobs) never cross protocols: keep Anthropic's own only.
                    if (r.Origin == ApiProtocol.Anthropic)
                    {
                        reasoning.Signature = signature;
                        frames.Add(Delta(reasoning, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }));
                    }
                }
                return frames;
            }

            case ReasoningDeltaEvent:
                return [];

            case ToolArgsDeltaEvent a when _blocks.TryGetValue(a.Index, out var toolBlock):
                toolBlock.Args.Append(a.JsonFragment);
                return [Delta(toolBlock, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = a.JsonFragment })];

            case ToolArgsDeltaEvent:
                return [];

            case BlockStopEvent s when _blocks.TryGetValue(s.Index, out var stopping):
            {
                // A reasoning block that never opened has nothing emit-able (no text, no Anthropic signature):
                // skip it entirely instead of writing an empty thinking block.
                if (stopping.Kind == BlockKind.Reasoning && !stopping.Opened)
                {
                    stopping.Skipped = true;
                    return [];
                }
                var frames = new List<SseEvent>();
                if (!stopping.Opened)
                    frames.Add(StartFrame(stopping)); // text/tool block with no deltas: still emit the (empty) block
                frames.Add(Frame("content_block_stop", new JsonObject { ["index"] = stopping.Index }));
                return frames;
            }

            case BlockStopEvent:
                return [];

            case UsageEvent usage:
                _usage = usage.Usage;
                return [];

            case MessageStopEvent stop:
                _finish = stop.Reason;
                return _ctx.Stream
                    ?
                    [
                        Frame("message_delta", new JsonObject
                        {
                            ["delta"] = new JsonObject { ["stop_reason"] = StopReason(stop.Reason), ["stop_sequence"] = null },
                            ["usage"] = FullUsage(),
                        }),
                        Frame("message_stop", []),
                    ]
                    : [];

            case ErrorEvent error:
                return [Frame("error", new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["type"] = AnthropicCodec.MapErrorType(error.Status, error.Type),
                        ["message"] = error.Message,
                    },
                })];

            default:
                return [];
        }
    }

    public JsonObject BuildJson()
    {
        var content = new JsonArray();
        foreach (var b in _order.Where(b => !b.Skipped))
        {
            switch (b.Kind)
            {
                case BlockKind.Text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = b.Text.ToString() });
                    break;
                case BlockKind.Reasoning when b.Redacted:
                    content.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = b.Encrypted ?? "" });
                    break;
                case BlockKind.Reasoning:
                    var thinking = new JsonObject { ["type"] = "thinking", ["thinking"] = b.Text.ToString() };
                    if (b.Signature is { Length: > 0 } signature)
                        thinking["signature"] = signature;
                    content.Add(thinking);
                    break;
                case BlockKind.ToolCall:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = b.ToolCallId ?? "",
                        ["name"] = b.ToolName ?? "",
                        ["input"] = ParseArguments(b.Args.ToString()),
                    });
                    break;
            }
        }
        var message = MessageJson();
        message["content"] = content;
        message["stop_reason"] = StopReason(_finish ?? FinishReason.Stop);
        message["usage"] = FullUsage();
        return message;
    }

    // ---------------------------------------------------------------- helpers

    private JsonObject MessageJson() => new()
    {
        ["id"] = ResponseId(),
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = _ctx.Model,
        ["content"] = new JsonArray(),
        ["stop_reason"] = null,
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
    };

    private SseEvent StartFrame(OutBlock block)
    {
        block.Opened = true;
        JsonObject contentBlock = block.Kind switch
        {
            BlockKind.Text => new JsonObject { ["type"] = "text", ["text"] = "" },
            BlockKind.ToolCall => new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = block.ToolCallId ?? "",
                ["name"] = block.ToolName ?? "",
                ["input"] = new JsonObject(),
            },
            _ => block.Redacted
                ? new JsonObject { ["type"] = "redacted_thinking", ["data"] = block.Encrypted ?? "" }
                : new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
        };
        return Frame("content_block_start", new JsonObject { ["index"] = block.Index, ["content_block"] = contentBlock });
    }

    private static SseEvent Delta(OutBlock block, JsonObject delta) => Frame("content_block_delta", new JsonObject
    {
        ["index"] = block.Index,
        ["delta"] = delta,
    });

    /// <summary>The client-facing usage lines (plan §4.2, Anthropic column).</summary>
    private JsonObject FullUsage()
    {
        var n = _usage;
        return new JsonObject
        {
            ["input_tokens"] = n?.Get(TokenTypes.Input) ?? 0,
            ["cache_creation_input_tokens"] = (n?.Get(TokenTypes.CacheWrite5m) ?? 0) + (n?.Get(TokenTypes.CacheWrite1h) ?? 0),
            ["cache_read_input_tokens"] = n?.Get(TokenTypes.CacheRead) ?? 0,
            ["output_tokens"] = n?.TotalOutput ?? 0,
        };
    }

    private string ResponseId()
    {
        var id = _ctx.ResponseId;
        return id.StartsWith("msg_", StringComparison.Ordinal) ? id : "msg_" + id;
    }

    /// <summary>Stop → end_turn, Length → max_tokens, ToolCalls → tool_use, ContentFilter → refusal.</summary>
    private static string StopReason(FinishReason reason) => reason switch
    {
        FinishReason.Length => "max_tokens",
        FinishReason.ToolCalls => "tool_use",
        FinishReason.ContentFilter => "refusal",
        _ => "end_turn",
    };

    private static JsonNode ParseArguments(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    private static SseEvent Frame(string name, JsonObject data)
    {
        var body = new JsonObject { ["type"] = name };
        foreach (var (key, value) in data)
            if (value is not null)
                body[key] = value.DeepClone(); // values are owned by the caller's object
        return new SseEvent(name, body.ToJsonString(GatewayJson.Options));
    }
}
