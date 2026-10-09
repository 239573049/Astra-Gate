using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

/// <summary>
/// 找本机已登录的 GitHub 授权（VS Code 的系统凭据存储、GH_TOKEN、Copilot 插件配置）——
/// 接成 Copilot 订阅账号就不用再走一遍设备码授权。
/// </summary>
public class LocalCopilotLoginTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "astra-copilot-" + Guid.NewGuid().ToString("N"));

    private static string Token(string prefix = "gho_") => prefix + new string('a', 36);

    private LocalCredentialEnvironment Env(Dictionary<string, string?>? variables = null, string os = "linux") =>
        new(_home, os, name => variables is not null && variables.TryGetValue(name, out var value) ? value : null);

    private void WriteHosts(string json)
    {
        var dir = Path.Combine(_home, ".config", "github-copilot");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hosts.json"), json);
    }

    [Theory]
    [InlineData("gho_")]
    [InlineData("ghp_")]
    [InlineData("github_pat_")]
    public void Accepts_The_GitHub_Token_Shapes(string prefix)
    {
        Assert.True(LocalCopilotLogin.LooksLikeGitHubToken(Token(prefix)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-not-a-github-token-but-long-enough-to-pass-length")] // wrong prefix
    [InlineData("gho_short")]                                            // too short
    [InlineData("gho_aaaaaaaaaa aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]         // whitespace
    public void Rejects_Anything_Else(string? token)
    {
        Assert.False(LocalCopilotLogin.LooksLikeGitHubToken(token));
    }

    [Fact]
    public void Cheap_Prefers_Gh_Token_Over_GitHub_Token()
    {
        var found = LocalCopilotLogin.Cheap(Env(new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = Token("gho_"),
            ["GITHUB_TOKEN"] = Token("ghp_"),
        }));

        Assert.Equal(2, found.Count);
        Assert.Equal(Token("gho_"), found[0].Token);
        Assert.Contains("GH_TOKEN", found[0].Source);
    }

    [Fact]
    public void Cheap_Falls_Back_To_The_Copilot_Plugin_Hosts_File()
    {
        WriteHosts("""{"github.com":{"user":"me","oauth_token":"OAUTH"}}""".Replace("OAUTH", Token()));

        var found = LocalCopilotLogin.Cheap(Env());

        var credential = Assert.Single(found);
        Assert.Equal(Token(), credential.Token);
        Assert.Contains("github.com", credential.Source);
    }

    [Fact]
    public void Cheap_Drops_Duplicates_And_Malformed_Entries()
    {
        WriteHosts("""{"github.com":{"oauth_token":"OAUTH"}}""".Replace("OAUTH", Token()));

        var found = LocalCopilotLogin.Cheap(Env(new Dictionary<string, string?>
        {
            // 同一份 token 从两处出现只算一次；形态不对的来源直接丢掉。
            ["GITHUB_TOKEN"] = "sk-nope",
        }));

        Assert.Single(found);
        Assert.Equal(Token(), found[0].Token);
    }

    [Fact]
    public void Cheap_Ignores_A_Broken_Hosts_File_Instead_Of_Throwing()
    {
        WriteHosts("{not json");

        Assert.Empty(LocalCopilotLogin.Cheap(Env()));
    }

    [Fact]
    public void The_Credential_Store_Is_Never_Touched_By_Cheap()
    {
        // Cheap 是给页面加载时的探测用的：它只读环境变量和配置文件，绝不碰系统凭据存储
        // （macOS 上读 keychain 会弹系统授权框，而且会读到开发者自己的真实凭据）。
        Assert.Empty(LocalCopilotLogin.Cheap(Env(os: "osx")));
    }

    [Fact]
    public void Windows_Has_No_Credential_Store_Reader()
    {
        // Windows 凭据管理器没有稳定的命令行读法：这条路径必须安静地空手而归，
        // 而且不能去启动任何进程（所以这个用例不会碰到开发机的真实凭据）。
        Assert.Empty(LocalCopilotLogin.All(Env(os: "windows")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }
}
