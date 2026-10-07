using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// IR events → client OpenAI Responses output (SSE frames for Codex CLI / Grok Build, or the aggregated
/// non-streaming response object). Every streaming frame carries the event name as both the SSE
/// <c>event:</c> field and <c>data.type</c>, plus a <c>sequence_number</c> starting at 0.
/// Frame order per output item (own output_index, in block order):
///  text:      output_item.added → content_part.added → output_text.delta* → output_text.done →
///             content_part.done → output_item.done
///  reasoning: output_item.added → reasoning_summary_part.added → reasoning_summary_text.delta* →
///             reasoning_summary_text.done → reasoning_summary_part.done → output_item.done
///  tool call: output_item.added → function_call_arguments.delta* → function_call_arguments.done →
///             output_item.done
/// MessageStop ends with response.completed / response.incomplete; ErrorEvent with response.failed.
/// Each IR text block becomes one message output item with a single output_text part (content_index 0),
/// and each IR reasoning block one reasoning item with summary_index 0.
/// </summary>
public sealed class ResponsesResponseEncoder(ResponseEncodeContext ctx) : IResponseEncoder
{
    private readonly ResponseEncodeContext _ctx = ctx;
    private readonly string _id = PrefixedId(ctx.ResponseId);
    private readonly long _createdAt = ctx.Created.ToUnixTimeSeconds();

    private readonly Dictionary<int, OutBlock> _blocks = new();
    private readonly List<OutBlock> _order = [];
    private NormalizedUsage? _usage;
    private FinishReason? _stop;
    private string? _rawStop;
    private ErrorEvent? _error;
    private bool _started;
    private bool _terminated;
    private int _sequence;

    public IReadOnlyList<SseEvent> OnEvent(UnifiedStreamEvent e)
    {
        var frames = new List<SseEvent>();
        switch (e)
        {
            case MessageStartEvent:
                if (_started)
                    break;
                _started = true;
                Frame(frames, "response.created", new JsonObject { ["response"] = ResponseObject("in_progress", null, null, null, null) });
                Frame(frames, "response.in_progress", new JsonObject { ["response"] = ResponseObject("in_progress", null, null, null, null) });
                break;

            case BlockStartEvent start:
                BlockStart(start, frames);
                break;

            case TextDeltaEvent delta:
                if (_blocks.TryGetValue(delta.Index, out var textBlock))
                {
                    textBlock.Text.Append(delta.Text);
                    Frame(frames, "response.output_text.delta", new JsonObject
                    {
                        ["item_id"] = textBlock.ItemId,
                        ["output_index"] = textBlock.OutputIndex,
                        ["content_index"] = 0,
                        ["delta"] = delta.Text,
                    });
                }
                break;

            case ReasoningDeltaEvent reasoning:
                if (_blocks.TryGetValue(reasoning.Index, out var reasoningBlock))
                {
                    if (reasoning.Text is { } text)
                    {
                        reasoningBlock.Text.Append(text);
                        Frame(frames, "response.reasoning_summary_text.delta", new JsonObject
                        {
                            ["item_id"] = reasoningBlock.ItemId,
                            ["output_index"] = reasoningBlock.OutputIndex,
                            ["summary_index"] = 0,
                            ["delta"] = text,
                        });
                    }
                    // Encrypted content only round-trips to its own protocol (plan §6.4).
                    if (reasoning.EncryptedContent is { } encrypted && reasoning.Origin == ApiProtocol.OpenAIResponses)
                        reasoningBlock.EncryptedContent = encrypted;
                }
                break;

            case ToolArgsDeltaEvent args:
                if (_blocks.TryGetValue(args.Index, out var toolBlock))
                {
                    toolBlock.Text.Append(args.JsonFragment);
                    Frame(frames, "response.function_call_arguments.delta", new JsonObject
                    {
                        ["item_id"] = toolBlock.ItemId,
                        ["output_index"] = toolBlock.OutputIndex,
                        ["delta"] = args.JsonFragment,
                    });
                }
                break;

            case BlockStopEvent stop:
                if (_blocks.Remove(stop.Index, out var closing))
                    CloseBlock(closing, frames);
                break;

            case UsageEvent usage:
                _usage = usage.Usage;
                break;

            case MessageStopEvent messageStop:
                _stop = messageStop.Reason;
                _rawStop = messageStop.RawReason;
                TerminalFrames(frames);
                break;

            case ErrorEvent error:
                _error = error;
                TerminalFrames(frames);
                break;
        }
        return frames;
    }

