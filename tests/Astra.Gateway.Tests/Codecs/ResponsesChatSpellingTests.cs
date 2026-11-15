using System.Text.Json.Nodes;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

public class ResponsesChatSpellingTests
{
    [Fact]
    public void Moves_The_Chat_Effort_Into_Reasoning()
    {
        var body = JsonNode.Parse("""{"model":"m","input":"hi","reasoning_effort":"high"}""")!.AsObject();
        Assert.True(ResponsesChatSpelling.MoveReasoningEffort(body));
        Assert.False(body.ContainsKey("reasoning_effort"));
        Assert.Equal("high", (string?)body["reasoning"]!["effort"]);
    }

    [Fact]
    public void An_Effort_Already_In_Reasoning_Wins()
    {
        var body = JsonNode.Parse("""{"reasoning":{"effort":"low","summary":"auto"},"reasoning_effort":"high"}""")!.AsObject();
        Assert.True(ResponsesChatSpelling.MoveReasoningEffort(body));
        Assert.Equal("low", (string?)body["reasoning"]!["effort"]);
        Assert.Equal("auto", (string?)body["reasoning"]!["summary"]);
        Assert.False(body.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void A_Body_Without_The_Field_Is_Left_Alone()
    {
        var body = JsonNode.Parse("""{"reasoning":{"effort":"low"}}""")!.AsObject();
        Assert.False(ResponsesChatSpelling.MoveReasoningEffort(body));
        Assert.Equal("""{"reasoning":{"effort":"low"}}""", body.ToJsonString());
    }

    [Fact]
    public void A_Mistyped_Value_Is_Dropped_Not_Forwarded()
    {
        var body = JsonNode.Parse("""{"reasoning_effort":3}""")!.AsObject();
        Assert.True(ResponsesChatSpelling.MoveReasoningEffort(body));
        Assert.Empty(body);
    }
}
