using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;

namespace Astra.Gateway.Tests.Codecs;

public class ChatResponseDecoderTests
{
    private static readonly ChatCodec Codec = new();
    private static readonly ResponseDecodeContext Ctx = new() { Stream = true, Model = "deepseek-reasoner" };
    private static ResponseDecodeContext JsonCtx() => new() { Stream = false, Model = "deepseek-reasoner" };

    private static string[] Format(IEnumerable<UnifiedStreamEvent> events) =>
        events.Select(FormatOne).ToArray();

    private static string FormatOne(UnifiedStreamEvent e) => e switch
    {
        MessageStartEvent m => $"Start({m.Id},{m.Model})",
        BlockStartEvent b => $"Block({b.Index},{b.Kind},{b.ToolCallId},{b.ToolName})",
        TextDeltaEvent t => $"Text({t.Index},{t.Text})",
        ReasoningDeltaEvent r => $"Reason({r.Index},{r.Text})",
        ToolArgsDeltaEvent a => $"Args({a.Index},{a.JsonFragment})",
        BlockStopEvent s => $"Close({s.Index})",
        UsageEvent u => $"Usage({u.Protocol},{u.Usage.Get(TokenTypes.Input)},{u.Usage.Get(TokenTypes.CacheRead)},{u.Usage.Get(TokenTypes.Output)},{u.Usage.Get(TokenTypes.Reasoning)},{u.Usage.ServiceTier},{u.Raw.ToJsonString()})",
        MessageStopEvent m => $"MsgStop({m.Reason},{m.RawReason})",
        ErrorEvent er => $"Error({er.Status},{er.Type},{er.Message})",
        _ => e.ToString() ?? "",
    };

    [Fact]
    public void Stream_Fixture_Decodes_To_Exact_Event_List()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chat", "chat-stream.sse"));
        var decoder = Codec.CreateResponseDecoder(Ctx);

        var events = new List<UnifiedStreamEvent>();
        foreach (var sse in SseParser.ParseAll(text)) events.AddRange(decoder.DecodeSse(sse));
        events.AddRange(decoder.Complete());

        Assert.Equal(
        [
            "Start(chatcmpl-9xK2mQ,deepseek-reasoner)",
            "Block(0,Reasoning,,)",
            "Reason(0,The user asks for weather in Paris and Tokyo.)",
            "Reason(0, Two independent calls are needed.)",
            "Close(0)",
            "Block(1,Text,,)",
            "Text(1,Let me check)",
            "Text(1, both cities.)",
            "Close(1)",
            "Block(2,ToolCall,call_9abc,get_weather)",
            "Args(2,{\"city\":)",
            "Args(2,\"Paris\"})",
            "Close(2)",
            "Block(3,ToolCall,call_9def,get_weather)",
            "Args(3,{\"city\":\"Tokyo\"})",
            "Close(3)",
            "Usage(OpenAIChat,10,11,26,12,default,{\"prompt_tokens\":21,\"completion_tokens\":38,\"total_tokens\":59,\"prompt_tokens_details\":{\"cached_tokens\":11},\"completion_tokens_details\":{\"reasoning_tokens\":12}})",
            "MsgStop(ToolCalls,tool_calls)",
        ], Format(events));