    public JsonObject BuildJson()
    {
        var (status, incomplete, error) = TerminalState();
        return ResponseObject(status, OutputArray(), ResponsesCodec.ClientUsage(_usage), incomplete, error);
    }

    // ---------------------------------------------------------------- blocks

    private void BlockStart(BlockStartEvent start, List<SseEvent> frames)
    {
        if (_blocks.ContainsKey(start.Index))
            return;
        var block = new OutBlock
        {
            OutputIndex = _order.Count,
            Kind = start.Kind,
            ItemId = start.Kind switch
            {
                BlockKind.Reasoning => "rs_",
                BlockKind.ToolCall => "fc_",
                _ => "msg_",
            } + Ulid.NewUlid(),
            CallId = start.ToolCallId ?? "call_" + Ulid.NewUlid(),
            ToolName = start.ToolName ?? "",
        };
        _blocks[start.Index] = block;
        _order.Add(block);

        switch (block.Kind)
        {
            case BlockKind.Text:
                Frame(frames, "response.output_item.added", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: false),
                });
                Frame(frames, "response.content_part.added", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["content_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
                });
                break;

            case BlockKind.Reasoning:
                Frame(frames, "response.output_item.added", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: false),
                });
                Frame(frames, "response.reasoning_summary_part.added", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["summary_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" },
                });
                break;

            case BlockKind.ToolCall:
                Frame(frames, "response.output_item.added", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: false),
                });
                break;
        }
    }

    private void CloseBlock(OutBlock block, List<SseEvent> frames)
    {
        var text = block.Text.ToString();
        switch (block.Kind)
        {
            case BlockKind.Text:
                Frame(frames, "response.output_text.done", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["content_index"] = 0,
                    ["text"] = text,
                });
                Frame(frames, "response.content_part.done", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["content_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() },
                });
                Frame(frames, "response.output_item.done", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: true),
                });
                break;

            case BlockKind.Reasoning:
                Frame(frames, "response.reasoning_summary_text.done", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["summary_index"] = 0,
                    ["text"] = text,
                });
                Frame(frames, "response.reasoning_summary_part.done", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["summary_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = text },
                });
                Frame(frames, "response.output_item.done", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: true),
                });
                break;

            case BlockKind.ToolCall:
                Frame(frames, "response.function_call_arguments.done", new JsonObject
                {
                    ["item_id"] = block.ItemId,
                    ["output_index"] = block.OutputIndex,
                    ["arguments"] = text,
                });
                Frame(frames, "response.output_item.done", new JsonObject
                {
                    ["output_index"] = block.OutputIndex,
                    ["item"] = ItemJson(block, completed: true),
                });
                break;
        }
        block.Closed = true;
    }

    private JsonObject ItemJson(OutBlock block, bool completed)
    {
        switch (block.Kind)
        {
            case BlockKind.Text:
                return new JsonObject
                {
                    ["id"] = block.ItemId,
                    ["type"] = "message",
                    ["status"] = completed ? "completed" : "in_progress",
                    ["role"] = "assistant",
                    ["content"] = completed
                        ? new JsonArray(TextPartJson(block.Text.ToString()))
                        : [],
                };
            case BlockKind.Reasoning:
            {
                // Reasoning items carry no status field.
                var item = new JsonObject
                {
                    ["id"] = block.ItemId,
                    ["type"] = "reasoning",
                    ["summary"] = completed && block.Text.Length > 0
                        ? new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = block.Text.ToString() })
                        : new JsonArray(),
                };
                if (completed && block.EncryptedContent is { } encrypted)
                    item["encrypted_content"] = encrypted;
                return item;
            }
            default:
                return new JsonObject
                {
                    ["id"] = block.ItemId,
                    ["type"] = "function_call",
                    ["status"] = completed ? "completed" : "in_progress",
                    ["call_id"] = block.CallId,
                    ["name"] = block.ToolName,
                    ["arguments"] = completed ? block.Text.ToString() : "",
                };
        }
    }

    private static JsonObject TextPartJson(string text) => new()
    {
        ["type"] = "output_text",
        ["text"] = text,
        ["annotations"] = new JsonArray(),
    };

    // ---------------------------------------------------------------- terminal

    private void TerminalFrames(List<SseEvent> frames)
    {
        if (_terminated)
            return;
        _terminated = true;
        // Defensive: close blocks the pipeline never stopped (partial content still reaches the client).
        foreach (var open in _order.Where(b => !b.Closed).ToArray())
            CloseBlock(open, frames);
        _blocks.Clear();

        var (status, incomplete, error) = TerminalState();
        var eventName = error is not null ? "response.failed"
            : status == "incomplete" ? "response.incomplete"
            : "response.completed";
        Frame(frames, eventName, new JsonObject
        {
            ["response"] = ResponseObject(status, OutputArray(), ResponsesCodec.ClientUsage(_usage), incomplete, error),
        });
    }

    private (string Status, JsonObject? Incomplete, JsonObject? Error) TerminalState()
    {
        if (_error is { } error)
            return ("failed", null, new JsonObject { ["code"] = error.Type, ["message"] = error.Message });
        return _stop switch
        {
            FinishReason.Length => ("incomplete", new JsonObject { ["reason"] = "max_output_tokens" }, null),
            FinishReason.ContentFilter => ("incomplete", new JsonObject { ["reason"] = "content_filter" }, null),
            FinishReason.Error => ("failed", null, new JsonObject { ["code"] = _rawStop ?? "error", ["message"] = _rawStop ?? "upstream error" }),
            _ => ("completed", null, null),
        };
    }

    private JsonArray OutputArray()
    {
        var output = new JsonArray();
        foreach (var block in _order)
            output.Add(ItemJson(block, completed: true));
        return output;
    }

    private JsonObject ResponseObject(string status, JsonArray? output, JsonObject? usage, JsonObject? incomplete, JsonObject? error)
    {
        var response = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "response",
            ["created_at"] = _createdAt,
            ["status"] = status,
            ["model"] = _ctx.Model,
            ["output"] = output ?? new JsonArray(),
            ["usage"] = usage,
            ["error"] = error,
            ["incomplete_details"] = incomplete,
            ["metadata"] = _ctx.Request?.Metadata is { Count: > 0 } m ? m.DeepClone() : new JsonObject(),
        };
        // Small echo of what the client asked for (Codex compares these); optional, kept minimal.
        if (_ctx.Request is { } request)
        {
            var echo = new JsonObject();
            ResponsesCodec.WriteTools(echo, request.Tools, null);
            if (echo["tools"] is JsonArray tools && tools.Count > 0)
                response["tools"] = tools.DeepClone();
            if (ResponsesCodec.ToolChoiceToJson(request.ToolChoice) is { } choice)
                response["tool_choice"] = choice.DeepClone();
            if (request.Temperature is { } temperature)
                response["temperature"] = temperature;
            if (request.TopP is { } topP)
                response["top_p"] = topP;
            if (request.MaxOutputTokens is { } max)
                response["max_output_tokens"] = max;
            if (request.ParallelToolCalls is { } parallel)
                response["parallel_tool_calls"] = parallel;
            if (ResponsesCodec.ReasoningToJson(request.Reasoning, new EffortBudgets()) is { } reasoning)
                response["reasoning"] = reasoning.DeepClone();
        }
        return response;
    }

    private void Frame(List<SseEvent> frames, string eventName, JsonObject data)
    {
        data["type"] = eventName;
        data["sequence_number"] = _sequence++;
        frames.Add(new SseEvent(eventName, data.ToJsonString(GatewayJson.Options)));
    }

    private static string PrefixedId(string? id) =>
        string.IsNullOrEmpty(id) ? "resp_" + Ulid.NewUlid()
        : id.StartsWith("resp_", StringComparison.Ordinal) ? id
        : "resp_" + id;

    private sealed class OutBlock
    {
        public int OutputIndex { get; init; }
        public BlockKind Kind { get; init; }
        public string ItemId { get; init; } = "";
        public string CallId { get; init; } = "";
        public string ToolName { get; init; } = "";
        public StringBuilder Text { get; } = new();
        public string? EncryptedContent { get; set; }
        public bool Closed { get; set; }
    }
}
