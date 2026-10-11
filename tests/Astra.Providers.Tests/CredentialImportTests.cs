using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class CredentialImportTests
{
    [Fact]
    public void Splits_Lines_Json_Documents_And_Json_Lines()
    {
        Assert.Equal(["a1", "b2", "c3"], CredentialImport.SplitEntries("# mine\na1\n\n\"b2\", c3\n"));

        // One pretty-printed JSON document stays whole (auth.json / .credentials.json pasted as is).
        var doc = "{\n  \"claudeAiOauth\": {\n    \"refreshToken\": \"sk-ant-ort01-x\"\n  }\n}";
        Assert.Equal(["""{"claudeAiOauth":{"refreshToken":"sk-ant-ort01-x"}}"""], CredentialImport.SplitEntries(doc));

        // An array mixes objects and plain strings; one JSON object per line works too.
        Assert.Equal(["""{"a":1}""", "tok"], CredentialImport.SplitEntries("""[{"a":1}, "tok"]"""));
        Assert.Equal(["""{"a":1}""", """{"b":2}""", "tok"], CredentialImport.SplitEntries("{\"a\":1}\n{\"b\":2}\ntok"));
        Assert.Empty(CredentialImport.SplitEntries("  \n# only a comment"));
    }

    [Theory]
    [InlineData("claude-subscription", "sk-ant-ort01-abcdefghijklmnop", CredentialKinds.RefreshToken)]
    [InlineData("claude-subscription", "sk-ant-oat01-abcdefghijklmnop", CredentialKinds.SetupToken)]
    [InlineData("openai-subscription", "rt_abcdefghijklmnopqrstuvwxyz", CredentialKinds.RefreshToken)]
    [InlineData("grok-subscription", "opaque-refresh-token-0123456789", CredentialKinds.RefreshToken)]
    [InlineData("kimi-subscription", "sk-kimi-abcdefghijklmnop", CredentialKinds.ApiKey)]
    [InlineData("kimi-subscription", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.sig", CredentialKinds.RefreshToken)]
    [InlineData("zcode-subscription", "0123456789abcdef0123456789abcdef.AbCdEfGhIjKlMnOp", CredentialKinds.ApiKey)]
    [InlineData("bigmodel-subscription", "0123456789abcdef0123456789abcdef.AbCdEfGhIjKlMnOp", CredentialKinds.ApiKey)]
    [InlineData("github-copilot-subscription", "gho_abcdefghijklmnopqrstuvwxyz0123456789", CredentialKinds.GitHubToken)]
    public void Recognises_Each_Familys_Credentials(string family, string entry, string kind)
    {
        var parsed = CredentialImport.Classify(family, entry, asApiKey: false, out var error);
        Assert.Null(error);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(entry, parsed.Secret);
    }

    [Theory]
    [InlineData("claude-subscription", "sk-ant-api03-abcdefghijklmnop", "Anthropic API Key")]
    [InlineData("claude-subscription", "hello-world", "不是可识别的 Claude 令牌")]
    [InlineData("openai-subscription", "sk-proj-abcdefghijklmnopqrstuvwxyz", "OpenAI API Key")]
    [InlineData("grok-subscription", "xai-abcdefghijklmnopqrstuvwxyz", "xAI API Key")]
    [InlineData("github-copilot-subscription", "not-a-token", "gho_")]
    [InlineData("custom-subscription", "anything-goes-here-123456", "不支持导入")]
    public void Rejects_What_Does_Not_Belong_With_A_Reason(string family, string entry, string reason)
    {
        Assert.Null(CredentialImport.Classify(family, entry, asApiKey: false, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Api_Key_Mode_Is_Only_Offered_Where_The_Endpoint_Takes_Keys()
    {
        Assert.Equal(CredentialKinds.ApiKey,
            CredentialImport.Classify("zcode-subscription", "abcdefghijklmnopqrstuvwx", asApiKey: true, out _)!.Kind);
        Assert.Null(CredentialImport.Classify("claude-subscription", "sk-ant-ort01-abcdefghijklmnop", asApiKey: true, out var error));
        Assert.Contains("不接受 API Key", error);
        Assert.Null(CredentialImport.Classify("kimi-subscription", "short", asApiKey: true, out _));

        Assert.True(CredentialImport.FormatsOf("kimi-subscription").ApiKey);
        Assert.False(CredentialImport.FormatsOf("claude-subscription").ApiKey);
        Assert.Empty(CredentialImport.FormatsOf("something-else").Formats);
    }

    [Fact]
    public void Reads_Claude_Code_Credentials_Json()
    {
        var json = """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-a","refreshToken":"sk-ant-ort01-r","expiresAt":4102444800000,"subscriptionType":"max"}}""";
        var parsed = CredentialImport.Classify("claude-subscription", json, asApiKey: false, out _)!;
        Assert.Equal(CredentialKinds.ClaudeCredentials, parsed.Kind);
        Assert.Equal("sk-ant-oat01-a", parsed.Secret);
        Assert.Equal("sk-ant-ort01-r", parsed.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), parsed.ExpiresAt);
        Assert.Equal("max", parsed.Plan);

        // Only an access token (no refresh grant): it is used until it expires, like a setup-token.
        var accessOnly = CredentialImport.Classify("claude-subscription", """{"accessToken":"sk-ant-oat01-a"}""", false, out _)!;
        Assert.Equal(CredentialKinds.SetupToken, accessOnly.Kind);

        Assert.Null(CredentialImport.Classify("claude-subscription", """{"foo":1}""", false, out var error));
        Assert.Contains("claudeAiOauth", error);
    }

    [Fact]
    public void Reads_Codex_Auth_Json()
    {
        var json = """{"tokens":{"access_token":"at","refresh_token":"rt","id_token":"id","account_id":"acc"}}""";
        var parsed = CredentialImport.Classify("openai-subscription", json, asApiKey: false, out var error);
        Assert.Null(error);
        Assert.Equal(CredentialKinds.CodexAuthJson, parsed!.Kind);
        Assert.Equal("rt", parsed.RefreshToken);
        Assert.Equal("id", parsed.IdToken);
    }
}
