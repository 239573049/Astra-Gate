using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;
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
    public void Error_Event_Nested_Like_The_ChatGPT_Backend_Keeps_Its_Message()
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        var events = decoder.DecodeSse(new SseEvent("error",
            """{"type":"error","sequence_number":2,"error":{"message":"Our servers are currently overloaded. Please try again later.","type":"service_unavailable_error","param":null,"code":"server_is_overloaded"}}""")).ToList();

        Assert.Equal(new ErrorEvent(500, "server_is_overloaded", "Our servers are currently overloaded. Please try again later."), Assert.Single(events));
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

/// <summary>
/// A tool call's arguments reach the IR whichever way the upstream sends them: as deltas, only in
/// <c>function_call_arguments.done</c>, or only in the item of <c>output_item.done</c> (GitHub issue #38).
/// </summary>
public class ResponsesToolArgumentsTests
{
    private static List<UnifiedStreamEvent> Decode(string sse)
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true, Model = "gpt-5.1" });
        var events = new List<UnifiedStreamEvent>();
        foreach (var e in SseParser.ParseAll(sse))
            events.AddRange(decoder.DecodeSse(e));
        events.AddRange(decoder.Complete());
        return events;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", name));

    private static string Event(string type, JsonObject data)
    {
        data["type"] = type;
        return $"event: {type}\ndata: {data.ToJsonString()}\n\n";
    }

    private static JsonObject Call(string call, string name, string arguments) =>
        new() { ["type"] = "function_call", ["call_id"] = call, ["name"] = name, ["arguments"] = arguments };

    private static string Added(int index, string call, string name) =>
        Event("response.output_item.added", new() { ["output_index"] = index, ["item"] = Call(call, name, "") });

    private static string Delta(int index, string delta) =>
        Event("response.function_call_arguments.delta", new() { ["output_index"] = index, ["delta"] = delta });

    private static string ArgsDone(int index, string arguments) =>
        Event("response.function_call_arguments.done", new() { ["output_index"] = index, ["arguments"] = arguments });

    private static string ItemDone(int index, string call, string name, string arguments) =>
        Event("response.output_item.done", new() { ["output_index"] = index, ["item"] = Call(call, name, arguments) });

    private static string Completed() =>
        Event("response.completed", new()
        {
            ["response"] = new JsonObject
            {
                ["id"] = "resp_x",
                ["status"] = "completed",
                ["model"] = "gpt-5.1",
                ["output"] = new JsonArray(new JsonObject { ["type"] = "function_call" }),
            },
        });

    /// <summary>The arguments each tool-call block was sent, in block order.</summary>
    private static Dictionary<int, string> ArgsByBlock(IEnumerable<UnifiedStreamEvent> events) =>
        events.OfType<ToolArgsDeltaEvent>().GroupBy(e => e.Index).ToDictionary(g => g.Key, g => string.Concat(g.Select(e => e.JsonFragment)));

    [Fact]
    public void Arguments_Only_In_The_Done_Events_Are_Not_Lost_For_Parallel_Calls()
    {
        var events = Decode(Fixture("stream-parallel-args-done-only.sse"));

        Assert.Collection(events.Where(e => e is not UsageEvent),
            e => Assert.Equal(new MessageStartEvent("resp_par", "gpt-5.1"), e),
            e => Assert.Equal(new BlockStartEvent(0, BlockKind.ToolCall, "call_a", "transport_alpha"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(0, """{"marker":"A"}"""), e),
            e => Assert.Equal(new BlockStopEvent(0), e),
            e => Assert.Equal(new BlockStartEvent(1, BlockKind.ToolCall, "call_b", "transport_beta"), e),
            e => Assert.Equal(new ToolArgsDeltaEvent(1, """{"marker":"B"}"""), e),
            e => Assert.Equal(new BlockStopEvent(1), e),
            e => Assert.Equal(new MessageStopEvent(FinishReason.ToolCalls, "completed"), e));
    }

    [Fact]
    public void Chat_Client_Receives_Each_Calls_Own_Arguments()
    {
        var encoder = new ChatCodec().CreateResponseEncoder(new ResponseEncodeContext
        {
            Stream = true,
            Model = "gpt-5.1",
            ResponseId = "01JTEST",
            Created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var frames = Decode(Fixture("stream-parallel-args-done-only.sse")).SelectMany(e => encoder.OnEvent(e));

        // What a Chat client assembles: arguments per tool_calls[].index.
        var byIndex = new SortedDictionary<int, System.Text.StringBuilder>();
        foreach (var frame in frames.Where(f => f.Data != "[DONE]"))
            foreach (var call in (JsonNode.Parse(frame.Data!)!["choices"] as JsonArray)?.FirstOrDefault()?["delta"]?["tool_calls"] as JsonArray ?? [])
            {
                var i = call!["index"]!.GetValue<int>();
                if (!byIndex.TryGetValue(i, out var sb)) byIndex[i] = sb = new();
                sb.Append(call["function"]?["arguments"]?.GetValue<string>());
            }

        Assert.Equal(["""{"marker":"A"}""", """{"marker":"B"}"""], byIndex.Values.Select(v => v.ToString()));
    }

    [Fact]
    public void Calls_Whose_Done_Events_Interleave_Keep_Their_Own_Arguments()
    {
        var events = Decode(
            Added(0, "call_a", "alpha") + Added(1, "call_b", "beta")
            + ArgsDone(1, """{"marker":"B"}""") + ArgsDone(0, """{"marker":"A"}""")
            + ItemDone(1, "call_b", "beta", """{"marker":"B"}""") + ItemDone(0, "call_a", "alpha", """{"marker":"A"}""")
            + Completed());

        Assert.Equal("""{"marker":"A"}""", ArgsByBlock(events)[0]);
        Assert.Equal("""{"marker":"B"}""", ArgsByBlock(events)[1]);
    }

    [Fact]
    public void Arguments_Only_On_The_Finished_Item_Are_Sent()
    {
        var events = Decode(Added(0, "call_a", "alpha") + ItemDone(0, "call_a", "alpha", """{"marker":"A"}""") + Completed());

        Assert.Equal("""{"marker":"A"}""", ArgsByBlock(events)[0]);
        Assert.Single(events.OfType<ToolArgsDeltaEvent>());
    }

    [Fact]
    public void Deltas_And_Both_Done_Events_Are_Not_Doubled()
    {
        var events = Decode(
            Added(0, "call_a", "alpha") + Delta(0, """{"marker":""") + Delta(0, "\"A\"}")
            + ArgsDone(0, """{"marker":"A"}""") + ItemDone(0, "call_a", "alpha", """{"marker":"A"}""") + Completed());

        Assert.Equal("""{"marker":"A"}""", ArgsByBlock(events)[0]);
        Assert.Equal(2, events.OfType<ToolArgsDeltaEvent>().Count());
    }

    [Fact]
    public void A_Done_Event_Sends_Only_The_Part_The_Deltas_Missed()
    {
        var events = Decode(
            Added(0, "call_a", "alpha") + Delta(0, """{"marker":""")
            + ArgsDone(0, """{"marker":"A"}""") + ItemDone(0, "call_a", "alpha", """{"marker":"A"}""") + Completed());

        Assert.Equal("""{"marker":"A"}""", ArgsByBlock(events)[0]);
    }

    [Fact]
    public void Done_Text_That_Does_Not_Continue_The_Deltas_Is_Not_Appended()
    {
        // The stream is already on its way: appending text that disagrees with it would corrupt the JSON.
        var events = Decode(
            Added(0, "call_a", "alpha") + Delta(0, """{"marker":"A"}""")
            + ArgsDone(0, """{"marker": "A", "other": 1}""") + Completed());

        Assert.Equal("""{"marker":"A"}""", ArgsByBlock(events)[0]);
    }
}
