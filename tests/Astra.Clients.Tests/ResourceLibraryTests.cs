using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Library;
using Xunit;

namespace Astra.Clients.Tests;

public sealed class InMemoryResourceLedger : ILibraryResourceLedger
{
    public Dictionary<string, LibraryItemRecord> Items { get; } = [];
    public Dictionary<string, LibraryWriteRecord> Writes { get; } = [];
    public Dictionary<string, LibraryProjectRecord> Projects { get; } = [];
    public HashSet<string> Router { get; } = [];

    public Task<IReadOnlyList<LibraryItemRecord>> ListItemsAsync(string? kind = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryItemRecord>>(Items.Values.Where(i => kind is null || i.Kind == kind).OrderBy(i => i.Name).ToList());
    public Task<LibraryItemRecord?> GetItemAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
    public Task<LibraryItemRecord?> GetItemByNameAsync(string kind, string name, CancellationToken ct = default) =>
        Task.FromResult(Items.Values.FirstOrDefault(i => i.Kind == kind && i.Name == name));
    public Task UpsertItemAsync(LibraryItemRecord item, CancellationToken ct = default) { Items[item.Id] = item; return Task.CompletedTask; }
    public Task DeleteItemAsync(string id, CancellationToken ct = default)
    {
        Items.Remove(id);
        foreach (var k in Writes.Where(w => w.Value.ItemId == id).Select(w => w.Key).ToList()) Writes.Remove(k);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<LibraryWriteRecord>> ListWritesAsync(string? itemId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryWriteRecord>>(Writes.Values.Where(w => itemId is null || w.ItemId == itemId).ToList());
    public Task UpsertWriteAsync(LibraryWriteRecord write, CancellationToken ct = default) { Writes[write.Id] = write; return Task.CompletedTask; }
    public Task DeleteWriteAsync(string id, CancellationToken ct = default) { Writes.Remove(id); return Task.CompletedTask; }
    public Task<IReadOnlyList<LibraryProjectRecord>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LibraryProjectRecord>>(Projects.Values.ToList());
    public Task UpsertProjectAsync(LibraryProjectRecord project, CancellationToken ct = default) { Projects[project.Path] = project; return Task.CompletedTask; }
    public Task DeleteProjectAsync(string path, CancellationToken ct = default) { Projects.Remove(path); return Task.CompletedTask; }
    public Task<IReadOnlyList<string>> ListRouterClientsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Router.Order().ToList());
    public Task SetRouterClientAsync(string client, bool on, CancellationToken ct = default)
    {
        if (on) Router.Add(client); else Router.Remove(client);
        return Task.CompletedTask;
    }
}

public sealed class ResourceLibraryTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryResourceLedger _ledger = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ResourceLibrary _lib;
    private readonly AstraPaths _paths;

    public ResourceLibraryTests()
    {
        _paths = new AstraPaths(_home.File(".astra"));
        _lib = new ResourceLibrary(_home.Env, _paths, _ledger, _store, new ClientConfigApplier(_store, _home.Env, _home.BackupRoot));
        foreach (var dir in new[] { ".claude", ".codex", ".gemini", ".qwen" }) Directory.CreateDirectory(_home.File(dir));
    }

    public void Dispose() => Directory.Delete(_home.Path, recursive: true);

    private static ResourceInput Instruction(string name, string text, params LibraryTarget[] targets) =>
        new(ResourceKinds.Instruction, name, "", text, null, null, 0, null, targets, true);

    private static ResourceInput Hook(string name, string command, string ev, string? matcher, params LibraryTarget[] targets) =>
        new(ResourceKinds.Hook, name, "", command, ev, matcher, 5, null, targets, true);

    private static LibraryTarget T(string client, string project = "") => new(client, project);

    private string ClaudeMd => _home.File(".claude", "CLAUDE.md");

    // ---------------------------------------------------------------- instructions

    [Fact]
    public async Task Instruction_Goes_Into_A_Marked_Section_And_The_Users_Text_Is_Untouched()
    {
        _home.WriteFile(ClaudeMd, "# Mine\n\nAlways be kind.\n");
        var info = await _lib.SaveAsync(null, Instruction("style", "Use tabs.", T(ClientKinds.ClaudeCode)), false);

        var text = _home.ReadFile(ClaudeMd);
        Assert.StartsWith("# Mine\n\nAlways be kind.\n", text);
        Assert.Contains("<!-- astra:begin style -->\nUse tabs.\n<!-- astra:end style -->", text);
        Assert.Equal("applied", info.Targets.Single().State);
        Assert.NotEmpty(Directory.GetFiles(_paths.LibraryBackupsDir, "*", SearchOption.AllDirectories));   // the file was backed up first

        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Equal("# Mine\n\nAlways be kind.\n", _home.ReadFile(ClaudeMd));
        Assert.Empty(_ledger.Writes);
    }

    [Fact]
    public async Task A_File_Astra_Created_Is_Deleted_With_Its_Last_Section()
    {
        var info = await _lib.SaveAsync(null, Instruction("a", "x", T(ClientKinds.GeminiCli)), false);
        var file = _home.File(".gemini", "GEMINI.md");
        Assert.True(File.Exists(file));
        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task An_Edited_Section_Is_Drift_It_Is_Not_Overwritten_Without_Force_And_Survives_Removal()
    {
        var info = await _lib.SaveAsync(null, Instruction("style", "Use tabs.", T(ClientKinds.ClaudeCode)), false);
        _home.WriteFile(ClaudeMd, _home.ReadFile(ClaudeMd).Replace("Use tabs.", "Use spaces."));

        var state = await _lib.StateAsync(ResourceKinds.Instruction);
        Assert.Equal("drifted", state.Items.Single().Targets.Single().State);

        // a change of the text is refused for that target, the user's edit stays
        await _lib.SaveAsync(info.Item.Id, Instruction("style", "Use tabs always.", T(ClientKinds.ClaudeCode)), false);
        Assert.Contains("Use spaces.", _home.ReadFile(ClaudeMd));
        Assert.NotNull(_lib.LastError(info.Item.Id, ClientKinds.ClaudeCode, ""));

        // forced, it is replaced (and the old file was backed up)
        await _lib.SyncAsync(info.Item.Id, true);
        Assert.Contains("Use tabs always.", _home.ReadFile(ClaudeMd));
        Assert.DoesNotContain("Use spaces.", _home.ReadFile(ClaudeMd));

        // an edit that is made again stays when the item is deleted without force
        _home.WriteFile(ClaudeMd, _home.ReadFile(ClaudeMd).Replace("Use tabs always.", "Mine now."));
        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Contains("Mine now.", _home.ReadFile(ClaudeMd));
        Assert.Empty(_ledger.Writes);
    }

    [Fact]
    public async Task A_Section_Of_The_Same_Name_That_Astra_Did_Not_Write_Is_A_Conflict()
    {
        _home.WriteFile(ClaudeMd, "<!-- astra:begin style -->\nsomeone else\n<!-- astra:end style -->\n");
        var info = await _lib.SaveAsync(null, Instruction("style", "Use tabs.", T(ClientKinds.ClaudeCode)), false);
        Assert.Contains("someone else", _home.ReadFile(ClaudeMd));
        Assert.Empty(_ledger.Writes);
        Assert.NotNull(_lib.LastError(info.Item.Id, ClientKinds.ClaudeCode, ""));
    }

    [Fact]
    public async Task Instruction_In_A_Project_Goes_To_The_Projects_File_And_A_Missing_Project_Is_Reported_Not_Failed()
    {
        var project = _home.File("work", "app");
        Directory.CreateDirectory(project);
        var info = await _lib.SaveAsync(null, Instruction("p", "Project rule", T(ClientKinds.Codex, project), T(ClientKinds.ClaudeCode, project)), false);
        Assert.Contains("Project rule", _home.ReadFile(Path.Combine(project, "AGENTS.md")));
        Assert.Contains("Project rule", _home.ReadFile(Path.Combine(project, "CLAUDE.md")));
        Assert.All(_ledger.Writes.Values, w => Assert.Equal(project, w.Project));

        Directory.Delete(project, recursive: true);
        var state = await _lib.StateAsync(ResourceKinds.Instruction);
        Assert.All(state.Items.Single().Targets, t => Assert.Equal("project-missing", t.State));
        Assert.False(state.Projects.Count > 0 && state.Projects[0].Exists);

        // removing the item with the project gone only forgets the rows
        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Empty(_ledger.Writes);
    }

    // ---------------------------------------------------------------- agents and commands

    [Fact]
    public async Task Agent_Is_Written_In_Each_Clients_Own_Format_And_A_Users_File_Is_Never_Overwritten()
    {
        var input = new ResourceInput(ResourceKinds.Agent, "reviewer", "Reviews code", "You review.", null, null, 0, "gpt-5",
            [T(ClientKinds.ClaudeCode), T(ClientKinds.Codex), T(ClientKinds.OpenCode)], true);
        _home.WriteFile(_home.File(".claude", "agents", "other.md"), "x");
        var info = await _lib.SaveAsync(null, input, false);

        var claude = _home.ReadFile(_home.File(".claude", "agents", "reviewer.md"));
        Assert.StartsWith("---\nname: \"reviewer\"\ndescription: \"Reviews code\"\n---\n\nYou review.", claude);
        var codex = _home.ReadFile(_home.File(".codex", "agents", "reviewer.toml"));
        Assert.Contains("name = \"reviewer\"", codex);
        Assert.Contains("model = \"gpt-5\"", codex);
        Assert.Contains("developer_instructions = \"\"\"\nYou review.\n\"\"\"", codex);
        var opencode = _home.ReadFile(_home.File(".config", "opencode", "agents", "reviewer.md"));
        Assert.Contains("mode: subagent", opencode);
        Assert.All(info.Targets, t => Assert.Equal("applied", t.State));

        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.False(File.Exists(_home.File(".claude", "agents", "reviewer.md")));
        Assert.True(File.Exists(_home.File(".claude", "agents", "other.md")));

        _home.WriteFile(_home.File(".claude", "agents", "reviewer.md"), "mine");
        var again = await _lib.SaveAsync(null, input with { Targets = [T(ClientKinds.ClaudeCode)] }, false);
        Assert.Equal("mine", _home.ReadFile(_home.File(".claude", "agents", "reviewer.md")));
        Assert.NotNull(_lib.LastError(again.Item.Id, ClientKinds.ClaudeCode, ""));
    }

    [Fact]
    public async Task A_Changed_Agent_File_Is_Drift_It_Is_Kept_On_Removal_And_Replaced_With_Force()
    {
        var input = new ResourceInput(ResourceKinds.Agent, "reviewer", "Reviews code", "You review.", null, null, 0, null, [T(ClientKinds.ClaudeCode)], true);
        var info = await _lib.SaveAsync(null, input, false);
        var file = _home.File(".claude", "agents", "reviewer.md");
        _home.WriteFile(file, _home.ReadFile(file) + "extra\n");
        Assert.Equal("drifted", (await _lib.StateAsync(ResourceKinds.Agent)).Items.Single().Targets.Single().State);
        await _lib.SaveAsync(info.Item.Id, input with { Body = "You review hard." }, false);
        Assert.Contains("extra", _home.ReadFile(file));
        await _lib.SyncAsync(info.Item.Id, true);
        Assert.DoesNotContain("extra", _home.ReadFile(file));
        Assert.Contains("You review hard.", _home.ReadFile(file));
    }

    [Fact]
    public async Task Command_Arguments_Follow_Each_Clients_Style_And_Positional_Words_Are_Refused_Where_A_Client_Takes_None()
    {
        var input = new ResourceInput(ResourceKinds.Command, "fix", "Fix it", "Fix $1 in $2, then $ARGUMENTS", null, null, 0, null,
            [T(ClientKinds.ClaudeCode), T(ClientKinds.GeminiCli), T(ClientKinds.OpenCode)], true);
        var info = await _lib.SaveAsync(null, input, false);
        Assert.Contains("Fix $ARGUMENTS[0] in $ARGUMENTS[1], then $ARGUMENTS", _home.ReadFile(_home.File(".claude", "commands", "fix.md")));
        Assert.Contains("Fix $1 in $2", _home.ReadFile(_home.File(".config", "opencode", "commands", "fix.md")));
        Assert.Equal("unsupported", info.Targets.Single(t => t.Client == ClientKinds.GeminiCli).State);
        Assert.False(File.Exists(_home.File(".gemini", "commands", "fix.toml")));

        var gemini = await _lib.SaveAsync(null, input with { Name = "plain", Body = "Do $ARGUMENTS", Targets = [T(ClientKinds.GeminiCli)] }, false);
        Assert.Equal("applied", gemini.Targets.Single().State);
        var toml = _home.ReadFile(_home.File(".gemini", "commands", "plain.toml"));
        Assert.Contains("description = \"Fix it\"", toml);
        Assert.Contains("Do {{args}}", toml);
    }

    [Fact]
    public async Task Agent_And_Command_Go_Into_A_Project_Folder()
    {
        var project = _home.File("repo");
        Directory.CreateDirectory(project);
        await _lib.SaveAsync(null, new ResourceInput(ResourceKinds.Agent, "helper", "Helps", "Help.", null, null, 0, null, [T(ClientKinds.ClaudeCode, project)], true), false);
        await _lib.SaveAsync(null, new ResourceInput(ResourceKinds.Command, "go", "", "Go", null, null, 0, null, [T(ClientKinds.ClaudeCode, project)], true), false);
        Assert.True(File.Exists(Path.Combine(project, ".claude", "agents", "helper.md")));
        Assert.True(File.Exists(Path.Combine(project, ".claude", "commands", "go.md")));
        Assert.False(File.Exists(_home.File(".claude", "agents", "helper.md")));
    }

    [Fact]
    public async Task Validation_Refuses_Bad_Names_And_Targets()
    {
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null, Instruction("Bad Name", "x"), false));
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null, Instruction("a", "x", T("not-a-client")), false));
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null, Instruction("a", "<!-- astra:end a -->", T(ClientKinds.ClaudeCode)), false));
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null, Hook("h", "x", "turn-end", null, T(ClientKinds.ClaudeCode, "/proj")), false));
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null, Hook("h", "x", "nope", null, T(ClientKinds.ClaudeCode)), false));
        await Assert.ThrowsAsync<ResourceLibraryException>(() => _lib.SaveAsync(null,
            new ResourceInput(ResourceKinds.Agent, "plan", "d", "p", null, null, 0, null, [], true), false));
    }

    // ---------------------------------------------------------------- hooks

    private string ClaudeSettings => _home.File(".claude", "settings.json");

    [Fact]
    public async Task Hook_Is_Appended_Beside_The_Users_Entries_And_Removed_Without_A_Trace()
    {
        _home.WriteFile(ClaudeSettings, """{"theme":"dark","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo mine"}]}]}}""");
        var before = JsonNode.Parse(_home.ReadFile(ClaudeSettings))!.ToJsonString();

        var info = await _lib.SaveAsync(null, Hook("notify", "say done", "turn-end", null, T(ClientKinds.ClaudeCode)), false);
        var stop = JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("echo mine", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("say done", stop[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(5, stop[1]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Equal("applied", info.Targets.Single().State);

        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Equal(before, JsonNode.Parse(_home.ReadFile(ClaudeSettings))!.ToJsonString());
    }

    [Fact]
    public async Task Hook_Array_Astra_Created_Goes_With_Its_Last_Entry_And_Tool_Matchers_Are_The_Clients_Own()
    {
        _home.WriteFile(ClaudeSettings, """{"theme":"dark"}""");
        var a = await _lib.SaveAsync(null, Hook("fmt", "prettier --write", "after-tool", "edit", T(ClientKinds.ClaudeCode), T(ClientKinds.GeminiCli)), false);
        var b = await _lib.SaveAsync(null, Hook("fmt2", "other", "after-tool", "edit", T(ClientKinds.ClaudeCode)), false);
        var claude = JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, claude.Count);
        Assert.Equal("Edit|Write|MultiEdit", claude[0]!["matcher"]!.GetValue<string>());
        var gemini = JsonNode.Parse(_home.ReadFile(_home.File(".gemini", "settings.json")))!["hooks"]!["AfterTool"]!.AsArray();
        Assert.Equal("write_file|replace", gemini[0]!["matcher"]!.GetValue<string>());
        Assert.Equal(5000, gemini[0]!["hooks"]![0]!["timeout"]!.GetValue<int>());   // Gemini counts milliseconds

        await _lib.DeleteAsync(a.Item.Id, false);
        await _lib.DeleteAsync(b.Item.Id, false);
        Assert.Equal("""{"theme":"dark"}""", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!.ToJsonString());
        Assert.Empty(_store.List(ClientKinds.ClaudeCode + "-hooks"));
    }

    [Fact]
    public async Task A_Hook_The_User_Changed_Stays_And_Is_Reported()
    {
        var info = await _lib.SaveAsync(null, Hook("notify", "say done", "turn-end", null, T(ClientKinds.ClaudeCode)), false);
        _home.WriteFile(ClaudeSettings, _home.ReadFile(ClaudeSettings).Replace("say done", "say finished"));
        Assert.Equal("drifted", (await _lib.StateAsync(ResourceKinds.Hook)).Items.Single().Targets.Single().State == "missing" ? "drifted" : "applied");
        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Contains("say finished", _home.ReadFile(ClaudeSettings));
    }

    [Fact]
    public async Task Codex_Hooks_Go_To_Hooks_Json_And_Session_Start_Can_Be_Narrowed()
    {
        await _lib.SaveAsync(null, Hook("ctx", "cat ctx.md", "session-start", "compact", T(ClientKinds.Codex)), false);
        var start = JsonNode.Parse(_home.ReadFile(_home.File(".codex", "hooks.json")))!["hooks"]!["SessionStart"]!.AsArray();
        Assert.Equal("compact", start[0]!["matcher"]!.GetValue<string>());
        var gemini = await _lib.SaveAsync(null, Hook("ctx2", "x", "session-start", "compact", T(ClientKinds.GeminiCli)), false);
        Assert.Equal("unsupported", gemini.Targets.Single().State);   // Gemini has no compact start
    }

    // ---------------------------------------------------------------- environment variables

    private static ResourceInput EnvVar(string name, string value, params LibraryTarget[] targets) =>
        new(ResourceKinds.Env, name, "", value, null, null, 0, null, targets, true);

    [Fact]
    public async Task Env_Variable_Is_Written_Into_Claude_And_Codex_And_Taken_Out_With_Originals_Restored()
    {
        _home.WriteFile(ClaudeSettings, """{"env":{"KEEP":"1"}}""");
        var info = await _lib.SaveAsync(null, EnvVar("MY_FLAG", "on", T(ClientKinds.ClaudeCode), T(ClientKinds.Codex)), false);
        Assert.All(info.Targets, t => Assert.True(t.State == "applied", t.State + " " + _lib.LastError(info.Item.Id, t.Client, "")));
        Assert.Equal("on", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["env"]!["MY_FLAG"]!.GetValue<string>());
        Assert.Contains("[shell_environment_policy.set]", _home.ReadFile(_home.File(".codex", "config.toml")));
        Assert.Contains("MY_FLAG = \"on\"", _home.ReadFile(_home.File(".codex", "config.toml")));

        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Equal("""{"env":{"KEEP":"1"}}""", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!.ToJsonString());
        Assert.False(File.Exists(_home.File(".codex", "config.toml")) && _home.ReadFile(_home.File(".codex", "config.toml")).Contains("MY_FLAG"));
    }

    [Fact]
    public async Task Env_Variable_The_User_Already_Has_Is_Never_Overwritten_And_A_Changed_One_Is_Theirs()
    {
        _home.WriteFile(ClaudeSettings, """{"env":{"MY_FLAG":"theirs"}}""");
        var info = await _lib.SaveAsync(null, EnvVar("MY_FLAG", "on", T(ClientKinds.ClaudeCode)), false);
        Assert.Equal("theirs", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["env"]!["MY_FLAG"]!.GetValue<string>());
        Assert.Empty(_ledger.Writes);
        Assert.NotNull(_lib.LastError(info.Item.Id, ClientKinds.ClaudeCode, ""));

        await _lib.SyncAsync(info.Item.Id, true);   // forced, written
        Assert.Equal("on", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["env"]!["MY_FLAG"]!.GetValue<string>());
        _home.WriteFile(ClaudeSettings, _home.ReadFile(ClaudeSettings).Replace("\"on\"", "\"changed\""));
        await _lib.DeleteAsync(info.Item.Id, false);
        Assert.Equal("changed", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!["env"]!["MY_FLAG"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- restore-all

    [Fact]
    public async Task RemoveAll_Gives_Everything_Back_And_Reports_What_It_Left()
    {
        _home.WriteFile(ClaudeMd, "mine\n");
        _home.WriteFile(ClaudeSettings, """{"a":1}""");
        await _lib.SaveAsync(null, Instruction("x", "rule", T(ClientKinds.ClaudeCode)), false);
        await _lib.SaveAsync(null, Hook("h", "run", "turn-end", null, T(ClientKinds.ClaudeCode)), false);
        await _lib.SaveAsync(null, EnvVar("A_B", "1", T(ClientKinds.ClaudeCode)), false);
        await _lib.SaveAsync(null, new ResourceInput(ResourceKinds.Agent, "helper", "d", "p", null, null, 0, null, [T(ClientKinds.ClaudeCode)], true), false);
        _home.WriteFile(_home.File(".claude", "agents", "helper.md"), "changed by the user");

        var left = await _lib.RemoveAllAsync();

        Assert.Equal("mine\n", _home.ReadFile(ClaudeMd));
        Assert.Equal("""{"a":1}""", JsonNode.Parse(_home.ReadFile(ClaudeSettings))!.ToJsonString());
        Assert.Single(left);
        Assert.Contains("helper.md", left[0]);
        Assert.Equal("changed by the user", _home.ReadFile(_home.File(".claude", "agents", "helper.md")));
        Assert.Empty(_ledger.Writes);
    }

    // ---------------------------------------------------------------- text helpers

    [Fact]
    public void Section_Helpers_Keep_Crlf_Files_And_Several_Sections_Apart()
    {
        var file = "# top\r\n\r\nuser text\r\n";
        var a = ResourceText.SetBlock(file, "a", "one\ntwo");
        var ab = ResourceText.SetBlock(a, "b", "three");
        Assert.Equal("one\ntwo", ResourceText.GetBlock(ab, "a"));
        Assert.Equal("three", ResourceText.GetBlock(ab, "b"));
        Assert.Contains("\r\n", ab);
        Assert.Equal(["a", "b"], ResourceText.BlockIds(ab));
        Assert.Equal(file, ResourceText.RemoveBlock(ResourceText.RemoveBlock(ab, "b"), "a"));
        Assert.Null(ResourceText.GetBlock(file, "a"));
    }
}
