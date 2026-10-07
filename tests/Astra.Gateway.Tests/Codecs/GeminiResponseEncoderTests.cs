using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

public class GeminiResponseEncoderTests
{
    private static readonly ResponseEncodeContext Ctx = new() { Stream = true, Model = "gemini-2.5-flash", ResponseId = "rid-1" };

    // Parsed from JSON text (like the wire) — UsageNormalizer reads number nodes, not typed ints.
    private static readonly JsonObject GeminiUsageRaw = (JsonObject)JsonNode.Parse(
        """{"promptTokenCount":120,"cachedContentTokenCount":40,"candidatesTokenCount":26,"thoughtsTokenCount":11,"totalTokenCount":157}""")!;

    private static (List<SseEvent> Frames, JsonObject Json) Run(params UnifiedStreamEvent[] events)
    {
        var encoder = new GeminiResponseEncoder(Ctx);
        var frames = new List<SseEvent>();
        foreach (var e in events) frames.AddRange(encoder.OnEvent(e));
        return (frames, encoder.BuildJson());
    }

    private static JsonObject Chunk(SseEvent frame) => (JsonObject)JsonNode.Parse(frame.Data)!;

    private static JsonArray Parts(SseEvent frame) => (JsonArray)((JsonObject)((JsonArray)Chunk(frame)["candidates"]!)[0]!)["content"]!["parts"]!;

    private static string Serialize(JsonNode? node) => node!.ToJsonString(GatewayJson.Options);

    [Fact]
    public void Signature_Is_Attached_To_The_Next_Emitted_Part()
    {
        var (frames, json) = Run(
            new MessageStartEvent("resp-1", "m"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, null, "sig-1", Origin: ApiProtocol.Gemini),
            new ReasoningDeltaEvent(0, "Checking.", Origin: ApiProtocol.Gemini),
            new BlockStopEvent(0),
            new BlockStartEvent(1, BlockKind.Text),
            new TextDeltaEvent(1, "Hello"),
            new BlockStopEvent(1),
            new MessageStopEvent(FinishReason.Stop, "STOP"));

        Assert.Equal(3, frames.Count); // reasoning chunk, text chunk, final chunk — MessageStart emits nothing

        var thoughtPart = Parts(frames[0])[0]!;
        Assert.Equal("Checking.", thoughtPart["text"]!.GetValue<string>());
        Assert.True(thoughtPart["thought"]!.GetValue<bool>());
        Assert.Equal("sig-1", thoughtPart["thoughtSignature"]!.GetValue<string>());

        Assert.Equal("Hello", Parts(frames[1])[0]!["text"]!.GetValue<string>());

        // Final chunk: finishReason, modelVersion, responseId; no usage was reported.
        var final = Chunk(frames[2]);
        var candidate = (JsonObject)((JsonArray)final["candidates"]!)[0]!;
        Assert.Equal("STOP", candidate["finishReason"]!.GetValue<string>());
        Assert.Equal(0, candidate["index"]!.GetValue<int>());
        Assert.Equal("gemini-2.5-flash", final["modelVersion"]!.GetValue<string>());
        Assert.Equal("rid-1", final["responseId"]!.GetValue<string>());
        Assert.False(final.ContainsKey("usageMetadata"));

        // Aggregated body keeps both parts (the signed thought part is not merged into plain text).
        var parts = (JsonArray)((JsonArray)json["candidates"]!)[0]!["content"]!["parts"]!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(3, ((JsonObject)parts[0]!).Count);
    }