        // Complete() after [DONE] must not duplicate anything.
        Assert.Empty(decoder.Complete());
    }

    [Fact]
    public void Usage_Only_Chunk_Emits_Usage_Event()
    {
        var decoder = Codec.CreateResponseDecoder(Ctx);
        var events = new List<UnifiedStreamEvent>();
        events.AddRange(decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""")));
        events.AddRange(decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":40,"prompt_cache_miss_tokens":60,"completion_tokens":20}}""")));
        events.AddRange(decoder.DecodeSse(new SseEvent(null, "[DONE]")));

        var usage = Assert.IsType<UsageEvent>(events.Single(e => e is UsageEvent));
        Assert.Equal(ApiProtocol.OpenAIChat, usage.Protocol);
        Assert.Equal(60, usage.Usage.Get(TokenTypes.Input)); // DeepSeek hit/miss counters
        Assert.Equal(40, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(20, usage.Usage.Get(TokenTypes.Output));
        Assert.Null(usage.Usage.ServiceTier); // no service_tier on the chunk
    }

    [Fact]
    public void Error_Chunk_Mid_Stream_Emits_Error_And_Swallows_MessageStop()
    {
        var decoder = Codec.CreateResponseDecoder(Ctx);
        var first = decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"so far"},"finish_reason":null}]}""")).ToList();
        var error = decoder.DecodeSse(new SseEvent(null,
            """{"error":{"message":"The server is overloaded.","type":"server_error","code":"503"}}""")).ToList();
        var rest = decoder.Complete().ToList();

        Assert.Equal(["Start(c1,m)", "Block(0,Text,,)", "Text(0,so far)"], Format(first));
        Assert.Equal(["Error(500,server_error,The server is overloaded.)"], Format(error));
        Assert.Equal(["Close(0)"], Format(rest)); // no MessageStop after Error
    }

    [Fact]
    public void Missing_Done_Is_Closed_By_Complete_Once()
    {
        var decoder = Codec.CreateResponseDecoder(Ctx);
        var events = new List<UnifiedStreamEvent>();
        events.AddRange(decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":null}]}""")));
        events.AddRange(decoder.Complete()); // upstream stream ended without [DONE]
        events.AddRange(decoder.Complete()); // pipeline always calls Complete again

        Assert.Equal(
        [
            "Start(c1,m)",
            "Block(0,Text,,)",
            "Text(0,partial)",
            "Close(0)",
            "MsgStop(Other,)",
        ], Format(events));
        Assert.Single(events.OfType<MessageStopEvent>());
    }

    [Fact]
    public void Kind_Changes_Open_New_Blocks()
    {
        var decoder = Codec.CreateResponseDecoder(Ctx);
        var events = new List<UnifiedStreamEvent>();
        events.AddRange(decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":null}]}""")));
        events.AddRange(decoder.DecodeSse(new SseEvent(null,
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"reasoning_content":"late thought"},"finish_reason":null}]}""")));
        events.AddRange(decoder.Complete());

        Assert.Equal(
        [
            "Start(c1,m)",
            "Block(0,Text,,)",
            "Text(0,answer)",
            "Close(0)",
            "Block(1,Reasoning,,)",
            "Reason(1,late thought)",
            "Close(1)",
            "MsgStop(Other,)",
        ], Format(events));
    }

    [Fact]
    public void Non_Streaming_Body_Decodes_To_Full_Sequence()
    {
        var decoder = Codec.CreateResponseDecoder(JsonCtx());
        var body = (JsonObject)JsonNode.Parse(
            """
            {
              "id": "chatcmpl-json1",
              "object": "chat.completion",
              "created": 1759800000,
              "model": "gpt-4o",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "reasoning_content": "thinking about it",
                    "tool_calls": [
                      { "id": "call_j1", "type": "function", "function": { "name": "get_time", "arguments": "{\"tz\":\"UTC\"}" } },
                      { "id": "call_j2", "type": "function", "function": { "name": "noop" } }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 9, "completion_tokens": 17, "total_tokens": 26 },
              "service_tier": "default"
            }
            """)!;

        var events = decoder.DecodeJson(body).ToList();

        Assert.Equal(
        [
            "Start(chatcmpl-json1,gpt-4o)",
            "Block(0,Reasoning,,)",
            "Reason(0,thinking about it)",
            "Close(0)",
            "Block(1,ToolCall,call_j1,get_time)",
            "Args(1,{\"tz\":\"UTC\"})",
            "Close(1)",
            "Block(2,ToolCall,call_j2,noop)",
            "Args(2,{})", // empty arguments normalized to "{}"
            "Close(2)",
            "Usage(OpenAIChat,9,0,17,0,default,{\"prompt_tokens\":9,\"completion_tokens\":17,\"total_tokens\":26})",
            "MsgStop(ToolCalls,tool_calls)",
        ], Format(events));
    }

    [Theory]
    [InlineData("stop", "Stop")]
    [InlineData("length", "Length")]
    [InlineData("content_filter", "ContentFilter")]
    [InlineData("computer_call", "Other")]
    public void Finish_Reasons_Are_Mapped(string wire, string expected)
    {
        var decoder = Codec.CreateResponseDecoder(JsonCtx());
        var body = (JsonObject)JsonNode.Parse(
            $$$"""{"id":"c","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"{{{wire}}}"}]}""")!;
        var events = decoder.DecodeJson(body).ToList();
        Assert.Contains($"MsgStop({expected},{wire})", Format(events));
    }
}

public class ChatResponseEncoderTests
{
    private static readonly ChatCodec Codec = new();

