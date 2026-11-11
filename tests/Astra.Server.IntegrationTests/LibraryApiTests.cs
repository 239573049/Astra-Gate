using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Library;

namespace Astra.Server.IntegrationTests;

/// <summary>The Skill library's admin API (<c>/api/library/skills</c>).</summary>
public sealed class LibraryApiTests
{
    private static string MakeSource(TestHost host, string name)
    {
        var dir = Path.Combine(host.Root, "source", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: Does {name}\n---\nbody\n");
        return dir;
    }

    private static async Task<string> InstallAsync(TestHost host, string name, params string[] clients)
    {
        var (status, probe) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/probe", new { kind = "local", source = MakeSource(host, name) });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("new", probe!["skills"]![0]!["status"]!.GetValue<string>());
        (status, var done) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/install",
            new { probeId = probe["probeId"]!.GetValue<string>(), names = new[] { name }, clients });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(done![0]!["ok"]!.GetValue<bool>(), done.ToJsonString());
        return name;
    }

    [Fact]
    public async Task An_empty_library_lists_the_clients_and_allows_creating_on_this_machine()
    {
        await using var host = await TestHost.StartAsync();
        var state = await host.GetJsonAsync("/api/library/skills");
        Assert.True(state["canCreate"]!.GetValue<bool>());
        Assert.Empty(state["skills"]!.AsArray());
        Assert.Contains(state["targets"]!.AsArray(), t => t!["kind"]!.GetValue<string>() == "claude-code");
    }

    [Fact]
    public async Task Install_then_switch_a_client_creates_and_removes_only_the_link()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(host.ClientHome, ".claude"));
        await InstallAsync(host, "pdf");

        var (status, result) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/clients/claude-code", new { on = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["done"]!.GetValue<bool>());
        var link = Path.Combine(host.ClientHome, ".claude", "skills", "pdf");
        Assert.True(FsLinks.IsLinkTo(link, Path.Combine(host.Paths.LibrarySkillsDir, "pdf")));

        var state = await host.GetJsonAsync("/api/library/skills");
        var skill = state["skills"]![0]!;
        Assert.Equal("Does pdf", skill["description"]!.GetValue<string>());
        Assert.Equal("own", skill["clients"]!.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "claude-code")!["state"]!.GetValue<string>());

        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/clients/claude-code", new { on = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(host.Paths.LibrarySkillsDir, "pdf", "SKILL.md")));
    }

    [Fact]
    public async Task Shared_switch_off_needs_confirmation()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var dir in new[] { ".codex", ".gemini" }) Directory.CreateDirectory(Path.Combine(host.ClientHome, dir));
        await InstallAsync(host, "pdf", "codex");

        var (_, first) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/clients/codex", new { on = false });
        Assert.True(first!["needsConfirm"]!.GetValue<bool>());
        Assert.Contains(first["affected"]!.AsArray(), a => a!.GetValue<string>() == "gemini-cli");

        var (_, second) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/pdf/clients/codex", new { on = false, confirm = true });
        Assert.True(second!["done"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Errors_use_the_admin_error_shape()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/nope/clients/claude-code", new { on = true });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.NotNull(body!["error"]);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/library/skills/..%2Fx/clients/claude-code", new { on = true });
        Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/probe", new { kind = "local", source = "relative/path" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/install", new { probeId = "gone", names = new[] { "x" }, clients = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Migrate_preview_and_migrate_move_a_found_skill_into_the_library()
    {
        await using var host = await TestHost.StartAsync();
        var original = Path.Combine(host.ClientHome, ".claude", "skills", "review");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "SKILL.md"), "---\nname: review\ndescription: r\n---\n");

        var (status, preview) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/migrate/preview", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("review", preview![0]!["name"]!.GetValue<string>());

        (status, var done) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/migrate", new { items = new[] { new { name = "review" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(done![0]!["ok"]!.GetValue<bool>(), done.ToJsonString());
        Assert.True(FsLinks.IsLinkTo(original, Path.Combine(host.Paths.LibrarySkillsDir, "review")));
    }

    [Fact]
    public async Task Delete_and_return_work_over_the_api()
    {
        await using var host = await TestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(host.ClientHome, ".claude"));
        await InstallAsync(host, "a", "claude-code");
        await InstallAsync(host, "b", "claude-code");

        var (status, back) = await host.SendAsync(HttpMethod.Post, "/api/library/skills/a/return");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(back![0]!["ok"]!.GetValue<bool>());
        Assert.False(FsLinks.IsLink(Path.Combine(host.ClientHome, ".claude", "skills", "a")));
        Assert.True(File.Exists(Path.Combine(host.ClientHome, ".claude", "skills", "a", "SKILL.md")));

        (status, _) = await host.SendAsync(HttpMethod.Delete, "/api/library/skills/b");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.False(Directory.Exists(Path.Combine(host.ClientHome, ".claude", "skills", "b")));
    }
}
