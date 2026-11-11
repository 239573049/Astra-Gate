using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Protocol.Chat;

/// <summary>
/// Converts IR events into Chat Completions client output: streaming chunks (SSE frames) and the
/// aggregated non-streaming chat.completion body. Signatures / encrypted reasoning are dropped here —
/// only the plain text survives cross-protocol (plan §6.4) — except a Gemini thought signature in front of a tool
/// call: it rides on that call as <c>extra_content.google.thought_signature</c> (<see cref="ChatCodec.ThoughtSignatureOf"/>),
/// which Gemini 3 requires back on the next turn. The encoder only runs on the conversion path
/// (same-protocol traffic is pass-through), so usage is always synthesized into the OpenAI shape from the
/// normalized counters — the upstream's raw usage is a foreign shape (plan §4.2).
/// </summary>
public sealed class ChatResponseEncoder : IResponseEncoder
{
    private readonly ResponseEncodeContext _ctx;
    private readonly List<ToolCallBuffer> _tools = [];
    private readonly Dictionary<int, ToolCallBuffer> _toolByBlock = [];
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private FinishReason _finish = FinishReason.Stop;
    private NormalizedUsage? _usage;
    private string? _signature; // a Gemini signature seen, waiting for the tool call it belongs to

    public ChatResponseEncoder(ResponseEncodeContext ctx) => _ctx = ctx;

    public IReadOnlyList<SseEvent> OnEvent(UnifiedStreamEvent e)
    {
        // Gemini puts a part's signature before the part (GeminiResponseDecoder.SignatureBlock): remember it for the next call.
        if (e is ReasoningDeltaEvent { Origin: ApiProtocol.Gemini, Signature: { Length: > 0 } signature }) _signature = signature;
        switch (e)
        {
            case MessageStartEvent:
                return [Frame(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }))];

