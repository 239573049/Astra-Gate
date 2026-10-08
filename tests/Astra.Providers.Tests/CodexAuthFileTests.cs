using System.Text;
using System.Text.Json.Nodes;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

/// <summary>
/// 读 codex-cli 的本地登录文件（~/.codex/auth.json）——让"已经用 codex login 登过"的用户
/// 不用再走一遍浏览器授权。
/// </summary>
public class CodexAuthFileTests
{
    private static string Segment(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string payloadJson) => $"{Segment("""{"alg":"none"}""")}.{Segment(payloadJson)}.sig";

    [Fact]
    public void Parses_A_ChatGpt_Login_With_Both_Tokens()
    {
        var access = Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"ws-nested","chatgpt_plan_type":"promax"}}""");
        var id = Jwt("""{"email":"me@example.com"}""");
        var json = """{"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{"id_token":"ID","access_token":"ACCESS","refresh_token":"rt-1","account_id":"ws-stored"}}"""
            .Replace("ID", id).Replace("ACCESS", access);

        var credentials = CodexAuthFile.Parse(json, out var error);

        Assert.Null(error);
        Assert.NotNull(credentials);
        Assert.Equal(access, credentials.AccessToken);
        Assert.Equal(id, credentials.IdToken);
        Assert.Equal("rt-1", credentials.RefreshToken);
        Assert.Equal("ws-stored", credentials.AccountId);
    }

    [Fact]
    public void Falls_Back_To_The_Token_Claim_When_Account_Id_Is_Absent()
    {
        var access = Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"ws-from-token"}}""");
        var json = """{"auth_mode":"chatgpt","tokens":{"access_token":"ACCESS","refresh_token":"rt-1"}}""".Replace("ACCESS", access);

        var credentials = CodexAuthFile.Parse(json, out _);

        Assert.Equal("ws-from-token", credentials!.AccountId);
    }

    [Fact]
    public void Rejects_An_Api_Key_Login_With_A_Clear_Reason()
    {
        var credentials = CodexAuthFile.Parse("""{"auth_mode":"apikey","OPENAI_API_KEY":"sk-x","tokens":null}""", out var error);

        Assert.Null(credentials);
        Assert.Contains("API Key", error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"auth_mode":"chatgpt","tokens":{"access_token":"at-only"}}""")]
    public void Rejects_Malformed_Or_Incomplete_Files(string json)
    {
        Assert.Null(CodexAuthFile.Parse(json, out var error));
        Assert.NotNull(error);
    }
}
