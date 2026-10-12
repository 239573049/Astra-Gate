using Astra.Core;
using Astra.Core.Privacy;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class PrivacyGuardServiceTests
{
    private static async Task<AstraDatabase> NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-gw-privacy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return await AstraDatabase.InitializeAsync(new AstraPaths(dir));
    }

    private static PrivacySettings Settings(Action<PrivacySettings>? mutate = null)
    {
        var settings = new PrivacySettings();
        mutate?.Invoke(settings);
        return settings;
    }

    private const string RequestJson = """
        {"model":"claude-x","messages":[{"role":"user","content":"my key is AKIAIOSFODNN7EXAMPLE"}]}
        """;

    [Fact]
    public async Task Disabled_By_Default_Passes_Requests_Unchanged()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.LoadAsync();

        var result = guard.Inspect(RequestJson, "claude-code");

        Assert.False(result.Applied);
        Assert.False(result.Blocked);
        Assert.Empty(result.Hits);
        Assert.Equal(RequestJson, result.Body);
    }

    [Fact]
    public async Task DryRun_Records_Hits_But_Returns_The_Original_Body()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = true;
            s.DefaultAction = PrivacyActions.Redact;
        }));

        var result = guard.Inspect(RequestJson, "claude-code");

        Assert.True(result.Applied);
        Assert.True(result.DryRun);
        Assert.False(result.Blocked);
        Assert.Equal(RequestJson, result.Body); // untouched
        Assert.Contains(result.Hits, h => h.Category == PrivacyCategories.AwsKey);
    }

    [Fact]
    public async Task Redact_Rewrites_The_Body_And_Prepares_A_Restorer()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
        }));

        var result = guard.Inspect(RequestJson, "claude-code");

        Assert.False(result.Blocked);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.Body);
        Assert.Single(result.RestoreMap);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", result.RestoreMap.Values.Single());

        var restorer = new ChunkRestorer(result.RestoreMap);
        var restored = restorer.Push(result.Body) + restorer.Flush();
        Assert.Equal(RequestJson, restored);
    }

    [Fact]
    public async Task Redactions_In_Separate_Strings_Get_Distinct_Placeholders()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
        }));
        const string twoStrings = """
            {"model":"claude-x","system":"ops mail alice@example.com","messages":[{"role":"user","content":"cc bob@example.com"}]}
            """;

        var result = guard.Inspect(twoStrings, "claude-code");

        Assert.Equal(2, result.RestoreMap.Count);
        Assert.Equal(["alice@example.com", "bob@example.com"], result.RestoreMap.Values.Order().ToArray());
        var restorer = new ChunkRestorer(result.RestoreMap);
        Assert.Equal(twoStrings, restorer.Push(result.Body) + restorer.Flush());
    }

    [Fact]
    public async Task Block_Rejects_The_Request_And_Keeps_The_Original_Body()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.CategoryActions = new Dictionary<string, string> { [PrivacyCategories.AwsKey] = PrivacyActions.Block };
        }));

        var result = guard.Inspect(RequestJson, "claude-code");

        Assert.True(result.Blocked);
        Assert.Equal(RequestJson, result.Body);
        Assert.Empty(result.RestoreMap);
    }

    [Fact]
    public async Task Requests_Without_Matches_Are_Forwarded_Byte_For_Byte()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
        }));

        const string clean = """{"model":"gpt-5","messages":[{"role":"user","content":"hello"}]}""";
        var result = guard.Inspect(clean, "codex");

        Assert.False(result.Blocked);
        Assert.Equal(clean, result.Body);
    }

    [Fact]
    public async Task Policy_Persists_Across_Service_Restarts_And_Validates_Patterns()
    {
        var db = await NewDb();
        var first = new PrivacyGuardService(db);
        await first.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.CustomRules =
            [
                new PrivacyCustomRule { Id = "custom.employee", Name = "员工编号", Pattern = @"EMP-[0-9]{6}" },
            ];
        }));

        // A fresh service instance (server restart) picks the policy up from the database.
        var second = new PrivacyGuardService(db);
        await second.LoadAsync();
        var result = second.Inspect("""{"content":"EMP-123456"}""", null);
        Assert.Contains("[REDACTED:custom#1]", result.Body);

        await Assert.ThrowsAsync<ArgumentException>(() => second.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.CustomRules = [new PrivacyCustomRule { Id = "custom.bad", Name = "bad", Pattern = "([" }];
        })));
    }

    [Fact]
    public async Task Policy_And_Custom_Regexes_Are_Reused_For_Repeated_And_Unknown_Clients()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.ClientDefaults = new Dictionary<string, string> { ["claude-code"] = PrivacyActions.Block };
            s.CustomRules =
            [
                new PrivacyCustomRule { Id = "custom.employee", Name = "员工编号", Pattern = @"EMP-[0-9]{6}" },
            ];
        }));

        var first = guard.EffectiveFor("claude-code");
        var again = guard.EffectiveFor("claude-code");

        // The same client kind reuses the materialized policy, custom regex included.
        Assert.Same(first, again);
        Assert.Throws<NotSupportedException>(() => ((IList<PrivacyRule>)first.Rules).Clear());
        Assert.Same(first.Rules.Single(r => r.Id == "custom.employee").Regex,
            again.Rules.Single(r => r.Id == "custom.employee").Regex);

        // Clients the policy says nothing about all resolve to the same default action, so they share
        // one entry — an unknown (and unbounded) client id never materializes a policy of its own.
        var unknown = guard.EffectiveFor("never-seen");
        Assert.Same(unknown, guard.EffectiveFor("another-unknown"));
        Assert.All(Enumerable.Range(0, 50).Select(i => guard.EffectiveFor($"unknown-{i}")),
            p => Assert.Same(unknown, p));
    }

    [Fact]
    public async Task Update_Replaces_The_Cached_Policy_And_A_Rejected_Update_Keeps_It()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.CustomRules =
            [
                new PrivacyCustomRule { Id = "custom.employee", Name = "员工编号", Pattern = @"EMP-[0-9]{6}" },
            ];
        }));
        var before = guard.EffectiveFor(null);
        Assert.Contains("[REDACTED:custom#1]", guard.Inspect("""{"content":"EMP-123456"}""", null).Body);

        // The update publishes a new generation: its own policy, with the new rule set.
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.CustomRules = [new PrivacyCustomRule { Id = "custom.ticket", Name = "工单", Pattern = @"TKT-[0-9]{4}" }];
        }));

        var after = guard.EffectiveFor(null);
        Assert.NotSame(before, after);
        Assert.DoesNotContain(after.Rules, r => r.Id == "custom.employee");
        Assert.Contains(after.Rules, r => r.Id == "custom.ticket");
        Assert.Contains("[REDACTED:custom#1]", guard.Inspect("""{"content":"TKT-1234"}""", null).Body);

        // A rejected update publishes nothing: neither the active generation nor the stored settings move.
        await Assert.ThrowsAsync<ArgumentException>(() => guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.CustomRules = [new PrivacyCustomRule { Id = "custom.bad", Name = "bad", Pattern = "([" }];
        })));

        Assert.Same(after, guard.EffectiveFor(null));
        Assert.Contains("[REDACTED:custom#1]", guard.Inspect("""{"content":"TKT-1234"}""", null).Body);
        var stored = await db.Settings.GetAsync<PrivacySettings>(PrivacySettings.StorageKey);
        Assert.NotNull(stored);
        Assert.Equal("custom.ticket", Assert.Single(stored!.CustomRules).Id);
    }

    [Fact]
    public async Task Mutating_Settings_Handed_In_Or_Out_Never_Reaches_The_Published_Guard()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        var input = Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = false;
            s.DefaultAction = PrivacyActions.Redact;
            s.ClientDefaults = new Dictionary<string, string> { ["claude-code"] = PrivacyActions.Redact };
            s.CustomRules =
            [
                new PrivacyCustomRule { Id = "custom.employee", Name = "员工编号", Pattern = @"EMP-[0-9]{6}" },
            ];
        });
        var returned = await guard.UpdateAsync(input);

        // The caller keeps its instance and mutates it after the call.
        input.Enabled = false;
        input.DefaultAction = PrivacyActions.Off;
        input.ClientDefaults.Clear();
        input.CustomRules.Add(new PrivacyCustomRule { Id = "custom.bad", Name = "bad", Pattern = "([" });
        returned.DefaultAction = PrivacyActions.Off;
        returned.CustomRules.Clear();

        Assert.True(guard.Settings.Enabled);
        Assert.Equal(PrivacyActions.Redact, guard.Settings.DefaultAction);
        Assert.Single(guard.Settings.CustomRules);
        Assert.Equal(PrivacyActions.Redact, guard.EffectiveFor("claude-code").ActionFor("builtin.email", PrivacyCategories.Email));
        Assert.Contains("[REDACTED:custom#1]", guard.Inspect("""{"content":"EMP-123456"}""", null).Body);

        // Neither does mutating the copy the getter hands out.
        var read = guard.Settings;
        read.Enabled = false;
        read.DefaultAction = PrivacyActions.Off;
        read.ClientDefaults.Clear();
        read.CustomRules.Clear();

        Assert.True(guard.Settings.Enabled);
        Assert.Equal(PrivacyActions.Redact, guard.Settings.DefaultAction);
        Assert.Single(guard.Settings.CustomRules);
        Assert.Equal(PrivacyActions.Redact, guard.EffectiveFor("claude-code").ActionFor("builtin.email", PrivacyCategories.Email));
        Assert.Contains("[REDACTED:custom#1]", guard.Inspect("""{"content":"EMP-123456"}""", null).Body);

        // What was persisted is the snapshot's copy too, so the input mutations never reached the database.
        var stored = await db.Settings.GetAsync<PrivacySettings>(PrivacySettings.StorageKey);
        Assert.NotNull(stored);
        Assert.True(stored!.Enabled);
        Assert.Equal(PrivacyActions.Redact, stored.DefaultAction);
        Assert.Equal(PrivacyActions.Redact, stored.ClientDefaults["claude-code"]);
        Assert.Single(stored.CustomRules);
    }

    [Fact]
    public async Task Client_Defaults_And_Override_Bools_Resolve_To_Distinct_Policies()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = true;
            s.DefaultAction = PrivacyActions.Redact;
            s.ClientDefaults = new Dictionary<string, string>
            {
                ["claude-code"] = PrivacyActions.Block,
                ["codex"] = PrivacyActions.Warn,
            };
        }));

        var claude = guard.EffectiveFor("claude-code");
        var codex = guard.EffectiveFor("codex");
        var unknown = guard.EffectiveFor("never-seen");

        // Each configured client default is its own entry with its own action; everything else is the default.
        Assert.Equal(PrivacyActions.Block, claude.ActionFor("builtin.aws-access-key", PrivacyCategories.AwsKey));
        Assert.Equal(PrivacyActions.Warn, codex.ActionFor("builtin.aws-access-key", PrivacyCategories.AwsKey));
        Assert.Equal(PrivacyActions.Redact, unknown.ActionFor("builtin.aws-access-key", PrivacyCategories.AwsKey));
        Assert.NotSame(claude, codex);
        Assert.NotSame(claude, unknown);
        Assert.NotSame(codex, unknown);

        // Override combinations are separate entries and leave the plain policy alone (the dry-run API
        // asks for enabled: true, dryRun: false).
        var dryRunApi = guard.EffectiveFor("codex", enabledOverride: true, dryRunOverride: false);
        Assert.True(dryRunApi.Enabled);
        Assert.False(dryRunApi.DryRun);
        var disabled = guard.EffectiveFor("codex", enabledOverride: false, dryRunOverride: true);
        Assert.False(disabled.Enabled);
        Assert.True(disabled.DryRun);

        Assert.NotSame(codex, dryRunApi);
        Assert.NotSame(dryRunApi, disabled);
        Assert.True(codex.Enabled);
        Assert.True(codex.DryRun);
        Assert.Same(codex, guard.EffectiveFor("codex"));
    }

    [Fact]
    public async Task A_Request_Never_Mixes_Settings_From_Two_Generations()
    {
        var db = await NewDb();
        var guard = new PrivacyGuardService(db);
        await guard.UpdateAsync(Settings(s =>
        {
            s.Enabled = true;
            s.DryRun = true;
            s.DefaultAction = PrivacyActions.Redact;
        }));

        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = guard.Inspect(RequestJson, "claude-code");
                // Generation A (enabled, dry run) keeps the body and reports DryRun; generation B is a
                // pass-through. "applied, not dry run" or a rewritten body can only come from a request
                // reading its settings and its policy from two different generations.
                Assert.False(result.Applied && !result.DryRun);
                if (result.Applied) Assert.Equal(RequestJson, result.Body);
            }
        })).ToArray();

        for (var i = 0; i < 25; i++)
        {
            await guard.UpdateAsync(Settings(s =>
            {
                s.Enabled = false;
                s.DryRun = false;
                s.DefaultAction = PrivacyActions.Redact;
            }));
            await guard.UpdateAsync(Settings(s =>
            {
                s.Enabled = true;
                s.DryRun = true;
                s.DefaultAction = PrivacyActions.Redact;
            }));
        }

        stop.Cancel();
        await Task.WhenAll(readers);
    }

    [Fact]
    public void Report_Json_Round_Trips()
    {
        var json = PrivacyGuardService.ReportJson(dryRun: false, blocked: true,
        [
            new PrivacyHit { RuleId = "builtin.aws-access-key", Category = PrivacyCategories.AwsKey, Action = PrivacyActions.Block, Count = 1 },
        ], redactionCount: 2);

        var report = Json.Deserialize<PrivacyReport>(json)!;
        Assert.False(report.DryRun);
        Assert.True(report.Blocked);
        Assert.Equal(2, report.Redactions);
        var hit = Assert.Single(report.Hits);
        Assert.Equal("builtin.aws-access-key", hit.RuleId);
        Assert.Equal(1, hit.Count);
    }
}
