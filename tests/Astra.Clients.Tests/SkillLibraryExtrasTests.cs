using System.Diagnostics;
using Astra.Clients.Config;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Library;
using Xunit;

namespace Astra.Clients.Tests;

/// <summary>Project placements, updates of Git-installed Skills, the skill router and the market's answer parsing.</summary>
public sealed class SkillLibraryExtrasTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryLedger _ledger = new();
    private readonly InMemoryResourceLedger _resources = new();
    private readonly SkillLibrary _lib;
    private readonly SkillRouter _router;
    private readonly AstraPaths _paths;

    public SkillLibraryExtrasTests()
    {
        _home.Variables["PATH"] = Environment.GetEnvironmentVariable("PATH");
        _paths = new AstraPaths(_home.File(".astra"));
        _lib = new SkillLibrary(_home.Env, _paths, _ledger);
        _router = new SkillRouter(_home.Env, _paths, _ledger, _resources);
        foreach (var dir in new[] { ".claude", ".codex", ".pi" }) Directory.CreateDirectory(_home.File(dir));
    }

    public void Dispose() => Directory.Delete(_home.Path, recursive: true);

    private async Task InstallAsync(string name, string description = "", string kind = SkillSourceKinds.Local, string? sourceJson = null, string? sourceRoot = null)
    {
        var dir = Path.Combine(sourceRoot ?? _home.File("src"), name);
        _home.WriteFile(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {(description.Length > 0 ? description : "Does " + name)}\n---\nbody\n");
        var found = await _lib.ProbeAsync(dir);
        var outcome = await _lib.InstallAsync(found, kind, sourceJson, []);
        Assert.All(outcome, o => Assert.True(o.Ok, o.Reason));
    }

    // ---------------------------------------------------------------- more clients, project places

    [Fact]
    public void The_Skill_Places_Cover_The_Added_Clients_And_Know_Their_Project_Directories()
    {
        foreach (var kind in new[] { ClientKinds.Pi, ClientKinds.Omp, ClientKinds.MiniMaxCode, ClientKinds.HermesAgent, ClientKinds.DeepSeekHarness, ClientKinds.CommandCode, ClientKinds.Crush, ClientKinds.ZCode, ClientKinds.AntigravityCli })
            Assert.NotNull(SkillPlaces.For(_home.Env, kind));
        Assert.Equal(_home.File(".pi", "agent", "skills"), SkillPlaces.For(_home.Env, ClientKinds.Pi)!.SkillsDir);
        Assert.Equal([ClientKinds.ClaudeCode], SkillPlaces.For(_home.Env, ClientKinds.Crush)!.AlsoReads);
        Assert.Equal(Path.Combine(".claude", "skills"), SkillPlaces.ProjectDir(ClientKinds.ClaudeCode));
        Assert.Equal(Path.Combine(".agents", "skills"), SkillPlaces.ProjectDir(ClientKinds.Codex));
        Assert.Null(SkillPlaces.ProjectDir(ClientKinds.Crush));
        Assert.Contains(SkillPlaces.ProjectDirs, d => d.Dir == Path.Combine(".agents", "skills") && d.Clients.Contains(ClientKinds.Codex));
    }

    [Fact]
    public async Task A_Skill_Is_Placed_Into_A_Project_Recorded_With_The_Project_And_Switched_Off_Without_Touching_The_Users_Directory()
    {
        await InstallAsync("pdf");
        var project = _home.File("work", "app");
        Directory.CreateDirectory(project);
        var dir = Path.Combine(".claude", "skills");

        Assert.True((await _lib.SetProjectAsync("pdf", project, dir, on: true)).Done);
        var link = Path.Combine(project, dir, "pdf");
        Assert.True(FsLinks.IsLinkTo(link, _lib.SkillDir("pdf")));
        var record = _ledger.Placements[link];
        Assert.Equal(SkillTargets.Project, record.Target);
        Assert.Equal(project, record.Project);

        // the user-wide state is unaffected (a project link is not "own")
        var state = await _lib.StateAsync();
        Assert.Equal("off", state.Skills.Single().Clients.Single(c => c.Kind == ClientKinds.ClaudeCode).State);
        Assert.Equal(project, state.Skills.Single().Placements.Single().Project);

        Assert.True((await _lib.SetProjectAsync("pdf", project, dir, on: false)).Done);
        Assert.False(Directory.Exists(link));
        Assert.Empty(_ledger.Placements);
        Assert.True(File.Exists(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md")));
    }

    [Fact]
    public async Task A_Users_Directory_In_The_Project_Is_A_Conflict_And_A_Deleted_Project_Is_Reported_Then_Forgotten()
    {
        await InstallAsync("pdf");
        var project = _home.File("work", "app");
        Directory.CreateDirectory(Path.Combine(project, ".agents", "skills", "pdf"));
        var ex = await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.SetProjectAsync("pdf", project, Path.Combine(".agents", "skills"), on: true));
        Assert.Equal(SkillFailure.Conflict, ex.Failure);
        Assert.True(Directory.Exists(Path.Combine(project, ".agents", "skills", "pdf")));
        await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.SetProjectAsync("pdf", project, "somewhere/else", on: true));
        await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.SetProjectAsync("pdf", "relative", Path.Combine(".agents", "skills"), on: true));

        Directory.Delete(Path.Combine(project, ".agents", "skills", "pdf"));
        await _lib.SetProjectAsync("pdf", project, Path.Combine(".agents", "skills"), on: true);
        Directory.Delete(project, recursive: true);
        var info = (await _lib.StateAsync()).Skills.Single().Placements.Single();
        Assert.True(info.ProjectMissing);
        Assert.True(info.Missing);
        await _lib.SetProjectAsync("pdf", project, Path.Combine(".agents", "skills"), on: false);   // forgets the record, fails on nothing
        Assert.Empty(_ledger.Placements);
    }

    // ---------------------------------------------------------------- updates

    private string? Git(string workingDirectory, params string[] args)
    {
        var git = _home.Env.FindTool("git");
        if (git is null) return null;
        var psi = new ProcessStartInfo(git) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    [Fact]
    public async Task A_Git_Skill_Reports_A_Newer_Commit_And_Updates_With_A_Backup_Unless_It_Was_Edited()
    {
        if (_home.Env.FindTool("git") is null) return;   // no git on this machine
        var repo = _home.File("remote");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        _home.WriteFile(Path.Combine(repo, "pdf", "SKILL.md"), "---\nname: pdf\ndescription: v1\n---\none\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "one");
        var url = new Uri(repo).AbsoluteUri;

        var temp = _home.File("fetch");
        var commit = await SkillGit.FetchAsync(_home.Env, url, null, temp, default);
        var candidates = await _lib.ProbeAsync(temp);
        await _lib.InstallAsync(candidates, SkillSourceKinds.Git, $"{{\"url\":\"{url}\",\"ref\":null,\"commit\":\"{commit}\"}}", [ClientKinds.ClaudeCode]);

        var first = Assert.Single(await _lib.CheckUpdatesAsync());
        Assert.False(first.Behind);
        Assert.Null(first.Error);

        _home.WriteFile(Path.Combine(repo, "pdf", "SKILL.md"), "---\nname: pdf\ndescription: v2\n---\ntwo\n");
        Git(repo, "commit", "-q", "-am", "two");
        var behind = Assert.Single(await _lib.CheckUpdatesAsync());
        Assert.True(behind.Behind);
        Assert.NotEqual(behind.Current, behind.Latest);

        // an edit made here since the install is not silently replaced
        File.AppendAllText(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md"), "mine\n");
        var ex = await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.UpdateAsync("pdf", force: false));
        Assert.Equal(SkillFailure.Conflict, ex.Failure);
        Assert.Contains("mine", File.ReadAllText(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md")));

        var outcome = await _lib.UpdateAsync("pdf", force: true);
        Assert.True(outcome.Ok);
        var text = File.ReadAllText(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md"));
        Assert.Contains("two", text);
        Assert.DoesNotContain("mine", text);
        Assert.Equal("v2", _ledger.Skills["pdf"].Description);
        Assert.Contains("mine", File.ReadAllText(Directory.GetFiles(_paths.LibraryBackupsDir, "SKILL.md", SearchOption.AllDirectories).Single()));
        // the link still points at the library directory, and the update is now current
        Assert.True(FsLinks.IsLinkTo(_home.File(".claude", "skills", "pdf"), _lib.SkillDir("pdf")));
        Assert.False(Assert.Single(await _lib.CheckUpdatesAsync()).Behind);

        await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.UpdateAsync("missing", force: false));
    }

    [Fact]
    public async Task A_Skill_That_Did_Not_Come_From_Git_Cannot_Be_Updated_And_Is_Not_Checked()
    {
        await InstallAsync("pdf");
        Assert.Empty(await _lib.CheckUpdatesAsync());
        var ex = await Assert.ThrowsAsync<SkillLibraryException>(() => _lib.UpdateAsync("pdf", force: true));
        Assert.Equal(SkillFailure.Invalid, ex.Failure);
    }

    // ---------------------------------------------------------------- router

    [Fact]
    public async Task The_Router_Lists_The_Skills_A_Client_Has_No_Link_For_And_Follows_Switches()
    {
        await InstallAsync("pdf", "Reads PDFs");
        await InstallAsync("docx", "Writes Word files");
        await _lib.SetAsync("docx", ClientKinds.ClaudeCode, on: true, confirm: false);

        await _router.EnableAsync(ClientKinds.ClaudeCode);
        var entry = _home.File(".claude", "skills", SkillRouter.EntryName);
        Assert.True(FsLinks.IsLink(entry));
        var md = File.ReadAllText(Path.Combine(entry, "SKILL.md"));
        Assert.StartsWith("---\nname: astra-skills\n", md);
        Assert.Contains("pdf", md);
        Assert.DoesNotContain("docx", md);   // it has its own link
        Assert.True(FsLinks.IsLinkTo(Path.Combine(entry, ".skills", "pdf"), _lib.SkillDir("pdf")));
        Assert.Contains(Path.Combine(_home.File(".claude", "skills"), SkillRouter.EntryName, ".skills", "pdf", "SKILL.md"), md);

        // switching the link off routes the Skill too, and the entry is not reported as a found Skill
        await _lib.SetAsync("docx", ClientKinds.ClaudeCode, on: false, confirm: false);
        await _router.RefreshAsync();
        Assert.Contains("docx", File.ReadAllText(Path.Combine(entry, "SKILL.md")));
        Assert.DoesNotContain((await _lib.StateAsync()).Found, f => f.Name == SkillRouter.EntryName);
        Assert.Equal(2, (await _router.StateAsync()).Single(c => c.Kind == ClientKinds.ClaudeCode).Listed);

        await _router.DisableAsync(ClientKinds.ClaudeCode);
        Assert.False(Directory.Exists(entry));
        Assert.False(Directory.Exists(Path.Combine(_paths.LibraryDir, "router", ClientKinds.ClaudeCode)));
        Assert.True(File.Exists(Path.Combine(_lib.SkillDir("pdf"), "SKILL.md")));
        Assert.Empty(_resources.Writes);
    }

    [Fact]
    public async Task The_Router_Is_Refused_For_Shared_Readers_And_Never_Replaces_The_Users_Own_Entry()
    {
        await InstallAsync("pdf");
        var shared = await Assert.ThrowsAsync<SkillLibraryException>(() => _router.EnableAsync(ClientKinds.Codex));
        Assert.Equal(SkillFailure.Invalid, shared.Failure);
        Assert.Equal("shared", (await _router.StateAsync()).Single(c => c.Kind == ClientKinds.Codex).Why);

        var own = _home.File(".claude", "skills", SkillRouter.EntryName);
        _home.WriteFile(Path.Combine(own, "SKILL.md"), "mine");
        var ex = await Assert.ThrowsAsync<SkillLibraryException>(() => _router.EnableAsync(ClientKinds.ClaudeCode));
        Assert.Equal(SkillFailure.Conflict, ex.Failure);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(own, "SKILL.md")));
        Assert.Equal("own", (await _router.StateAsync()).Single(c => c.Kind == ClientKinds.ClaudeCode).Why);
    }

    [Fact]
    public async Task A_Router_Entry_The_User_Repointed_Is_Theirs_And_RemoveAll_Leaves_It()
    {
        await InstallAsync("pdf");
        await _router.EnableAsync(ClientKinds.ClaudeCode);
        var entry = _home.File(".claude", "skills", SkillRouter.EntryName);
        FsLinks.RemoveLink(entry);
        var elsewhere = _home.File("elsewhere");
        Directory.CreateDirectory(elsewhere);
        FsLinks.Create(entry, elsewhere);

        Assert.Empty(await _router.RemoveAllAsync());
        Assert.True(FsLinks.IsLinkTo(entry, elsewhere));
        Assert.Empty(_resources.Writes);
    }

    [Fact]
    public void The_Router_Description_Stays_Within_The_Limit_Codex_Refuses_To_Exceed()
    {
        var many = Enumerable.Range(0, 200).Select(i => new LibrarySkillRecord { Name = $"skill-{i}", Description = new string('x', 80) + " 中文" }).ToList();
        var md = SkillRouter.Render(many, "/c/skills/astra-skills/.skills");
        var description = md.Split('\n').Single(l => l.StartsWith("description: ", StringComparison.Ordinal))["description: ".Length..];
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(description) <= 1024 + 2);
        Assert.Contains("skill-199", md);   // the body lists every Skill whatever the description fits
    }

    // ---------------------------------------------------------------- market

    [Fact]
    public void The_Market_Answer_Is_Parsed_And_Bad_Entries_And_Addresses_Are_Dropped()
    {
        var list = SkillMarket.Parse("""{"skills":[{"source":"acme/skills","skillId":"pdf","name":"PDF","installs":42},{"source":"bad source","skillId":"x"},{"source":"a/b","skillId":"../x"},{"source":"a/b","skillId":"ok"}]}""");
        Assert.Equal(["pdf", "ok"], list.Select(s => s.SkillId));
        Assert.Equal(42, list[0].Installs);
        Assert.Equal("ok", list[1].Name);
        Assert.Throws<SkillLibraryException>(() => SkillMarket.Parse("not json"));
        Assert.Throws<SkillLibraryException>(() => SkillMarket.Parse("{}"));
        Assert.Null(SkillMarket.CheckUrl("https://www.skills.sh"));
        Assert.NotNull(SkillMarket.CheckUrl("ftp://x"));
        Assert.NotNull(SkillMarket.CheckUrl("https://user:pw@x.example"));
        Assert.Equal("https://github.com/acme/skills", SkillMarket.RepoUrl("acme/skills"));
        Assert.True(SkillMarket.IsSource("acme/skills"));
        Assert.False(SkillMarket.IsSource("acme"));
    }
}
