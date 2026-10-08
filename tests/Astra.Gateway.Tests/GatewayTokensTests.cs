using Astra.Core.Clients;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class GatewayTokensTests
{
    [Fact]
    public void Generated_Tokens_Have_The_Expected_Shape_And_Verify_By_Hash()
    {
        var token = GatewayTokens.Generate();
        Assert.StartsWith("sk-astra-", token);
        Assert.Equal("sk-astra-".Length + 32, token.Length);
        Assert.NotEqual(token, GatewayTokens.Generate());
        Assert.Equal(token[.."sk-astra-XXXXXX".Length], GatewayTokens.PrefixOf(token));
        Assert.True(GatewayTokens.Verify(token, GatewayTokens.Hash(token)));
        Assert.False(GatewayTokens.Verify(token[..^1] + "x", GatewayTokens.Hash(token)));
        Assert.False(GatewayTokens.Verify(token, null));
    }

    [Fact]
    public void Client_Keys_Carry_The_Kind_And_Bare_Tokens_Are_Direct_Calls()
    {
        var token = GatewayTokens.Generate();
        foreach (var kind in ClientKinds.All)
        {
            Assert.True(GatewayTokens.TryParse(GatewayTokens.ForClient(token, kind), out var parsed, out var parsedKind));
            Assert.Equal(token, parsed);
            Assert.Equal(kind, parsedKind); // includes dashed kinds such as claude-code
        }
        Assert.True(GatewayTokens.TryParse(token, out var bare, out var none));
        Assert.Equal(token, bare);
        Assert.Null(none);
    }

    [Theory]
    [InlineData("sk-not-astra")]
    [InlineData("sk-astra-short")]
    [InlineData("sk-astra-0123456789abcdefghijABCDEFGHIJ!!")]
    [InlineData("sk-astra-0123456789abcdefghijABCDEFGHIJKL.unknown-client")]
    [InlineData("astra-codex-0123456789abcdefghijABCDEFGHIJKL")]
    public void Malformed_Keys_And_Unknown_Suffixes_Are_Rejected(string key) =>
        Assert.False(GatewayTokens.TryParse(key, out _, out _));

    [Fact]
    public void Legacy_Per_Client_Keys_Are_Recognised() =>
        Assert.True(GatewayTokens.IsLegacyKey("astra-codex-0123456789abcdefghijABCDEFGHIJKL"));
}
