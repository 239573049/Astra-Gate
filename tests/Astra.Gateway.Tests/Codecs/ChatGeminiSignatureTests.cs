using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>
/// Gemini 3 refuses a turn whose replayed function call has lost its <c>thoughtSignature</c>. A Chat client reaches
/// Gemini translated, so the signature has to cross it: out on the tool call as
/// <c>extra_content.google.thought_signature</c> (the shape Google's OpenAI-compatible endpoint uses), and back from there.
/// </summary>
public class ChatGeminiSignatureTests
{
    private static readonly ChatCodec Chat = new();
    private static readonly GeminiCodec Gemini = new();

    private static ResponseEncodeContext Ctx(bool stream) => new()
    {
        Stream = stream,
        Model = "gemini-3-pro",
        ResponseId = "01JTEST",
        Created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static UnifiedRequest DecodeChat(string json) =>
        Chat.DecodeRequest((JsonObject)JsonNode.Parse(json)!, new RequestDecodeContext());

    private static JsonObject ToGemini(UnifiedRequest ir) =>
        Gemini.EncodeRequest(ir, new RequestEncodeContext { UpstreamModel = "gemini-3-pro" });

    /// <summary>What GeminiResponseDecoder emits for a function call part that carries a signature: the signature first.</summary>
    private static List<UnifiedStreamEvent> SignedCallEvents(params (string Id, string Name, string? Signature)[] calls)
    {
        var events = new List<UnifiedStreamEvent> { new MessageStartEvent("resp-1", "gemini-3-pro") };
        var block = 0;
        foreach (var (id, name, signature) in calls)
        {
            if (signature is not null)
            {
                events.Add(new BlockStartEvent(block, BlockKind.Reasoning));
                events.Add(new ReasoningDeltaEvent(block, null, signature, Origin: ApiProtocol.Gemini));
                events.Add(new BlockStopEvent(block++));
            }
            events.Add(new BlockStartEvent(block, BlockKind.ToolCall, id, name));
            events.Add(new ToolArgsDeltaEvent(block, "{\"city\":\"Tokyo\"}"));
            events.Add(new BlockStopEvent(block++));
        }
        events.Add(new MessageStopEvent(FinishReason.ToolCalls, "STOP"));
        return events;
    }

    private static List<SseEvent> Stream(IEnumerable<UnifiedStreamEvent> events)
    {
        var encoder = Chat.CreateResponseEncoder(Ctx(stream: true));
        return events.SelectMany(e => encoder.OnEvent(e)).ToList();
    }

    private static JsonObject Aggregate(IEnumerable<UnifiedStreamEvent> events)
    {
        var encoder = Chat.CreateResponseEncoder(Ctx(stream: false));
        foreach (var e in events) encoder.OnEvent(e);
        return encoder.BuildJson();
    }

    private static string? SignatureOf(JsonNode? toolCall) =>
        toolCall?["extra_content"]?["google"]?["thought_signature"]?.GetValue<string>();

    // ---------------------------------------------------------------- request: Chat client → IR → Gemini

    [Fact]
    public void Echoed_Signature_Stands_Before_Its_Call_In_The_Ir()
    {
        var ir = DecodeChat("""
            {"model":"gemini-3-pro","messages":[
              {"role":"user","content":"Weather?"},
              {"role":"assistant","content":null,"tool_calls":[
                {"id":"call_1","type":"function","extra_content":{"google":{"thought_signature":"sig-A"}},"function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}},
                {"id":"call_2","type":"function","function":{"name":"get_time","arguments":"{}"}}]}]}
            """);

        var parts = ir.Messages[1].Parts;
        Assert.Collection(parts,
            p =>
            {
                var reasoning = Assert.IsType<ReasoningPart>(p);
                Assert.Equal("sig-A", reasoning.Signature);
                Assert.Equal(ApiProtocol.Gemini, reasoning.Origin);
                Assert.Null(reasoning.Text);
            },
            p => Assert.Equal("call_1", Assert.IsType<ToolCallPart>(p).Id),
            p => Assert.Equal("call_2", Assert.IsType<ToolCallPart>(p).Id));
    }

    [Fact]
    public void A_Chat_History_Without_Signatures_Decodes_As_Before()
    {
        var ir = DecodeChat("""
            {"model":"gemini-3-pro","messages":[
              {"role":"assistant","tool_calls":[
                {"id":"call_1","type":"function","extra_content":{"google":{"other":"x"}},"function":{"name":"t","arguments":"{}"}}]}]}
            """);

        Assert.All(ir.Messages[0].Parts, p => Assert.IsType<ToolCallPart>(p));
    }

    [Fact]
    public void Signature_Lands_On_The_Function_Call_Part_Of_The_Gemini_Request()
    {
        var body = ToGemini(DecodeChat("""
            {"model":"gemini-3-pro","messages":[
              {"role":"user","content":"Weather?"},
              {"role":"assistant","content":null,"tool_calls":[
                {"id":"call_1","type":"function","extra_content":{"google":{"thought_signature":"sig-A"}},"function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}},
                {"id":"call_2","type":"function","function":{"name":"get_time","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"22C"},
              {"role":"tool","tool_call_id":"call_2","content":"noon"}]}
            """));

        var modelParts = (JsonArray)((JsonObject)((JsonArray)body["contents"]!)[1]!)["parts"]!;
        Assert.Equal(2, modelParts.Count);
        Assert.Equal("get_weather", modelParts[0]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("sig-A", modelParts[0]!["thoughtSignature"]!.GetValue<string>());
        // Gemini signs the first call of a parallel batch only; the second part stays as the client sent it.
        Assert.Null(modelParts[1]!["thoughtSignature"]);
    }

    // ---------------------------------------------------------------- response: Gemini → Chat client

    [Fact]
    public void Streamed_Tool_Call_Carries_The_Signature_On_Its_First_Chunk()
    {
        var frames = Stream(SignedCallEvents(("call_1", "get_weather", "sig-A"), ("call_2", "get_time", null)));
        var calls = frames.Where(f => f.Data != "[DONE]")
            .Select(f => JsonNode.Parse(f.Data)!["choices"]![0]!["delta"]!["tool_calls"])
            .Where(n => n is JsonArray { Count: > 0 })
            .Select(n => n![0]!)
            .ToList();

        var first = calls.Single(c => c["id"]?.GetValue<string>() == "call_1");
        Assert.Equal("sig-A", SignatureOf(first));
        Assert.Equal(0, first["index"]!.GetValue<int>());
        var second = calls.Single(c => c["id"]?.GetValue<string>() == "call_2");
        Assert.Null(second["extra_content"]);
        // Argument fragments carry no signature: it is sent once, with the call's first chunk.
        Assert.All(calls.Where(c => c["id"] is null), c => Assert.Null(c["extra_content"]));
    }

    [Fact]
    public void Aggregated_Tool_Call_Carries_The_Signature()
    {
        var message = Aggregate(SignedCallEvents(("call_1", "get_weather", "sig-A"), ("call_2", "get_time", null)))
            ["choices"]![0]!["message"]!;
        var calls = message["tool_calls"]!.AsArray();

        Assert.Equal("sig-A", SignatureOf(calls[0]));
        Assert.Null(calls[1]!["extra_content"]);
    }

    [Fact]
    public void A_Response_Without_Signatures_Is_Unchanged()
    {
        var message = Aggregate(SignedCallEvents(("call_1", "get_weather", null)))["choices"]![0]!["message"]!;

        Assert.Null(message["tool_calls"]![0]!["extra_content"]);
    }

    [Fact]
    public void A_Signature_Before_Text_Does_Not_Move_To_A_Later_Call()
    {
        var message = Aggregate(
        [
            new MessageStartEvent("resp-1", "gemini-3-pro"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, null, "sig-text", Origin: ApiProtocol.Gemini),
            new BlockStopEvent(0),
            new BlockStartEvent(1, BlockKind.Text),
            new TextDeltaEvent(1, "Checking."),
            new BlockStopEvent(1),
            new BlockStartEvent(2, BlockKind.ToolCall, "call_1", "get_weather"),
            new ToolArgsDeltaEvent(2, "{}"),
            new BlockStopEvent(2),
            new MessageStopEvent(FinishReason.ToolCalls, "STOP"),
        ])["choices"]![0]!["message"]!;

        Assert.Null(message["tool_calls"]![0]!["extra_content"]);
    }

    [Fact]
    public void Another_Protocols_Signature_Is_Not_Sent_To_A_Chat_Client()
    {
        var message = Aggregate(
        [
            new MessageStartEvent("resp-1", "claude"),
            new BlockStartEvent(0, BlockKind.Reasoning),
            new ReasoningDeltaEvent(0, null, "anthropic-sig", Origin: ApiProtocol.Anthropic),
            new BlockStopEvent(0),
            new BlockStartEvent(1, BlockKind.ToolCall, "call_1", "get_weather"),
            new ToolArgsDeltaEvent(1, "{}"),
            new BlockStopEvent(1),
            new MessageStopEvent(FinishReason.ToolCalls, "tool_use"),
        ])["choices"]![0]!["message"]!;

        Assert.Null(message["tool_calls"]![0]!["extra_content"]);
    }

    // ---------------------------------------------------------------- the whole trip

    [Fact]
    public void Signature_Survives_Gemini_To_Chat_Client_And_Back_To_Gemini()
    {
        // Turn 1: Gemini answers with a signed function call (non-streaming body, decoded by the real decoder).
        var upstream = (JsonObject)JsonNode.Parse("""
            {"responseId":"g1","modelVersion":"gemini-3-pro","candidates":[{"index":0,"finishReason":"STOP","content":{"role":"model","parts":[
              {"functionCall":{"name":"get_weather","args":{"city":"Tokyo"}},"thoughtSignature":"c2lnLXJlYWw="},
              {"functionCall":{"name":"get_time","args":{}}}]}}]}
            """)!;
        var decoder = Gemini.CreateResponseDecoder(new ResponseDecodeContext { Stream = false, Model = "gemini-3-pro" });
        var toChat = Chat.CreateResponseEncoder(Ctx(stream: false));
        foreach (var e in decoder.DecodeJson(upstream)) toChat.OnEvent(e);
        var assistant = (JsonObject)toChat.BuildJson()["choices"]![0]!["message"]!.DeepClone();
        Assert.Equal("c2lnLXJlYWw=", SignatureOf(assistant["tool_calls"]![0]));

        // Turn 2: the Chat client appends that message untouched, plus the tool results, and asks again.
        var calls = assistant["tool_calls"]!.AsArray();
        var request = new JsonObject
        {
            ["model"] = "gemini-3-pro",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Weather in Tokyo?" },
                assistant,
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = calls[0]!["id"]!.GetValue<string>(), ["content"] = "22C" },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = calls[1]!["id"]!.GetValue<string>(), ["content"] = "noon" }),
        };
        var body = ToGemini(Chat.DecodeRequest(request, new RequestDecodeContext()));

        var modelParts = (JsonArray)((JsonObject)((JsonArray)body["contents"]!)[1]!)["parts"]!;
        Assert.Equal("c2lnLXJlYWw=", modelParts[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("get_weather", modelParts[0]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Null(modelParts[1]!["thoughtSignature"]);
    }
}
