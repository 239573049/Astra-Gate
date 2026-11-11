using Astra.Clients.Config;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Library;
using Xunit;

namespace Astra.Clients.Tests;

public sealed class InMemoryLedger : ILibraryLedger
{
    public Dictionary<string, LibrarySkillRecord> Skills { get; } = [];
    public Dictionary<string, LibraryPlacementRecord> Placements { get; } = [];

    public Task<IReadOnlyList<LibrarySkillRecord>> ListSkillsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibrarySkillRecord>>(Skills.Values.OrderBy(s => s.Name).ToList());
    public Task<LibrarySkillRecord?> GetSkillAsync(string name, CancellationToken ct = default) => Task.FromResult(Skills.GetValueOrDefault(name));
    public Task UpsertSkillAsync(LibrarySkillRecord skill, CancellationToken ct = default) { Skills[skill.Name] = skill; return Task.CompletedTask; }
    public Task DeleteSkillAsync(string name, CancellationToken ct = default)
    {
        Skills.Remove(name);
        foreach (var k in Placements.Where(p => p.Value.SkillName == name).Select(p => p.Key).ToList()) Placements.Remove(k);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryPlacementRecord>>(Placements.Values.ToList());
    public Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(string skillName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryPlacementRecord>>(Placements.Values.Where(p => p.SkillName == skillName).ToList());
    public Task UpsertPlacementAsync(LibraryPlacementRecord placement, CancellationToken ct = default) { Placements[placement.Path] = placement; return Task.CompletedTask; }
    public Task DeletePlacementAsync(string path, CancellationToken ct = default) { Placements.Remove(path); return Task.CompletedTask; }
}

public sealed class SkillLibraryTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryLedger _ledger = new();
    private readonly SkillLibrary _lib;
    private readonly AstraPaths _paths;

    public SkillLibraryTests()
    {
        _paths = new AstraPaths(_home.File(".astra"));
        _lib = new SkillLibrary(_home.Env, _paths, _ledger);
        // Clients are "detected" when their config directory exists.
        foreach (var dir in new[] { ".claude", ".codex", ".gemini", ".copilot" }) Directory.CreateDirectory(_home.File(dir));
    }

    public void Dispose() => Directory.Delete(_home.Path, recursive: true);

    private string MakeSkill(string root, string name, string body = "hello")
    {
        var dir = Path.Combine(root, name);
        _home.WriteFile(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: Does {name}\n---\n{body}\n");
        return dir;
    }

    private async Task InstallAsync(string name, params string[] clients)
    {
        var src = MakeSkill(_home.File("src"), name);
        var found = await _lib.ProbeAsync(src);
        var outcome = await _lib.InstallAsync(found, SkillSourceKinds.Local, null, clients);
        Assert.All(outcome, o => Assert.True(o.Ok, o.Reason));
    }

    [Fact]
    public async Task Install_copies_into_the_library_and_links_the_client()
    {
        await InstallAsync("pdf", "claude-code");
        var link = _home.File(".claude", "skills", "pdf");
        Assert.True(FsLinks.IsLinkTo(link, _lib.SkillDir("pdf")));
        Assert.Equal("Does pdf", _ledger.Skills["pdf"].Description);
        Assert.Equal("claude-code", _ledger.Placements[link].Target);
    }

    [Fact]
    public async Task A_client_that_reads_the_shared_directory_gets_its_link_there()
    {
        await InstallAsync("pdf", "codex");
        Assert.True(FsLinks.IsLinkTo(Path.Combine(_lib.SharedDir, "pdf"), _lib.SkillDir("pdf")));
        Assert.False(Directory.Exists(_home.File(".codex", "skills", "pdf")));
        var state = await _lib.StateAsync();
        var views = state.Skills.Single().Clients.ToDictionary(c => c.Kind);
        Assert.Equal("via-shared", views["codex"].State);
        Assert.Equal("via-shared", views["gemini-cli"].State);
        Assert.Equal("off", views["claude-code"].State);
        // OpenCode reads the shared directory too.
        Assert.Equal("via-shared", views["opencode"].State);
    }

    [Fact]
    public async Task Switching_off_a_shared_client_asks_first_then_removes_only_the_link()
    {
        await InstallAsync("pdf", "codex");
        var shared = Path.Combine(_lib.SharedDir, "pdf");

        var first = await _lib.SetAsync("pdf", "codex", on: false, confirm: false);
        Assert.True(first.NeedsConfirm);
        Assert.Contains("gemini-cli", first.Affected);
        Assert.True(FsLinks.IsLink(shared));

        var second = await _lib.SetAsync("pdf", "codex", on: false, confirm: true);
        Assert.True(second.Done);
        Assert.False(Directory.Exists(shared));
        // The library copy is untouched.
        Assert.True(File.Exists(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md")));
        Assert.Empty(_ledger.Placements);
    }

    [Fact]
    public async Task Switching_off_removes_the_link_but_never_the_target()
    {
        await InstallAsync("pdf", "claude-code");
        await _lib.SetAsync("pdf", "claude-code", on: false, confirm: false);
        Assert.False(Directory.Exists(_home.File(".claude", "skills", "pdf")));
        Assert.True(File.Exists(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md")));
    }

    [Fact]
    public async Task A_directory_of_the_users_in_the_way_is_a_conflict_and_is_left_alone()
    {
        await InstallAsync("pdf");
        MakeSkill(_home.File(".claude", "skills"), "pdf", "mine");
        var e = await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.SetAsync("pdf", "claude-code", on: true, confirm: false));
        Assert.Equal(SkillFailure.Conflict, e.Failure);
        Assert.Contains("mine", _home.ReadFile(_home.File(".claude", "skills", "pdf", "SKILL.md")));
    }

    [Fact]
    public async Task A_link_the_user_replaced_is_not_removed()
    {
        await InstallAsync("pdf", "claude-code");
        var path = _home.File(".claude", "skills", "pdf");
        FsLinks.RemoveLink(path);
        MakeSkill(_home.File(".claude", "skills"), "pdf", "now mine");
        await _lib.SetAsync("pdf", "claude-code", on: false, confirm: false);
        Assert.Contains("now mine", _home.ReadFile(Path.Combine(path, "SKILL.md")));
        Assert.Empty(_ledger.Placements);
    }

    [Fact]
    public async Task Migrate_moves_the_skill_and_links_it_back_in_place_without_changing_who_sees_it()
    {
        MakeSkill(_home.File(".codex", "skills"), "review");
        var preview = await _lib.MigratePreviewAsync(null);
        Assert.Equal("review", preview.Single().Name);

        var outcome = await _lib.MigrateAsync([new SkillMigrateChoice("review", null)]);
        Assert.True(outcome.Single().Ok, outcome.Single().Reason);

        var original = _home.File(".codex", "skills", "review");
        Assert.True(FsLinks.IsLinkTo(original, _lib.SkillDir("review")));
        Assert.True(File.Exists(Path.Combine(_lib.SkillDir("review"), "SKILL.md")));
        // Not moved to the shared directory: Gemini CLI must not gain a Skill it never had.
        Assert.False(Directory.Exists(Path.Combine(_lib.SharedDir, "review")));
        Assert.Equal(PlacementOrigins.Migrated, _ledger.Placements[original].Origin);
        Assert.NotEmpty(Directory.GetDirectories(_paths.LibraryBackupsDir));
    }

    [Fact]
    public async Task Migrate_needs_a_choice_when_copies_differ_and_backs_up_the_other()
    {
        MakeSkill(_home.File(".claude", "skills"), "sql", "claude's");
        MakeSkill(_home.File(".codex", "skills"), "sql", "codex's");
        var found = (await _lib.StateAsync()).Found.Single();
        Assert.True(found.Conflict);

        var refused = await _lib.MigrateAsync([new SkillMigrateChoice("sql", null)]);
        Assert.False(refused.Single().Ok);
        Assert.False(FsLinks.IsLink(_home.File(".claude", "skills", "sql")));

        var keep = found.Locations.Single(l => l.Target == "codex").Path;
        var done = await _lib.MigrateAsync([new SkillMigrateChoice("sql", keep)]);
        Assert.True(done.Single().Ok, done.Single().Reason);
        Assert.Contains("codex's", _home.ReadFile(Path.Combine(_lib.SkillDir("sql"), "SKILL.md")));
        Assert.True(FsLinks.IsLink(_home.File(".claude", "skills", "sql")));
        var backups = Directory.GetFiles(_paths.LibraryBackupsDir, "SKILL.md", SearchOption.AllDirectories);
        Assert.Contains(backups, b => File.ReadAllText(b).Contains("claude's"));
    }

    [Fact]
    public async Task Discovery_flags_external_links_and_leaves_them_alone()
    {
        var dotfiles = MakeSkill(_home.File("dotfiles"), "mine");
        Directory.CreateDirectory(_home.File(".claude", "skills"));
        Directory.CreateSymbolicLink(_home.File(".claude", "skills", "mine"), dotfiles);
        var found = (await _lib.StateAsync()).Found.Single();
        Assert.Equal("external", found.Kind);
        Assert.Empty(await _lib.MigratePreviewAsync(null));
    }

    [Fact]
    public async Task Skills_managed_by_the_skills_cli_are_flagged()
    {
        MakeSkill(_home.File(".agents", "skills"), "fd");
        _home.WriteFile(_home.File(".agents", ".skill-lock.json"), "{\"skills\":{\"fd\":{}}}");
        var preview = await _lib.MigratePreviewAsync(null);
        Assert.Equal("skills-cli", preview.Single().Skip);
    }

    [Fact]
    public async Task Return_replaces_links_with_real_directories_and_keeps_the_content()
    {
        await InstallAsync("pdf", "claude-code", "codex");
        await _lib.ReturnAsync("pdf");
        var claude = _home.File(".claude", "skills", "pdf");
        Assert.False(FsLinks.IsLink(claude));
        Assert.True(File.Exists(Path.Combine(claude, "SKILL.md")));
        Assert.False(FsLinks.IsLink(Path.Combine(_lib.SharedDir, "pdf")));
        Assert.Empty(_ledger.Placements);
    }

    [Fact]
    public async Task Delete_removes_links_and_the_library_directory_but_backs_it_up()
    {
        await InstallAsync("pdf", "claude-code");
        await _lib.DeleteAsync("pdf");
        Assert.False(Directory.Exists(_home.File(".claude", "skills", "pdf")));
        Assert.False(Directory.Exists(_lib.SkillDir("pdf")));
        Assert.Empty(_ledger.Skills);
        Assert.Single(Directory.GetFiles(_paths.LibraryBackupsDir, "SKILL.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Install_skips_a_name_that_exists_with_other_content()
    {
        await InstallAsync("pdf");
        var other = MakeSkill(_home.File("src2"), "pdf", "different");
        var probe = await _lib.ProbeAsync(other);
        Assert.Equal("differs", probe.Single().Status);
        var outcome = await _lib.InstallAsync(probe, SkillSourceKinds.Local, null, []);
        Assert.False(outcome.Single().Ok);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("x.")]
    public void Names_that_could_leave_the_directory_are_refused(string name) => Assert.False(SkillLibrary.IsValidName(name));

    [Fact]
    public void A_copy_hash_ignores_the_managed_marker_and_ds_store()
    {
        var a = MakeSkill(_home.File("h1"), "s");
        var b = MakeSkill(_home.File("h2"), "s");
        _home.WriteFile(Path.Combine(b, ".DS_Store"), "junk");
        _home.WriteFile(Path.Combine(b, FsLinks.ManagedMarker), "{}");
        Assert.Equal(FsLinks.HashDirectory(a), FsLinks.HashDirectory(b));
    }
}

public sealed class SkillGitTests
{
    [Theory]
    [InlineData("https://github.com/anthropics/skills", null)]
    [InlineData("https://github.com/anthropics/skills.git", null)]
    [InlineData("git@github.com:anthropics/skills.git", null)]
    [InlineData("ssh://git@example.com/x/y.git", null)]
    [InlineData("file:///tmp/repo", null)]
    [InlineData("-upload-pack=evil", "option")]
    [InlineData("ext::sh -c evil", "transport")]
    [InlineData("fd::17/foo", "transport")]
    [InlineData("https://user:secret@example.com/x.git", "credentials")]
    [InlineData("https://ghp_token@example.com/x.git", "credentials")]
    [InlineData("ftp://example.com/x", "scheme")]
    [InlineData("", "empty")]
    [InlineData("not a url", "invalid")]
    public void Addresses_are_checked(string url, string? expected) => Assert.Equal(expected, SkillGit.CheckUrl(url));

    [Theory]
    [InlineData("main", null)]
    [InlineData("v1.2.3", null)]
    [InlineData("--upload-pack=x", "ref")]
    [InlineData("a..b", "ref")]
    public void Refs_are_checked(string value, string? expected) => Assert.Equal(expected, SkillGit.CheckRef(value));

    [Fact]
    public async Task Fetches_a_local_repository_without_its_dot_git()
    {
        using var home = new TestHome();
        var env = new ClientEnvironment2(home.Path).Env;
        if (env.FindTool("git") is not { } git) return; // no git here: nothing to run
        var repo = home.File("repo");
        Directory.CreateDirectory(Path.Combine(repo, "skills", "pdf"));
        File.WriteAllText(Path.Combine(repo, "skills", "pdf", "SKILL.md"), "---\nname: pdf\ndescription: d\n---\n");
        foreach (var args in new[] { "init -q", "-c user.name=t -c user.email=t@t add -A", "-c user.name=t -c user.email=t@t commit -q -m x" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo(git, args) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            await p.WaitForExitAsync();
            Assert.Equal(0, p.ExitCode);
        }

        var dest = home.File("fetched");
        var commit = await SkillGit.FetchAsync(env, "file://" + repo, null, dest, CancellationToken.None);
        Assert.Equal(40, commit.Length);
        Assert.True(File.Exists(Path.Combine(dest, "skills", "pdf", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(dest, ".git")));
    }

    private sealed class ClientEnvironment2(string home)
    {
        public ClientEnvironment Env { get; } = new(home, "osx", Environment.GetEnvironmentVariable);
    }
}
