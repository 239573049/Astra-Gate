using System.Net;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The MCP gateway's admin API (<c>/api/mcp</c>, <c>/api/tokens</c> binding): validation, secrets, settings and "test connection".</summary>
public class McpAdminApiTests
{
    [Fact]
    public async Task A_Slug_Is_Derived_From_The_Name_And_Made_Unique()
    {
        await using var host = await TestHost.StartAsync();
        var (status, first) = await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", new { name = "My Server!", url = "https://mcp.example.com/mcp" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("my-server", first!["slug"]!.GetValue<string>());
        Assert.Equal("http", first["transport"]!.GetValue<string>());
        Assert.Equal("none", first["authType"]!.GetValue<string>());
        Assert.Equal("unknown", first["status"]!.GetValue<string>());

        var (_, second) = await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", new { name = "My Server", url = "https://mcp.example.com/mcp" });
        Assert.Equal("my-server-2", second!["slug"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", new { name = "X", slug = "my-server", url = "https://a.example/mcp" })).Status);
        // The slug prefixes tool names, and the first "__" must end it: no underscore, no capitals.
        foreach (var bad in new[] { "Bad_Slug", "UPPER", "-lead", "a--b", new string('a', 33) })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", new { name = "X", slug = bad, url = "https://a.example/mcp" })).Status);
    }

    [Theory]
    [InlineData("""{"name":" ","url":"https://a.example/mcp"}""")]
    [InlineData("""{"name":"X","url":"not a url"}""")]
    [InlineData("""{"name":"X","url":"ftp://a.example/mcp"}""")]
    [InlineData("""{"name":"X","url":"https://user:pw@a.example/mcp"}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp#frag"}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","transport":"stdio"}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"basic"}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"header"}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"header","headers":{"Host":"evil"}}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"header","headers":{"Mcp-Session-Id":"1"}}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"header","headers":{"bad name":"v"}}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","authType":"header","headers":{"X-Key":"a\r\nInjected: 1"}}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","timeoutSeconds":0}""")]
    [InlineData("""{"name":"X","url":"https://a.example/mcp","timeoutSeconds":601}""")]
    public async Task A_Server_That_Cannot_Work_Is_Refused(string body)
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/servers") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await host.GetJsonAsync("/api/mcp/servers")).AsArray());
    }

    [Fact]
    public async Task Header_Values_Are_Stored_Protected_And_Never_Returned()
    {
        await using var host = await TestHost.StartAsync();
        var id = await McpFixture.CreateServerAsync(host, "Keyed", "https://a.example/mcp",
            new { authType = "header", headers = new Dictionary<string, string> { ["X-Api-Key"] = "s3cret-value", ["Authorization"] = "Bearer abc123" } });

        var server = await host.GetJsonAsync($"/api/mcp/servers/{id}");
        Assert.Equal(["Authorization", "X-Api-Key"], server["headerNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        var everything = server.ToJsonString() + (await host.GetJsonAsync("/api/mcp/servers")).ToJsonString();
        Assert.DoesNotContain("s3cret-value", everything);
        Assert.DoesNotContain("abc123", everything);

        var row = (await host.Db.Mcp.GetAsync(id))!;
        Assert.DoesNotContain("s3cret-value", row.HeadersEnc);
        var plain = JsonNode.Parse(host.App.Services.GetRequiredService<ISecretProtector>().Unprotect(row.HeadersEnc!))!;
        Assert.Equal("s3cret-value", plain["X-Api-Key"]!.GetValue<string>());

        // Replacing the set: a name sent as null keeps the value already stored, a new name needs one.
        var (status, updated) = await host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{id}",
            new { headers = new Dictionary<string, string?> { ["X-Api-Key"] = null, ["X-Other"] = "v2" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["X-Api-Key", "X-Other"], updated!["headerNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        var kept = JsonNode.Parse(host.App.Services.GetRequiredService<ISecretProtector>().Unprotect((await host.Db.Mcp.GetAsync(id))!.HeadersEnc!))!;
        Assert.Equal("s3cret-value", kept["X-Api-Key"]!.GetValue<string>());
        Assert.Null(kept["Authorization"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{id}",
            new { headers = new Dictionary<string, string?> { ["X-New"] = null } })).Status);

        // Other auth types carry no headers.
        var (_, none) = await host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{id}", new { authType = "none" });
        Assert.Empty(none!["headerNames"]!.AsArray());
        Assert.Null((await host.Db.Mcp.GetAsync(id))!.HeadersEnc);
    }

    [Fact]
    public async Task Binding_Servers_To_A_Token_Checks_That_They_Exist()
    {
        await using var host = await TestHost.StartAsync();
        var a = await McpFixture.CreateServerAsync(host, "A", "https://a.example/mcp");
        var b = await McpFixture.CreateServerAsync(host, "B", "https://b.example/mcp");
        var (_, token) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "T" });
        var id = token!["id"]!.GetValue<string>();

        var (status, bound) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { mcpServers = new[] { a, b, a } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([a, b], bound!["mcpServers"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { mcpServers = new[] { "nope" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { mcpServers = "a" })).Status);
        Assert.Equal(2, (await host.GetJsonAsync($"/api/tokens/{id}"))["mcpServers"]!.AsArray().Count); // a refused change changed nothing

        var servers = (await host.GetJsonAsync("/api/mcp/servers")).AsArray();
        Assert.All(servers, s => Assert.Equal(1, s!["boundTokens"]!.GetValue<int>()));

        var (_, cleared) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new Dictionary<string, object?> { ["mcpServers"] = null });
        Assert.Empty(cleared!["mcpServers"]!.AsArray());
    }

    [Fact]
    public async Task The_Gateway_Settings_Are_Validated_And_Origins_Are_Normalized()
    {
        await using var host = await TestHost.StartAsync();
        var initial = await host.GetJsonAsync("/api/mcp/settings");
        Assert.False(initial["enabled"]!.GetValue<bool>()); // a fresh install exposes nothing new
        Assert.EndsWith("/mcp", initial["endpoint"]!.GetValue<string>());

        var (status, updated) = await host.SendAsync(HttpMethod.Put, "/api/mcp/settings",
            new { enabled = true, allowedOrigins = new[] { "https://App.Example/", "https://app.example", "http://localhost:3000" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(updated!["enabled"]!.GetValue<bool>());
        Assert.Equal(["https://app.example", "http://localhost:3000"], updated["allowedOrigins"]!.AsArray().Select(o => o!.GetValue<string>()));

        foreach (var bad in new object[] { new { allowedOrigins = new[] { "https://a.example/path" } }, new { allowedOrigins = new[] { "*" } }, new { allowedOrigins = "x" }, new { enabled = "yes" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/mcp/settings", bad)).Status);
        Assert.True((await host.GetJsonAsync("/api/mcp/settings"))["enabled"]!.GetValue<bool>()); // the refused ones changed nothing
    }

    [Fact]
    public async Task Test_Connection_Finds_Out_Which_Era_The_Upstream_Speaks()
    {
        await using var f = await McpFixture.StartAsync();
        var (_, modern) = await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{f.ModernId}/test");
        Assert.True(modern!["ok"]!.GetValue<bool>());
        Assert.Equal("2026-07-28", modern["protocolVersion"]!.GetValue<string>());
        Assert.Equal(["echo", "fail"], modern["tools"]!.AsArray().Select(t => t!.GetValue<string>()).Order());

        var (_, legacy) = await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{f.LegacyId}/test");
        Assert.True(legacy!["ok"]!.GetValue<bool>());
        Assert.Equal("2025-11-25", legacy["protocolVersion"]!.GetValue<string>());

        // The outcome is the server's status too.
        var server = await f.Host.GetJsonAsync($"/api/mcp/servers/{f.LegacyId}");
        Assert.Equal("ok", server["status"]!.GetValue<string>());
        Assert.Equal("2025-11-25", server["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(server["checkedAt"]);

        var goneId = await McpFixture.CreateServerAsync(f.Host, "Gone", "http://gone.test/private/mcp");
        var (_, gone) = await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{goneId}/test");
        Assert.False(gone!["ok"]!.GetValue<bool>());
        Assert.Equal("unreachable", gone["status"]!.GetValue<string>());
        Assert.DoesNotContain("gone.test", gone.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await f.Host.SendAsync(HttpMethod.Post, "/api/mcp/servers/nope/test")).Status);
    }

    [Fact]
    public async Task Editing_A_Server_Reconnects_It()
    {
        await using var f = await McpFixture.StartAsync();
        Assert.Equal(["echo", "fail"], await f.ToolNamesAsync("/mcp/modern"));
        // Point "modern" at the legacy upstream: the next request must use the new address, not the old connection.
        await f.Host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{f.ModernId}", new { url = f.Legacy.Url });
        var (_, test) = await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{f.ModernId}/test");
        Assert.Equal("2025-11-25", test!["protocolVersion"]!.GetValue<string>());
    }
}
