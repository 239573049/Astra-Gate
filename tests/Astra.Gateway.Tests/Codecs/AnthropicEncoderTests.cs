using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Encoding IR events into the Anthropic client format: SSE frames, BuildJson, and error mapping.</summary>
public class AnthropicEncoderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic", name);

    private static List<UnifiedStreamEvent> DecodeFixture(string name, out AnthropicResponseDecoder decoder)
    {
        decoder = new AnthropicResponseDecoder(new ResponseDecodeContext { Stream = true, Model = "claude-sonnet-4-5" });
        var events = new List<UnifiedStreamEvent>();
        foreach (var frame in SseParser.ParseAll(File.ReadAllText(FixturePath(name))))
            events.AddRange(decoder.DecodeSse(frame));
        events.AddRange(decoder.Complete());
        return events;
    }

    private static (List<SseEvent> Frames, List<JsonObject> Json) EncodeToStream(
        IEnumerable<UnifiedStreamEvent> events, string responseId = "01JANCHORTEST")
    {
        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext
        {
            Stream = true,
            Model = "claude-sonnet-4-5",
            ResponseId = responseId,
        });
        var frames = new List<SseEvent>();
        foreach (var e in events)
            frames.AddRange(encoder.OnEvent(e));
        return (frames, frames.Select(f => (JsonNode.Parse(f.Data)!.AsObject())).ToList());
    }

    [Fact]
    public void Encoder_Stream_Round_Trips_Decoder_Fixture()
    {
        var events = DecodeFixture("messages-stream.sse", out _);
        var (frames, json) = EncodeToStream(events);

        // every frame's data carries "type" equal to its event name (what Claude Code keys off)
        Assert.All(frames, f =>
            Assert.Equal(f.Event, json[frames.IndexOf(f)]["type"]!.GetValue<string>()));

        Assert.Equal("message_start", frames[0].Event);
        var message = json[0]["message"]!.AsObject();
        Assert.Equal("msg_01JANCHORTEST", (string?)message["id"]);
        Assert.Equal("message", (string?)message["type"]);
        Assert.Equal("assistant", (string?)message["role"]);
        Assert.Equal("claude-sonnet-4-5", (string?)message["model"]);
        Assert.Equal(0, message["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(0, message["usage"]!["output_tokens"]!.GetValue<int>());

        Assert.Equal("content_block_start", frames[1].Event);
        Assert.Equal(0, json[1]["index"]!.GetValue<int>());
        Assert.Equal("thinking", (string?)json[1]["content_block"]!["type"]);
        Assert.Equal("thinking_delta", (string?)json[2]["delta"]!["type"]);
        Assert.Equal("I should check the weather.", (string?)json[2]["delta"]!["thinking"]);
        Assert.Equal("signature_delta", (string?)json[3]["delta"]!["type"]);
        Assert.Equal("sig-abc-123", (string?)json[3]["delta"]!["signature"]);
        Assert.Equal("content_block_stop", frames[4].Event);
        Assert.Equal(0, json[4]["index"]!.GetValue<int>());

        Assert.Equal("content_block_start", frames[5].Event);
        Assert.Equal(1, json[5]["index"]!.GetValue<int>());
        Assert.Equal("text", (string?)json[5]["content_block"]!["type"]);
        Assert.Equal("text_delta", (string?)json[6]["delta"]!["type"]);
        Assert.Equal("Let me check.", (string?)json[6]["delta"]!["text"]);
        Assert.Equal("content_block_stop", frames[7].Event);

        Assert.Equal("content_block_start", frames[8].Event);
        Assert.Equal(2, json[8]["index"]!.GetValue<int>());
        var tool = json[8]["content_block"]!.AsObject();
        Assert.Equal("tool_use", (string?)tool["type"]);
        Assert.Equal("toolu_01A09q90qw", (string?)tool["id"]);
        Assert.Equal("get_weather", (string?)tool["name"]);
        Assert.Equal("input_json_delta", (string?)json[9]["delta"]!["type"]);
        Assert.Equal("input_json_delta", (string?)json[10]["delta"]!["type"]);
        Assert.Equal(
            "{\"city\":\"San Francisco\",\"unit\":\"celsius\"}",
            json[9]["delta"]!["partial_json"]!.GetValue<string>() + json[10]["delta"]!["partial_json"]!.GetValue<string>());
        Assert.Equal("content_block_stop", frames[11].Event);

        Assert.Equal("message_delta", frames[12].Event);
        Assert.Equal("tool_use", (string?)json[12]["delta"]!["stop_reason"]);
        Assert.Null(json[12]["delta"]!["stop_sequence"]);
        var usage = json[12]["usage"]!.AsObject();
        Assert.Equal(42L, usage["input_tokens"]!.GetValue<long>());
        Assert.Equal(100L, usage["cache_creation_input_tokens"]!.GetValue<long>()); // 80 (5m) + 20 (1h)
        Assert.Equal(500L, usage["cache_read_input_tokens"]!.GetValue<long>());
        Assert.Equal(260L, usage["output_tokens"]!.GetValue<long>());

        Assert.Equal("message_stop", frames[13].Event);
        Assert.Equal(14, frames.Count);
    }

    [Fact]
    public void BuildJson_Aggregates_Decoded_Non_Stream_Message()
    {
        var body = (JsonNode.Parse(File.ReadAllText(FixturePath("messages.json")))!.AsObject());
        var decoder = new AnthropicResponseDecoder(new ResponseDecodeContext { Stream = false, Model = "claude-sonnet-4-5" });
        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext
        {
            Stream = false,
            Model = "claude-sonnet-4-5",
            ResponseId = "01JBUILDTEST",
        });
        foreach (var e in decoder.DecodeJson(body))
            encoder.OnEvent(e);
        var json = encoder.BuildJson();

        Assert.Equal("message", (string?)json["type"]);
        Assert.Equal("msg_01JBUILDTEST", (string?)json["id"]);
        Assert.Equal("assistant", (string?)json["role"]);
        Assert.Equal("claude-sonnet-4-5", (string?)json["model"]);
        Assert.Equal("tool_use", (string?)json["stop_reason"]);
        Assert.Null(json["stop_sequence"]);

        var content = Assert.IsType<JsonArray>(json["content"]);
        Assert.Equal(3, content.Count);
        var thinking = Assert.IsType<JsonObject>(content[0]);
        Assert.Equal("thinking", (string?)thinking["type"]);
        Assert.Equal("The user greeted me. A haiku fits.", (string?)thinking["thinking"]);
        Assert.Equal("sig-xyz-123", (string?)thinking["signature"]);
        Assert.Equal("Hello!", (string?)Assert.IsType<JsonObject>(content[1])["text"]);
        var toolUse = Assert.IsType<JsonObject>(content[2]);
        Assert.Equal("tool_use", (string?)toolUse["type"]);
        Assert.Equal("get_weather", (string?)toolUse["name"]);
        Assert.Equal("Paris", toolUse["input"]!["city"]!.GetValue<string>());

        var usage = json["usage"]!.AsObject();
        Assert.Equal(55L, usage["input_tokens"]!.GetValue<long>());
        Assert.Equal(100L, usage["cache_creation_input_tokens"]!.GetValue<long>());
        Assert.Equal(7L, usage["cache_read_input_tokens"]!.GetValue<long>());
        Assert.Equal(503L, usage["output_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void Foreign_Reasoning_Shows_Text_But_Never_A_Signature()
    {
        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext { Stream = true, Model = "m", ResponseId = "r1" });
        var frames = new List<SseEvent>();
        frames.AddRange(encoder.OnEvent(new BlockStartEvent(0, BlockKind.Reasoning)));
        Assert.Empty(frames); // block start is deferred until the first delta
        frames.AddRange(encoder.OnEvent(new ReasoningDeltaEvent(0, "let me think", Origin: ApiProtocol.OpenAIResponses)));
        frames.AddRange(encoder.OnEvent(new ReasoningDeltaEvent(0, null, Signature: "sig", Origin: ApiProtocol.OpenAIResponses)));
        frames.AddRange(encoder.OnEvent(new BlockStopEvent(0)));

        var json = frames.Select(f => (f.Event, Data: JsonNode.Parse(f.Data)!.AsObject())).ToList();
        Assert.Equal("content_block_start", json[0].Event);
        Assert.Equal("thinking", (string?)json[0].Data["content_block"]!["type"]);
        Assert.Equal("thinking_delta", (string?)json[1].Data["delta"]!["type"]);
        Assert.Equal("let me think", (string?)json[1].Data["delta"]!["thinking"]);
        Assert.DoesNotContain(json, p => (string?)p.Data["delta"]?["type"] == "signature_delta");
        Assert.Equal("content_block_stop", json[^1].Event);

        // and the aggregated message shows the text without a signature
        var built = encoder.BuildJson();
        var thinking = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(built["content"])[0]);
        Assert.Equal("let me think", (string?)thinking["thinking"]);
        Assert.Null(thinking["signature"]);
    }

    [Fact]
    public void Anthropic_Encrypted_Only_Reasoning_Becomes_Redacted_Block()
    {
        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext { Stream = true, Model = "m", ResponseId = "r1" });
        var frames = new List<SseEvent>();
        frames.AddRange(encoder.OnEvent(new BlockStartEvent(0, BlockKind.Reasoning)));
        frames.AddRange(encoder.OnEvent(new ReasoningDeltaEvent(0, null, EncryptedContent: "blob", Origin: ApiProtocol.Anthropic)));
        frames.AddRange(encoder.OnEvent(new BlockStopEvent(0)));

        Assert.Equal(2, frames.Count);
        var start = JsonNode.Parse(frames[0].Data)!.AsObject();
        Assert.Equal("content_block_start", frames[0].Event);
        Assert.Equal("redacted_thinking", (string?)start["content_block"]!["type"]);
        Assert.Equal("blob", (string?)start["content_block"]!["data"]);
        Assert.Equal("content_block_stop", frames[1].Event);

        var built = encoder.BuildJson();
        var redacted = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(built["content"])[0]);
        Assert.Equal("redacted_thinking", (string?)redacted["type"]);
        Assert.Equal("blob", (string?)redacted["data"]);
    }

    [Fact]
    public void Error_Event_Becomes_Error_Frame()
    {
        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext { Stream = true, Model = "m", ResponseId = "r1" });
        var frames = encoder.OnEvent(new ErrorEvent(504, "timeout", "upstream idle"));

        var frame = Assert.Single(frames);
        Assert.Equal("error", frame.Event);
        var json = JsonNode.Parse(frame.Data)!.AsObject();
        Assert.Equal("error", (string?)json["type"]);
        Assert.Equal("api_error", (string?)json["error"]!["type"]);
        Assert.Equal("upstream idle", (string?)json["error"]!["message"]);
    }

    [Theory]
    [InlineData(400, "anything", "invalid_request_error")]
    [InlineData(401, "anything", "authentication_error")]
    [InlineData(403, "anything", "permission_error")]
    [InlineData(404, "anything", "not_found_error")]
    [InlineData(413, "anything", "request_too_large")]
    [InlineData(429, "anything", "rate_limit_error")]
    [InlineData(529, "anything", "overloaded_error")]
    [InlineData(500, "anything", "api_error")]
    [InlineData(503, "timeout", "api_error")]
    [InlineData(400, "rate_limit_error", "rate_limit_error")] // already an Anthropic type: kept
    public void EncodeError_Maps_Status_To_Anthropic_Types(int status, string type, string expected)
    {
        var body = new AnthropicCodec().EncodeError(status, type, "boom");

        Assert.Equal("error", (string?)body["type"]);
        var error = Assert.IsType<JsonObject>(body["error"]);
        Assert.Equal(expected, (string?)error["type"]);
        Assert.Equal("boom", (string?)error["message"]);
    }

    [Fact]
    public void DecodeError_Parses_Anthropic_Error_Body()
    {
        var (type, message) = new AnthropicCodec().DecodeError(
            404, """{"type":"error","error":{"type":"not_found_error","message":"model: foo"}}""");
        Assert.Equal("not_found_error", type);
        Assert.Equal("model: foo", message);
    }

    [Fact]
    public void DecodeError_Falls_Back_To_Truncated_Raw_Body()
    {
        var raw = "<html>" + new string('x', 600) + "</html>";
        var (type, message) = new AnthropicCodec().DecodeError(502, raw);
        Assert.Equal("api_error", type);
        Assert.Equal(500, message.Length);

        var (clientErrorType, _) = new AnthropicCodec().DecodeError(400, "not json");
        Assert.Equal("invalid_request_error", clientErrorType);
    }

    [Fact]
    public void Foreign_Signature_Only_Reasoning_Is_Not_Emitted_As_An_Empty_Thinking_Block()
    {
        // A Gemini thoughtSignature travels as a standalone reasoning block with a signature only;
        // replaying it to a Claude client must not produce {thinking:"",signature:""} noise.
        var events = new List<UnifiedStreamEvent>
        {
            new MessageStartEvent("u", "m"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, null, Signature: "sig-from-gemini", Origin: ApiProtocol.Gemini),
            new BlockStopEvent(0),
            new BlockStartEvent(1, BlockKind.Text),
            new TextDeltaEvent(1, "answer"),
            new BlockStopEvent(1),
            new MessageStopEvent(FinishReason.Stop, "end_turn"),
        };
        var (frames, json) = EncodeToStream(events);

        // Only message_start, the text block and the closing frames — no thinking block at all.
        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            frames.Select(f => f.Event).ToList());
        Assert.DoesNotContain(json, o => "thinking" == (string?)o["content_block"]?["type"]);
        Assert.DoesNotContain(json, o => (string?)o["delta"]?["type"] is "thinking_delta" or "signature_delta");

        var encoder = new AnthropicResponseEncoder(new ResponseEncodeContext { Stream = false, Model = "m", ResponseId = "r" });
        foreach (var e in events) encoder.OnEvent(e);
        var content = (JsonArray)encoder.BuildJson()["content"]!;
        Assert.Single(content); // only the text block survives into the non-streaming body too
        Assert.Equal("text", (string?)Assert.IsType<JsonObject>(content[0])["type"]);
    }
}