            case BlockStartEvent { Kind: BlockKind.ToolCall } start:
                var tool = new ToolCallBuffer(start.ToolCallId ?? $"call_{Ulid.NewUlid()}", start.ToolName ?? "", _signature);
                _signature = null;
                _tools.Add(tool);
                _toolByBlock[start.Index] = tool;
                var startCall = new JsonObject
                {
                    ["index"] = _tools.Count - 1,
                    ["id"] = tool.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = "" },
                };
                if (tool.Signature is { } startSignature) startCall["extra_content"] = ChatCodec.ThoughtSignatureExtra(startSignature);
                return [Frame(Chunk(new JsonObject { ["tool_calls"] = new JsonArray(startCall) }))];

            case TextDeltaEvent delta:
                _signature = null; // a signature before text belongs to that text, not to a later call
                _text.Append(delta.Text);
                return [Frame(Chunk(new JsonObject { ["content"] = delta.Text }))];

            case ReasoningDeltaEvent { Text: { Length: > 0 } reasoning }:
                _reasoning.Append(reasoning);
                return [Frame(Chunk(new JsonObject { ["reasoning_content"] = reasoning }))];

            case ToolArgsDeltaEvent args when _toolByBlock.TryGetValue(args.Index, out var target):
                target.Arguments.Append(args.JsonFragment);
                return [Frame(Chunk(ToolCallsDelta(target, args.JsonFragment)))];

            case BlockStopEvent stop when _toolByBlock.TryGetValue(stop.Index, out var closed):
                // A tool call whose arguments never streamed would leave the client holding "" — close it as {}.
                return closed.Arguments.Length == 0
                    ? [Frame(Chunk(ToolCallsDelta(closed, "{}")))]
                    : [];

            case UsageEvent usage:
                _usage = usage.Usage;
                return [];

            case MessageStopEvent stop:
                _finish = stop.Reason;
                var frames = new List<SseEvent> { Frame(Chunk(new JsonObject(), FinishOf(stop.Reason))) };
                if (_ctx.IncludeUsage && _usage is not null) frames.Add(Frame(UsageChunk()));
                frames.Add(new SseEvent(null, "[DONE]"));
                return frames;

            case ErrorEvent error:
                return
                [
                    Frame(new JsonObject
                    {
                        ["error"] = new JsonObject { ["message"] = error.Message, ["type"] = error.Type, ["code"] = null },
                    }),
                    new SseEvent(null, "[DONE]"),
                ];

            default:
                return [];
        }
    }

    public JsonObject BuildJson()
    {
        var content = _text.ToString();
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content.Length > 0 ? content : null,
        };
        if (_reasoning.Length > 0) message["reasoning_content"] = _reasoning.ToString();
        if (_tools.Count > 0)
        {
            message["tool_calls"] = new JsonArray(_tools.Select(tool =>
            {
                var call = new JsonObject
                {
                    ["id"] = tool.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["arguments"] = tool.Arguments.Length > 0 ? tool.Arguments.ToString() : "{}",
                    },
                };
                if (tool.Signature is { } signature) call["extra_content"] = ChatCodec.ThoughtSignatureExtra(signature);
                return (JsonNode)call;
            }).ToArray());
        }
        var completion = new JsonObject
        {
            ["id"] = ResponseId(),
            ["object"] = "chat.completion",
            ["created"] = _ctx.Created.ToUnixTimeSeconds(),
            ["model"] = _ctx.Model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = FinishOf(_finish),
            }),
        };
        if (_usage is not null) completion["usage"] = UsageJson(_usage);
        return completion;
    }

    // ---------------------------------------------------------------- chunk shape

    private JsonObject Chunk(JsonObject delta, string? finish = null) => new()
    {
        ["id"] = ResponseId(),
        ["object"] = "chat.completion.chunk",
        ["created"] = _ctx.Created.ToUnixTimeSeconds(),
        ["model"] = _ctx.Model,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finish,
        }),
    };

    private JsonObject ToolCallsDelta(ToolCallBuffer tool, string arguments) => new()
    {
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["index"] = _tools.IndexOf(tool),
            ["function"] = new JsonObject { ["arguments"] = arguments },
        }),
    };

    private JsonObject UsageChunk() => new()
    {
        ["id"] = ResponseId(),
        ["object"] = "chat.completion.chunk",
        ["created"] = _ctx.Created.ToUnixTimeSeconds(),
        ["model"] = _ctx.Model,
        ["choices"] = new JsonArray(),
        ["usage"] = UsageJson(_usage!),
    };

    /// <summary>OpenAI usage lines synthesized from the normalized usage (plan §4.2): totals include the details.</summary>
    private static JsonObject UsageJson(NormalizedUsage usage)
    {
        var promptDetails = new JsonObject { ["cached_tokens"] = usage.Get(TokenTypes.CacheRead) };
        UsageNormalizer.WriteOpenAICacheWrite(promptDetails, usage);
        return new JsonObject
        {
            ["prompt_tokens"] = usage.TotalInput,
            ["completion_tokens"] = usage.TotalOutput,
            ["total_tokens"] = usage.TotalInput + usage.TotalOutput,
            ["prompt_tokens_details"] = promptDetails,
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.Get(TokenTypes.Reasoning) },
        };
    }

    private SseEvent Frame(JsonObject chunk) => new(null, chunk.ToJsonString(GatewayJson.Options));

    private string ResponseId() =>
        _ctx.ResponseId.StartsWith("chatcmpl-", StringComparison.Ordinal) ? _ctx.ResponseId : "chatcmpl-" + _ctx.ResponseId;

    internal static string FinishOf(FinishReason reason) => reason switch
    {
        FinishReason.Stop => "stop",
        FinishReason.Length => "length",
        FinishReason.ToolCalls => "tool_calls",
        FinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    private sealed class ToolCallBuffer(string id, string name, string? signature)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string? Signature { get; } = signature;
        public StringBuilder Arguments { get; } = new();
    }
}
