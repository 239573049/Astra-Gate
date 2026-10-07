using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Upstream Responses SSE → exact IR events, using realistic stream fixtures.</summary>
public class ResponsesStreamDecodeTests
{
    private static IReadOnlyList<UnifiedStreamEvent> Decode(string fixtureName)
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true, Model = "gpt-5.1" });
        var events = new List<UnifiedStreamEvent>();
        foreach (var sse in SseParser.ParseAll(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", fixtureName))))
            events.AddRange(decoder.DecodeSse(sse));
        events.AddRange(decoder.Complete());
        return events;
    }

    [Fact]
    public void Completed_Stream_Decodes_To_Exact_Events()
    {
        var events = Decode("stream-completed.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("resp_0a1b", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, "The user asks for", Origin: ApiProtocol.OpenAIResponses), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, " the weather in Paris.", Origin: ApiProtocol.OpenAIResponses), e),
            // encrypted_content arrives with output_item.done
            e => Assert.Equal(new ReasoningDeltaEvent(0, null, EncryptedContent: "gAAAAABo2z1encrypted", Origin: ApiProtocol.OpenAIResponses), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(1, "Let me check"), e),
            e => Assert.Equal(new TextDeltaEvent(1, " the weather for you."), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.Equal(new BlockStartEvent(2, BlockKind.ToolCall, "call_weather_1", "get_weather"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(2, "{\"city\":"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(2, "\"Paris\",\"unit\":\"celsius\"}"), e),
            e => Assert.Equal(new BlockStopEvent(2), e),
            e => Usage(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.ToolCalls, "completed"), e));
        // response.completed already stopped the stream → Complete() emits nothing.
        Assert.Equal(16, events.Count);
    }

    private static void Usage(UnifiedStreamEvent e)
    {
        var usage = Assert.IsType<UsageEvent>(e);
        Assert.Equal(ApiProtocol.OpenAIResponses, usage.Protocol);
        Assert.Equal(120, usage.Raw["input_tokens"]!.GetValue<int>());
        Assert.Equal("default", usage.Usage.ServiceTier);
        Assert.Equal(56, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(64, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(32, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(16, usage.Usage.Get(TokenTypes.Reasoning));
    }

    [Fact]
    public void Incomplete_Stream_Yields_Length_Finish()
    {
        var events = Decode("stream-incomplete.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("resp_0c3e", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(0, "Partial an"), e),
            // The message item was never *_done → Complete-style close happens with the terminal event.
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.IsType<UsageEvent>(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.Length, "max_output_tokens"), e));
    }

    [Fact]
    public void Failed_Stream_Yields_Error_Event_And_Terminal_Complete()
    {
        var events = Decode("stream-failed.sse");

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("resp_9f8e", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(0, "I will n"), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new ErrorEvent(500, "server_error", "The model failed to generate a response."), e));

        // Nothing after a failed response: feed more + Complete → silence.
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        foreach (var sse in SseParser.ParseAll(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "stream-failed.sse"))))
            decoder.DecodeSse(sse);
        Assert.Empty(decoder.Complete());
    }

    [Fact]
    public void Error_Event_Becomes_ErrorEvent()
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        var events = decoder.DecodeSse(new SseEvent("error",
            """{"type":"error","code":"rate_limit_exceeded","message":"Too many requests."}""")).ToList();

        Assert.Single(events);
        Assert.Equal(new ErrorEvent(500, "rate_limit_exceeded", "Too many requests."), events[0]);
        // Terminal: Complete() stays silent afterwards.
        Assert.Empty(decoder.Complete());
    }

    [Fact]
    public void Delta_Without_Item_Added_Still_Opens_A_Block()
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        var events = decoder.DecodeSse(new SseEvent("response.output_text.delta",
            """{"type":"response.output_text.delta","item_id":"msg_x","output_index":0,"content_index":0,"delta":"hi"}""")).ToList();

        Assert.Equal(2, events.Count);
        Assert.Equal(new BlockStartEvent(0, BlockKind.Text), events[0]);
        Assert.Equal(new TextDeltaEvent(0, "hi"), events[1]);
    }
}

/// <summary>Non-streaming: the whole response JSON → the complete IR event sequence.</summary>
public class ResponsesJsonDecodeTests
{
    [Fact]
    public void Response_Object_Decodes_To_Full_Sequence()
    {
        var body = (JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "response-completed.json"))) as JsonObject)!;
        var events = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = false, Model = "gpt-5.1" })
            .DecodeJson(body).ToList();

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("resp_json1", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.Reasoning), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, "Need the capital of France.", Origin: ApiProtocol.OpenAIResponses), e),
            e => Assert.Equal(new ReasoningDeltaEvent(0, null, EncryptedContent: "gAAAAAjson456", Origin: ApiProtocol.OpenAIResponses), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.Text), e),
            e => Assert.Equal(new TextDeltaEvent(1, "Paris is the capital of France."), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.IsType<UsageEvent>(e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.Stop, "completed"), e));

        var usage = (UsageEvent)events[^2];
        Assert.Equal("flex", usage.Usage.ServiceTier);
        Assert.Equal(90, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(32, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(8, usage.Usage.Get(TokenTypes.Reasoning));
    }

    [Fact]
    public void FunctionCall_Output_Yields_ToolCalls_Finish()
    {
        var events = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = false })
            .DecodeJson((JsonNode.Parse("""
                {
                  "id": "resp_fc",
                  "object": "response",
                  "status": "completed",
                  "model": "gpt-5.1",
                  "output": [
                    { "id": "fc_1", "type": "function_call", "status": "completed",
                      "call_id": "call_9", "name": "shell", "arguments": "{\"cmd\":[\"ls\"]}" }
                  ],
                  "usage": null
                }
                """) as JsonObject)!).ToList();

        Assert.Collection(events,
            e => Assert.Equal(new MessageStartEvent("resp_fc", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.ToolCall, "call_9", "shell"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(0, """{"cmd":["ls"]}"""), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.ToolCalls, "completed"), e));
    }

    [Fact]
    public void Empty_Stream_Still_Opens_With_A_Synthetic_ResponseCreated()
    {
        // A degenerate upstream (zero usable events) must not leave Codex without response.created.
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true, Model = "gpt-5.1" });
        var events = decoder.Complete().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(new MessageStartEvent(null, "gpt-5.1"), events[0]);
        Assert.Equal(new MessageStopEvent(FinishReason.Stop), events[1]);
    }
}
