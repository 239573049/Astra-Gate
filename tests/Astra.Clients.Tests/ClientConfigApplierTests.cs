using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests;

/// <summary>End-to-end tests of the applier using the Codex adapter against realistic fixtures.</summary>
public class ClientConfigApplierTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly CodexClientAdapter _codex;
    private readonly string _config;

    public ClientConfigApplierTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _codex = new CodexClientAdapter(_home.Env, _store);
        _config = _home.File(".codex", "config.toml");
        _home.WriteFile(_config, TestHome.Fixture("codex-config.toml"));
    }

    private static EnableContext Ctx(string key = "astra-codex-abc", string? model = "gpt-5.1") =>
        TestGateway.Context(key, model);

    [Fact]
    public void Enable_ChangesOnlyTargetKeys_ExactText()
    {
        var plan = _codex.PlanEnable(Ctx());
        _applier.Apply(plan);
        Assert.Equal("""
            # Codex configuration
            # Written by hand; keep the comments.

            model = "gpt-5.1"
            model_reasoning_effort = "high"
            approval_policy = "on-request"
            sandbox_mode = "workspace-write"
            model_provider = "astra"

            # My preferred shell environment
            [shell_environment_policy]
            inherit = "core"
            ignore = ["AWS_*", "AZURE_*"]

            [projects."/Users/token/code/astra"]
            trust_level = "trusted"

            [sandbox_workspace_write]
            network_access = true

            # Third-party providers live here
            [model_providers.openrouter]
            name = "OpenRouter"
            base_url = "https://openrouter.ai/api/v1"
            env_key = "OPENROUTER_API_KEY"
            wire_api = "responses"

            [profiles.fast]
            model_provider = "openrouter"
            model = "openai/gpt-5.1"

            [model_providers.astra]
            name = "Astra"
            base_url = "http://127.0.0.1:17321/v1"
            wire_api = "responses"
            experimental_bearer_token = "astra-codex-abc"
            requires_openai_auth = false
            """ + "\n", _home.ReadFile(_config));
    }

    [Fact]
    public void Enable_PreviewDiff_MatchesActualFileChange()
    {
        var plan = _codex.PlanEnable(Ctx());
        Assert.Single(plan.Diffs);
        var before = _home.ReadFile(_config);
        _applier.Apply(plan);
        var after = _home.ReadFile(_config);
        // Rebuild "after" from the diff preview: apply +/- lines to the original, hunk by hunk.
        var preview = ApplyDiff(before, plan.Diffs[0].UnifiedDiff);
        Assert.Equal(after, preview);
    }

    private static string ApplyDiff(string before, string unified)
    {
        var lines = before.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        var result = new List<string>();
        var consumed = 0; // old lines already emitted
        foreach (var raw in unified.Split('\n'))
        {
            if (raw.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(raw, @"^@@ -(\d+)");
                var start = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
                while (consumed < start) result.Add(lines[consumed++]);
            }
            else if (raw.Length == 0 || raw.StartsWith("---", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal))
                continue;
            else if (raw.StartsWith('-')) consumed++;
            else if (raw.StartsWith('+')) result.Add(raw[1..]);
            else { result.Add(raw[1..]); consumed++; }
        }
        while (consumed < lines.Length) result.Add(lines[consumed++]);
        return string.Join("\n", result) + "\n";
    }

    [Fact]
    public void Disable_NoDrift_RestoresByteIdentical()
    {
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_codex.PlanEnable(Ctx()));
        var report = _applier.Disable(_codex.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(original, _home.ReadFileBytes(_config));
        Assert.Empty(_store.List("codex"));
    }

    [Fact]
    public void Disable_RestoresRefreshedFields_WhenTablePreExisted()
    {
        const string original = """
            model = "gpt-5.1-codex"
            model_provider = "openrouter"

            [model_providers.astra]
            name = "My own provider"
            base_url = "http://localhost:9999/v1"
            experimental_bearer_token = "user-own-token"
            """;
        _home.WriteFile(_config, original);
        var originalBytes = _home.ReadFileBytes(_config);

        _applier.Apply(_codex.PlanEnable(Ctx()));
        var refreshed = _home.ReadFile(_config);
        Assert.Contains("http://127.0.0.1:17321/v1", refreshed);
        Assert.Contains("astra-codex-abc", refreshed);

        var report = _applier.Disable(_codex.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void UserEditOfOurKey_DisableSkipsAndReportsDrift()
    {
        _applier.Apply(_codex.PlanEnable(Ctx()));
        // The user changes our model_provider value.
        var drifted = _home.ReadFile(_config).Replace("model_provider = \"astra\"", "model_provider = \"openrouter\"");
        _home.WriteFile(_config, drifted);

        var report = _applier.Disable(_codex.PlanDisable());
        Assert.True(report.HasDrift);
        Assert.Contains(report.Keys, k => k.KeyPath == "model_provider" && k.Outcome == RestoreKeyOutcome.Drifted);
        Assert.Contains("model_provider = \"openrouter\"", _home.ReadFile(_config));

        // Force restore wins anyway: the top-level key is gone again (the [profiles.fast] one stays).
        var force = _applier.ForceRestore(_codex.PlanDisable());
        Assert.False(force.HasDrift);
        var afterForce = new TomlEditor(_home.ReadFile(_config));
        Assert.Null(afterForce.GetValue("model_provider"));
        Assert.Equal("\"gpt-5.1-codex\"", afterForce.GetValue("model"));
    }

    [Fact]
    public void ReEnableTwice_KeepsTrueOriginal()
    {
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_codex.PlanEnable(Ctx("astra-codex-key-1")));
        _applier.Disable(_codex.PlanDisable());
        // Second cycle with a rotated key must not overwrite the recorded original.
        _applier.Apply(_codex.PlanEnable(Ctx("astra-codex-key-2")));
        _applier.Disable(_codex.PlanDisable());
        Assert.Equal(original, _home.ReadFileBytes(_config));
        Assert.Empty(_store.List("codex"));
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        var emptyHome = new TestHome();
        try
        {
            var store = new InMemoryClientConfigStateStore();
            var applier = new ClientConfigApplier(store, emptyHome.Env, emptyHome.BackupRoot);
            var adapter = new CodexClientAdapter(emptyHome.Env, store);
            var file = emptyHome.File(".codex", "config.toml");
            Assert.False(emptyHome.FileExists(file));

            applier.Apply(adapter.PlanEnable(Ctx()));
            Assert.True(emptyHome.FileExists(file));
            var created = emptyHome.ReadFile(file);
            Assert.Contains("model_provider = \"astra\"", created);

            var report = applier.Disable(adapter.PlanDisable());
            Assert.False(report.HasDrift);
            Assert.False(emptyHome.FileExists(file));
            Assert.Contains(report.DeletedFiles, f => f == file);
        }
        finally
        {
            emptyHome.Dispose();
        }
    }

    [Fact]
    public void CrlfFile_EnablesAndDisablesByteIdentically()
    {
        var original = TestHome.Fixture("codex-config.toml").Replace("\n", "\r\n");
        _home.WriteFile(_config, original);
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_codex.PlanEnable(Ctx()));
        Assert.Contains("model_provider = \"astra\"\r\n", _home.ReadFile(_config));
        _applier.Disable(_codex.PlanDisable());
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void FirstWriteBackup_KeptForever_WithManifest()
    {
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_codex.PlanEnable(Ctx()));
        var firstWriteDir = System.IO.Path.Combine(_home.BackupRoot, "codex", "first-write");
        var backups = _applier.ListBackups("codex");
        var fw = Assert.Single(backups, b => b.IsFirstWrite);
        var entry = Assert.Single(fw.Files);
        Assert.True(entry.Existed);
        Assert.Equal(_config, entry.OriginalPath);
        Assert.Equal(originalBytes, _home.ReadFileBytes(System.IO.Path.Combine(firstWriteDir, entry.BackupFile!)));
    }

    [Fact]
    public void FirstWriteBackup_RecordsMissingFile()
    {
        var emptyHome = new TestHome();
        try
        {
            var store = new InMemoryClientConfigStateStore();
            var applier = new ClientConfigApplier(store, emptyHome.Env, emptyHome.BackupRoot);
            applier.Apply(new CodexClientAdapter(emptyHome.Env, store).PlanEnable(Ctx()));
            var fw = Assert.Single(applier.ListBackups("codex"), b => b.IsFirstWrite);
            Assert.False(Assert.Single(fw.Files).Existed);
        }
        finally
        {
            emptyHome.Dispose();
        }
    }

    [Fact]
    public void RollingBackups_KeptAtMost20()
    {
        _applier.Apply(_codex.PlanEnable(Ctx("k0", null)));
        for (var i = 1; i <= 24; i++)
            _applier.Apply(_codex.PlanEnable(Ctx($"k{i}", null)));

        var backups = _applier.ListBackups("codex").ToList();
        Assert.Equal(21, backups.Count); // first-write + 20 rolling
        Assert.Single(backups, b => b.IsFirstWrite);
        foreach (var b in backups) Assert.Single(b.Files);
    }

    [Fact]
    public void RestoreFromBackup_RestoresOriginalBytes()
    {
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_codex.PlanEnable(Ctx()));
        _home.WriteFile(_config, "totally = \"broken\"\n");
        _applier.RestoreFromBackup("codex", "first-write");
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void RestoreAll_Purge_RemovesAstraTable()
    {
        var geminiAdapter = new GeminiCliClientAdapter(_home.Env, _store);
        var envFile = _home.File(".gemini", ".env");
        var settingsFile = _home.File(".gemini", "settings.json");
        _home.WriteFile(envFile, TestHome.Fixture("gemini-env.env"));
        _home.WriteFile(settingsFile, TestHome.Fixture("gemini-settings.json"));
        var codexOriginal = _home.ReadFileBytes(_config);
        var envOriginal = _home.ReadFileBytes(envFile);
        var settingsOriginal = _home.ReadFileBytes(settingsFile);

        _applier.Apply(_codex.PlanEnable(Ctx()));
        _applier.Apply(geminiAdapter.PlanEnable(TestGateway.Context("astra-gemini-x", null)));

        var reports = _applier.RestoreAll([_codex, geminiAdapter], purge: true);
        Assert.All(reports, r => Assert.False(r.HasDrift));
        Assert.Equal(codexOriginal, _home.ReadFileBytes(_config));
        Assert.Equal(envOriginal, _home.ReadFileBytes(envFile));
        Assert.Equal(settingsOriginal, _home.ReadFileBytes(settingsFile));
        // Purge removed the whole provider table (a plain disable would keep it).
        Assert.DoesNotContain("[model_providers.astra]", _home.ReadFile(_config));
    }

    public void Dispose() => _home.Dispose();
}
