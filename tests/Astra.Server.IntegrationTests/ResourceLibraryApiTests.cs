using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Library;
using System.Net.Http.Json;
using Astra.Core;
using Astra.Server.Hosting;
using Astra.Server.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The rest of the resource library's admin API: instructions, sub-agents, commands, hooks, environment variables, projects, Skill projects, router, market.</summary>
public sealed class ResourceLibraryApiTests
{
    private static string Home(TestHost host, params string[] parts) => Path.Combine([host.ClientHome, .. parts]);

    [Fact]
    public async Task An_instruction_is_written_as_a_marked_section_listed_and_taken_out_again()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(Home(host, ".claude"));
        File.WriteAllText(Home(host, ".claude", "CLAUDE.md"), "mine\n");

        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/library/instructions",
            new { name = "style", body = "Use tabs.", targets = new[] { new { client = "claude-code", project = "" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", created!["targets"]![0]!["state"]!.GetValue<string>());
        Assert.Contains("<!-- astra:begin style -->", File.ReadAllText(Home(host, ".claude", "CLAUDE.md")));

        var list = await host.GetJsonAsync("/api/library/instructions");
        Assert.True(list["canWrite"]!.GetValue<bool>());
        Assert.Equal("style", list["items"]![0]!["name"]!.GetValue<string>());
        Assert.Contains(list["clients"]!.AsArray(), c => c!["kind"]!.GetValue<string>() == "claude-code" && c["detected"]!.GetValue<bool>());

        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/library/instructions/{created["id"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal("mine\n", File.ReadAllText(Home(host, ".claude", "CLAUDE.md")));
    }

    [Fact]
    public async Task A_drifted_agent_file_is_reported_and_only_forced_over()
    {
        await using var host = await TestHost.StartAsync();
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/library/agents",
            new { name = "reviewer", description = "Reviews", body = "You review.", targets = new[] { new { client = "claude-code", project = "" } } });
        var file = Home(host, ".claude", "agents", "reviewer.md");
        Assert.True(File.Exists(file));
        File.AppendAllText(file, "extra\n");

        var (status, updated) = await host.SendAsync(HttpMethod.Put, $"/api/library/agents/{created!["id"]!.GetValue<string>()}", new { name = "reviewer", body = "You review hard." });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("drifted", updated!["targets"]![0]!["state"]!.GetValue<string>());
        Assert.NotNull(updated["lastError"]!["claude-code"]);
        Assert.Contains("extra", File.ReadAllText(file));

        (status, updated) = await host.SendAsync(HttpMethod.Post, $"/api/library/agents/{created["id"]!.GetValue<string>()}/apply?force=true");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", updated!["targets"]![0]!["state"]!.GetValue<string>());
        Assert.Contains("You review hard.", File.ReadAllText(file));
    }

    [Fact]
    public async Task Hooks_and_environment_variables_go_through_the_clients_settings()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(Home(host, ".claude"));
        File.WriteAllText(Home(host, ".claude", "settings.json"), """{"theme":"dark"}""");

        var (status, hook) = await host.SendAsync(HttpMethod.Post, "/api/library/hooks",
            new { name = "notify", body = "say done", @event = "turn-end", timeout = 5, targets = new[] { new { client = "claude-code", project = "" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("turn-end", hook!["event"]!.GetValue<string>());
        (status, var env) = await host.SendAsync(HttpMethod.Post, "/api/library/env",
            new { name = "MY_FLAG", body = "on", targets = new[] { new { client = "claude-code", project = "" } } });
        Assert.Equal(HttpStatusCode.OK, status);

        var settings = JsonNode.Parse(File.ReadAllText(Home(host, ".claude", "settings.json")))!;
        Assert.Equal("say done", settings["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("on", settings["env"]!["MY_FLAG"]!.GetValue<string>());
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());

        await host.SendAsync(HttpMethod.Delete, $"/api/library/hooks/{hook["id"]!.GetValue<string>()}");
        await host.SendAsync(HttpMethod.Delete, $"/api/library/env/{env!["id"]!.GetValue<string>()}");
        Assert.Equal("""{"theme":"dark"}""", JsonNode.Parse(File.ReadAllText(Home(host, ".claude", "settings.json")))!.ToJsonString());
    }

    [Fact]
    public async Task Validation_errors_use_the_admin_error_shape_and_a_project_must_exist()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/library/instructions", new { name = "Bad Name", body = "x", targets = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(body!["error"]);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/library/projects", new { path = Path.Combine(host.Root, "nowhere") });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/library/projects", new { path = "relative" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/instructions/none", new { name = "x", body = "y" });
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task A_project_scoped_item_goes_into_the_project_and_a_deleted_project_is_reported_not_failed()
    {
        await using var host = await TestHost.StartAsync();
        var project = Path.Combine(host.Root, "work", "app");
        Directory.CreateDirectory(project);
        var (status, added) = await host.SendAsync(HttpMethod.Post, "/api/library/projects", new { path = project });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("app", added!["name"]!.GetValue<string>());

        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/library/instructions",
            new { name = "rule", body = "Project rule", targets = new[] { new { client = "codex", project } } });
        Assert.Contains("Project rule", File.ReadAllText(Path.Combine(project, "AGENTS.md")));
        Assert.False(File.Exists(Home(host, ".codex", "AGENTS.md")));

        // the project is still in use: it cannot be forgotten
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/library/projects?path={Uri.EscapeDataString(project)}");
        Assert.Equal(HttpStatusCode.Conflict, status);

        Directory.Delete(project, recursive: true);
        var list = await host.GetJsonAsync("/api/library/instructions");
        Assert.Equal("project-missing", list["items"]![0]!["targets"]![0]!["state"]!.GetValue<string>());
        Assert.False(list["projects"]![0]!["exists"]!.GetValue<bool>());

        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/library/instructions/{created!["id"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/library/projects?path={Uri.EscapeDataString(project)}");
        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    [Fact]
    public async Task An_own_mcp_server_can_also_be_written_into_a_projects_file()
    {
        await using var host = await TestHost.StartAsync();
        var project = Path.Combine(host.Root, "work", "app");
        Directory.CreateDirectory(project);
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/local",
            new { name = "files", command = "npx", args = new[] { "-y", "@mcp/files" }, clients = new[] { "claude-code", "droid" }, projects = new[] { project } });
        Assert.Equal(HttpStatusCode.OK, status);
        var inProject = JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".mcp.json")))!;
        Assert.Equal("npx", inProject["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Equal("npx", JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".factory", "mcp.json")))!["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Equal("npx", JsonNode.Parse(File.ReadAllText(Home(host, ".factory", "mcp.json")))!["mcpServers"]!["files"]!["command"]!.GetValue<string>());
        Assert.Contains(created!["placements"]!.AsArray(), p => p!["project"]!.GetValue<string>() == project && p["state"]!.GetValue<string>() == "applied");

        // dropping the project from the list takes the entry out of its file, the user-wide one stays
        var id = created["id"]!.GetValue<string>();
        (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/mcp/local/{id}", new { name = "files", projects = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(File.Exists(Path.Combine(project, ".mcp.json")));
        Assert.True(File.Exists(Home(host, ".claude.json")));

        // a deleted project is reported, and deleting the server does not fail on it
        await host.SendAsync(HttpMethod.Put, $"/api/mcp/local/{id}", new { name = "files", projects = new[] { project } });
        Directory.Delete(project, recursive: true);
        var (_, listed) = await host.SendAsync(HttpMethod.Get, "/api/mcp/local");
        Assert.Contains(listed!.AsArray()[0]!["placements"]!.AsArray(), p => p!["state"]!.GetValue<string>() == "project-missing");
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/mcp/local/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    [Fact]
    public async Task A_skill_can_be_placed_into_a_project_and_the_router_lists_the_ones_a_client_has_no_link_for()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(Home(host, ".claude"));
        var source = Path.Combine(host.Root, "source", "pdf");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: pdf\ndescription: Reads PDFs\n---\nbody\n");
        var (_, probe) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/probe", new { kind = "local", source });
        await host.SendAsync(HttpMethod.Post, "/api/library/skills/install", new { probeId = probe!["probeId"]!.GetValue<string>(), names = new[] { "pdf" }, clients = Array.Empty<string>() });

        var project = Path.Combine(host.Root, "work", "app");
        Directory.CreateDirectory(project);
        var state = await host.GetJsonAsync("/api/library/skills");
        Assert.Contains(state["projectDirs"]!.AsArray(), d => d!["dir"]!.GetValue<string>() == Path.Combine(".claude", "skills"));
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/projects", new { project, dir = Path.Combine(".claude", "skills"), on = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(FsLinks.IsLinkTo(Path.Combine(project, ".claude", "skills", "pdf"), Path.Combine(host.Paths.LibrarySkillsDir, "pdf")));
        state = await host.GetJsonAsync("/api/library/skills");
        Assert.Equal(project, state["skills"]![0]!["placements"]![0]!["project"]!.GetValue<string>());

        // router: on for Claude Code, the Skill it has no link for is listed
        (status, var router) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/router/claude-code", new { on = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(router!.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "claude-code")!["enabled"]!.GetValue<bool>());
        var entry = Home(host, ".claude", "skills", "astra-skills");
        Assert.Contains("pdf", File.ReadAllText(Path.Combine(entry, "SKILL.md")));
        // a client reading the shared directory is refused
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/router/codex", new { on = true });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/router/claude-code", new { on = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Directory.Exists(entry));
        Assert.True(Directory.Exists(Path.Combine(host.Paths.LibrarySkillsDir, "pdf")));

        // switching the project placement off removes only its link
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/projects", new { project, dir = Path.Combine(".claude", "skills"), on = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Directory.Exists(Path.Combine(project, ".claude", "skills", "pdf")));
    }

    [Fact]
    public async Task The_market_registry_is_a_setting_with_skills_sh_as_the_default_and_an_unreachable_one_is_reported()
    {
        await using var host = await TestHost.StartAsync();
        var source = await host.GetJsonAsync("/api/library/skills/market/source");
        Assert.Equal("https://www.skills.sh", source["url"]!.GetValue<string>());
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/market/source", new { url = "ftp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, source) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/market/source", new { url = "http://127.0.0.1:1" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("http://127.0.0.1:1", source!["url"]!.GetValue<string>());
        var result = await host.GetJsonAsync("/api/library/skills/market?q=pdf");
        Assert.NotNull(result["error"]);
        Assert.Empty(result["skills"]!.AsArray());
        (status, source) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/market/source", new { url = "" });
        Assert.Equal("https://www.skills.sh", source!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Writes_are_refused_off_this_machine()
    {
        await using var host = await TestHost.StartAsync();
        host.App.Services.GetRequiredService<ServerOptions>().AdminPasswordHash = PasswordHasher.Hash("correct-horse");
        await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { allowAdmin = true });

        // a signed-in admin session that came through the tunnel: it can read the library, but not write client files
        var login = await host.Client.SendAsync(Remote(HttpMethod.Post, "/api/auth/login", new { password = "correct-horse" }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        async Task<HttpStatusCode> Send(HttpMethod method, string url, object? body = null)
        {
            using var req = Remote(method, url, body);
            req.Headers.Add("Cookie", cookie);
            return (await host.Client.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Get, "/api/library/instructions"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/library/instructions",
            new { name = "style", body = "x", targets = new[] { new { client = "claude-code", project = "" } } }));
        Assert.False(File.Exists(Home(host, ".claude", "CLAUDE.md")));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Put, "/api/library/skills/router/claude-code", new { on = true }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Put, "/api/library/skills/pdf/projects", new { project = "/x", dir = ".claude/skills", on = true }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/library/projects", new { path = "/x" }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Put, "/api/library/skills/market/source", new { url = "http://example.com" }));
    }

    private static HttpRequestMessage Remote(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cf-Connecting-IP", "203.0.113.7");
        req.Headers.Add("Cf-Ray", "8a1b2c3d4e5f-LAX");
        req.Headers.Add("Cf-Visitor", "{\"scheme\":\"https\"}");
        if (body is not null) req.Content = JsonContent.Create(body, options: TestHost.JsonOptions);
        return req;
    }
}
