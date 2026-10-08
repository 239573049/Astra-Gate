using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests;

/// <summary>Claude Code: env keys in ~/.claude/settings.json (JSONC).</summary>
public class ClaudeCodeClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly ClaudeCodeClientAdapter _adapter;
    private readonly string _settings;

    public ClaudeCodeClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        _settings = _home.File(".claude", "settings.json");
        _home.WriteFile(_settings, TestHome.Fixture("claude-settings.json"));
    }

    private static EnableContext Ctx(string key = "astra-claude-x", string? model = "claude-sonnet-4-5") =>
        TestGateway.Context(key, null, new JsonObject
        {
            ["models"] = new JsonObject { ["ANTHROPIC_MODEL"] = model, ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "claude-haiku-4-5" },
        });

    [Fact]
    public void Enable_WritesOnlyEnvKeys_JsoncFormattingPreserved()
    {
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var text = _home.ReadFile(_settings);
        // Comments and unrelated content survive.
        Assert.Contains("// Personal Claude Code settings", text);
        Assert.Contains("// Custom variables must survive untouched", text);
        Assert.Contains("\"MY_TEAM_ENDPOINT\": \"https://internal.example.com\"", text);
        Assert.Contains("\"includeCoAuthoredBy\": false", text);
        var node = JsonNode.Parse(text, nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var env = node["env"]!;
        Assert.Equal("http://127.0.0.1:17321", env["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("astra-claude-x", env["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal("claude-sonnet-4-5", env["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Equal("claude-haiku-4-5", env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]!.GetValue<string>());
        Assert.Equal("vim", env["EDITOR"]!.GetValue<string>());
        Assert.Null(node["env"]!["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void Enable_EmptyModelSlots_WriteNothing()
    {
        // Every slot defaults to empty: the file keeps only what it had, no model key is added.
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-claude-x")));
        var node = JsonNode.Parse(_home.ReadFile(_settings), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var env = node["env"]!;
        Assert.Null(env["ANTHROPIC_MODEL"]);
        Assert.Null(env["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Null(env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Null(env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("astra-claude-x", env["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
    }

    [Fact]
    public void Enable_KeepsTheOlderSmallFastModelExtraWorking()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-claude-x", null, new JsonObject { ["smallFastModel"] = "glm-4.5-air" })));
        var node = JsonNode.Parse(_home.ReadFile(_settings), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        Assert.Equal("glm-4.5-air", node["env"]!["ANTHROPIC_DEFAULT_HAIKU_MODEL"]!.GetValue<string>());
    }

    [Fact]
    public void Enable_RemovesApiKey_DisableRestoresItByteIdentically()
    {
        // The API key sits last so that re-appending it on disable reproduces the original bytes.
        var original = """
            {
              "env": {
                "EDITOR": "vim",
                "ANTHROPIC_API_KEY": "sk-ant-user-own"
              }
            }
            """;
        _home.WriteFile(_settings, original);
        var originalBytes = _home.ReadFileBytes(_settings);

        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var enabled = JsonNode.Parse(_home.ReadFile(_settings), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true })!;
        Assert.Null(enabled["env"]!["ANTHROPIC_API_KEY"]);
        Assert.Equal("astra-claude-x", enabled["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());

        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(originalBytes, _home.ReadFileBytes(_settings));
        Assert.Empty(_store.List("claude-code"));
    }

    [Fact]
    public void Disable_NoDrift_RestoresByteIdentical()
    {
        var originalBytes = _home.ReadFileBytes(_settings);
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(originalBytes, _home.ReadFileBytes(_settings));
        Assert.Empty(_store.List("claude-code"));
    }

    [Fact]
    public void UserEdit_Drift_SkippedUntilForce()
    {
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var drifted = _home.ReadFile(_settings).Replace("http://127.0.0.1:17321", "http://my-proxy:8080");
        _home.WriteFile(_settings, drifted);

        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.True(report.HasDrift);
        Assert.Contains("http://my-proxy:8080", _home.ReadFile(_settings));

        _applier.ForceRestore(_adapter.PlanDisable());
        var after = JsonNode.Parse(_home.ReadFile(_settings), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        Assert.Null(after["env"]!["ANTHROPIC_BASE_URL"]);
        Assert.Null(after["env"]!["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        var emptyHome = new TestHome();
        try
        {
            var store = new InMemoryClientConfigStateStore();
            var applier = new ClientConfigApplier(store, emptyHome.Env, emptyHome.BackupRoot);
            var adapter = new ClaudeCodeClientAdapter(emptyHome.Env, store);
            var file = emptyHome.File(".claude", "settings.json");
            Assert.False(emptyHome.FileExists(file));

            applier.Apply(adapter.PlanEnable(Ctx()));
            Assert.True(emptyHome.FileExists(file));

            var report = applier.Disable(adapter.PlanDisable());
            Assert.False(report.HasDrift);
            Assert.False(emptyHome.FileExists(file));
            Assert.Contains(file, report.DeletedFiles);
        }
        finally
        {
            emptyHome.Dispose();
        }
    }

    [Fact]
    public void Inspect_WarnsOnApiKeyHelper()
    {
        _home.WriteFile(_settings, TestHome.Fixture("claude-settings.json").Replace("\"includeCoAuthoredBy\": false",
            "\"apiKeyHelper\": \"/bin/own-key.sh\",\n  \"includeCoAuthoredBy\": false"));
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Contains(status.Warnings, w => w.Contains("apiKeyHelper"));
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>Gemini CLI: .gemini/.env + .gemini/settings.json.</summary>
public class GeminiCliClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly GeminiCliClientAdapter _adapter;
    private readonly string _envFile;
    private readonly string _settingsFile;

    public GeminiCliClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new GeminiCliClientAdapter(_home.Env, _store);
        _envFile = _home.File(".gemini", ".env");
        _settingsFile = _home.File(".gemini", "settings.json");
        _home.WriteFile(_envFile, TestHome.Fixture("gemini-env.env"));
        _home.WriteFile(_settingsFile, TestHome.Fixture("gemini-settings.json"));
    }

    [Fact]
    public void Enable_WritesEnvKeys_AndPinsAuthType_PreservingComments()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-gem-1", "gemini-2.5-flash")));
        var envText = _home.ReadFile(_envFile);
        Assert.Contains("GOOGLE_GEMINI_BASE_URL=http://127.0.0.1:17321", envText);
        Assert.Contains("GEMINI_API_KEY=astra-gem-1", envText);
        Assert.Contains("GEMINI_MODEL=gemini-2.5-flash", envText);
        // Unrelated lines, comments and quoting survive untouched.
        Assert.Contains("CUSTOM_FLAG=keep-me", envText);
        Assert.Contains("# A comment line that must survive", envText);
        Assert.Contains("QUOTED_NAME=\"Doe, Jane\"", envText);
        Assert.DoesNotContain("AIzaSyOriginal", envText);

        var settingsText = _home.ReadFile(_settingsFile);
        Assert.Contains("// Gemini CLI settings", settingsText);
        Assert.Contains("\"selectedType\": \"gemini-api-key\"", settingsText);
        Assert.Contains("\"oauth-personal\"", settingsText.Replace("selectedAuthType", "__")); // the other key stays
        Assert.Contains("\"gemini-2.5-pro\"", settingsText);
    }

    [Fact]
    public void Disable_NoDrift_RestoresBothFilesByteIdentically()
    {
        var envOriginal = _home.ReadFileBytes(_envFile);
        var settingsOriginal = _home.ReadFileBytes(_settingsFile);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-gem-2", null)));
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(envOriginal, _home.ReadFileBytes(_envFile));
        Assert.Equal(settingsOriginal, _home.ReadFileBytes(_settingsFile));
        Assert.Empty(_store.List("gemini-cli"));
    }

    [Fact]
    public void EnvWithoutTrailingNewline_RestoresByteIdentically()
    {
        const string original = "GEMINI_API_KEY=orig";
        _home.WriteFile(_envFile, original);
        var originalBytes = _home.ReadFileBytes(_envFile);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-gem-3", null)));
        Assert.Contains("GOOGLE_GEMINI_BASE_URL", _home.ReadFile(_envFile));
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(originalBytes, _home.ReadFileBytes(_envFile)); // ReconcileTrailingNewline
    }

    [Fact]
    public void UserEditOfApiKey_Drift_ForceRestores()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-gem-4", null)));
        _home.WriteFile(_envFile, _home.ReadFile(_envFile).Replace("GEMINI_API_KEY=astra-gem-4", "GEMINI_API_KEY=mine"));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.True(report.HasDrift);
        Assert.Contains("GEMINI_API_KEY=mine", _home.ReadFile(_envFile));
        _applier.ForceRestore(_adapter.PlanDisable());
        // Force restores the ORIGINAL value (the key existed before the takeover).
        Assert.Contains("GEMINI_API_KEY=AIzaSyOriginal0123456789abcdef", _home.ReadFile(_envFile));
    }

    [Fact]
    public void FilesOriginallyAbsent_CreatedThenEnvDeleted()
    {
        var emptyHome = new TestHome();
        try
        {
            var store = new InMemoryClientConfigStateStore();
            var applier = new ClientConfigApplier(store, emptyHome.Env, emptyHome.BackupRoot);
            var adapter = new GeminiCliClientAdapter(emptyHome.Env, store);
            var envFile = emptyHome.File(".gemini", ".env");
            var settingsFile = emptyHome.File(".gemini", "settings.json");
            Assert.False(emptyHome.FileExists(envFile));
            applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-gem-5", null)));
            Assert.True(emptyHome.FileExists(envFile));
            var report = applier.Disable(adapter.PlanDisable());
            // Both files were pure Astra scaffolding and are removed again.
            Assert.False(emptyHome.FileExists(envFile));
            Assert.False(emptyHome.FileExists(settingsFile));
            Assert.Contains(envFile, report.DeletedFiles);
            Assert.Contains(settingsFile, report.DeletedFiles);
        }
        finally
        {
            emptyHome.Dispose();
        }
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>OpenCode: provider.astra added alongside existing providers (coexist mode).</summary>
public class OpenCodeClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly OpenCodeClientAdapter _adapter;
    private readonly string _config;

    public OpenCodeClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new OpenCodeClientAdapter(_home.Env, _store);
        _config = _home.File(".config", "opencode", "opencode.json");
        _home.WriteFile(_config, TestHome.Fixture("opencode.jsonc"));
    }

    private static EnableContext Ctx(string key = "astra-open-1", string? model = "gpt-5.1") => TestGateway.Context(key, model,
        new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["entry1"] = new JsonObject { ["id"] = "gpt-5.1", ["name"] = "GPT-5.1" },
            },
        });

    [Fact]
    public void Enable_AddsProviderAlongsideExistingOnes()
    {
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var text = _home.ReadFile(_config);
        Assert.Contains("// OpenCode keeps its own providers", text);
        var node = JsonNode.Parse(text, nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var provider = node["provider"]!;
        Assert.Equal("https://api.anthropic.com", provider["anthropic"]!["options"]!["baseURL"]!.GetValue<string>());
        Assert.Equal("sk-or-v1-000", provider["openrouter"]!["options"]!["apiKey"]!.GetValue<string>());
        var ours = provider["astra"]!;
        Assert.Equal("@ai-sdk/openai-compatible", ours["npm"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["options"]!["baseURL"]!.GetValue<string>());
        Assert.Equal("astra-open-1", ours["options"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("GPT-5.1", ours["models"]!["gpt-5.1"]!["name"]!.GetValue<string>());
        Assert.Equal("astra/gpt-5.1", node["model"]!.GetValue<string>());
        Assert.Equal("anthropic/claude-haiku-4-5", node["small_model"]!.GetValue<string>());
    }

    [Fact]
    public void Disable_RestoresByteIdentical_AndNeverTouchesForeignModel()
    {
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
        Assert.Empty(_store.List("opencode"));
    }

    [Fact]
    public void Disable_KeepsUserModel_ChosenAfterEnable()
    {
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        // The user picks a non-Astra model afterwards.
        _home.WriteFile(_config, _home.ReadFile(_config).Replace("astra/gpt-5.1", "openrouter/openai/gpt-5.1"));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Contains("\"model\": \"openrouter/openai/gpt-5.1\"", _home.ReadFile(_config));
        Assert.DoesNotContain("\"astra\"", _home.ReadFile(_config));
    }

    [Fact]
    public void ModelsRefresh_RewritesOnlyOurModelList()
    {
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        var refresh = _adapter.PlanModelsRefresh(TestGateway.Context("astra-open-1", null, new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["entry1"] = new JsonObject { ["id"] = "gpt-5.2", ["name"] = "GPT-5.2" },
            },
        }));
        Assert.Single(refresh.Changes, c => c.KeyPath == "provider.astra.models");
        _applier.Apply(refresh);
        var node = JsonNode.Parse(_home.ReadFile(_config), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        Assert.NotNull(node["provider"]!["astra"]!["models"]!["gpt-5.2"]);
        Assert.Null(node["provider"]!["astra"]!["models"]!["gpt-5.1"]);
        // The rest of the file is untouched: the openrouter provider still parses to the same values.
        Assert.Equal("OpenRouter", node["provider"]!["openrouter"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Disable_Removes_The_Provider_Object_Astra_Created_But_Keeps_A_Users_Own_Empty_One()
    {
        // No "provider" before Astra: enabling creates it, disabling must take it away again.
        const string bare = "{\n  // mine\n  \"theme\": \"tokyonight\"\n}\n";
        _home.WriteFile(_config, bare);
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(bare, _home.ReadFile(_config));

        // A user's own (empty) "provider" object is theirs and stays.
        using var other = new TestHome();
        var store = new InMemoryClientConfigStateStore();
        var applier = new ClientConfigApplier(store, other.Env, other.BackupRoot);
        var adapter = new OpenCodeClientAdapter(other.Env, store);
        var config = other.File(".config", "opencode", "opencode.json");
        const string withEmptyProvider = "{\n  \"theme\": \"tokyonight\",\n  \"provider\": {}\n}\n";
        other.WriteFile(config, withEmptyProvider);
        applier.Apply(adapter.PlanEnable(Ctx()));
        // A user edit elsewhere in the file prevents the wholesale byte restore, so the pruning rule itself decides.
        other.WriteFile(config, other.ReadFile(config).Replace("tokyonight", "dracula"));
        applier.Disable(adapter.PlanDisable());
        var node = JsonNode.Parse(other.ReadFile(config), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        Assert.NotNull(node["provider"]);
        Assert.Empty(node["provider"]!.AsObject());
        Assert.Equal("dracula", node["theme"]!.GetValue<string>());
    }

    [Fact]
    public void JsoncFile_Preferred_WhenBothExist()
    {
        var jsonc = _home.File(".config", "opencode", "opencode.jsonc");
        _home.WriteFile(jsonc, "{\n  \"theme\": \"opencode\",\n  // jsonc wins\n}\n");
        _applier.Apply(_adapter.PlanEnable(Ctx()));
        Assert.Contains("jsonc wins", _home.ReadFile(jsonc));
        Assert.Contains("\"astra\"", _home.ReadFile(jsonc));
        // The plain json file stays untouched when a jsonc config exists.
        Assert.DoesNotContain("astra", _home.ReadFile(_config));
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>Claude Desktop: 3p configLibrary profile + _meta.json + deploymentMode.</summary>
public class ClaudeDesktopClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly ClaudeDesktopClientAdapter _adapter;
    private readonly string _normalConfig;
    private readonly string _threepConfig;
    private readonly string _profileFile;
    private readonly string _metaFile;

    public ClaudeDesktopClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new ClaudeDesktopClientAdapter(_home.Env, _store);
        _normalConfig = _home.File("Library", "Application Support", "Claude", "claude_desktop_config.json");
        _threepConfig = _home.File("Library", "Application Support", "Claude-3p", "claude_desktop_config.json");
        _profileFile = _home.File("Library", "Application Support", "Claude-3p", "configLibrary",
            ClaudeDesktopClientAdapter.ProfileId + ".json");
        _metaFile = _home.File("Library", "Application Support", "Claude-3p", "configLibrary", "_meta.json");
        _home.WriteFile(_normalConfig, "{\n  \"globalShortcuts\": []\n}\n");
    }

    [Fact]
    public void Enable_WritesProfile_MetaAndDeploymentMode()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-desk-1", null)));
        var profile = JsonNode.Parse(_home.ReadFile(_profileFile), nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true })!;
        Assert.Equal("http://127.0.0.1:17321", profile["inferenceGatewayBaseUrl"]!.GetValue<string>());
        Assert.Equal("astra-desk-1", profile["inferenceGatewayApiKey"]!.GetValue<string>());
        Assert.Equal("gateway", profile["inferenceProvider"]!.GetValue<string>());
        Assert.Equal("bearer", profile["inferenceGatewayAuthScheme"]!.GetValue<string>());
        Assert.True(profile["disableDeploymentModeChooser"]!.GetValue<bool>());

        var meta = JsonNode.Parse(_home.ReadFile(_metaFile))!;
        Assert.Equal(ClaudeDesktopClientAdapter.ProfileId, meta["appliedId"]!.GetValue<string>());
        Assert.Contains(meta["entries"]!.AsArray(), e => e!["id"]!.GetValue<string>() == ClaudeDesktopClientAdapter.ProfileId);
        Assert.Equal("{\"id\":\"" + ClaudeDesktopClientAdapter.ProfileId + "\",\"name\":\"Astra\"}",
            meta["entries"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == ClaudeDesktopClientAdapter.ProfileId)!.ToJsonString());
        Assert.Equal("3p", JsonNode.Parse(_home.ReadFile(_normalConfig))!["deploymentMode"]!.GetValue<string>());
        Assert.Equal("3p", JsonNode.Parse(_home.ReadFile(_threepConfig))!["deploymentMode"]!.GetValue<string>());
    }

    [Fact]
    public void Disable_RestoresOriginalState()
    {
        var normalOriginal = _home.ReadFileBytes(_normalConfig);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-desk-2", null)));
        _applier.Disable(_adapter.PlanDisable());
        // Pre-existing file restored byte-identically.
        Assert.Equal(normalOriginal, _home.ReadFileBytes(_normalConfig));
        // Files Astra created are gone again.
        Assert.False(_home.FileExists(_profileFile));
        Assert.False(_home.FileExists(_metaFile));
        Assert.False(_home.FileExists(_threepConfig));
        Assert.Empty(_store.List("claude-desktop"));
    }

    [Fact]
    public void Disable_KeepsForeignMetaEntries_AndTheirAppliedId()
    {
        _home.WriteFile(_metaFile, """
            {
              "entries": [
                {"id": "someone-else", "name": "Other Gateway"}
              ],
              "appliedId": "someone-else"
            }
            """);
        var metaOriginal = _home.ReadFileBytes(_metaFile);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-desk-3", null)));
        var meta = JsonNode.Parse(_home.ReadFile(_metaFile))!;
        Assert.Equal(2, meta["entries"]!.AsArray().Count);
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(metaOriginal, _home.ReadFileBytes(_metaFile)); // foreign appliedId restored untouched
    }

    [Fact]
    public void Inspect_ReportsReverseEngineeredFormatWarning()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-desk-4", null)));
        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Contains(status.Warnings, w => w.Contains("reverse-engineered") || w.Contains("cc-switch"));
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>Grok Build: ~/.grok/config.toml with [models] default + [model.astra] profile.</summary>
public class GrokBuildClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly GrokBuildClientAdapter _adapter;
    private readonly string _config;

    public GrokBuildClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new GrokBuildClientAdapter(_home.Env, _store);
        _config = _home.File(".grok", "config.toml");
    }

    [Fact]
    public void Enable_FlipsDefault_AndAppendsProfile_ExactText()
    {
        _home.WriteFile(_config, """
            # Grok Build config
            telemetry = false

            [models]
            default = "personal"

            [model.personal]
            model = "grok-4"
            base_url = "https://api.x.ai/v1"
            api_key = "user-key"
            """);
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-grok-1", null)));
        Assert.Equal("""
            # Grok Build config
            telemetry = false

            [models]
            default = "astra"

            [model.personal]
            model = "grok-4"
            base_url = "https://api.x.ai/v1"
            api_key = "user-key"

            [model.astra]
            model = "grok-4"
            base_url = "http://127.0.0.1:17321/v1"
            name = "Astra"
            api_key = "astra-grok-1"
            api_backend = "responses"
            context_window = 256000
            """ + "\n", _home.ReadFile(_config));

        // Disabling restores byte-identically (the profile we created is removed again).
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
        Assert.Empty(_store.List("grok-build"));
    }

    [Fact]
    public void Disable_RestoresRefreshedFields_WhenProfilePreExisted()
    {
        const string original = """
            [models]
            default = "astra"

            [model.astra]
            model = "grok-4"
            base_url = "http://localhost:9999/v1"
            name = "My own"
            api_key = "mine"
            """;
        _home.WriteFile(_config, original);
        var originalBytes = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-grok-2", "grok-4-fast")));
        Assert.Contains("http://127.0.0.1:17321/v1", _home.ReadFile(_config));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(originalBytes, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Assert.False(_home.FileExists(_config));
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-grok-3", null)));
        Assert.True(_home.FileExists(_config));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(_home.FileExists(_config));
        Assert.Contains(_config, report.DeletedFiles);
    }

    [Fact]
    public void Inspect_WarnsAboutUnofficialFormat()
    {
        _home.WriteFile(_config, "[models]\ndefault = \"x\"\n");
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-grok-4", null)));
        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Contains(status.Warnings, w => w.Contains("cc-switch") || w.Contains("official"));
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>The default registry exposes every client kind, in order, with unique kinds.</summary>
public class ClientAdapterRegistryTests
{
    [Fact]
    public void CreateDefault_ListsAllClients()
    {
        using var home = new TestHome();
        var registry = ClientAdapterRegistry.CreateDefault(home.Env, new InMemoryClientConfigStateStore());
        Assert.Equal(Astra.Core.Clients.ClientKinds.All, registry.All.Select(a => a.Kind));
        var kinds = registry.All.Select(a => a.Kind).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(11, kinds.Count);
        Assert.Equal(kinds, kinds.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList());
        Assert.NotNull(registry.Get("codex"));
        Assert.Null(registry.Get("nope"));
        Assert.Equal("codex", registry.Require("codex").Kind);
        Assert.Throws<KeyNotFoundException>(() => registry.Require("nope"));
    }
}
