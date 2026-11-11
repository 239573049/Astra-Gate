using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The MCP gateway entry written into Claude Code and Codex (<see cref="ClientMcpApplier"/>, issue #34).</summary>
public class ClientMcpApplierTests : IDisposable
{
    private const string Gateway = "http://127.0.0.1:17321";
    private const string Token = "astra-key-0123456789abcdef";

    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientMcpApplier _mcp;

    public ClientMcpApplierTests() =>
        _mcp = new ClientMcpApplier(_home.Env, _store, new ClientConfigApplier(_store, _home.Env, _home.BackupRoot));

    public void Dispose() => _home.Dispose();

    private string ClaudeFile => _home.Env.Combine(".claude.json");
    private string CodexFile => Path.Combine(_home.Env.Combine(".codex"), "config.toml");

    [Fact]
    public void ClaudeCode_Entry_Is_Written_Beside_The_Users_Own_Servers_And_Removed_Without_A_Trace()
    {
        _home.WriteFile(ClaudeFile, """{"numStartups": 7, "mcpServers": {"mine": {"type": "http", "url": "https://example.com/mcp"}}}""");
        var before = _home.ReadFile(ClaudeFile);

        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false);

        var json = JsonNode.Parse(_home.ReadFile(ClaudeFile))!;
        Assert.Equal(7, json["numStartups"]!.GetValue<int>());
        Assert.Equal("https://example.com/mcp", json["mcpServers"]!["mine"]!["url"]!.GetValue<string>());
        var entry = json["mcpServers"]!["astra"]!;
        Assert.Equal("http", entry["type"]!.GetValue<string>());
        Assert.Equal(Gateway + "/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer " + Token, entry["headers"]!["Authorization"]!.GetValue<string>());
        var status = _mcp.Inspect(ClientKinds.ClaudeCode);
        Assert.Equal(ClientMcpState.Applied, status.State);
        Assert.Equal(Gateway + "/mcp", status.Url);

        _mcp.Remove(ClientKinds.ClaudeCode, force: false);
        Assert.Equal(JsonNode.Parse(before)!.ToJsonString(), JsonNode.Parse(_home.ReadFile(ClaudeFile))!.ToJsonString());
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.ClaudeCode).State);
    }

    [Fact]
    public void ClaudeCode_Honours_CLAUDE_CONFIG_DIR_And_Removes_A_File_It_Created()
    {
        var dir = _home.File("custom-claude");
        _home.Variables["CLAUDE_CONFIG_DIR"] = dir;

        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false);
        var file = Path.Combine(dir, ".claude.json");
        Assert.True(_home.FileExists(file));
        Assert.False(_home.FileExists(ClaudeFile));

        _mcp.Remove(ClientKinds.ClaudeCode, force: false);
        Assert.False(_home.FileExists(file)); // the file did not exist before Astra wrote it
    }

    [Fact]
    public void Codex_Entry_Is_A_Table_With_The_Address_And_A_Bearer_Header()
    {
        _home.WriteFile(CodexFile, "model = \"gpt-5\"\n\n[mcp_servers.mine]\ncommand = \"npx\"\n");

        _mcp.Apply(ClientKinds.Codex, Gateway, Token, force: false);

        var text = _home.ReadFile(CodexFile);
        Assert.Contains("[mcp_servers.astra]", text);
        Assert.Contains($"url = \"{Gateway}/mcp\"", text);
        Assert.Contains("http_headers", text);
        Assert.Contains("Bearer " + Token, text);
        Assert.Contains("[mcp_servers.mine]", text); // the user's own server stays
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.Codex).State);

        _mcp.Remove(ClientKinds.Codex, force: false);
        var after = _home.ReadFile(CodexFile);
        Assert.DoesNotContain("astra", after);
        Assert.Contains("[mcp_servers.mine]", after);
        Assert.Contains("model = \"gpt-5\"", after);
    }

    [Theory]
    [InlineData(ClientKinds.ClaudeCode)]
    [InlineData(ClientKinds.Codex)]
    public void Applying_Again_Replaces_The_Token_And_Keeps_One_Entry(string kind)
    {
        _mcp.Apply(kind, Gateway, Token, force: false);
        _mcp.Apply(kind, Gateway, "astra-key-second-token-xyz", force: false);

        var file = kind == ClientKinds.Codex ? CodexFile : ClaudeFile;
        var text = _home.ReadFile(file);
        Assert.Contains("astra-key-second-token-xyz", text);
        Assert.DoesNotContain(Token, text);
        Assert.Equal(1, text.Split("\"url\"").Length - 1 + text.Split("url = ").Length - 1);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(kind).State);
    }

    [Fact]
    public void An_Entry_The_User_Wrote_Is_Never_Overwritten_Or_Removed()
    {
        _home.WriteFile(ClaudeFile, """{"mcpServers": {"astra": {"type": "http", "url": "https://mine.example/mcp"}}}""");

        Assert.Equal(ClientMcpState.Conflict, _mcp.Inspect(ClientKinds.ClaudeCode).State);
        var e = Assert.Throws<ClientMcpException>(() => _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: true));
        Assert.Equal(ClientMcpState.Conflict, e.State);
        _mcp.Remove(ClientKinds.ClaudeCode, force: true); // nothing of Astra's to remove
        Assert.Contains("https://mine.example/mcp", _home.ReadFile(ClaudeFile));
    }

    [Fact]
    public void A_Changed_Entry_Is_Reported_As_Drift_And_Only_Replaced_On_Request()
    {
        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false);
        _home.WriteFile(ClaudeFile, _home.ReadFile(ClaudeFile).Replace(Gateway + "/mcp", "https://elsewhere.example/mcp"));

        Assert.Equal(ClientMcpState.Drifted, _mcp.Inspect(ClientKinds.ClaudeCode).State);
        Assert.Throws<ClientMcpException>(() => _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false));
        // a plain remove leaves the changed value alone
        _mcp.Remove(ClientKinds.ClaudeCode, force: false);
        Assert.Contains("https://elsewhere.example/mcp", _home.ReadFile(ClaudeFile));

        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: true);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.ClaudeCode).State);
        Assert.Contains(Gateway + "/mcp", _home.ReadFile(ClaudeFile));
    }

    [Fact]
    public void The_Entry_Has_Its_Own_State_So_Disabling_The_Clients_Takeover_Leaves_It()
    {
        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false);
        // the client's own take-over keeps its state under the client kind; the MCP entry under <kind>-mcp
        Assert.Empty(_store.List(ClientKinds.ClaudeCode));
        Assert.Single(_store.List(ClientMcpApplier.StateKind(ClientKinds.ClaudeCode)));

        _store.DeleteAll(ClientKinds.ClaudeCode);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.ClaudeCode).State);
    }

    [Fact]
    public void RemoveAll_Takes_Out_Every_Clients_Entry()
    {
        _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false);
        _mcp.Apply(ClientKinds.Codex, Gateway, Token, force: false);

        var reports = _mcp.RemoveAll();

        Assert.Equal(2, reports.Count);
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.ClaudeCode).State);
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.Codex).State);
    }

    [Fact]
    public void A_File_The_Client_Cannot_Parse_Is_Reported_Not_Written()
    {
        _home.WriteFile(ClaudeFile, "{ this is not json");
        var status = _mcp.Inspect(ClientKinds.ClaudeCode);
        Assert.NotNull(status.Error);
        Assert.Throws<ClientMcpException>(() => _mcp.Apply(ClientKinds.ClaudeCode, Gateway, Token, force: false));
        Assert.Equal("{ this is not json", _home.ReadFile(ClaudeFile));
    }

    [Fact]
    public void Only_Claude_Code_And_Codex_Are_Supported()
    {
        Assert.True(ClientMcpApplier.Supports(ClientKinds.ClaudeCode));
        Assert.True(ClientMcpApplier.Supports(ClientKinds.Codex));
        Assert.False(ClientMcpApplier.Supports("gemini-cli"));
        Assert.Throws<ArgumentException>(() => _mcp.Inspect("gemini-cli"));
    }
}
