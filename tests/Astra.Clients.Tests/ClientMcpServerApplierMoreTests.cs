using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core.Clients;
using Xunit;

namespace Astra.Clients.Tests;

/// <summary>The clients added with the rest of the library (Droid, Crush, Copilot CLI) and the project files (<see cref="ClientMcpServerApplier"/>).</summary>
public sealed class ClientMcpServerApplierMoreTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientMcpServerApplier _mcp;

    private static readonly LocalMcpSpec Stdio = new("files", "npx", ["-y", "@mcp/files"], new Dictionary<string, string> { ["K"] = "v" });
    private static readonly LocalMcpSpec Remote = new("docs", "", [], new Dictionary<string, string>(), "https://mcp.example.com/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer x" });

    public ClientMcpServerApplierMoreTests() =>
        _mcp = new ClientMcpServerApplier(_home.Env, _store, new ClientConfigApplier(_store, _home.Env, _home.BackupRoot));

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Droid_Crush_And_Copilot_Get_Their_Own_Entry_Shapes_In_Their_Own_Files()
    {
        _mcp.Apply(ClientKinds.Droid, Stdio, false);
        var droid = JsonNode.Parse(_home.ReadFile(_home.File(".factory", "mcp.json")))!["mcpServers"]!["files"]!;
        Assert.Equal("stdio", droid["type"]!.GetValue<string>());
        Assert.Equal("npx", droid["command"]!.GetValue<string>());

        _mcp.Apply(ClientKinds.Crush, Remote, false);
        var crush = JsonNode.Parse(_home.ReadFile(_home.File(".config", "crush", "crush.json")))!["mcp"]!["docs"]!;
        Assert.Equal("http", crush["type"]!.GetValue<string>());
        Assert.Equal("Bearer x", crush["headers"]!["Authorization"]!.GetValue<string>());

        _mcp.Apply(ClientKinds.CopilotCli, Stdio, false);
        _mcp.Apply(ClientKinds.CopilotCli, Remote, false);
        var copilot = JsonNode.Parse(_home.ReadFile(_home.File(".copilot", "mcp-config.json")))!["mcpServers"]!;
        Assert.Equal("local", copilot["files"]!["type"]!.GetValue<string>());
        Assert.Equal("http", copilot["docs"]!["type"]!.GetValue<string>());
        Assert.Equal("*", copilot["files"]!["tools"]![0]!.GetValue<string>());
        foreach (var kind in new[] { ClientKinds.Droid, ClientKinds.Crush, ClientKinds.CopilotCli })
            Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(kind, kind == ClientKinds.Crush ? "docs" : "files").State);

        // an entry of the same name that Astra did not write is a conflict, never overwritten
        _home.WriteFile(_home.File(".factory", "mcp.json"), """{"mcpServers":{"other":{"type":"stdio","command":"x"}}}""");
        _store.DeleteAll(ClientKinds.Droid + "-mcpx");
        Assert.Equal(ClientMcpState.Missing, _mcp.Inspect(ClientKinds.Droid, "files").State);
    }

    [Fact]
    public void The_Added_Clients_Entries_Are_Read_Back_For_Import()
    {
        _home.WriteFile(_home.File(".copilot", "mcp-config.json"),
            """{"mcpServers":{"a":{"type":"local","command":"npx","args":["x"],"tools":["*"]},"b":{"type":"http","url":"https://x.example/mcp","tools":["*"]},"c":{"type":"sse","url":"https://y.example"}}}""");
        var found = McpClientTargets.ReadEntries(_home.Env, ClientKinds.CopilotCli).ToDictionary(e => e.Name);
        Assert.Equal("npx", found["a"].Spec!.Command);
        Assert.Equal("https://x.example/mcp", found["b"].Spec!.Url);
        Assert.Null(found["c"].Spec);
        _home.WriteFile(_home.File(".config", "crush", "crush.json"), """{"mcp":{"d":{"type":"stdio","command":"uvx","args":["y"]}}}""");
        Assert.Equal("uvx", McpClientTargets.ReadEntries(_home.Env, ClientKinds.Crush).Single().Spec!.Command);
    }

    [Fact]
    public void A_Server_Goes_Into_The_Projects_Own_File_And_RemoveAll_Takes_It_Out_Of_Every_File_Or_Forgets_A_Deleted_Project()
    {
        var project = _home.File("work", "app");
        var gone = _home.File("work", "gone");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(gone);

        _mcp.Apply(ClientKinds.ClaudeCode, Stdio, false);
        _mcp.Apply(ClientKinds.ClaudeCode, Stdio, false, project);
        _mcp.Apply(ClientKinds.Codex, Stdio, false, project);
        _mcp.Apply(ClientKinds.ClaudeCode, Stdio, false, gone);

        Assert.Equal("npx", JsonNode.Parse(_home.ReadFile(Path.Combine(project, ".mcp.json")))!["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Contains("[mcp_servers.files]", _home.ReadFile(Path.Combine(project, ".codex", "config.toml")));
        Assert.Equal(3, _mcp.WrittenFiles(ClientKinds.ClaudeCode, "files").Count);
        Assert.Equal(["files"], _mcp.WrittenNames(ClientKinds.ClaudeCode, project));
        Assert.Equal(ClientMcpState.Applied, _mcp.Inspect(ClientKinds.ClaudeCode, "files", project).State);

        // a project file the user edited stays a drift for that file alone
        Directory.Delete(gone, recursive: true);
        _mcp.RemoveAll();

        Assert.False(File.Exists(Path.Combine(project, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(project, ".codex", "config.toml")));
        Assert.False(File.Exists(_home.File(".claude.json")) && _home.ReadFile(_home.File(".claude.json")).Contains("files"));
        Assert.Empty(_mcp.WrittenFiles(ClientKinds.ClaudeCode, "files"));
    }

    [Fact]
    public void Project_Files_Follow_Each_Clients_Own_Convention_And_Clients_Without_One_Refuse()
    {
        var project = _home.File("p");
        Assert.Equal(Path.Combine(project, ".gemini", "settings.json"), McpClientTargets.File(_home.Env, ClientKinds.GeminiCli, project));
        Assert.Equal(Path.Combine(project, "opencode.json"), McpClientTargets.File(_home.Env, ClientKinds.OpenCode, project));
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "opencode.jsonc"), "{}");
        Assert.Equal(Path.Combine(project, "opencode.jsonc"), McpClientTargets.File(_home.Env, ClientKinds.OpenCode, project));
        Assert.False(McpClientTargets.SupportsProject(ClientKinds.Crush));
        Assert.Throws<ArgumentException>(() => McpClientTargets.File(_home.Env, ClientKinds.Crush, project));
    }
}
