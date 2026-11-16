using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Tracing;

namespace Astra.Gateway.Tests;

public sealed class ContextEstimatorTests
{
    [Fact]
    public void Anthropic_Body_Splits_Into_System_Tools_History_And_The_Latest_Turn()
    {
        var body = JsonNode.Parse("""
            {"system":"aaaaaaaaaa","tools":[{"name":"t","description":"dddd"}],
             "messages":[{"role":"user","content":"hhhhhhhhhh"},{"role":"assistant","content":"hhhhhhhhhh"},{"role":"user","content":"llllllllll"}]}
            """)!.AsObject();
        var shares = ContextEstimator.Estimate(ApiProtocol.Anthropic, body)!;
        Assert.Equal(1.0, shares.System + shares.Tools + shares.History + shares.Latest, 3);
        Assert.Equal(2 * shares.Latest, shares.History, 3);
        Assert.Equal(shares.Latest, shares.System, 3);
        Assert.True(shares.Tools > 0);
    }

    [Fact]
    public void Chat_System_And_Developer_Messages_Count_As_Instructions()
    {
        var body = JsonNode.Parse("""{"messages":[{"role":"system","content":"abcd"},{"role":"developer","content":"abcd"},{"role":"user","content":"abcd"}]}""")!.AsObject();
        var shares = ContextEstimator.Estimate(ApiProtocol.OpenAIChat, body)!;
        Assert.Equal(0.6667, shares.System, 3);
        Assert.Equal(0.3333, shares.Latest, 3);
        Assert.Equal(0, shares.History);
    }

    [Fact]
    public void Responses_And_Gemini_Bodies_Are_Read_Too()
    {
        var responses = JsonNode.Parse("""{"instructions":"abcd","input":[{"role":"user","content":[{"type":"input_text","text":"abcd"}]}]}""")!.AsObject();
        Assert.Equal(0.5, ContextEstimator.Estimate(ApiProtocol.OpenAIResponses, responses)!.System, 3);
        var gemini = JsonNode.Parse("""{"systemInstruction":{"parts":[{"text":"abcd"}]},"contents":[{"role":"user","parts":[{"text":"abcd"}]}]}""")!.AsObject();
        Assert.Equal(0.5, ContextEstimator.Estimate(ApiProtocol.Gemini, gemini)!.Latest, 3);
    }

    [Fact]
    public void Inline_Media_Weighs_A_Flat_Amount_And_An_Empty_Body_Has_No_Estimate()
    {
        var huge = new string('A', 5_000_000);
        var body = JsonNode.Parse("""{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","data":"@@"}}]}]}""".Replace("@@", huge))!.AsObject();
        Assert.Equal(1.0, ContextEstimator.Estimate(ApiProtocol.Anthropic, body)!.Latest, 3);
        Assert.Null(ContextEstimator.Estimate(ApiProtocol.Anthropic, new JsonObject()));
    }
}