    private static ResponseEncodeContext Ctx(bool stream, bool includeUsage = true) => new()
    {
        Stream = stream,
        Model = "gpt-5-mini",
        ResponseId = "01JTEST",
        Created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        IncludeUsage = includeUsage,
    };

    /// <summary>The sample IR event stream: reasoning → text → two tool calls → usage → stop.</summary>
    private static List<UnifiedStreamEvent> SampleEvents() =>
    [
        new MessageStartEvent("upstream-1", "deepseek-v4"),
        new BlockStartEvent(0, BlockKind.Reasoning),
        new ReasoningDeltaEvent(0, "think ", Origin: ApiProtocol.OpenAIChat),
        new ReasoningDeltaEvent(0, "hard"),
        new BlockStopEvent(0),
        new BlockStartEvent(1, BlockKind.Text),
        new TextDeltaEvent(1, "Hello"),
        new TextDeltaEvent(1, " world"),
        new BlockStopEvent(1),
        new BlockStartEvent(2, BlockKind.ToolCall, "call_a", "get_weather"),
        new ToolArgsDeltaEvent(2, "{\"city\":"),
        new ToolArgsDeltaEvent(2, "\"Paris\"}"),
        new BlockStopEvent(2),
        new BlockStartEvent(3, BlockKind.ToolCall, "call_b", "get_time"),
        new ToolArgsDeltaEvent(3, "{}"),
        new BlockStopEvent(3),
        new UsageEvent(ApiProtocol.OpenAIChat,
            (JsonObject)JsonNode.Parse("""{"prompt_tokens":12,"completion_tokens":30,"total_tokens":42}""")!,
            UsageNormalizer.Normalize(ApiProtocol.OpenAIChat, (JsonObject)JsonNode.Parse("""{"prompt_tokens":12,"completion_tokens":30,"total_tokens":42}""")!)!),
        new MessageStopEvent(FinishReason.ToolCalls, "tool_calls"),
    ];

    private static List<SseEvent> Stream(IReadOnlyList<UnifiedStreamEvent> events, ResponseEncodeContext ctx)
    {
        var encoder = Codec.CreateResponseEncoder(ctx);
        return events.SelectMany(e => encoder.OnEvent(e)).ToList();
    }

