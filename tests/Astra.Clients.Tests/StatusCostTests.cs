using Astra.Clients.Adapters;
using Astra.Clients.Config;

namespace Astra.Clients.Tests;

/// <summary>
/// What keeps the client list cheap to poll: plans without diffs, read tracking for the staleness check, and the drift
/// comparison that is only redone when a file or a recorded value changed.
/// </summary>
public sealed class StatusCostTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void A_Tracked_Read_Is_Unchanged_Until_The_File_Or_A_Checked_Path_Changes()
    {
        var config = _home.File("config.json");
        var later = _home.File("later.json");
        _home.WriteFile(config, """{"a":1}""");
        IReadOnlyList<FileProbe> reads;
        using (var tracking = ClientEnvironment.TrackReads())
        {
            Assert.NotNull(_home.Env.ReadTextOrNull(config));
            Assert.False(_home.Env.FileExists(later));
            reads = tracking.Reads;
        }
        Assert.Equal([FileProbeKind.Content, FileProbeKind.FileExists], reads.Select(r => r.Kind));
        Assert.True(ClientEnvironment.Unchanged(reads));

        _home.WriteFile(config, """{"a":2}"""); // same length, other bytes
        Assert.False(ClientEnvironment.Unchanged(reads));
        _home.WriteFile(config, """{"a":1}""");
        Assert.True(ClientEnvironment.Unchanged(reads));
        _home.WriteFile(later, "{}");
        Assert.False(ClientEnvironment.Unchanged(reads));
    }

    [Fact]
    public void Reads_Outside_A_Scope_Are_Not_Recorded_And_Scopes_Nest()
    {
        var file = _home.File("x.toml");
        _home.WriteFile(file, "a = 1");
        Assert.NotNull(_home.Env.ReadTextOrNull(file)); // no scope open: nothing to record into
        using var outer = ClientEnvironment.TrackReads();
        using (var inner = ClientEnvironment.TrackReads())
        {
            Assert.True(_home.Env.DirectoryExists(_home.Path));
            Assert.Single(inner.Reads);
        }
        Assert.Equal(FileProbeKind.DirectoryExists, Assert.Single(outer.Reads).Kind); // the inner scope's probes count here too
    }

    [Fact]
    public void ChangesOnly_Plans_Carry_The_Changes_Without_Diffs()
    {
        var adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        _home.WriteFile(_home.File(".claude", "settings.json"), "{}");
        var context = TestGateway.Context("astra-claude-x");

        var full = adapter.PlanEnable(context);
        var lean = ClientAdapterBase.ChangesOnly(() => adapter.PlanEnable(context));

        Assert.NotEmpty(full.Diffs);
        Assert.Empty(lean.Diffs);
        Assert.Equal(full.Changes.Select(c => (c.KeyPath, c.Before, c.After)), lean.Changes.Select(c => (c.KeyPath, c.Before, c.After)));
        Assert.NotEmpty(adapter.PlanEnable(context).Diffs); // the mode ends with the call
    }

    [Fact]
    public void Drift_Is_Seen_After_A_Same_Length_Edit_And_Cleared_When_The_Value_Comes_Back()
    {
        var adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        var settings = _home.File(".claude", "settings.json");
        _home.WriteFile(settings, "{}");
        new ClientConfigApplier(_store, _home.Env, _home.BackupRoot).Apply(adapter.PlanEnable(TestGateway.Context("astra-claude-x")));
        var written = _home.ReadFile(settings);
        Assert.Empty(adapter.Inspect().DriftedKeys);
        Assert.Empty(adapter.Inspect().DriftedKeys); // the second look reuses the first comparison

        _home.WriteFile(settings, written.Replace("17321", "17322"));
        Assert.Equal(["env.ANTHROPIC_BASE_URL"], adapter.Inspect().DriftedKeys);

        _home.WriteFile(settings, written);
        Assert.Empty(adapter.Inspect().DriftedKeys);

        // A new recorded value (a re-apply elsewhere) is compared afresh even though the file did not change.
        var entry = _store.Get(adapter.Kind, settings, "env.ANTHROPIC_BASE_URL")!;
        entry.AppliedValueJson = "\"http://127.0.0.1:9\"";
        _store.Upsert(entry);
        Assert.Equal(["env.ANTHROPIC_BASE_URL"], adapter.Inspect().DriftedKeys);
    }
}
