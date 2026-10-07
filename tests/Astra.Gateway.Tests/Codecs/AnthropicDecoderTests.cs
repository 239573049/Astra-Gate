using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Decoding upstream Anthropic output (stream fixtures and non-streaming JSON) into IR events.</summary>
public class AnthropicDecoderTests
{
    private static AnthropicResponseDecoder Decoder(bool stream = true) =>
        new(new ResponseDecodeContext { Stream = stream, Model = "claude-sonnet-4-5" });

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic", name);

    private static IReadOnlyList<SseEvent> Sse(string name) =>
        SseParser.ParseAll(File.ReadAllText(FixturePath(name)));

    private static List<UnifiedStreamEvent> Decode(string name)
    {
        var decoder = Decoder();
        var events = new List<UnifiedStreamEvent>();
        foreach (var sse in Sse(name))
            events.AddRange(decoder.DecodeSse(sse));
        events.AddRange(decoder.Complete());
        return events;
    }

    [Fact]
    public void Stream_Fixture_Decodes_To_Exact_Event_Sequence()
    {
        var events = Decode("messages-stream.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("msg_01FD3Bqfn6ZYWmA9xa2Rgo", "claude-sonnet-4-5"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, "I should check the weather.", Origin: ApiProtocol.Anthropic), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, null, Signature: "sig-abc-123", Origin: ApiProtocol.Anthropic), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(1, "Let me check."), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.Equal(new BlockStartEvent(2, BlockKind.ToolCall, "toolu_01A09q90qw", "get_weather"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(2, "{\"city\":"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(2, "\"San Francisco\",\"unit\":\"celsius\"}"), e),
            e => Assert.Equal(new BlockStopEvent(2), e),
            e => Assert.IsType<UsageEvent>(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.ToolCalls, "tool_use"), e));

        // exactly one usage event, merged from message_start + message_delta, normalized with the split
        var usage = Assert.Single(events.OfType<UsageEvent>());
        Assert.Equal(ApiProtocol.Anthropic, usage.Protocol);
        Assert.Equal(42, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(500, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(80, usage.Usage.Get(TokenTypes.CacheWrite5m));
        Assert.Equal(20, usage.Usage.Get(TokenTypes.CacheWrite1h));
        Assert.Equal(260, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(150, usage.Raw["cache_creation_input_tokens"]!.GetValue<int>());
        Assert.Equal(80, usage.Raw["cache_creation"]!["ephemeral_5m_input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Redacted_Thinking_Stream_Decodes_With_Encrypted_Payload()
    {
        var events = Decode("messages-redacted.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("msg_redact01", "claude-sonnet-4-5"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, null, EncryptedContent: "EoIBCkgIBRABGAIipRFuZYWkBCnW", Origin: ApiProtocol.Anthropic), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(1, "Done."), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.IsType<UsageEvent>(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.Stop, "end_turn"), e));

        // usage merged: message_delta output (29) overrides message_start output (3)
        var usage = Assert.Single(events.OfType<UsageEvent>());
        Assert.Equal(17, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(29, usage.Usage.Get(TokenTypes.Output));
    }

    [Fact]
    public void Error_Mid_Stream_Ends_The_Response_With_Error_Not_MessageStop()
    {
        var events = Decode("messages-error.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("msg_err01", "claude-opus-4-6"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(0, "Partial ans"), e),
            e => Assert.Equal(new ErrorEvent(529, "overloaded_error", "Overloaded"), e));
    }

    [Fact]
    public void Server_Tool_Use_Blocks_Are_Ignored_Without_Shifting_Indexes()
    {
        var sse = SseParser.ParseAll("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_x1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_01","name":"web_search","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"queries\":[\"x\"]}"}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hi"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":4}}

            event: message_stop
            data: {"type":"message_stop"}
            """);
        var decoder = Decoder();
        var events = new List<UnifiedStreamEvent>();
        foreach (var frame in sse)
            events.AddRange(decoder.DecodeSse(frame));
        events.AddRange(decoder.Complete());

        // the server_tool_use block (upstream index 0) vanishes: the text block becomes IR index 0
        Assert.Single(events.OfType<BlockStartEvent>());
        Assert.Equal(new BlockStartEvent(0, BlockKind.Text), events.OfType<BlockStartEvent>().Single());
        Assert.Equal(new TextDeltaEvent(0, "hi"), events.OfType<TextDeltaEvent>().Single());
        Assert.Empty(events.OfType<ToolArgsDeltaEvent>());
        Assert.Equal(new MessageStopEvent(FinishReason.Stop, "end_turn"), events[^1]);
    }

    [Fact]
    public void Non_Stream_Json_Decodes_To_Full_Sequence()
    {
        var body = (JsonNode.Parse(File.ReadAllText(FixturePath("messages.json")))!.AsObject());
        var events = Decoder(stream: false).DecodeJson(body).ToList();

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("msg_01XFDUDYJgAACzvnptvVoYEL", "claude-sonnet-4-5"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, "The user greeted me. A haiku fits.", Origin: ApiProtocol.Anthropic), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, null, Signature: "sig-xyz-123", Origin: ApiProtocol.Anthropic), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(1, "Hello!"), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.Equal(new BlockStartEvent(2, BlockKind.ToolCall, "toolu_01WrCIpKvyjMSUyVcB9eHMbN", "get_weather"), e),
            // the tool arguments arrive as one single fragment
            e => Assert.Equal(new ToolArgsDeltaEvent(2, """{"city":"Paris","unit":"celsius"}"""), e),
            e => Assert.Equal(new BlockStopEvent(2), e),
            e => Assert.IsType<UsageEvent>(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.ToolCalls, "tool_use"), e));

        var usage = Assert.Single(events.OfType<UsageEvent>());
        Assert.Equal(55, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(80, usage.Usage.Get(TokenTypes.CacheWrite5m));
        Assert.Equal(20, usage.Usage.Get(TokenTypes.CacheWrite1h));
        Assert.Equal(7, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(503, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal("standard", usage.Usage.ServiceTier);
    }

    [Fact]
    public void Complete_Closes_Open_Blocks_And_Emits_Remembered_Usage_And_Stop()
    {
        var decoder = Decoder();
        var events = new List<UnifiedStreamEvent>();
        foreach (var frame in SseParser.ParseAll("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_cut","type":"message","role":"assistant","model":"m","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":2}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"cut off"}}
            """))
        {
            events.AddRange(decoder.DecodeSse(frame));
        }

        events.AddRange(decoder.Complete());

        var last = events[^3..];
        Assert.Equal(new BlockStopEvent(0), last[0]);
        var usage = Assert.IsType<UsageEvent>(last[1]); // safety net: usage remembered from message_start
        Assert.Equal(3, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(new MessageStopEvent(FinishReason.Stop, null), last[2]); // stop_reason never seen → Stop
    }

    [Fact]
    public void Empty_Stream_Still_Opens_With_A_Synthetic_MessageStart()
    {
        // A degenerate upstream (zero usable events) must not leave the client without message_start.
        var decoder = Decoder();
        var events = decoder.Complete().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(new MessageStartEvent(null, "claude-sonnet-4-5"), events[0]);
        Assert.Equal(new MessageStopEvent(FinishReason.Stop, null), events[1]);
    }
}
