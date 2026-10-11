using System.Text.Json.Nodes;
using Astra.Core.Clients;

namespace Astra.Core.Tests;

public class ClientOptionsTests
{
    [Theory]
    [InlineData("""{"options":{"cacheTtl":"1h"}}""", "1h")]
    [InlineData("""{"options":{"cacheTtl":"5m"}}""", "5m")]
    [InlineData("""{"options":{"cacheTtl":"default"}}""", null)]
    [InlineData("""{"options":{"cacheTtl":"2h"}}""", null)]
    [InlineData("""{"models":{}}""", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void CacheTtl_OnlyAcceptsFiveMinutesAndOneHour(string? extraJson, string? expected) =>
        Assert.Equal(expected, ClientOptions.GetCacheTtl(extraJson));

    [Fact]
    public void Validate_RejectsUnknownKeysAndBadValues()
    {
        Assert.Null(ClientOptions.Validate(ClientKinds.ClaudeCode, new JsonObject { ["context1m"] = true, ["cacheTtl"] = "1h", ["maxThinkingTokens"] = 8000 }));
        Assert.NotNull(ClientOptions.Validate(ClientKinds.ClaudeCode, new JsonObject { ["nope"] = true }));
        Assert.NotNull(ClientOptions.Validate(ClientKinds.ClaudeCode, new JsonObject { ["cacheTtl"] = "2h" }));
        Assert.NotNull(ClientOptions.Validate(ClientKinds.ClaudeCode, new JsonObject { ["context1m"] = "yes" }));
        Assert.NotNull(ClientOptions.Validate(ClientKinds.Codex, new JsonObject { ["contextWindow"] = -1 }));
        Assert.NotNull(ClientOptions.Validate(ClientKinds.Zed, new JsonObject { ["context1m"] = true }));
    }

    [Fact]
    public void GetInt_IgnoresZeroAndMissing()
    {
        var extras = new JsonObject { ["options"] = new JsonObject { ["maxThinkingTokens"] = 0, ["contextWindow"] = 400000 } };
        Assert.Null(ClientOptions.GetInt(extras, ClientOptions.MaxThinkingTokens));
        Assert.Equal(400000, ClientOptions.GetInt(extras, ClientOptions.ContextWindow));
    }
}
