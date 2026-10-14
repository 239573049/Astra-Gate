using System.Text.Json.Nodes;
using Astra.Core.Clients;

namespace Astra.Core.Tests;

public class ClientProtocolsTests
{
    [Fact]
    public void EveryClientsDefaultIsItsProtocolOf_First()
    {
        foreach (var kind in ClientKinds.All)
            Assert.Equal(ClientKinds.ProtocolOf(kind), ClientProtocols.For(kind)[0]);
        Assert.True(ClientProtocols.IsPerModel(ClientKinds.NextCoWork));
        Assert.False(ClientProtocols.IsPerModel(ClientKinds.Codex));
        Assert.False(ClientProtocols.IsPerModel(ClientKinds.ClaudeCode));
    }

    [Fact]
    public void Overrides_SkipWhatDoesNotParse()
    {
        var extras = JsonNode.Parse("""{"protocolOverrides":{"a":"openai-responses","b":"anthropic","c":"nope","d":3}}""")!.AsObject();
        var overrides = ClientProtocols.Overrides(extras);
        Assert.Equal(2, overrides.Count);
        Assert.Equal(ApiProtocol.OpenAIResponses, overrides["a"]);
        Assert.Equal(ApiProtocol.Anthropic, overrides["b"]);
        Assert.Empty(ClientProtocols.Overrides(null));
        Assert.Empty(ClientProtocols.Overrides(new JsonObject { ["protocolOverrides"] = "x" }));
    }

    [Theory]
    [InlineData(ClientKinds.NextCoWork, """{"m":"anthropic"}""", null)]
    [InlineData(ClientKinds.NextCoWork, """{"m":null}""", null)]
    [InlineData(ClientKinds.NextCoWork, """{"m":"gemini"}""", "cannot use gemini")]
    [InlineData(ClientKinds.NextCoWork, """{"m":"smoke-signals"}""", "Invalid protocol")]
    [InlineData(ClientKinds.Codex, """{"m":"openai-chat"}""", "cannot use openai-chat")]
    [InlineData(ClientKinds.Pi, """{"m":"gemini"}""", null)]
    public void Validate_AcceptsOnlyProtocolsTheClientCanBeGiven(string kind, string overrides, string? error)
    {
        var message = ClientProtocols.Validate(kind, JsonNode.Parse(overrides)!.AsObject());
        if (error is null) Assert.Null(message);
        else Assert.Contains(error, message);
    }
}
