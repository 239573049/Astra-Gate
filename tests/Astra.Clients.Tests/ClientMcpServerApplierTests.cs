using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>A user-defined local (stdio) MCP server written into Claude Code and Codex (<see cref="ClientMcpServerApplier"/>, issue #34).</summary>
public class ClientMcpServerApplierTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientMcpServerApplier _mcp;

    private static readonly LocalMcpSpec Spec = new("files", "npx", ["-y", "@mcp/files", "C:\\data"], new Dictionary<string, string> { ["TOKEN"] = "s3cret" });

    public ClientMcpServerApplierTests() =>
        _mcp = new ClientMcpServerApplier(_home.Env, _store, new ClientConfigApplier(_store, _home.Env, _home.BackupRoot));

    public void Dispose() => _home.Dispose();

    private string ClaudeFile => _home.Env.Combine(".claude.json");
    private string CodexFile => Path.Combine(_home.Env.Combine(".codex"), "config.toml");

    [Fact]
    public void ClaudeCode_Gets_A_Stdio_Entry_Beside_The_Users_Own_And_Loses_It_Without_A_Trace()
    {
        _home.WriteFile(ClaudeFile, """{"numStartups": 7, "mcpServers": {"mine": {"type": "stdio", "command": "x"}}}""");
        var before = _home.ReadFile(ClaudeFile);

        _mcp.Apply(ClientKinds.ClaudeCode, Spec, force: false);

        var json = JsonNode.Parse(_home.ReadFile(ClaudeFile))!;
        Assert.Equal("x", json["mcpServers"]!["mine"]!["command"]!.GetValue<string>());
        var entry = json["mcpServers"]!["files"]!;
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        Assert.Equal("npx", entry["command"]!.GetValue<string>());
        Assert.Equal(["-y", "@mcp/files", "C:\\data"], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList());
        Assert.Equal("s3cret", entry["env"]!["TOKEN"]!.GetValue<string>());
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.ClaudeCode, "files").State);
        Assert.Equal(["files"], _mcp.WrittenNames(ClientKinds.ClaudeCode));

        _mcp.Remove(ClientKinds.ClaudeCode, "files", force: false);
        Assert.Equal(JsonNode.Parse(before)!.ToJsonString(), JsonNode.Parse(_home.ReadFile(ClaudeFile))!.ToJsonString());
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.ClaudeCode, "files").State);
    }

    [Fact]
    public void Codex_Gets_A_Table_With_Escaped_Values_And_Stays_Applied()
    {
        _mcp.Apply(ClientKinds.Codex, Spec, force: false);

        var text = _home.ReadFile(CodexFile);
        Assert.Contains("[mcp_servers.files]", text);
        Assert.Contains("command = \"npx\"", text);
        Assert.Contains("\"C:\\\\data\"", text);
        Assert.Contains("TOKEN", text);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.Codex, "files").State);

        _mcp.Remove(ClientKinds.Codex, "files", force: false);
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.Codex, "files").State);
    }

    [Theory]
    [InlineData(ClientKinds.ClaudeCode)]
    [InlineData(ClientKinds.Codex)]
    public void A_Foreign_Entry_Of_The_Same_Name_Is_Never_Overwritten(string kind)
    {
        if (kind == ClientKinds.Codex) _home.WriteFile(CodexFile, "[mcp_servers.files]\ncommand = \"mine\"\n");
        else _home.WriteFile(ClaudeFile, """{"mcpServers": {"files": {"type": "stdio", "command": "mine"}}}""");

        Assert.Equal(ClientMcpState.Conflict, _mcp.Inspect(kind, "files").State);
        var e = Assert.Throws<ClientMcpException>(() => _mcp.Apply(kind, Spec, force: true));
        Assert.Equal(ClientMcpState.Conflict, e.State);
        Assert.Contains("mine", _home.ReadFile(kind == ClientKinds.Codex ? CodexFile : ClaudeFile));
    }

    [Theory]
    [InlineData(ClientKinds.ClaudeCode)]
    [InlineData(ClientKinds.Codex)]
    public void A_Changed_Entry_Is_Drifted_And_Replaced_Only_With_Force(string kind)
    {
        _mcp.Apply(kind, Spec, force: false);
        var file = kind == ClientKinds.Codex ? CodexFile : ClaudeFile;
        _home.WriteFile(file, _home.ReadFile(file).Replace("npx", "bunx"));

        Assert.Equal(ClientMcpState.Drifted, _mcp.Inspect(kind, "files").State);
        Assert.Throws<ClientMcpException>(() => _mcp.Apply(kind, Spec, force: false));
        _mcp.Apply(kind, Spec, force: true);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(kind, "files").State);
        Assert.Contains("npx", _home.ReadFile(file));
    }

    [Fact]
    public void Applying_Again_Replaces_The_Entry_And_RemoveAll_Takes_Every_Written_One()
    {
        _mcp.Apply(ClientKinds.ClaudeCode, Spec, force: false);
        _mcp.Apply(ClientKinds.ClaudeCode, Spec with { Command = "uvx", Args = ["srv"] }, force: false);
        _mcp.Apply(ClientKinds.Codex, Spec with { Name = "other" }, force: false);
        Assert.Equal("uvx", JsonNode.Parse(_home.ReadFile(ClaudeFile))!["mcpServers"]!["files"]!["command"]!.GetValue<string>());

        _mcp.RemoveAll();
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.ClaudeCode, "files").State);
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.Codex, "other").State);
    }

    private string GeminiFile => _home.Env.Combine(".gemini", "settings.json");
    private string QwenFile => _home.Env.Combine(".qwen", "settings.json");
    private string OpenCodeFile => _home.Env.Combine(".config", "opencode", "opencode.json");

    private static readonly LocalMcpSpec Remote = new("docs", "", [], new Dictionary<string, string>(), "https://docs.example.com/mcp",
        new Dictionary<string, string> { ["Authorization"] = "Bearer k-1" });

    [Fact]
    public void Gemini_And_Qwen_Share_The_McpServers_Shape_With_HttpUrl_For_Remote()
    {
        _home.WriteFile(GeminiFile, """{"theme": "dark", "mcpServers": {"mine": {"command": "x"}}}""");
        _mcp.Apply(ClientKinds.GeminiCli, Spec, force: false);
        _mcp.Apply(ClientKinds.GeminiCli, Remote, force: false);
        _mcp.Apply(ClientKinds.QwenCode, Spec, force: false);

        var gemini = JsonNode.Parse(_home.ReadFile(GeminiFile))!;
        Assert.Equal("dark", gemini["theme"]!.GetValue<string>());
        Assert.Equal("x", gemini["mcpServers"]!["mine"]!["command"]!.GetValue<string>());
        Assert.Equal("npx", gemini["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Null(gemini["mcpServers"]!["files"]!["type"]);
        Assert.Equal("https://docs.example.com/mcp", gemini["mcpServers"]!["docs"]!["httpUrl"]!.GetValue<string>());
        Assert.Equal("Bearer k-1", gemini["mcpServers"]!["docs"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("npx", JsonNode.Parse(_home.ReadFile(QwenFile))!["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.GeminiCli, "docs").State);
    }

    [Fact]
    public void OpenCode_Gets_Local_And_Remote_Entries_Under_Mcp()
    {
        _mcp.Apply(ClientKinds.OpenCode, Spec, force: false);
        _mcp.Apply(ClientKinds.OpenCode, Remote, force: false);

        var mcp = JsonNode.Parse(_home.ReadFile(OpenCodeFile))!["mcp"]!;
        Assert.Equal("local", mcp["files"]!["type"]!.GetValue<string>());
        Assert.Equal(["npx", "-y", "@mcp/files", "C:\\data"], mcp["files"]!["command"]!.AsArray().Select(a => a!.GetValue<string>()).ToList());
        Assert.Equal("s3cret", mcp["files"]!["environment"]!["TOKEN"]!.GetValue<string>());
        Assert.Equal("remote", mcp["docs"]!["type"]!.GetValue<string>());
        Assert.Equal("https://docs.example.com/mcp", mcp["docs"]!["url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ClientKinds.ClaudeCode)]
    [InlineData(ClientKinds.Codex)]
    public void A_Remote_Entry_Is_Written_And_Tracked(string kind)
    {
        _mcp.Apply(kind, Remote, force: false);
        var text = _home.ReadFile(kind == ClientKinds.Codex ? CodexFile : ClaudeFile);
        Assert.Contains("https://docs.example.com/mcp", text);
        Assert.Contains("Bearer k-1", text);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(kind, "docs").State);

        _home.WriteFile(kind == ClientKinds.Codex ? CodexFile : ClaudeFile, text.Replace("docs.example.com", "evil.example.com"));
        Assert.Equal(ClientMcpState.Drifted, _mcp.Inspect(kind, "docs").State);
    }

    [Fact]
    public void Entries_The_User_Had_Are_Listed_And_Adopting_One_Keeps_Their_Original_For_Removal()
    {
        _home.WriteFile(ClaudeFile, """
            {"mcpServers": {
              "files": {"type": "stdio", "command": "npx", "args": ["-y", "srv"], "env": {"K": "v"}},
              "docs": {"type": "http", "url": "https://d.example/mcp", "headers": {"X": "1"}},
              "legacy": {"type": "sse", "url": "https://old.example/sse"},
              "astra": {"type": "http", "url": "http://127.0.0.1:1/mcp"}}}
            """);
        _home.WriteFile(CodexFile, "[mcp_servers.files]\ncommand = \"npx\"\nargs = [\"-y\", \"srv\"]\nenv = { K = \"v\" }\n\n[mcp_servers.keyed]\nurl = \"https://k.example\"\nbearer_token_env_var = \"T\"\n");

        var claude = _mcp.Unmanaged(ClientKinds.ClaudeCode);
        Assert.Equal(["docs", "files", "legacy"], claude.Select(e => e.Name).Order().ToList());
        Assert.NotNull(claude.Single(e => e.Name == "files").Spec);
        Assert.Equal("https://d.example/mcp", claude.Single(e => e.Name == "docs").Spec!.Url);
        Assert.NotNull(claude.Single(e => e.Name == "legacy").Unsupported);
        var codex = _mcp.Unmanaged(ClientKinds.Codex);
        Assert.Equal(["npx", "-y", "srv"], [codex.Single(e => e.Name == "files").Spec!.Command, .. codex.Single(e => e.Name == "files").Spec!.Args]);
        Assert.Equal("v", codex.Single(e => e.Name == "files").Spec!.Env["K"]);
        Assert.NotNull(codex.Single(e => e.Name == "keyed").Unsupported);

        var spec = claude.Single(e => e.Name == "files").Spec!;
        _mcp.Adopt(ClientKinds.ClaudeCode, spec);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.ClaudeCode, "files").State);
        Assert.DoesNotContain(_mcp.Unmanaged(ClientKinds.ClaudeCode), e => e.Name == "files");

        // removing it from Astra puts the user's own entry back
        _mcp.Remove(ClientKinds.ClaudeCode, "files", force: false);
        var back = JsonNode.Parse(_home.ReadFile(ClaudeFile))!["mcpServers"]!["files"]!;
        Assert.Equal("npx", back["command"]!.GetValue<string>());
        Assert.Equal("v", back["env"]!["K"]!.GetValue<string>());
        Assert.Equal(ClientMcpState.Conflict, _mcp.Inspect(ClientKinds.ClaudeCode, "files").State);

        // a Codex table can be adopted too
        _mcp.Adopt(ClientKinds.Codex, codex.Single(e => e.Name == "files").Spec!);
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.Codex, "files").State);
    }
}