    [Fact]
    public void Signature_With_Nothing_After_Is_Flushed_At_Block_Stop()
    {
        var (frames, json) = Run(
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, "Hmm.", Origin: ApiProtocol.Gemini),
            new ReasoningDeltaEvent(0, null, "sig-2", Origin: ApiProtocol.Gemini),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Stop, "STOP"));

        Assert.Equal(3, frames.Count); // "Hmm." chunk, flushed signature chunk, final chunk
        var flushed = Parts(frames[1])[0]!;
        Assert.Equal("", flushed["text"]!.GetValue<string>());
        Assert.Equal("sig-2", flushed["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("""{"text":"","thoughtSignature":"sig-2"}""", Serialize(flushed));

        var parts = (JsonArray)((JsonArray)json["candidates"]!)[0]!["content"]!["parts"]!;
        Assert.Equal(2, parts.Count); // "Hmm." thought part + the flushed signature part
    }

    [Fact]
    public void Tool_Call_Args_Are_Buffered_Until_Block_Stop()
    {
        var usage = UsageNormalizer.Normalize(ApiProtocol.Gemini, GeminiUsageRaw.DeepClone())!;
        var (frames, json) = Run(
            new BlockStartEvent(0, BlockKind.ToolCall, "call_1", "get_weather"),
            new ToolArgsDeltaEvent(0, "{\"city\":"),
            new ToolArgsDeltaEvent(0, "\"Tokyo\"}"),
            new BlockStopEvent(0),
            new UsageEvent(ApiProtocol.Gemini, (JsonObject)GeminiUsageRaw.DeepClone(), usage),
            new MessageStopEvent(FinishReason.ToolCalls, "STOP"));

        Assert.Equal(2, frames.Count); // no frames for Start / ArgsDelta
        var call = Parts(frames[0])[0]!["functionCall"]!;
        Assert.Equal("get_weather", call["name"]!.GetValue<string>());
        Assert.Equal("Tokyo", call["args"]!["city"]!.GetValue<string>());
        Assert.Equal("call_1", call["id"]!.GetValue<string>());

        // Final chunk carries the finish reason and the usage in the Gemini client shape.
        var final = Chunk(frames[1]);
        var candidate = (JsonObject)((JsonArray)final["candidates"]!)[0]!;
        Assert.Equal("STOP", candidate["finishReason"]!.GetValue<string>());
        var usageJson = (JsonObject)final["usageMetadata"]!;
        Assert.Equal(120, usageJson["promptTokenCount"]!.GetValue<int>());   // 80 input + 40 cache_read
        Assert.Equal(40, usageJson["cachedContentTokenCount"]!.GetValue<int>());
        Assert.Equal(26, usageJson["candidatesTokenCount"]!.GetValue<int>());
        Assert.Equal(11, usageJson["thoughtsTokenCount"]!.GetValue<int>());
        Assert.Equal(157, usageJson["totalTokenCount"]!.GetValue<int>());

        var jsonCall = ((JsonArray)((JsonArray)json["candidates"]!)[0]!["content"]!["parts"]!)[0]!["functionCall"]!;
        Assert.Equal("""{"city":"Tokyo"}""", Serialize(jsonCall["args"]));
        Assert.Equal("STOP", ((JsonObject)((JsonArray)json["candidates"]!)[0]!)["finishReason"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_Tool_Args_Become_An_Empty_Object()
    {
        var (frames, _) = Run(
            new BlockStartEvent(0, BlockKind.ToolCall, "call_1", "f"),
            new ToolArgsDeltaEvent(0, "{broken"),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Stop, "STOP"));

        Assert.Equal("{}", Serialize(Parts(frames[0])[0]!["functionCall"]!["args"]));
    }

    [Fact]
    public void Finish_Reasons_Map_To_Gemini_Stop_Reasons()
    {
        (FinishReason, string)[] cases =
        [
            (FinishReason.Length, "MAX_TOKENS"),
            (FinishReason.ToolCalls, "STOP"),
            (FinishReason.ContentFilter, "SAFETY"),
            (FinishReason.Other, "OTHER"),
        ];
        foreach (var (reason, expected) in cases)
        {
            var (frames, json) = Run(new MessageStopEvent(reason, expected));
            Assert.Equal(expected, ((JsonObject)((JsonArray)Chunk(frames[^1])["candidates"]!)[0]!)["finishReason"]!.GetValue<string>());
            Assert.Equal(expected, ((JsonObject)((JsonArray)json["candidates"]!)[0]!)["finishReason"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Error_Event_Becomes_A_Gemini_Error_Frame()
    {
        var (frames, json) = Run(new ErrorEvent(429, "RESOURCE_EXHAUSTED", "quota exceeded"));

        Assert.Single(frames);
        var error = (JsonObject)Chunk(frames[0])["error"]!;
        Assert.Equal(429, error["code"]!.GetValue<int>());
        Assert.Equal("quota exceeded", error["message"]!.GetValue<string>());
        Assert.Equal("RESOURCE_EXHAUSTED", error["status"]!.GetValue<string>());
        // The aggregated body is independent of the error frame.
        Assert.True(json.ContainsKey("candidates"));
    }

    [Fact]
    public void BuildJson_Merges_Consecutive_Text_And_Keeps_Thought_Parts()
    {
        var (_, json) = Run(
            new TextDeltaEvent(0, "Hello "),
            new TextDeltaEvent(0, "world"),
            new ReasoningDeltaEvent(0, "thinking", Origin: ApiProtocol.Gemini),
            new TextDeltaEvent(1, "!"),
            new MessageStopEvent(FinishReason.Stop, "STOP"));

        var parts = (JsonArray)((JsonArray)json["candidates"]!)[0]!["content"]!["parts"]!;
        Assert.Equal(3, parts.Count);
        Assert.Equal("Hello world", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("thinking", parts[1]!["text"]!.GetValue<string>());
        Assert.True(parts[1]!["thought"]!.GetValue<bool>());
        Assert.Equal("!", parts[2]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Foreign_Reasoning_Text_Is_Kept_But_Signature_Dropped()
    {
        var (frames, json) = Run(
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, "from anthropic", Signature: "sig-x", Origin: ApiProtocol.Anthropic),
            new BlockStopEvent(0),
            new MessageStopEvent(FinishReason.Stop, "STOP"));

        Assert.Equal(2, frames.Count);
        var part = Parts(frames[0])[0]!;
        Assert.Equal("from anthropic", part["text"]!.GetValue<string>());
        Assert.True(part["thought"]!.GetValue<bool>());
        Assert.Null(part["thoughtSignature"]); // signature only round-trips for its own protocol
        Assert.Single((JsonArray)((JsonArray)json["candidates"]!)[0]!["content"]!["parts"]!);
    }
}
