using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Privacy;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

/// <summary>Stable placeholders across requests, the persisted key, "once masked always masked" and failing closed.</summary>
public class PrivacyGuardStabilityTests
{
    /// <summary>Reversible stand-in for DataProtection.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "p:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[2..]));
    }

    private static async Task<AstraDatabase> NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-gw-privacy-stable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return await AstraDatabase.InitializeAsync(new AstraPaths(dir));
    }

    private static PrivacySettings Enforcing(Action<PrivacySettings>? change = null)
    {
        var s = new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact };
        change?.Invoke(s);
        return s;
    }

    private static string Body(string text) => new JsonObject { ["model"] = "m", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }) }.ToJsonString();

    [Fact]
    public async Task One_Conversation_Keeps_One_Placeholder_Per_Value_Across_Requests()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(Enforcing());

        var first = guard.Inspect(Body("mail alice@corp.io"), null);
        var second = guard.Inspect(Body("earlier I mailed alice@corp.io and bob@corp.io"), null);

        var placeholder = first.RestoreMap.Single(kv => kv.Value == "alice@corp.io").Key;
        Assert.Equal(placeholder, second.RestoreMap.Single(kv => kv.Value == "alice@corp.io").Key);
        Assert.Equal(2, second.RestoreMap.Count);
        Assert.Contains(placeholder, second.Body);
    }

    [Fact]
    public async Task The_Placeholder_Key_Is_Made_Once_Kept_Protected_And_Survives_A_Restart()
    {
        var db = await NewDb();
        var protector = new FakeProtector();
        var one = new PrivacyGuardService(db, protector);
        await one.LoadAsync();
        await one.UpdateAsync(Enforcing());
        var placeholder = one.Inspect(Body("mail alice@corp.io"), null).RestoreMap.Keys.Single();

        var stored = await db.Settings.GetAsync<string>("privacy_placeholder_key");
        Assert.StartsWith("p:", stored); // protected, not the raw key
        Assert.DoesNotContain("alice", stored);

        var two = new PrivacyGuardService(db, protector); // a new process
        await two.LoadAsync();
        Assert.Equal(placeholder, two.Inspect(Body("mail alice@corp.io"), null).RestoreMap.Keys.Single());

        // another install (its own key) names it differently
        var other = new PrivacyGuardService(await NewDb(), protector);
        await other.LoadAsync();
        await other.UpdateAsync(Enforcing());
        Assert.NotEqual(placeholder, other.Inspect(Body("mail alice@corp.io"), null).RestoreMap.Keys.Single());
    }

    [Fact]
    public async Task A_Key_That_Cannot_Be_Unprotected_Is_Replaced_Instead_Of_Failing_The_Guard()
    {
        var db = await NewDb();
        await db.Settings.SetAsync("privacy_placeholder_key", "garbage-not-protected");
        var guard = new PrivacyGuardService(db, new FakeProtector());
        await guard.LoadAsync();
        await guard.UpdateAsync(Enforcing());
        Assert.Single(guard.Inspect(Body("mail alice@corp.io"), null).RestoreMap);
    }

    [Fact]
    public async Task A_Value_Masked_Once_Stays_Masked_When_Its_Rule_No_Longer_Finds_It()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(Enforcing(s =>
        {
            s.DefaultAction = PrivacyActions.Off;
            s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.p", Name = "p", Pattern = "Project-[A-Z]{4}" });
            s.CategoryActions[PrivacyCategories.Custom] = PrivacyActions.Redact;
        }));
        var first = guard.Inspect(Body("ship Project-ABCD"), null);
        Assert.DoesNotContain("Project-ABCD", first.Body);

        // the pattern changes (it would not match this text any more), the value is still masked
        await guard.UpdateAsync(Enforcing(s =>
        {
            s.DefaultAction = PrivacyActions.Off;
            s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.p", Name = "p", Pattern = "Nothing-[0-9]{4}" });
            s.CategoryActions[PrivacyCategories.Custom] = PrivacyActions.Redact;
        }));
        var later = guard.Inspect(Body("status of Project-ABCD?"), null);
        Assert.DoesNotContain("Project-ABCD", later.Body);
        Assert.Equal(first.RestoreMap.Keys, later.RestoreMap.Keys);
    }

    [Fact]
    public async Task The_Dry_Run_Shows_Real_Placeholders_And_Teaches_The_Guard_Nothing()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(Enforcing());

        var tried = guard.DryRun("write to carol@corp.io", isJson: false, null);
        var real = guard.Inspect(Body("write to carol@corp.io"), null);
        Assert.Contains(tried.Redactions[0].Placeholder, tried.Text);
        Assert.Equal(tried.Redactions[0].Placeholder, real.RestoreMap.Keys.Single()); // the placeholder a request gets

        var other = new PrivacyGuardService(await NewDb());
        await other.UpdateAsync(Enforcing(s => s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.q", Name = "q", Pattern = "Q-[0-9]{4}" })));
        other.DryRun("Q-1234", false, null);
        // not remembered: with the rule gone, the value is not masked in a real request
        await other.UpdateAsync(Enforcing());
        Assert.Contains("Q-1234", other.Inspect(Body("Q-1234"), null).Body);
    }

    // --- failing closed -----------------------------------------------------------------------------------------------

    private static PrivacySettings WithCatastrophicRule(bool dryRun) => Enforcing(s =>
    {
        s.DryRun = dryRun;
        s.CategoryActions[PrivacyCategories.Custom] = PrivacyActions.Redact;
        s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.slow", Name = "slow", Pattern = "^(a+)+$" });
    });

    private static readonly string Slow = new string('a', 30_000) + "!";

    [Fact]
    public async Task A_Detector_Error_Refuses_The_Request_With_A_503_Instead_Of_Sending_It_Unmasked()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(WithCatastrophicRule(dryRun: false));

        var text = Assert.Throws<GatewayException>(() => guard.Inspect(Body(Slow), null));
        Assert.Equal(503, text.Status);
        Assert.Equal("api_error", text.Type);
        Assert.Contains("隐私护栏", text.Message);

        // the parsed-body path is guarded the same way
        var json = Body(Slow);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var tree = JsonObject.Create(document.RootElement)!;
        Assert.Equal(503, Assert.Throws<GatewayException>(() => guard.InspectJson(document.RootElement, tree, null)).Status);
    }

    [Fact]
    public async Task In_Dry_Run_A_Detector_Error_Does_Not_Stop_Traffic_Nothing_Is_Masked_Anyway()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(WithCatastrophicRule(dryRun: true));
        var result = guard.Inspect(Body(Slow), null);
        Assert.True(result.DryRun);
        Assert.False(result.Blocked);
    }

    [Fact]
    public async Task A_Disabled_Guard_Never_Refuses()
    {
        var guard = new PrivacyGuardService(await NewDb());
        await guard.UpdateAsync(WithCatastrophicRule(dryRun: false).Also(s => s.Enabled = false));
        Assert.False(guard.Inspect(Body(Slow), null).Applied);
    }

    [Fact]
    public async Task An_Unreadable_Stored_Policy_Fails_Closed_Until_A_Policy_Is_Saved()
    {
        var db = await NewDb();
        // a blob the settings type cannot read: whether the guard was on is unknown
        await db.Settings.SetAsync(PrivacySettings.StorageKey, "not a policy");
        var guard = new PrivacyGuardService(db);
        await guard.LoadAsync();

        Assert.Equal(503, Assert.Throws<GatewayException>(() => guard.Inspect(Body("hello"), null)).Status);

        await guard.UpdateAsync(Enforcing()); // the user saves a policy: the guard runs again
        Assert.True(guard.Inspect(Body("hello"), null).Applied);
    }
}

internal static class PrivacySettingsExtensions
{
    public static PrivacySettings Also(this PrivacySettings settings, Action<PrivacySettings> change)
    {
        change(settings);
        return settings;
    }
}
