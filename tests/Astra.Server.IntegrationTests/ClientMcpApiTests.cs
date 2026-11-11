using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;

namespace Astra.Server.IntegrationTests;

/// <summary>The MCP gateway entry in Claude Code and Codex (<c>/api/clients/{kind}/mcp</c>, issue #34).</summary>
public sealed class ClientMcpApiTests
{
    private static async Task<(string TokenId, string Key)> TokenWithMcpAsync(TestHost host, string name = "Work", bool bind = true)
    {
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created!["id"]!.GetValue<string>();
        if (bind)
        {
            var server = await McpFixture.CreateServerAsync(host, "Docs " + name, "https://docs.example.com/mcp");
            (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { mcpServers = new[] { server } });
            Assert.Equal(HttpStatusCode.OK, status);
        }
        var (_, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{id}/reveal");
        return (id, secret!["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_Writes_The_Tokens_Key_Into_The_Client_And_Never_Returns_It()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, key) = await TokenWithMcpAsync(host);

        var (status, before) = await host.SendAsync(HttpMethod.Get, "/api/clients/claude-code/mcp");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("missing", before!["state"]!.GetValue<string>());
        Assert.False(before["gatewayEnabled"]!.GetValue<bool>()); // off by default: the UI warns

        (status, var applied) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", applied!["state"]!.GetValue<string>());
        Assert.Equal(tokenId, applied["tokenId"]!.GetValue<string>());
        Assert.Equal("Work", applied["tokenName"]!.GetValue<string>());
        Assert.DoesNotContain(key, applied.ToJsonString());

        var file = await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".claude.json"));
        Assert.Contains("Bearer " + key, file);
        Assert.EndsWith("/mcp", JsonNode.Parse(file)!["mcpServers"]!["astra"]!["url"]!.GetValue<string>());

        (status, var removed) = await host.SendAsync(HttpMethod.Delete, "/api/clients/claude-code/mcp");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("missing", removed!["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".claude.json")));
    }

    [Fact]
    public async Task Codex_Gets_A_Table_And_The_List_Shows_Both_Clients()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, key) = await TokenWithMcpAsync(host);

        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/mcp", new { tokenId });
        Assert.Equal(HttpStatusCode.OK, status);
        var text = await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".codex", "config.toml"));
        Assert.Contains("[mcp_servers.astra]", text);
        Assert.Contains("Bearer " + key, text);

        var list = (await host.GetJsonAsync("/api/clients/mcp")).AsArray();
        Assert.Equal(["claude-code", "codex"], list.Select(i => i!["kind"]!.GetValue<string>()).ToList());
        Assert.Equal(["missing", "applied"], list.Select(i => i!["state"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task A_Token_Without_MCP_Servers_A_Disabled_One_And_An_Unknown_Client_Are_Refused()
    {
        await using var host = await TestHost.StartAsync();
        var (unbound, _) = await TokenWithMcpAsync(host, "Plain", bind: false);
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId = unbound });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId = "missing" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Get, "/api/clients/gemini-cli/mcp");
        Assert.Equal(HttpStatusCode.NotFound, status);

        var (bound, _) = await TokenWithMcpAsync(host, "Bound");
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{bound}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId = bound });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".claude.json")));
    }

    [Fact]
    public async Task Offline_Restore_All_Removes_The_Entries_Astra_Wrote_And_Leaves_The_Users_Own()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await TokenWithMcpAsync(host);
        Directory.CreateDirectory(Path.Combine(host.ClientHome, ".codex"));
        var codex = Path.Combine(host.ClientHome, ".codex", "config.toml");
        await File.WriteAllTextAsync(codex, "[mcp_servers.mine]\ncommand = \"npx\"\n");
        await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId });
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/mcp", new { tokenId });

        var result = await Commands.RestoreAllAsync(new AstraPaths(host.Root), false, new ClientEnvironment(host.ClientHome, "osx"));

        Assert.Equal(0, result);
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".claude.json")));
        var text = await File.ReadAllTextAsync(codex);
        Assert.DoesNotContain("astra", text);
        Assert.Contains("[mcp_servers.mine]", text);
    }

    [Fact]
    public async Task An_Entry_The_User_Wrote_Is_A_Conflict_And_Stays()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await TokenWithMcpAsync(host);
        Directory.CreateDirectory(host.ClientHome);
        var file = Path.Combine(host.ClientHome, ".claude.json");
        await File.WriteAllTextAsync(file, """{"mcpServers":{"astra":{"type":"http","url":"https://mine.example/mcp"}}}""");

        var (_, status) = await host.SendAsync(HttpMethod.Get, "/api/clients/claude-code/mcp");
        Assert.Equal("conflict", status!["state"]!.GetValue<string>());
        var (code, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/mcp", new { tokenId, force = true });
        Assert.Equal(HttpStatusCode.Conflict, code);
        (code, _) = await host.SendAsync(HttpMethod.Delete, "/api/clients/claude-code/mcp?force=true");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Contains("https://mine.example/mcp", await File.ReadAllTextAsync(file));
    }
}
