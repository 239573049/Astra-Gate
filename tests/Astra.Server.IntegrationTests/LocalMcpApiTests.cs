using System.Net;
using System.Text.Json.Nodes;

namespace Astra.Server.IntegrationTests;

/// <summary>Local (stdio) MCP servers written into clients (<c>/api/mcp/local</c>, issue #34).</summary>
public sealed class LocalMcpApiTests
{
    private static object Body(string name = "files", string[]? clients = null, bool? enabled = null) => new
    {
        name, command = "npx", args = new[] { "-y", "@mcp/files" }, env = new Dictionary<string, string> { ["API_KEY"] = "sk-secret-123" },
        clients = clients ?? ["claude-code", "codex"], enabled,
    };

    [Fact]
    public async Task Create_Writes_The_Chosen_Clients_And_Never_Returns_Env_Values()
    {
        await using var host = await TestHost.StartAsync();

        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body(clients: ["claude-code"]));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("sk-secret-123", created!.ToJsonString());
        Assert.Equal(["API_KEY"], created["envNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        var placements = created["placements"]!.AsArray();
        Assert.Equal("applied", placements[0]!["state"]!.GetValue<string>());
        Assert.Equal("missing", placements[1]!["state"]!.GetValue<string>());

        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".claude.json")))!;
        Assert.Equal("sk-secret-123", file["mcpServers"]!["files"]!["env"]!["API_KEY"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".codex", "config.toml")));
    }

    [Fact]
    public async Task Update_Moves_Between_Clients_Disables_And_Keeps_Env_When_Omitted()
    {
        await using var host = await TestHost.StartAsync();
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body(clients: ["claude-code"]));
        var id = created!["id"]!.GetValue<string>();

        var (status, moved) = await host.SendAsync(HttpMethod.Put, $"/api/mcp/local/{id}",
            new { name = "files", command = "uvx", clients = new[] { "codex" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["API_KEY"], moved!["envNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        Assert.Equal(["missing", "applied"], moved["placements"]!.AsArray().Take(2).Select(p => p!["state"]!.GetValue<string>()).ToList());
        var toml = await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".codex", "config.toml"));
        Assert.Contains("uvx", toml);
        Assert.Contains("sk-secret-123", toml);

        (_, var off) = await host.SendAsync(HttpMethod.Put, $"/api/mcp/local/{id}", new { name = "files", command = "uvx", enabled = false });
        Assert.All(off!["placements"]!.AsArray(), p => Assert.Equal("missing", p!["state"]!.GetValue<string>()));
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".codex", "config.toml")));
    }

    [Fact]
    public async Task Rename_Moves_The_Entry_And_Delete_Removes_It()
    {
        await using var host = await TestHost.StartAsync();
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body(clients: ["claude-code"]));
        var id = created!["id"]!.GetValue<string>();

        await host.SendAsync(HttpMethod.Put, $"/api/mcp/local/{id}", new { name = "docs", command = "npx" });
        var claude = Path.Combine(host.ClientHome, ".claude.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(claude))!["mcpServers"]!.AsObject();
        Assert.True(json.ContainsKey("docs"));
        Assert.False(json.ContainsKey("files"));

        var (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/mcp/local/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.False(File.Exists(claude));
        Assert.Empty((await host.GetJsonAsync("/api/mcp/local")).AsArray());
    }

    [Fact]
    public async Task A_Foreign_Entry_Is_Reported_As_A_Conflict_And_Left_Alone()
    {
        await using var host = await TestHost.StartAsync();
        var claude = Path.Combine(host.ClientHome, ".claude.json");
        Directory.CreateDirectory(host.ClientHome);
        await File.WriteAllTextAsync(claude, """{"mcpServers":{"files":{"type":"stdio","command":"mine"}}}""");

        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body(clients: ["claude-code"]));
        Assert.Equal(HttpStatusCode.OK, status);
        var placement = created!["placements"]![0]!;
        Assert.Equal("conflict", placement["state"]!.GetValue<string>());
        Assert.Contains("did not write", placement["error"]!.GetValue<string>());
        Assert.Contains("\"mine\"", await File.ReadAllTextAsync(claude));
    }

    [Theory]
    [InlineData("astra")]
    [InlineData("Bad Name")]
    [InlineData("")]
    public async Task Bad_Names_And_Duplicates_Are_Refused(string name)
    {
        await using var host = await TestHost.StartAsync();
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body(name));
        Assert.Equal(HttpStatusCode.BadRequest, status);

        await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", Body());
        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [Fact]
    public async Task A_Remote_Server_Goes_Straight_Into_The_Client_And_Its_Headers_Stay_Secret()
    {
        await using var host = await TestHost.StartAsync();
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", new
        {
            name = "docs", transport = "http", url = "https://docs.example.com/mcp",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer h-secret" }, clients = new[] { "gemini-cli", "opencode" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("h-secret", created!.ToJsonString());
        Assert.Equal("http", created["transport"]!.GetValue<string>());
        Assert.Equal(["Authorization"], created["headerNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());

        var gemini = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".gemini", "settings.json")))!;
        Assert.Equal("https://docs.example.com/mcp", gemini["mcpServers"]!["docs"]!["httpUrl"]!.GetValue<string>());
        var opencode = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json")))!;
        Assert.Equal("remote", opencode["mcp"]!["docs"]!["type"]!.GetValue<string>());

        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local", new { name = "bad", transport = "http", url = "ftp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Import_Takes_Over_What_The_Clients_Already_Have()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(host.ClientHome);
        await File.WriteAllTextAsync(Path.Combine(host.ClientHome, ".claude.json"),
            """{"mcpServers":{"files":{"type":"stdio","command":"npx","args":["-y","srv"],"env":{"K":"secret-v"}},"old":{"type":"sse","url":"https://o.example/sse"}}}""");
        Directory.CreateDirectory(Path.Combine(host.ClientHome, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(host.ClientHome, ".codex", "config.toml"),
            "[mcp_servers.files]\ncommand = \"npx\"\nargs = [\"-y\", \"srv\"]\nenv = { K = \"secret-v\" }\n");

        var candidates = (await host.GetJsonAsync("/api/mcp/local/import")).AsArray();
        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain("secret-v", candidates.ToJsonString());
        Assert.False(candidates.Single(c => c!["name"]!.GetValue<string>() == "old")!["importable"]!.GetValue<bool>());

        var (status, result) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local/import", new
        {
            items = new[] { new { kind = "claude-code", name = "files" }, new { kind = "codex", name = "files" }, new { kind = "claude-code", name = "old" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var imported = result!["imported"]!.AsArray();
        Assert.Single(imported);
        Assert.Equal(["claude-code", "codex"], imported[0]!["clients"]!.AsArray().Select(c => c!.GetValue<string>()).Order().ToList());
        Assert.Equal(["applied", "applied"], imported[0]!["placements"]!.AsArray().Where(p => p!["wanted"]!.GetValue<bool>()).Select(p => p!["state"]!.GetValue<string>()).ToList());
        Assert.Equal(["K"], imported[0]!["envNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        Assert.Single(result["skipped"]!.AsArray()); // the sse one

        // nothing left to import for the two taken over; deleting the definition puts the user's entries back
        Assert.DoesNotContain((await host.GetJsonAsync("/api/mcp/local/import")).AsArray(), c => c!["name"]!.GetValue<string>() == "files" && !c["taken"]!.GetValue<bool>());
        var id = imported[0]!["id"]!.GetValue<string>();
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/mcp/local/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Contains("secret-v", await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".claude.json")));
    }
}