    [Fact]
    public void Streamed_Frames_Parse_Back_Into_Chat_Chunks()
    {
        var frames = Stream(SampleEvents(), Ctx(stream: true));
        var parsed = SseParser.ParseAll(string.Concat(frames.Select(SseWriter.Format)));

        Assert.Equal(13, parsed.Count); // start, reasoning x2, text x2, tool starts x2, args x3, finish, usage, [DONE]
        Assert.All(parsed, e => Assert.Null(e.Event));

        // Every chunk carries the same id / object / created / model; content chunks carry one choice at index 0.
        foreach (var e in parsed)
        {
            if (e.Data == "[DONE]") continue;
            var chunk = JsonNode.Parse(e.Data)!.AsObject();
            Assert.Equal("chatcmpl-01JTEST", chunk["id"]!.GetValue<string>());
            Assert.Equal("chat.completion.chunk", chunk["object"]!.GetValue<string>());
            Assert.Equal(1767225600, chunk["created"]!.GetValue<long>());
            Assert.Equal("gpt-5-mini", chunk["model"]!.GetValue<string>());
            var choices = (JsonArray)chunk["choices"]!;
            if (choices.Count > 0) Assert.Equal(0, choices[0]!["index"]!.GetValue<int>());
        }

        var data = parsed.Select(e => e.Data).ToList();
        Assert.Equal("[DONE]", data[^1]);

        var start = JsonNode.Parse(data[0])!;
        Assert.Equal("assistant", start["choices"]![0]!["delta"]!["role"]!.GetValue<string>());

        Assert.Equal("think ", JsonNode.Parse(data[1])!["choices"]![0]!["delta"]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal("hard", JsonNode.Parse(data[2])!["choices"]![0]!["delta"]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal("Hello", JsonNode.Parse(data[3])!["choices"]![0]!["delta"]!["content"]!.GetValue<string>());

        var toolStart = JsonNode.Parse(data[5])!["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal(0, toolStart["index"]!.GetValue<int>());
        Assert.Equal("call_a", toolStart["id"]!.GetValue<string>());
        Assert.Equal("function", toolStart["type"]!.GetValue<string>());
        Assert.Equal("get_weather", toolStart["function"]!["name"]!.GetValue<string>());
        Assert.Equal("\"Paris\"}", JsonNode.Parse(data[7])!["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());

        var finish = JsonNode.Parse(data[10])!;
        Assert.Equal("tool_calls", finish["choices"]![0]!["finish_reason"]!.GetValue<string>());

        var usageChunk = JsonNode.Parse(data[11])!;
        Assert.Empty((JsonArray)usageChunk["choices"]!);
        Assert.Equal(42, usageChunk["usage"]!["total_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Usage_Chunk_Is_Sent_Only_When_The_Client_Asked_For_It()
    {
        var withUsage = Stream(SampleEvents(), Ctx(stream: true, includeUsage: true));
        Assert.Equal("[DONE]", withUsage[^1].Data);
        Assert.Contains("\"usage\"", withUsage[^2].Data);

        var withoutUsage = Stream(SampleEvents(), Ctx(stream: true, includeUsage: false));
        Assert.Equal("[DONE]", withoutUsage[^1].Data);
        Assert.DoesNotContain(withoutUsage, f => f.Data.Contains("\"usage\""));
        Assert.Equal(12, withoutUsage.Count);
    }

    [Fact]
    public void Error_Event_Becomes_An_Error_Chunk_Then_Done()
    {
        var frames = Stream([new MessageStartEvent("u", "m"), new ErrorEvent(502, "api_error", "boom")], Ctx(stream: true));
        Assert.Equal(3, frames.Count);
        var error = JsonNode.Parse(frames[1].Data)!;
        Assert.Equal("boom", error["error"]!["message"]!.GetValue<string>());
        Assert.Equal("api_error", error["error"]!["type"]!.GetValue<string>());
        Assert.Null(error["error"]!["code"]);
        Assert.Equal("[DONE]", frames[^1].Data);
    }

    [Fact]
    public void BuildJson_Aggregates_The_Sample_Stream()
    {
        var encoder = Codec.CreateResponseEncoder(Ctx(stream: false));
        foreach (var e in SampleEvents()) encoder.OnEvent(e);
        var completion = encoder.BuildJson();

        Assert.Equal("chat.completion", completion["object"]!.GetValue<string>());
        Assert.Equal("chatcmpl-01JTEST", completion["id"]!.GetValue<string>());
        Assert.Equal(1767225600, completion["created"]!.GetValue<long>());
        Assert.Equal("gpt-5-mini", completion["model"]!.GetValue<string>());

        var choice = completion["choices"]![0]!;
        var message = choice["message"]!;
        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.Equal("Hello world", message["content"]!.GetValue<string>());
        Assert.Equal("think hard", message["reasoning_content"]!.GetValue<string>());
        var calls = (JsonArray)message["tool_calls"]!;
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_a", calls[0]!["id"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Paris\"}", calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("get_time", calls[1]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());
        // Usage is synthesized into the OpenAI shape (prompt includes cached; total = prompt + completion).
        var usage = completion["usage"]!;
        Assert.Equal(12L, usage["prompt_tokens"]!.GetValue<long>());
        Assert.Equal(30L, usage["completion_tokens"]!.GetValue<long>());
        Assert.Equal(42L, usage["total_tokens"]!.GetValue<long>());
        Assert.Equal(0L, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
        Assert.Equal(0L, usage["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void BuildJson_Without_Text_Has_Null_Content()
    {
        var encoder = Codec.CreateResponseEncoder(Ctx(stream: false));
        encoder.OnEvent(new MessageStartEvent("u", "m"));
        encoder.OnEvent(new BlockStartEvent(0, BlockKind.ToolCall, "call_x", "f"));
        encoder.OnEvent(new ToolArgsDeltaEvent(0, ""));
        encoder.OnEvent(new BlockStopEvent(0));
        encoder.OnEvent(new MessageStopEvent(FinishReason.ToolCalls));
        var completion = encoder.BuildJson();

        var message = completion["choices"]![0]!["message"]!.AsObject();
        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.Null(message["content"]);
        Assert.False(message.ContainsKey("reasoning_content"));
        Assert.Equal("f", message["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{}", message["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.False(completion.ContainsKey("usage"));
    }

    [Theory]
    [InlineData(FinishReason.Stop, "stop")]
    [InlineData(FinishReason.Length, "length")]
    [InlineData(FinishReason.ToolCalls, "tool_calls")]
    [InlineData(FinishReason.ContentFilter, "content_filter")]
    [InlineData(FinishReason.Other, "stop")]
    public void Finish_Reasons_Are_Encoded(FinishReason reason, string wire)
    {
        var frames = Stream([new MessageStopEvent(reason)], Ctx(stream: true));
        var chunk = JsonNode.Parse(frames[0].Data)!;
        Assert.Equal(wire, chunk["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal("[DONE]", frames[^1].Data);
    }

    [Fact]
    public void Usage_From_A_Foreign_Upstream_Is_Synthesized_Into_The_OpenAI_Shape()
    {
        // The encoder only runs cross-protocol: an Anthropic raw usage must not leak its field names through.
        var raw = (JsonObject)JsonNode.Parse("""{"input_tokens":20,"cache_read_input_tokens":100,"output_tokens":7}""")!;
        var frames = Stream(
        [
            new MessageStartEvent("u", "m"),
            new UsageEvent(ApiProtocol.Anthropic, raw, UsageNormalizer.Normalize(ApiProtocol.Anthropic, raw)!),
            new MessageStopEvent(FinishReason.Stop, "end_turn"),
        ], Ctx(stream: true));

        var usageChunk = JsonNode.Parse(frames[^2].Data)!;
        Assert.Empty((JsonArray)usageChunk["choices"]!);
        var usage = usageChunk["usage"]!;
        Assert.Null(usage["input_tokens"]); // foreign shape gone
        Assert.Equal(120L, usage["prompt_tokens"]!.GetValue<long>()); // input + cache_read
        Assert.Equal(7L, usage["completion_tokens"]!.GetValue<long>());
        Assert.Equal(127L, usage["total_tokens"]!.GetValue<long>());
        Assert.Equal(100L, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
        Assert.Equal(0L, usage["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void Empty_Streaming_Tool_Args_Are_Closed_With_A_Json_Object()
    {
        var frames = Stream(
        [
            new MessageStartEvent("u", "m"),
            new BlockStartEvent(0, BlockKind.ToolCall, "call_x", "noop"),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.ToolCalls, "tool_calls"),
        ], Ctx(stream: true));

        var withArgs = frames.Where(f => f.Data != "[DONE]")
            .Select(f => JsonNode.Parse(f.Data)!["choices"]![0]!["delta"]!["tool_calls"])
            .Where(n => n is JsonArray { Count: > 0 })
            .Cast<JsonArray>()
            .Select(a => a[0]!)
            .ToList();
        Assert.Equal(2, withArgs.Count); // the start frame ("") and the closing delta ("{}")
        Assert.Equal("", withArgs[0]["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("{}", withArgs[1]["function"]!["arguments"]!.GetValue<string>());
    }
}

public class ChatErrorTests
{
    private static readonly ChatCodec Codec = new();

    [Fact]
    public void EncodeError_Uses_The_OpenAI_Shape()
    {
        var body = Codec.EncodeError(400, "invalid_request_error", "bad role");
        var error = body["error"]!;
        Assert.Equal("bad role", error["message"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Null(error["param"]);
        Assert.Null(error["code"]);
    }

    [Fact]
    public void DecodeError_Parses_Message_And_Type()
    {
        var (type, message) = Codec.DecodeError(401,
            """{"error":{"message":"Incorrect API key","type":"invalid_request_error","code":"invalid_api_key"}}""");
        Assert.Equal("invalid_request_error", type);
        Assert.Equal("Incorrect API key", message);
    }

    [Fact]
    public void DecodeError_Falls_Back_To_Code_Then_Status()
    {
        var (type, message) = Codec.DecodeError(429, """{"error":{"message":"quota exceeded","code":"insufficient_quota"}}""");
        Assert.Equal("insufficient_quota", type);
        Assert.Equal("quota exceeded", message);

        var (plainType, plainMessage) = Codec.DecodeError(502, "bad gateway");
        Assert.Equal("api_error", plainType);
        Assert.Equal("bad gateway", plainMessage);
    }

    [Fact]
    public void DecodeError_Truncates_And_Handles_An_Empty_Body()
    {
        var longBody = new string('x', 900);
        var (type, message) = Codec.DecodeError(400, longBody);
        Assert.Equal("invalid_request_error", type);
        Assert.Equal(500, message.Length);

        var (_, emptyMessage) = Codec.DecodeError(502, "");
        Assert.Equal("HTTP 502", emptyMessage);
    }
}
