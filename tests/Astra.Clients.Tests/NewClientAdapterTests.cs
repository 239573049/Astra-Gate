using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests;

file static class Models
{
    /// <summary>The "models" extra as ClientService builds it from the bound provider.</summary>
    public static JsonObject Extra(params string[] ids)
    {
        var models = new JsonObject();
        foreach (var id in ids) models[id] = new JsonObject { ["id"] = id, ["name"] = id.ToUpperInvariant() };
        return new JsonObject { ["models"] = models };
    }

    public static JsonNode ParseJsonc(string text) => JsonNode.Parse(text, nodeOptions: new JsonNodeOptions(),
        documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
}

/// <summary>Pi: providers.astra in ~/.pi/agent/models.json + defaultProvider/defaultModel in settings.json.</summary>
public class PiClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly PiClientAdapter _adapter;
    private readonly string _models;
    private readonly string _settings;

    public PiClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new PiClientAdapter(_home.Env, _store);
        _models = _home.File(".pi", "agent", "models.json");
        _settings = _home.File(".pi", "agent", "settings.json");
    }

    [Fact]
    public void Enable_AddsProviderAndDefaults_DisableRestoresBytes()
    {
        _home.WriteFile(_models, """
            {
              "providers": {
                "ollama": {
                  "baseUrl": "http://localhost:11434/v1",
                  "api": "openai-completions",
                  "apiKey": "ollama",
                  "models": [{ "id": "qwen2.5-coder:7b" }]
                }
              }
            }
            """);
        _home.WriteFile(_settings, "{\n  \"theme\": \"dark\",\n  \"defaultProvider\": \"anthropic\",\n  \"defaultModel\": \"claude-sonnet-5\"\n}\n");
        var modelsOriginal = _home.ReadFileBytes(_models);
        var settingsOriginal = _home.ReadFileBytes(_settings);

        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-pi-1", "gpt-5.1", Models.Extra("gpt-5.1", "glm-5"))));
        var models = Models.ParseJsonc(_home.ReadFile(_models));
        var ours = models["providers"]!["astra"]!;
        Assert.Equal("http://127.0.0.1:17321/v1", ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("openai-completions", ours["api"]!.GetValue<string>());
        Assert.Equal("astra-pi-1", ours["apiKey"]!.GetValue<string>());
        Assert.Equal(["gpt-5.1", "glm-5"], ours["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal("ollama", models["providers"]!["ollama"]!["apiKey"]!.GetValue<string>());
        var settings = Models.ParseJsonc(_home.ReadFile(_settings));
        Assert.Equal("astra", settings["defaultProvider"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", settings["defaultModel"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(modelsOriginal, _home.ReadFileBytes(_models));
        Assert.Equal(settingsOriginal, _home.ReadFileBytes(_settings));
    }

    [Fact]
    public void Disable_KeepsAProviderTheUserSelectedAfterwards()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-pi-2", "gpt-5.1", Models.Extra("gpt-5.1"))));
        _home.WriteFile(_settings, "{\"defaultProvider\": \"anthropic\", \"defaultModel\": \"claude-sonnet-5\"}");
        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal("anthropic", Models.ParseJsonc(_home.ReadFile(_settings))["defaultProvider"]!.GetValue<string>());
        Assert.False(_home.FileExists(_models)); // models.json was Astra's alone
    }

    [Fact]
    public void AgentDirOverride_IsHonored()
    {
        var dir = _home.File("custom-pi");
        _home.Variables["PI_CODING_AGENT_DIR"] = dir;
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-pi-3", null, Models.Extra("m1"))));
        Assert.True(_home.FileExists(Path.Combine(dir, "models.json")));
        Assert.False(_home.FileExists(Path.Combine(dir, "settings.json"))); // no model chosen: defaults untouched
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>Hermes Agent: the model section of ~/.hermes/config.yaml (YAML).</summary>
public class HermesAgentClientAdapterTests : IDisposable
{
    private const string Config = """
        # Hermes Agent CLI Configuration
        _config_version: 50

        model:
          # Default model to use (can be overridden with --model flag)
          default: "anthropic/claude-opus-4.6"
          # Inference provider selection
          provider: "auto"
          base_url: "https://openrouter.ai/api/v1"

        terminal:
          backend: "local"
          timeout: 180

        """;

    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly HermesAgentClientAdapter _adapter;
    private readonly string _config;

    public HermesAgentClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new HermesAgentClientAdapter(_home.Env, _store);
        _config = _home.File(".hermes", "config.yaml");
    }

    [Fact]
    public void Enable_PointsModelSectionAtGateway_ExactText_DisableRestoresBytes()
    {
        _home.WriteFile(_config, Config);
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-hermes-1", "gpt-5.1")));
        Assert.Equal("""
            # Hermes Agent CLI Configuration
            _config_version: 50

            model:
              # Default model to use (can be overridden with --model flag)
              default: "gpt-5.1"
              # Inference provider selection
              provider: "custom"
              base_url: "http://127.0.0.1:17321/v1"
              api_key: "astra-hermes-1"

            terminal:
              backend: "local"
              timeout: 180

            """, _home.ReadFile(_config));
        Assert.True(_adapter.Inspect().Enabled);

        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.Equal(original, _home.ReadFileBytes(_config));
        Assert.Empty(_store.List("hermes-agent"));
    }

    [Fact]
    public void Enable_SwitchesNonChatApiMode_AndUsesModelAlias()
    {
        _home.WriteFile(_config, "model:\n  model: x/y\n  provider: anthropic\n  api_mode: anthropic_messages\n");
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-hermes-2", "glm-5")));
        var model = YamlEditor.ParseToJson(_home.ReadFile(_config))!["model"]!;
        Assert.Equal("glm-5", model["model"]!.GetValue<string>());
        Assert.Null(model["default"]);
        Assert.Equal("chat_completions", model["api_mode"]!.GetValue<string>());
        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(original, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void LegacyScalarModel_IsReplacedByMapping_AndRestored()
    {
        _home.WriteFile(_config, "model: anthropic/claude-opus-4.6\nterminal:\n  backend: local\n");
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-hermes-3", null)));
        var model = YamlEditor.ParseToJson(_home.ReadFile(_config))!["model"]!;
        Assert.Equal("anthropic/claude-opus-4.6", model["default"]!.GetValue<string>());
        Assert.Equal("custom", model["provider"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);
        // Re-applying (e.g. after a key rotation) keeps owning the whole section.
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-hermes-3b", null)));
        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted_AndWarnsOnKeyEnv()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-hermes-4", null)));
        Assert.True(_home.FileExists(_config));
        _home.WriteFile(_config, _home.ReadFile(_config) + "  key_env: MY_KEY\n");
        Assert.Contains(_adapter.Inspect().Warnings, w => w.Contains("key_env"));
        _home.WriteFile(_config, _home.ReadFile(_config).Replace("  key_env: MY_KEY\n", ""));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.Contains(_config, report.DeletedFiles);
    }

    [Fact]
    public void HermesHomeOverride_IsHonored()
    {
        var dir = _home.File("profiles", "work");
        _home.Variables["HERMES_HOME"] = dir;
        Assert.Equal(Path.Combine(dir, "config.yaml"), Assert.Single(_adapter.ConfigPaths()));
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>MiniMax Code: custom_provider.astra + defaultModel in ~/.minimax/config.yaml.</summary>
public class MiniMaxCodeClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly MiniMaxCodeClientAdapter _adapter;
    private readonly string _config;

    public MiniMaxCodeClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new MiniMaxCodeClientAdapter(_home.Env, _store);
        _config = _home.File(".minimax", "config.yaml");
    }

    [Fact]
    public void Enable_AddsCustomProvider_AndSelectsModel_DisableRestoresBytes()
    {
        _home.WriteFile(_config, "logLevel: info\ndefaultModel: minimax/MiniMax-M3\nmemory:\n  enabled: false\n");
        var original = _home.ReadFileBytes(_config);
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-mm-1", "gpt-5.1", Models.Extra("gpt-5.1", "kimi-k2.6"))));

        var doc = YamlEditor.ParseToJson(_home.ReadFile(_config))!;
        var ours = doc["custom_provider"]!["astra"]!;
        Assert.Equal("openai-completions", ours["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["options"]!["baseURL"]!.GetValue<string>());
        Assert.Equal("astra-mm-1", ours["options"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("KIMI-K2.6", ours["models"]!["kimi-k2.6"]!["name"]!.GetValue<string>());
        Assert.Equal("custom_provider:astra/gpt-5.1", doc["defaultModel"]!.GetValue<string>());
        Assert.False(doc["memory"]!["enabled"]!.GetValue<bool>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, _home.ReadFileBytes(_config));
    }

    [Fact]
    public void Disable_KeepsModelTheUserPickedAfterwards()
    {
        _home.WriteFile(_config, "logLevel: info\n");
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-mm-2", "gpt-5.1", Models.Extra("gpt-5.1"))));
        _home.WriteFile(_config, _home.ReadFile(_config).Replace("custom_provider:astra/gpt-5.1", "minimax/MiniMax-M3"));
        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        var doc = YamlEditor.ParseToJson(_home.ReadFile(_config))!;
        Assert.Equal("minimax/MiniMax-M3", doc["defaultModel"]!.GetValue<string>());
        Assert.Null(doc["custom_provider"]);
    }

    [Fact]
    public void DataDirOverride_FileCreatedThenDeleted()
    {
        var dir = _home.File("mm-data");
        _home.Variables["MINIMAX_DATA_DIR"] = dir;
        var file = Path.Combine(dir, "config.yaml");
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-mm-3", null, Models.Extra("m1"))));
        Assert.True(_home.FileExists(file));
        Assert.Contains(file, _applier.Disable(_adapter.PlanDisable()).DeletedFiles);
    }

    public void Dispose() => _home.Dispose();
}

/// <summary>Copilot CLI: BYOK entries in ~/.copilot/providers.json + model in settings.json.</summary>
public class CopilotCliClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly CopilotCliClientAdapter _adapter;
    private readonly string _providers;
    private readonly string _settings;

    public CopilotCliClientAdapterTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new CopilotCliClientAdapter(_home.Env, _store);
        _providers = _home.File(".copilot", "providers.json");
        _settings = _home.File(".copilot", "settings.json");
    }

    [Fact]
    public void Enable_AppendsEntriesNextToUserOnes_StrictJson_DisableRestoresBytes()
    {
        _home.WriteFile(_providers, """
            {
              "providers": [
                {"name": "ollama", "type": "openai", "baseUrl": "http://localhost:11434/v1"}
              ],
              "models": [
                {"id": "qwen3", "provider": "ollama"}
              ]
            }
            """);
        _home.WriteFile(_settings, "{\n  // mine\n  \"model\": \"gpt-5.4\",\n  \"theme\": \"dim\"\n}\n");
        var providersOriginal = _home.ReadFileBytes(_providers);
        var settingsOriginal = _home.ReadFileBytes(_settings);

        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-cp-1", "gpt-4.1", Models.Extra("gpt-4.1", "glm-5"))));
        var doc = JsonNode.Parse(_home.ReadFile(_providers))!; // strict parse: Copilot reads plain JSON
        var astra = doc["providers"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "astra")!;
        Assert.Equal("openai", astra["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", astra["baseUrl"]!.GetValue<string>());
        Assert.Equal("astra-cp-1", astra["apiKey"]!.GetValue<string>());
        Assert.Equal(2, doc["providers"]!.AsArray().Count);
        Assert.Equal(["qwen3", "gpt-4.1", "glm-5"], doc["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal("astra/gpt-4.1", Models.ParseJsonc(_home.ReadFile(_settings))["model"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(providersOriginal, _home.ReadFileBytes(_providers));
        Assert.Equal(settingsOriginal, _home.ReadFileBytes(_settings));
    }

    [Fact]
    public void ModelListChange_RemovesModelsNoLongerOffered()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-cp-2", null, Models.Extra("a", "b"))));
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-cp-2", null, Models.Extra("b", "c"))));
        var ids = JsonNode.Parse(_home.ReadFile(_providers))!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>());
        Assert.Equal(["b", "c"], ids);
        Assert.Empty(_adapter.Inspect().DriftedKeys);
    }

    [Fact]
    public void FilesOriginallyAbsent_CreatedThenDeleted()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-cp-3", "m1", Models.Extra("m1"))));
        Assert.True(_home.FileExists(_providers));
        Assert.True(_home.FileExists(_settings));
        var report = _applier.Disable(_adapter.PlanDisable());
        Assert.False(report.HasDrift);
        Assert.False(_home.FileExists(_providers));
        Assert.False(_home.FileExists(_settings));
    }

    [Fact]
    public void Disable_KeepsModelTheUserPickedAfterwards()
    {
        _applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-cp-4", "m1", Models.Extra("m1"))));
        _home.WriteFile(_settings, "{\"model\": \"claude-sonnet-4.6\"}");
        Assert.False(_applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal("claude-sonnet-4.6", Models.ParseJsonc(_home.ReadFile(_settings))["model"]!.GetValue<string>());
    }

    public void Dispose() => _home.Dispose();
}
