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
