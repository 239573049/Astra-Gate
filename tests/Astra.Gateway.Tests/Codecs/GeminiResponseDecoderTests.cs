using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

public class GeminiResponseDecoderTests
{
    private static ResponseDecodeContext Ctx(bool stream = true) => new() { Stream = stream, Model = "gemini-2.5-flash" };

    private static IReadOnlyList<UnifiedStreamEvent> Stream(params string[] chunks)
    {
        var decoder = new GeminiResponseDecoder(Ctx());
        var events = new List<UnifiedStreamEvent>();
        foreach (var chunk in chunks) events.AddRange(decoder.DecodeSse(new SseEvent(null, chunk)));
        events.AddRange(decoder.Complete());
        return events;
    }

    private static IReadOnlyList<UnifiedStreamEvent> StreamFixture(string name)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gemini", name));
        var decoder = new GeminiResponseDecoder(Ctx());
        var events = new List<UnifiedStreamEvent>();
        foreach (var sse in SseParser.ParseAll(text)) events.AddRange(decoder.DecodeSse(sse));
        events.AddRange(decoder.Complete());
        return events;
    }

    private static JsonObject JsonBody(string name) =>
        (JsonObject)(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gemini", name)))
            ?? throw new InvalidOperationException("missing fixture " + name));

    [Fact]
    public void Cli_Stream_Decodes_To_Exact_Event_Sequence()
    {
        var events = StreamFixture("cli-stream.sse");

        Assert.Equal(17, events.Count);
        Assert.Equal(new MessageStartEvent("resp-abc123", "gemini-2.5-flash"), events[0]);

        // Thought text opens the reasoning block and keeps it open across chunks.
        Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), events[1]);
        var thought1 = Assert.IsType<ReasoningDeltaEvent>(events[2]);
        Assert.Equal((0, "Thinking about the weather request.", null, ApiProtocol.Gemini), (thought1.Index, thought1.Text, thought1.Signature, thought1.Origin));
        var thought2 = Assert.IsType<ReasoningDeltaEvent>(events[3]);
        Assert.Equal((0, "I should call the tool.", null, ApiProtocol.Gemini), (thought2.Index, thought2.Text, thought2.Signature, thought2.Origin));
        Assert.Equal(new BlockStopEvent(0), events[4]); // kind change: reasoning → text

        Assert.Equal(new BlockStartEvent(1, BlockKind.Text), events[5]);
        Assert.Equal(new TextDeltaEvent(1, "It is currently "), events[6]);
        Assert.Equal(new TextDeltaEvent(1, "22C and sunny in Tokyo."), events[7]);
        Assert.Equal(new BlockStopEvent(1), events[8]);

        // thoughtSignature on a functionCall part: standalone reasoning block before the tool block.
        Assert.Equal(new BlockStartEvent(2, BlockKind.Reasoning), events[9]);
        var signature = Assert.IsType<ReasoningDeltaEvent>(events[10]);
        Assert.Equal((2, null, "sig-xyz789", ApiProtocol.Gemini), (signature.Index, signature.Text, signature.Signature, signature.Origin));
        Assert.Equal(new BlockStopEvent(2), events[11]);

        var toolStart = Assert.IsType<BlockStartEvent>(events[12]);
        Assert.Equal(3, toolStart.Index);
        Assert.Equal(BlockKind.ToolCall, toolStart.Kind);
        Assert.Equal("get_weather", toolStart.ToolName);
        Assert.StartsWith("call_", toolStart.ToolCallId); // Gemini sent no id: "call_" + ULID
        var args = Assert.IsType<ToolArgsDeltaEvent>(events[13]);
        Assert.Equal("""{"city":"Tokyo"}""", args.JsonFragment);
        Assert.Equal(new BlockStopEvent(3), events[14]);

        // The last (cumulative) usageMetadata wins, normalized through UsageNormalizer.
        var usage = Assert.IsType<UsageEvent>(events[15]);
        Assert.Equal(ApiProtocol.Gemini, usage.Protocol);
        Assert.Equal(80, usage.Usage.Get(TokenTypes.Input));   // 120 prompt − 40 cached
        Assert.Equal(40, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(26, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(11, usage.Usage.Get(TokenTypes.Reasoning));

        // finishReason STOP + a function call in the response → ToolCalls.
        var stop = Assert.IsType<MessageStopEvent>(events[16]);
        Assert.Equal(FinishReason.ToolCalls, stop.Reason);
        Assert.Equal("STOP", stop.RawReason);
    }

    [Fact]
    public void Max_Tokens_Continues_Text_And_Finishes_With_Length()
    {
        var events = StreamFixture("stream-max-tokens.sse");

        Assert.Equal(7, events.Count);
        Assert.Equal(new MessageStartEvent("resp-max-1", "gemini-2.5-flash"), events[0]);
        Assert.Equal(new BlockStartEvent(0, BlockKind.Text), events[1]);
        Assert.Equal(new TextDeltaEvent(0, "The quick brown fox"), events[2]);
        Assert.Equal(new TextDeltaEvent(0, " jumps"), events[3]); // same text block across chunks
        Assert.Equal(new BlockStopEvent(0), events[4]);
        Assert.Equal(8, Assert.IsType<UsageEvent>(events[5]).Usage.Get(TokenTypes.Output));
        var stop = Assert.IsType<MessageStopEvent>(events[6]);
        Assert.Equal(FinishReason.Length, stop.Reason);
        Assert.Equal("MAX_TOKENS", stop.RawReason);
    }

    [Fact]
    public void Blocked_Prompt_Decodes_To_ContentFilter()
    {
        var events = StreamFixture("stream-blocked.sse");

        Assert.Equal(2, events.Count);
        Assert.Equal(new MessageStartEvent(null, "gemini-2.5-flash"), events[0]);
        var stop = Assert.IsType<MessageStopEvent>(events[1]);
        Assert.Equal(FinishReason.ContentFilter, stop.Reason);
        Assert.Equal("SAFETY", stop.RawReason);
    }

    [Fact]
    public void Error_Chunk_Decodes_To_Error_Event_And_Ends_The_Response()
    {
        var text = """{"candidates":[{"content":{"parts":[{"text":"hi"}],"role":"model"},"index":0}],"modelVersion":"m"}""";
        var error = """{"error":{"code":429,"message":"Resource exhausted","status":"RESOURCE_EXHAUSTED"}}""";

        var events = Stream(text, error, text);

        Assert.Equal(5, events.Count);
        Assert.Equal(new MessageStartEvent(null, "m"), events[0]);
        Assert.Equal(new BlockStartEvent(0, BlockKind.Text), events[1]);
        Assert.Equal(new TextDeltaEvent(0, "hi"), events[2]);
        Assert.Equal(new BlockStopEvent(0), events[3]);
        var errorEvent = Assert.IsType<ErrorEvent>(events[4]);
        Assert.Equal((429, "RESOURCE_EXHAUSTED", "Resource exhausted"), (errorEvent.Status, errorEvent.Type, errorEvent.Message));
        // No MessageStop after the error: the error replaces the tail of the response.
        Assert.IsType<ErrorEvent>(events[^1]);
    }

    [Fact]
    public void Safety_Finish_Reason_Decodes_To_ContentFilter()
    {
        var events = Stream("""{"candidates":[{"finishReason":"SAFETY","index":0}],"usageMetadata":{"promptTokenCount":5},"modelVersion":"m"}""");

        var stop = Assert.IsType<MessageStopEvent>(events[^1]);
        Assert.Equal(FinishReason.ContentFilter, stop.Reason);
        Assert.Equal("SAFETY", stop.RawReason);
        Assert.Equal(5, Assert.IsType<UsageEvent>(events[^2]).Usage.Get(TokenTypes.Input));
    }

    [Fact]
    public void Stream_Without_Finish_Reason_Still_Stops_With_Stop()
    {
        var events = Stream("""{"candidates":[{"content":{"parts":[{"text":"hi"}],"role":"model"},"index":0}],"modelVersion":"m"}""");

        var stop = Assert.IsType<MessageStopEvent>(events[^1]);
        Assert.Equal(FinishReason.Stop, stop.Reason);
        Assert.Null(stop.RawReason);
    }

    [Fact]
    public void Explicit_Tool_Call_Id_Is_Kept()
    {
        var events = Stream("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"f","args":{},"id":"call_9"}}],"role":"model"},"finishReason":"STOP","index":0}],"modelVersion":"m"}""");

        var start = events.OfType<BlockStartEvent>().Single();
        Assert.Equal("call_9", start.ToolCallId);
        Assert.Equal("""{}""", Assert.IsType<ToolArgsDeltaEvent>(events[2]).JsonFragment);
    }

    [Fact]
    public void Non_Stream_Body_Decodes_To_Full_Sequence()
    {
        var decoder = new GeminiResponseDecoder(Ctx(stream: false));
        var events = decoder.DecodeJson(JsonBody("non-stream.json")).ToList();

        Assert.Equal(10, events.Count);
        Assert.Equal(new MessageStartEvent("resp-nonstream-1", "gemini-2.5-flash"), events[0]);

        // The thought part carries its signature in the same reasoning block, before its own text.
        Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), events[1]);
        var signature = Assert.IsType<ReasoningDeltaEvent>(events[2]);
        Assert.Null(signature.Text);
        Assert.Equal("sig-thought-1", signature.Signature);
        var thought = Assert.IsType<ReasoningDeltaEvent>(events[3]);
        Assert.Equal("Let me check the forecast.", thought.Text);
        Assert.Equal(new BlockStopEvent(0), events[4]);

        Assert.Equal(new BlockStartEvent(1, BlockKind.Text), events[5]);
        Assert.Equal(new TextDeltaEvent(1, "The weather is 22C and sunny."), events[6]);
        Assert.Equal(new BlockStopEvent(1), events[7]);

        var usage = Assert.IsType<UsageEvent>(events[8]);
        Assert.Equal(50, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(12, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(5, usage.Usage.Get(TokenTypes.Reasoning));
        var stop = Assert.IsType<MessageStopEvent>(events[9]);
        Assert.Equal(FinishReason.Stop, stop.Reason);
    }

    [Fact]
    public void Error_Body_Decoded_As_Json_Yields_Only_An_Error()
    {
        var decoder = new GeminiResponseDecoder(Ctx(stream: false));
        var body = (JsonObject)JsonNode.Parse("""{"error":{"code":404,"message":"Model not found","status":"NOT_FOUND"}}""")!;

        var events = decoder.DecodeJson(body).ToList();

        var error = Assert.IsType<ErrorEvent>(Assert.Single(events));
        Assert.Equal((404, "NOT_FOUND", "Model not found"), (error.Status, error.Type, error.Message));
    }
}
