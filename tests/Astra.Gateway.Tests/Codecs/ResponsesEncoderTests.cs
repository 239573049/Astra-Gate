using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>IR events → Codex/Grok-compatible Responses SSE frames.</summary>
public class ResponsesEncoderStreamTests
{
    private static readonly string ResponseId = "01J8K3Q5VBNWWB0X5Z6P9QW2R3"; // ULID, no protocol prefix

    private static ResponseEncodeContext Ctx(bool stream = true) => new()
    {
        Stream = stream,
        Model = "gpt-5.1",
        ResponseId = ResponseId,
        Created = DateTimeOffset.FromUnixTimeSeconds(1791432000),
    };

    private static UsageEvent UsageEvent()
    {
        var raw = (JsonNode.Parse("""
            {"input_tokens":120,"input_tokens_details":{"cached_tokens":64},"output_tokens":48,"output_tokens_details":{"reasoning_tokens":16},"total_tokens":168}
            """) as JsonObject)!;
        return new UsageEvent(ApiProtocol.OpenAIResponses, raw, UsageNormalizer.Normalize(ApiProtocol.OpenAIResponses, raw)!);
    }

    private static List<SseEvent> Stream(params UnifiedStreamEvent[] events)
    {
        var encoder = new ResponsesCodec().CreateResponseEncoder(Ctx());
        var frames = new List<SseEvent>();
        foreach (var e in events)
            frames.AddRange(encoder.OnEvent(e));
        return frames;
    }

    private static JsonObject Json(SseEvent sse) => (JsonNode.Parse(sse.Data) as JsonObject)!;

