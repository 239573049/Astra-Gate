using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;
using Astra.Gateway.Protocol.Chat;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Cross-protocol mapping of thinking display, verbosity and effort names.</summary>
public class ReasoningFormatConversionTests
{
    private static RequestEncodeContext Ctx(string model) => new() { UpstreamModel = model, EffortBudgets = new EffortBudgets() };

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Anthropic_Thinking_Display_Round_Trips_For_Budget_And_Adaptive()
    {
        var codec = new AnthropicCodec();
        foreach (var thinking in new[]
                 {
                     """{"type":"enabled","budget_tokens":2048,"display":"summarized"}""",
                     """{"type":"adaptive","display":"summarized"}""",
                 })
        {
            var ir = codec.DecodeRequest(Obj($$"""{"model":"m","max_tokens":4096,"messages":[{"role":"user","content":"hi"}],"thinking":{{thinking}}}"""),
                new RequestDecodeContext());
            var body = codec.EncodeRequest(ir, Ctx("claude-opus"));
            Assert.Equal("summarized", (string?)body["thinking"]!["display"]);
        }
    }

    [Fact]
    public void Verbosity_Maps_Between_Chat_And_Responses()
    {
        var chat = new ChatCodec();
        var responses = new ResponsesCodec();

        var fromChat = chat.DecodeRequest(Obj("""{"model":"m","messages":[{"role":"user","content":"hi"}],"verbosity":"low"}"""), new RequestDecodeContext());
        var toResponses = responses.EncodeRequest(fromChat, Ctx("gpt-5"));
        Assert.Equal("low", (string?)toResponses["text"]!["verbosity"]);

        var fromResponses = responses.DecodeRequest(
            Obj("""{"model":"m","input":"hi","text":{"verbosity":"high","format":{"type":"json_object"}}}"""), new RequestDecodeContext());
        Assert.Equal("high", fromResponses.Verbosity);
        var toChat = chat.EncodeRequest(fromResponses, Ctx("gpt-5"));
        Assert.Equal("high", (string?)toChat["verbosity"]);
        Assert.Equal("json_object", (string?)toChat["response_format"]!["type"]);
    }

    [Fact]
    public void Max_Effort_Is_Sent_As_Xhigh_To_OpenAI()
    {
        var ir = new UnifiedRequest { Model = "m", Reasoning = new ReasoningConfig { Effort = "max" } };
        ir.Messages.Add(new UnifiedMessage(MessageRole.User, [new TextPart("hi")]));
        Assert.Equal("xhigh", (string?)new ChatCodec().EncodeRequest(ir, Ctx("gpt-5"))["reasoning_effort"]);
        Assert.Equal("xhigh", (string?)new ResponsesCodec().EncodeRequest(ir, Ctx("gpt-5"))["reasoning"]!["effort"]);
    }
}
