using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Tests;

/// <summary>
/// Placeholders put back into streamed responses — text and tool-call arguments, all four wire protocols — with the
/// placeholder cut anywhere (golden SSE streams in Fixtures/privacy, and every chunk size).
/// </summary>
public class PrivacyStreamRestorerTests
{
    // A value that is hostile to JSON: line breaks, a quote and a backslash.
    private const string Key = "-----BEGIN KEY-----\nab\"c\\d\n-----END KEY-----";

    private static readonly Dictionary<string, string> Map = new()
    {
        ["{{EMAIL_k3v9x2mq}}"] = "alice@acme.io",
        ["{{PRIVATE_KEY_aaaaaaaa}}"] = Key,
    };

    private static List<SseEvent> Parse(string fixture)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "privacy", fixture));
        var parser = new SseParser();
        var events = parser.Feed(bytes).ToList();
        events.AddRange(parser.Flush());
        return events;
    }

    private static List<SseEvent> Restore(IEnumerable<SseEvent> frames)
    {
        var restorer = new PrivacyStreamRestorer(Map);
        var output = new List<SseEvent>();
        foreach (var frame in frames) output.AddRange(restorer.Process(frame));
        output.AddRange(restorer.Flush());
        return output;
    }

    private static string Join(IEnumerable<string?> parts) => string.Concat(parts.Where(p => p is not null));

    private static void AssertArgs(string args)
    {
        // The arguments the client assembles from the fragments are one valid JSON document holding the originals.
        var parsed = JsonNode.Parse(args)!;
        Assert.Equal("alice@acme.io", parsed["to"]!.GetValue<string>());
        Assert.Equal(Key, parsed["key"]!.GetValue<string>());
    }

    private static JsonNode? Data(SseEvent e) => e.Data.StartsWith('{') ? JsonNode.Parse(e.Data) : null;

    // --- golden streams -----------------------------------------------------------------------------------------------

    [Fact]
    public void Chat_Text_And_Tool_Arguments_Are_Restored_Across_Frames()
    {
        var output = Restore(Parse("chat-tool-args-split.sse"));
        var deltas = output.Select(Data).Where(d => d is not null).Select(d => d!["choices"]![0]!["delta"]!).ToList();

        Assert.Equal("Sending to alice@acme.io now.", Join(deltas.Select(d => d["content"]?.GetValue<string>())));
        AssertArgs(Join(deltas.SelectMany(d => d["tool_calls"]?.AsArray() ?? []).Select(c => c!["function"]!["arguments"]!.GetValue<string>())));
        Assert.DoesNotContain(output, e => e.Data.Contains("{{"));
        Assert.Equal("[DONE]", output[^1].Data); // the end marker stays last
    }

    [Fact]
    public void Anthropic_Text_And_Input_Json_Deltas_Are_Restored_Across_Frames()
    {
        var output = Restore(Parse("anthropic-tool-args-split.sse"));
        var deltas = output.Select(Data).Where(d => d?["type"]?.GetValue<string>() == "content_block_delta").Select(d => d!).ToList();

        Assert.Equal("Sending to alice@acme.io now.",
            Join(deltas.Where(d => d["index"]!.GetValue<int>() == 0).Select(d => d["delta"]!["text"]?.GetValue<string>())));
        AssertArgs(Join(deltas.Where(d => d["index"]!.GetValue<int>() == 1).Select(d => d["delta"]!["partial_json"]?.GetValue<string>())));
        Assert.DoesNotContain(output, e => e.Data.Contains("{{"));
        // event names and order survive
        Assert.Equal("message_start", output[0].Event);
        Assert.Equal("message_stop", output[^1].Event);
    }

    [Fact]
    public void Responses_Deltas_Done_And_Completed_Frames_Are_All_Restored()
    {
        var output = Restore(Parse("responses-tool-args-split.sse"));
        var frames = output.Select(Data).Where(d => d is not null).Select(d => d!).ToList();

        Assert.Equal("Sending to alice@acme.io now.",
            Join(frames.Where(f => f["type"]!.GetValue<string>() == "response.output_text.delta").Select(f => f["delta"]!.GetValue<string>())));
        AssertArgs(Join(frames.Where(f => f["type"]!.GetValue<string>() == "response.function_call_arguments.delta").Select(f => f["delta"]!.GetValue<string>())));
        // the whole-arguments frames carry the arguments as a JSON text (escaped for it), valid and restored
        AssertArgs(frames.Single(f => f["type"]!.GetValue<string>() == "response.function_call_arguments.done")["arguments"]!.GetValue<string>());
        AssertArgs(frames.Single(f => f["type"]!.GetValue<string>() == "response.output_item.done")["item"]!["arguments"]!.GetValue<string>());
        AssertArgs(frames.Single(f => f["type"]!.GetValue<string>() == "response.completed")["response"]!["output"]![0]!["arguments"]!.GetValue<string>());
        Assert.DoesNotContain(output, e => e.Data.Contains("{{"));
    }

    [Fact]
    public void Gemini_Text_Is_Restored_Across_Frames_And_Function_Call_Args_Whole()
    {
        var output = Restore(Parse("gemini-tool-args-split.sse"));
        var frames = output.Select(Data).Select(d => d!).ToList();

        Assert.Equal("Sending to alice@acme.io now.",
            Join(frames.SelectMany(f => f["candidates"]![0]!["content"]!["parts"]!.AsArray()).Select(p => p!["text"]?.GetValue<string>())));
        var args = frames.SelectMany(f => f["candidates"]![0]!["content"]!["parts"]!.AsArray()).Select(p => p!["functionCall"]?["args"]).Single(a => a is not null)!;
        Assert.Equal("alice@acme.io", args["to"]!.GetValue<string>());
        Assert.Equal(Key, args["key"]!.GetValue<string>());
        Assert.Equal("STOP", frames[^1]["candidates"]![0]!["finishReason"]!.GetValue<string>());
    }

    // --- placeholder cut anywhere ---------------------------------------------------------------------------------------

    private const string RawArgs = "{\"to\":\"{{EMAIL_k3v9x2mq}}\",\"key\":\"{{PRIVATE_KEY_aaaaaaaa}}\"}";
    private const string RawText = "mail {{EMAIL_k3v9x2mq}}, key {{PRIVATE_KEY_aaaaaaaa}}! and a lone { brace";

    private static IEnumerable<string> Chunks(string s, int size) => Enumerable.Range(0, (s.Length + size - 1) / size).Select(i => s.Substring(i * size, Math.Min(size, s.Length - i * size)));

    private static SseEvent Json(JsonNode node, string? name = null) => new(name, node.ToJsonString());

    private static SseEvent ChatDelta(JsonObject delta) =>
        Json(new JsonObject { ["id"] = "c", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }) });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void Chat_Placeholders_Cut_Anywhere_Are_Restored(int size)
    {
        var frames = new List<SseEvent>();
        foreach (var piece in Chunks(RawText, size))
            frames.Add(ChatDelta(new JsonObject { ["content"] = piece }));
        foreach (var piece in Chunks(RawArgs, size))
            frames.Add(ChatDelta(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = piece } }),
            }));
        frames.Add(Json(JsonNode.Parse("""{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""")!));

        var output = Restore(frames).Select(Data).Select(d => d!["choices"]![0]!["delta"]!).ToList();
        Assert.Equal($"mail alice@acme.io, key {Key}! and a lone {{ brace", Join(output.Select(d => d["content"]?.GetValue<string>())));
        AssertArgs(Join(output.SelectMany(d => d["tool_calls"]?.AsArray() ?? []).Select(c => c!["function"]!["arguments"]!.GetValue<string>())));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(13)]
    public void Anthropic_Placeholders_Cut_Anywhere_Are_Restored(int size)
    {
        var frames = new List<SseEvent>();
        foreach (var piece in Chunks(RawArgs, size))
            frames.Add(Json(new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 1,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = piece },
            }, "content_block_delta"));
        frames.Add(Json(JsonNode.Parse("""{"type":"content_block_stop","index":1}""")!, "content_block_stop"));

        var output = Restore(frames);
        AssertArgs(Join(output.Select(Data).Where(d => d!["type"]!.GetValue<string>() == "content_block_delta").Select(d => d!["delta"]!["partial_json"]!.GetValue<string>())));
        Assert.Equal("content_block_stop", output[^1].Event);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void Responses_Placeholders_Cut_Anywhere_Are_Restored(int size)
    {
        var frames = Chunks(RawArgs, size).Select(piece => Json(new JsonObject
        {
            ["type"] = "response.function_call_arguments.delta", ["item_id"] = "fc_1", ["output_index"] = 0, ["delta"] = piece,
        })).ToList();
        AssertArgs(Join(Restore(frames).Select(Data).Select(d => d!["delta"]?.GetValue<string>())));
    }

    [Fact]
    public void A_Tail_That_Only_Looks_Like_A_Placeholder_Is_Released_Before_The_Next_Other_Frame_And_At_The_End()
    {
        // The text ends in "{{EM", which could still become a placeholder: held, then released ahead of the finish frame.
        var frames = new[]
        {
            Json(JsonNode.Parse("""{"id":"c","choices":[{"index":0,"delta":{"content":"fn x() {{EM"}}]}""")!),
            Json(JsonNode.Parse("""{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"total_tokens":3}}""")!),
        };
        var output = Restore(frames);
        var contents = output.Select(Data).Select(d => d!["choices"]![0]!["delta"]!["content"]?.GetValue<string>()).ToList();
        Assert.Equal("fn x() {{EM", Join(contents));
        // the leftover came out in a frame of its own before the finish, which kept its finish reason and usage
        Assert.Equal(3, output.Count);
        Assert.Null(Data(output[1])!["choices"]![0]!["finish_reason"]?.GetValue<string>());
        Assert.Null(Data(output[1])!["usage"]);
        Assert.Equal("stop", Data(output[2])!["choices"]![0]!["finish_reason"]!.GetValue<string>());

        // the stream ending right after it releases it too
        var ended = Restore([frames[0]]);
        Assert.Equal("fn x() {{EM", Join(ended.Select(Data).Select(d => d!["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
    }

    // --- the rest of the contract ---------------------------------------------------------------------------------------

    [Fact]
    public void Frames_Without_A_Placeholder_Are_Passed_On_Untouched()
    {
        var restorer = new PrivacyStreamRestorer(Map);
        var frame = Json(JsonNode.Parse("""{"id":"c","choices":[{"index":0,"delta":{"content":"plain text, nothing masked"}}]}""")!);
        Assert.Same(frame, Assert.Single(restorer.Process(frame)));
        var done = new SseEvent(null, "[DONE]");
        Assert.Same(done, Assert.Single(restorer.Process(done)));
    }

    [Fact]
    public void An_Empty_Map_Is_A_PassThrough()
    {
        var restorer = new PrivacyStreamRestorer(new Dictionary<string, string>());
        var frame = new SseEvent(null, """{"x":"{{EMAIL_k3v9x2mq}}"}""");
        Assert.Same(frame, Assert.Single(restorer.Process(frame)));
        Assert.Empty(restorer.Flush());
    }

    [Fact]
    public void Non_Streaming_Bodies_Are_Restored_As_Json_Too()
    {
        const string body = """
            {"choices":[{"message":{"content":"mail {{EMAIL_k3v9x2mq}}","tool_calls":[{"function":{"name":"f","arguments":"{\"to\":\"{{EMAIL_k3v9x2mq}}\",\"key\":\"{{PRIVATE_KEY_aaaaaaaa}}\"}"}}]}}]}
            """;
        var restored = JsonNode.Parse(PrivacyGuardService.RestoreJson(body, Map))!;
        var message = restored["choices"]![0]!["message"]!;
        Assert.Equal("mail alice@acme.io", message["content"]!.GetValue<string>());
        AssertArgs(message["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());

        // no placeholder: the very same string; not JSON: plain replace
        const string clean = """{"a":"nothing"}""";
        Assert.Same(clean, PrivacyGuardService.RestoreJson(clean, Map));
        Assert.Equal("hi alice@acme.io", PrivacyGuardService.RestoreJson("hi {{EMAIL_k3v9x2mq}}", Map));
    }
}