    [Fact]
    public void Stream_Emits_Real_Response_Frames_With_Increasing_Sequence_Numbers()
    {
        var frames = Stream(
            new MessageStartEvent("upstream-id", "gpt-5.1"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, "Thinking", Origin: ApiProtocol.OpenAIResponses),
            new ReasoningDeltaEvent(0, null, EncryptedContent: "gAAAAAenc", Origin: ApiProtocol.OpenAIResponses),
            new BlockStopEvent(0),
            new BlockStartEvent(1, BlockKind.Text),
            new TextDeltaEvent(1, "Hello"),
            new TextDeltaEvent(1, " world"),
            new BlockStopEvent(1),
            new BlockStartEvent(2, BlockKind.ToolCall, "call_1", "shell"),
            new ToolArgsDeltaEvent(2, "{\"cmd\""),
            new ToolArgsDeltaEvent(2, ":[\"ls\"]}"),
            new BlockStopEvent(2),
            UsageEvent(),
            new MessageStopEvent(FinishReason.ToolCalls, "completed"));

        var names = frames.Select(f => f.Event).ToList();
        Assert.Equal(
        [
            "response.created", "response.in_progress",
            "response.output_item.added", "response.reasoning_summary_part.added",
            "response.reasoning_summary_text.delta",
            "response.reasoning_summary_text.done", "response.reasoning_summary_part.done", "response.output_item.done",
            "response.output_item.added", "response.content_part.added",
            "response.output_text.delta", "response.output_text.delta",
            "response.output_text.done", "response.content_part.done", "response.output_item.done",
            "response.output_item.added",
            "response.function_call_arguments.delta", "response.function_call_arguments.delta",
            "response.function_call_arguments.done", "response.output_item.done",
            "response.completed",
        ], names);

        // Every frame: data.type == event name, sequence_number strictly increasing from 0.
        for (var i = 0; i < frames.Count; i++)
        {
            var json = Json(frames[i]);
            Assert.Equal(names[i], json["type"]!.GetValue<string>());
            Assert.Equal(i, json["sequence_number"]!.GetValue<long>());
        }

        // Gateway response id (prefixed), model, in_progress status, empty output at creation.
        var created = Json(frames[0])["response"]!.AsObject();
        Assert.Equal("resp_" + ResponseId, created["id"]!.GetValue<string>());
        Assert.Equal("response", created["object"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", created["model"]!.GetValue<string>());
        Assert.Equal("in_progress", created["status"]!.GetValue<string>());
        Assert.Equal(1791432000, created["created_at"]!.GetValue<long>());
        Assert.Empty(created["output"]!.AsArray());
        Assert.Null(created["usage"]);

        // output_index follows block order: reasoning 0, message 1, function_call 2.
        Assert.Equal(0, Json(frames[2])["output_index"]!.GetValue<long>());
        Assert.Equal(1, Json(frames[8])["output_index"]!.GetValue<long>());
        Assert.Equal(2, Json(frames[15])["output_index"]!.GetValue<long>());

        // Item ids carry the protocol prefixes; reasoning starts with an empty summary.
        var reasoningItem = Json(frames[2])["item"]!.AsObject();
        Assert.StartsWith("rs_", reasoningItem["id"]!.GetValue<string>());
        Assert.Equal("reasoning", reasoningItem["type"]!.GetValue<string>());
        Assert.Empty(reasoningItem["summary"]!.AsArray());
        Assert.Equal(0, Json(frames[3])["summary_index"]!.GetValue<long>());

        var messageItem = Json(frames[8])["item"]!.AsObject();
        Assert.StartsWith("msg_", messageItem["id"]!.GetValue<string>());
        Assert.Equal("message", messageItem["type"]!.GetValue<string>());
        Assert.Equal("assistant", messageItem["role"]!.GetValue<string>());
        Assert.Equal("in_progress", messageItem["status"]!.GetValue<string>());
        Assert.Empty(messageItem["content"]!.AsArray());

        var callItem = Json(frames[15])["item"]!.AsObject();
        Assert.StartsWith("fc_", callItem["id"]!.GetValue<string>());
        Assert.Equal("function_call", callItem["type"]!.GetValue<string>());
        Assert.Equal("call_1", callItem["call_id"]!.GetValue<string>());
        Assert.Equal("shell", callItem["name"]!.GetValue<string>());
        Assert.Equal("", callItem["arguments"]!.GetValue<string>());

        // Deltas concatenate; *_done carry the full payloads.
        Assert.Equal("Hello world", Json(frames[12])["text"]!.GetValue<string>());
        Assert.Equal("""{"cmd":["ls"]}""", Json(frames[18])["arguments"]!.GetValue<string>());

        // Encrypted content from an Origin==Responses delta lands in the reasoning item at output_item.done.
        var reasoningDone = Json(frames[7])["item"]!.AsObject();
        Assert.Equal("Thinking", reasoningDone["summary"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("gAAAAAenc", reasoningDone["encrypted_content"]!.GetValue<string>());

        // response.completed carries the full output array + usage.
        var completed = Json(frames[^1])["response"]!.AsObject();
        Assert.Equal("completed", completed["status"]!.GetValue<string>());
        var output = completed["output"]!.AsArray();
        Assert.Equal(3, output.Count);
        Assert.Equal("reasoning", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("message", output[1]!["type"]!.GetValue<string>());
        Assert.Equal("Hello world", output[1]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("function_call", output[2]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", output[2]!["call_id"]!.GetValue<string>());
        var usage = completed["usage"]!.AsObject();
        Assert.Equal(120, usage["input_tokens"]!.GetValue<long>());
        Assert.Equal(64, usage["input_tokens_details"]!["cached_tokens"]!.GetValue<long>());
        Assert.Equal(48, usage["output_tokens"]!.GetValue<long>());
        Assert.Equal(16, usage["output_tokens_details"]!["reasoning_tokens"]!.GetValue<long>());
        Assert.Equal(168, usage["total_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void Encrypted_Content_From_Foreign_Origin_Is_Not_Re_Emitted()
    {
        var frames = Stream(
            new MessageStartEvent(null, "gpt-5.1"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, "hmm", Signature: "sigA", EncryptedContent: "encA", Origin: ApiProtocol.Anthropic),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Stop, "completed"));

        var itemDone = Json(frames[^2])["item"]!.AsObject();
        Assert.Null(itemDone["encrypted_content"]); // Anthropic payload never re-emitted
        Assert.Equal("hmm", itemDone["summary"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("completed", Json(frames[^1])["response"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Length_Stops_As_Incomplete_MaxOutputTokens()
    {
        var frames = Stream(
            new MessageStartEvent(null, "gpt-5.1"),
            new BlockStartEvent(0, BlockKind.Text),
            new TextDeltaEvent(0, "Partial"),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Length, "max_output_tokens"));

        var last = Json(frames[^1]);
        Assert.Equal("response.incomplete", last["type"]!.GetValue<string>());
        Assert.Equal("incomplete", last["response"]!["status"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", last["response"]!["incomplete_details"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ErrorEvent_Becomes_Response_Failed_And_Closes_Open_Blocks()
    {
        var frames = Stream(
            new MessageStartEvent(null, "gpt-5.1"),
            new BlockStartEvent(0, BlockKind.Text),
            new TextDeltaEvent(0, "So far"),
            new ErrorEvent(504, "timeout", "upstream idle"));

        Assert.Equal("response.failed", frames[^1].Event);
        var failed = Json(frames[^1])["response"]!.AsObject();
        Assert.Equal("failed", failed["status"]!.GetValue<string>());
        Assert.Equal("timeout", failed["error"]!["code"]!.GetValue<string>());
        Assert.Equal("upstream idle", failed["error"]!["message"]!.GetValue<string>());
        // The interrupted text block still reaches the client as a completed item.
        Assert.Equal("message", failed["output"]!.AsArray()[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void NonAscii_Text_Stays_Readable_In_Frames()
    {
        var frames = Stream(
            new MessageStartEvent(null, "gpt-5.1"),
            new BlockStartEvent(0, BlockKind.Text),
            new TextDeltaEvent(0, "你好，世界"),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Stop, "completed"));

        Assert.Contains(frames, f => f.Data.Contains("你好，世界", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, f => f.Data.Contains("\\u", StringComparison.Ordinal));
    }
}

/// <summary>Non-streaming aggregation: BuildJson produces the final response object.</summary>
public class ResponsesEncoderBuildJsonTests
{
    [Fact]
    public void BuildJson_Aggregates_The_Response_Object()
    {
        var encoder = new ResponsesCodec().CreateResponseEncoder(new ResponseEncodeContext
        {
            Stream = false,
            Model = "gpt-5.1",
            ResponseId = "abc123",
            Created = DateTimeOffset.FromUnixTimeSeconds(1791432500),
        });

        var raw = (JsonNode.Parse("""
            {"input_tokens":90,"input_tokens_details":{"cached_tokens":0},"output_tokens":40,"output_tokens_details":{"reasoning_tokens":8},"total_tokens":130}
            """) as JsonObject)!;

        foreach (var e in new UnifiedStreamEvent[]
                 {
                     new MessageStartEvent(null, "gpt-5.1"),
                     new BlockStartEvent(0, BlockKind.Reasoning),
                     new ReasoningDeltaEvent(0, "Need the capital of France.", Origin: ApiProtocol.OpenAIResponses),
                     new BlockStopEvent(0),
                     new BlockStartEvent(1, BlockKind.Text),
                     new TextDeltaEvent(1, "Paris is the capital of France."),
                     new BlockStopEvent(1),
                     new UsageEvent(ApiProtocol.OpenAIResponses, raw, UsageNormalizer.Normalize(ApiProtocol.OpenAIResponses, raw)!),
                     new MessageStopEvent(FinishReason.Stop, "completed"),
                 })
            encoder.OnEvent(e);

        var json = encoder.BuildJson();
        Assert.Equal("resp_abc123", json["id"]!.GetValue<string>());
        Assert.Equal("response", json["object"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", json["model"]!.GetValue<string>());
        Assert.Equal("completed", json["status"]!.GetValue<string>());
        Assert.Equal(1791432500, json["created_at"]!.GetValue<long>());

        var output = json["output"]!.AsArray();
        Assert.Equal(2, output.Count);
        Assert.Equal("reasoning", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("Need the capital of France.", output[0]!["summary"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("message", output[1]!["type"]!.GetValue<string>());
        Assert.Equal("Paris is the capital of France.", output[1]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());

        var usage = json["usage"]!.AsObject();
        Assert.Equal(90, usage["input_tokens"]!.GetValue<long>());
        Assert.Equal(40, usage["output_tokens"]!.GetValue<long>());
        Assert.Equal(130, usage["total_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void BuildJson_Without_Terminal_Event_Is_Completed_With_Empty_Output()
    {
        var encoder = new ResponsesCodec().CreateResponseEncoder(new ResponseEncodeContext { Stream = false, Model = "m" });
        var json = encoder.BuildJson();
        Assert.Equal("completed", json["status"]!.GetValue<string>());
        Assert.Empty(json["output"]!.AsArray());
        Assert.Null(json["usage"]);
    }
}

/// <summary>Codec-level error mapping in the Responses error shape.</summary>
public class ResponsesErrorMappingTests
{
    private readonly ResponsesCodec _codec = new();

    [Fact]
    public void EncodeError_Uses_The_OpenAI_Error_Shape()
    {
        var json = _codec.EncodeError(429, "rate_limit_error", "slow down");
        Assert.Equal("slow down", json["error"]!["message"]!.GetValue<string>());
        Assert.Equal("rate_limit_error", json["error"]!["type"]!.GetValue<string>());
        Assert.Null(json["error"]!["param"]);
        Assert.Null(json["error"]!["code"]);
    }

    [Fact]
    public void DecodeError_Parses_Message_Type_And_Falls_Back_To_Raw()
    {
        var (type, message) = _codec.DecodeError(429,
            """{"error":{"message":"Too many requests","type":"rate_limit_error","code":"429","param":null}}""");
        Assert.Equal("rate_limit_error", type);
        Assert.Equal("Too many requests", message);

        var (rawType, rawMessage) = _codec.DecodeError(502, "<html>bad gateway");
        Assert.Equal("upstream_error", rawType);
        Assert.Equal("<html>bad gateway", rawMessage);

        var (longType, longMessage) = _codec.DecodeError(500, new string('x', 600));
        Assert.Equal("upstream_error", longType);
        Assert.Equal(500, longMessage.Length); // raw fallback is truncated to ≤500
    }

    [Fact]
    public void Codec_Satisfies_The_Codec_Contract()
    {
        Assert.Equal(ApiProtocol.OpenAIResponses, _codec.Protocol);
        Assert.IsType<ResponsesResponseDecoder>(_codec.CreateResponseDecoder(new ResponseDecodeContext { Stream = true }));
        Assert.IsType<ResponsesResponseEncoder>(_codec.CreateResponseEncoder(new ResponseEncodeContext { Stream = true }));
    }
}
