using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

file static class Hints
{
    /// <summary>The "models" extra as ClientService builds it from the bound provider (ids with optional limits).</summary>
    public static JsonObject Extra(params string[] ids)
    {
        var models = new JsonObject();
        foreach (var id in ids) models[id] = new JsonObject { ["id"] = id, ["name"] = id.ToUpperInvariant() };
        return new JsonObject { ["models"] = models };
    }

    public static JsonNode Jsonc(string text) => JsonNode.Parse(text,
        documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
}

/// <summary>Shared setup: fake home, in-memory state store and the real applier.</summary>
public abstract class AdapterTestBase : IDisposable
{
    protected readonly TestHome Home = new();
    protected readonly InMemoryClientConfigStateStore Store = new();
    protected readonly ClientConfigApplier Applier;

    protected AdapterTestBase() => Applier = new ClientConfigApplier(Store, Home.Env, Home.BackupRoot);

    public void Dispose() => Home.Dispose();
}

public class CrushClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          // my providers
          "providers": {
            "local": { "type": "openai-compat", "base_url": "http://localhost:1234/v1", "api_key": "x", "models": [] }
          },
          "models": { "small": { "provider": "local", "model": "tiny" } }
        }
        """;

    private readonly CrushClientAdapter _adapter;
    private readonly string _config;

    public CrushClientAdapterTests()
    {
        _adapter = new CrushClientAdapter(Home.Env, Store);
        _config = Home.File(".config", "crush", "crush.json");
    }

    [Fact]
    public void Enable_AddsProviderAndLargeModel_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.1", "glm-5");
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-1", "gpt-5.1", extra)));

        var json = Hints.Jsonc(Home.ReadFile(_config));
        var ours = json["providers"]!["astra"]!;
        Assert.Equal("openai-compat", ours["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["base_url"]!.GetValue<string>());
        Assert.Equal("astra-crush-1", ours["api_key"]!.GetValue<string>());
        var first = ours["models"]![0]!;
        Assert.Equal("gpt-5.1", first["id"]!.GetValue<string>());
        Assert.Equal(400_000, first["context_window"]!.GetValue<long>());
        Assert.True(first["supports_attachments"]!.GetValue<bool>());
        Assert.Equal(CrushClientAdapter.DefaultMaxOutputTokens, ours["models"]![1]!["default_max_tokens"]!.GetValue<long>());
        Assert.Equal("astra", json["models"]!["large"]!["provider"]!.GetValue<string>());
        Assert.Equal("tiny", json["models"]!["small"]!["model"]!.GetValue<string>()); // the user's small model is untouched
        Assert.Equal("x", json["providers"]!["local"]!["api_key"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void Disable_KeepsAModelTheUserSelectedAfterwards()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-2", "gpt-5.1", Hints.Extra("gpt-5.1"))));
        var edited = Hints.Jsonc(Home.ReadFile(_config));
        edited["models"]!["large"] = new JsonObject { ["provider"] = "local", ["model"] = "tiny" };
        Home.WriteFile(_config, edited.ToJsonString());
        _ = Applier.Disable(_adapter.PlanDisable());
        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("local", json["models"]!["large"]!["provider"]!.GetValue<string>());
        Assert.Null(json["providers"]);
    }

    [Fact]
    public void ConfigDirectoryOverrides_AreHonored_AndCrushrcIsWarnedAbout()
    {
        var dir = Home.File("custom-crush");
        Home.Variables["CRUSH_GLOBAL_CONFIG"] = dir;
        Home.WriteFile(Path.Combine(dir, "crushrc"), "export FOO=1\n");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-3", null, Hints.Extra("m1"))));
        Assert.True(Home.FileExists(Path.Combine(dir, "crush.json")));
        Assert.Null(Hints.Jsonc(Home.ReadFile(Path.Combine(dir, "crush.json")))["models"]); // no model chosen: defaults untouched
        var warning = Assert.Single(_adapter.Inspect().Warnings);
        Assert.Contains("crushrc", warning);
        Assert.DoesNotContain("astra-crush-3", warning);

        Home.Variables.Remove("CRUSH_GLOBAL_CONFIG");
        Home.Variables["XDG_CONFIG_HOME"] = Home.File("xdg");
        Assert.Equal([Home.File("xdg", "crush", "crush.json")], _adapter.ConfigPaths());
    }
}

public class QwenCodeClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          "theme": "dark",
          "env": { "MY_KEY": "k" },
          "modelProviders": {
            "openai": [ { "id": "mine", "envKey": "MY_KEY", "baseUrl": "https://example.com/v1" } ]
          },
          "security": { "auth": { "selectedType": "qwen-oauth" } },
          "model": { "name": "qwen3-coder-plus" }
        }
        """;

    private readonly QwenCodeClientAdapter _adapter;
    private readonly string _config;

    public QwenCodeClientAdapterTests()
    {
        _adapter = new QwenCodeClientAdapter(Home.Env, Store);
        _config = Home.File(".qwen", "settings.json");
    }

    [Fact]
    public void Enable_AppendsOwnedElementsAndKey_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-1", "gpt-5.1", Hints.Extra("gpt-5.1", "glm-5"))));

        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("astra-qwen-1", json["env"]![QwenCodeClientAdapter.KeyVariable]!.GetValue<string>());
        Assert.Equal("k", json["env"]!["MY_KEY"]!.GetValue<string>());
        var elements = json["modelProviders"]!["openai"]!.AsArray();
        Assert.Equal(["mine", "gpt-5.1", "glm-5"], elements.Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal("http://127.0.0.1:17321/v1", elements[1]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(QwenCodeClientAdapter.KeyVariable, elements[1]!["envKey"]!.GetValue<string>());
        Assert.Equal("openai", json["security"]!["auth"]!["selectedType"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", json["model"]!["name"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void Reapply_DropsModelsTheProviderNoLongerOffers()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-2", "gpt-5.1", Hints.Extra("gpt-5.1", "glm-5"))));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-2", "gpt-5.1", Hints.Extra("gpt-5.1"))));
        var ids = Hints.Jsonc(Home.ReadFile(_config))["modelProviders"]!["openai"]!.AsArray().Select(e => e!["id"]!.GetValue<string>());
        Assert.Equal(["gpt-5.1"], ids);
    }

    [Fact]
    public void Disable_KeepsAModelTheUserSelectedAfterwards()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-3", "gpt-5.1", Hints.Extra("gpt-5.1"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("\"name\": \"gpt-5.1\"", "\"name\": \"qwen3-max\""));
        _ = Applier.Disable(_adapter.PlanDisable());
        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("qwen3-max", json["model"]!["name"]!.GetValue<string>());
        Assert.Equal(["mine"], json["modelProviders"]!["openai"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.Null(json["env"]![QwenCodeClientAdapter.KeyVariable]);
    }
}

public class DroidClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          "model": "claude-opus-4-1",
          "customModels": [
            { "model": "mine", "displayName": "Mine", "baseUrl": "https://example.com/v1", "apiKey": "k", "provider": "openai" }
          ]
        }
        """;

    private readonly DroidClientAdapter _adapter;
    private readonly string _config;

    public DroidClientAdapterTests()
    {
        _adapter = new DroidClientAdapter(Home.Env, Store);
        _config = Home.File(".factory", "settings.json");
    }

    [Fact]
    public void Enable_AddsOneElementPerModel_NeverSelectsADefault_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-droid-1", "gpt-5.1", Hints.Extra("gpt-5.1", "glm-5"))));

        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("claude-opus-4-1", json["model"]!.GetValue<string>());
        var elements = json["customModels"]!.AsArray();
        Assert.Equal(["mine", "gpt-5.1", "glm-5"], elements.Select(e => e!["model"]!.GetValue<string>()));
        var ours = elements[1]!;
        Assert.Equal("Astra: gpt-5.1", ours["displayName"]!.GetValue<string>());
        Assert.Equal("generic-chat-completion-api", ours["provider"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("astra-droid-1", ours["apiKey"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void KeyRotationAndStaleModels_OnlyTouchOwnedElements()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-droid-2", null, Hints.Extra("a", "b"))));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-droid-3", null, Hints.Extra("a"))));
        var elements = Hints.Jsonc(Home.ReadFile(_config))["customModels"]!.AsArray();
        Assert.Equal(["mine", "a"], elements.Select(e => e!["model"]!.GetValue<string>()));
        Assert.Equal("astra-droid-3", elements[1]!["apiKey"]!.GetValue<string>());
        Assert.Equal("k", elements[0]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-droid-4", null, Hints.Extra("a"))));
        Assert.True(Home.FileExists(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config));
    }
}

public class KimiCodeClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        # my kimi config
        default_model = "kimi/k2"

        [providers.kimi]
        type = "kimi"
        api_key = "k"

        [models."kimi/k2"]
        provider = "kimi"
        model = "kimi-k2"
        max_context_size = 262144
        """;

    private readonly KimiCodeClientAdapter _adapter;
    private readonly string _config;

    public KimiCodeClientAdapterTests()
    {
        _adapter = new KimiCodeClientAdapter(Home.Env, Store);
        _config = Home.File(".kimi-code", "config.toml");
    }

    [Fact]
    public void Enable_AddsProviderAndModelTables_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.1", "glm 5");
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["reasoning"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-1", "gpt-5.1", extra)));

        var text = Home.ReadFile(_config);
        Assert.Contains("default_model = \"astra-gpt-5-1\"", text);
        Assert.Contains("[providers.astra]\ntype = \"openai\"\nbase_url = \"http://127.0.0.1:17321/v1\"\napi_key = \"astra-kimi-1\"\n", text);
        Assert.Contains("[models.astra-gpt-5-1]\nprovider = \"astra\"\nmodel = \"gpt-5.1\"\nmax_context_size = 400000\n"
            + "capabilities = [\"tool_use\", \"thinking\"]\n", text);
        Assert.Contains("[models.astra-glm-5]\nprovider = \"astra\"\nmodel = \"glm 5\"\nmax_context_size = 131072\n", text);
        Assert.Contains("[models.\"kimi/k2\"]", text); // the user's own model survives
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void Aliases_DoNotCollide_AndReapplyRemovesStaleTables()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-2", "a.b", Hints.Extra("a.b", "a-b", "gone"))));
        var text = Home.ReadFile(_config);
        Assert.Contains("[models.astra-a-b]", text);
        Assert.Contains("[models.astra-a-b-2]", text);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-2", "a.b", Hints.Extra("a.b", "a-b"))));
        Assert.DoesNotContain("astra-gone", Home.ReadFile(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config)); // created by Astra, removed again
    }

    [Fact]
    public void KeyRotation_RefreshesFieldsAndRestoresTheOriginals()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-3", "m", Hints.Extra("m"))));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-4", "m", Hints.Extra("m"))));
        Assert.Contains("astra-kimi-4", Home.ReadFile(_config));
        Assert.DoesNotContain("astra-kimi-3", Home.ReadFile(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void Disable_KeepsADefaultModelTheUserPickedAfterwards()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-5", "m", Hints.Extra("m"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("default_model = \"astra-m\"", "default_model = \"kimi/k2\""));
        _ = Applier.Disable(_adapter.PlanDisable());
        Assert.Contains("default_model = \"kimi/k2\"", Home.ReadFile(_config));
        Assert.DoesNotContain("astra", Home.ReadFile(_config));
    }

    [Fact]
    public void HomeOverride_IsHonored_AndLegacyDirectoryIsWarnedAbout()
    {
        Directory.CreateDirectory(Home.File(".kimi"));
        Assert.Contains(".kimi", Assert.Single(_adapter.Inspect().Warnings));
        var dir = Home.File("kimi-home");
        Home.Variables["KIMI_CODE_HOME"] = dir;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-6", null, Hints.Extra("m"))));
        Assert.True(Home.FileExists(Path.Combine(dir, "config.toml")));
        Assert.DoesNotContain("default_model", Home.ReadFile(Path.Combine(dir, "config.toml")));
    }
}

public class ZedClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        // Zed settings
        {
          "theme": "One Dark",
          "agent": { "default_model": { "provider": "anthropic", "model": "claude-sonnet-4" } },
          "language_models": { "openai_compatible": { "other": { "api_url": "https://example.com/v1", "available_models": [] } } }
        }
        """;

    private readonly ZedClientAdapter _adapter;
    private readonly string _config;

    public ZedClientAdapterTests()
    {
        _adapter = new ZedClientAdapter(Home.Env, Store);
        _config = Home.File(".config", "zed", "settings.json");
    }

    [Fact]
    public void Enable_WritesProviderAndDefaultModel_NeverTheToken_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zed-secret", "gpt-5.1", Hints.Extra("gpt-5.1", "glm-5"))));

        var text = Home.ReadFile(_config);
        Assert.DoesNotContain("astra-zed-secret", text); // Zed reads keys only from its own store / environment
        var json = Hints.Jsonc(text);
        var ours = json["language_models"]!["openai_compatible"]!["astra"]!;
        Assert.Equal("http://127.0.0.1:17321/v1", ours["api_url"]!.GetValue<string>());
        Assert.Equal(["gpt-5.1", "glm-5"], ours["available_models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        Assert.Equal(ZedClientAdapter.DefaultContextWindow, ours["available_models"]![0]!["max_tokens"]!.GetValue<long>());
        Assert.True(ours["available_models"]![0]!["capabilities"]!["tools"]!.GetValue<bool>());
        Assert.NotNull(json["language_models"]!["openai_compatible"]!["other"]);
        Assert.Equal("astra", json["agent"]!["default_model"]!["provider"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void Inspect_AlwaysExplainsTheManualTokenStep_WithoutLeakingAToken()
    {
        var warning = Assert.Single(_adapter.Inspect().Warnings);
        Assert.Contains("Tokens page", warning);
        Assert.DoesNotContain("sk-astra", warning);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("sk-astra-xyz.zed", null, Hints.Extra("m"))));
        Assert.DoesNotContain("sk-astra-xyz", Assert.Single(_adapter.Inspect().Warnings));
    }

    [Fact]
    public void Disable_KeepsADefaultModelTheUserPickedAfterwards()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zed-2", "m", Hints.Extra("m"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("\"provider\": \"astra\"", "\"provider\": \"anthropic\""));
        _ = Applier.Disable(_adapter.PlanDisable());
        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("anthropic", json["agent"]!["default_model"]!["provider"]!.GetValue<string>());
        Assert.Null(json["language_models"]!["openai_compatible"]!["astra"]);
    }
}

public class OmpClientAdapterTests : AdapterTestBase
{
    private const string Models = """
        # my models
        providers:
          ollama:
            baseUrl: http://localhost:11434/v1
            api: openai-completions
            apiKey: ollama
        """;

    private const string Config = "theme: dark\nmodelRoles:\n  default: anthropic/claude-sonnet-4\n  smol: anthropic/claude-haiku\n";

    private readonly OmpClientAdapter _adapter;
    private readonly string _models;
    private readonly string _config;

    public OmpClientAdapterTests()
    {
        _adapter = new OmpClientAdapter(Home.Env, Store);
        _models = Home.File(".omp", "agent", "models.yml");
        _config = Home.File(".omp", "agent", "config.yml");
    }

    [Fact]
    public void Enable_AddsProviderAndDefaultRole_DisableRestoresBytes()
    {
        Home.WriteFile(_models, Models);
        Home.WriteFile(_config, Config);
        var modelsOriginal = Home.ReadFileBytes(_models);
        var configOriginal = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-omp-1", "gpt-5.1", Hints.Extra("gpt-5.1", "glm-5"))));

        var models = YamlEditor.ParseToJson(Home.ReadFile(_models))!["providers"]!;
        var ours = models["astra"]!;
        Assert.Equal("openai-completions", ours["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("astra-omp-1", ours["apiKey"]!.GetValue<string>());
        Assert.Equal(["gpt-5.1", "glm-5"], ours["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal("ollama", models["ollama"]!["apiKey"]!.GetValue<string>());
        var roles = YamlEditor.ParseToJson(Home.ReadFile(_config))!["modelRoles"]!;
        Assert.Equal("astra/gpt-5.1", roles["default"]!.GetValue<string>());
        Assert.Equal("anthropic/claude-haiku", roles["smol"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(modelsOriginal, Home.ReadFileBytes(_models));
        Assert.Equal(configOriginal, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void YamlSpellingAndAgentDirOverride_AreHonored()
    {
        var dir = Home.File("custom-omp");
        Home.Variables["PI_CODING_AGENT_DIR"] = dir;
        Home.WriteFile(Path.Combine(dir, "models.yaml"), Models);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-omp-2", null, Hints.Extra("m1"))));
        Assert.Contains("astra:", Home.ReadFile(Path.Combine(dir, "models.yaml")));
        Assert.False(Home.FileExists(Path.Combine(dir, "models.yml")));
        Assert.False(Home.FileExists(Path.Combine(dir, "config.yml"))); // no model chosen: roles untouched
    }

    [Fact]
    public void Disable_KeepsARoleTheUserSetAfterwards()
    {
        Home.WriteFile(_config, Config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-omp-3", "m", Hints.Extra("m"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("astra/m", "anthropic/claude-opus"));
        _ = Applier.Disable(_adapter.PlanDisable());
        Assert.Equal("anthropic/claude-opus", YamlEditor.ParseToJson(Home.ReadFile(_config))!["modelRoles"]!["default"]!.GetValue<string>());
    }
}

public class ForkAdapterTests : AdapterTestBase
{
    [Fact]
    public void MiMoCode_UsesTheOpenCodeSchemaUnderItsOwnDirectory()
    {
        var adapter = new OpenCodeClientAdapter(Home.Env, Store, OpenCodeFlavour.MiMoCode);
        var config = Home.File(".config", "mimocode", "mimocode.jsonc");
        Home.WriteFile(config, "{\n  // mimo\n  \"theme\": \"dark\"\n}\n");
        var original = Home.ReadFileBytes(config);
        Assert.Equal(ClientKinds.MiMoCode, adapter.Kind);
        Assert.Equal([Home.File(".config", "mimocode", "mimocode.json"), config], adapter.ConfigPaths());

        Applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-mimo-1", "gpt-5.1", Hints.Extra("gpt-5.1"))));
        var json = Hints.Jsonc(Home.ReadFile(config));
        Assert.Equal("@ai-sdk/openai-compatible", json["provider"]!["astra"]!["npm"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", json["provider"]!["astra"]!["options"]!["baseURL"]!.GetValue<string>());
        Assert.Equal("astra/gpt-5.1", json["model"]!.GetValue<string>());
        Assert.True(adapter.Inspect().Enabled);
        Assert.False(Applier.Disable(adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(config));

        Home.Variables["MIMOCODE_HOME"] = Home.File("elsewhere");
        Assert.Contains("MIMOCODE_HOME", Assert.Single(adapter.Inspect().Warnings));
    }

    [Fact]
    public void OpenCode_KeepsItsOwnFiles()
    {
        var adapter = new OpenCodeClientAdapter(Home.Env, Store);
        Assert.Equal(ClientKinds.OpenCode, adapter.Kind);
        Assert.Equal([Home.File(".config", "opencode", "opencode.json"), Home.File(".config", "opencode", "opencode.jsonc")], adapter.ConfigPaths());
    }

    [Theory]
    [InlineData(ClientKinds.VsCodeInsiders, "Code - Insiders")]
    [InlineData(ClientKinds.VsCodium, "VSCodium")]
    public void VsCodeVariants_UseTheirOwnUserDirectory(string kind, string dirName)
    {
        var adapter = ClientAdapterRegistry.CreateDefault(Home.Env, Store).Require(kind);
        var file = Home.File("Library", "Application Support", dirName, "User", "chatLanguageModels.json");
        Assert.Equal([file], adapter.ConfigPaths());
        Applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-vs-1", null, Hints.Extra("m1"))));
        var group = Hints.Jsonc(Home.ReadFile(file)).AsArray().Single(g => g!["name"]!.GetValue<string>() == "Astra")!;
        Assert.Equal("customendpoint", group["vendor"]!.GetValue<string>());
        Assert.Equal("m1", group["models"]![0]!["id"]!.GetValue<string>());
        Assert.True(adapter.Inspect().Enabled);
        Assert.False(Applier.Disable(adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(file));
        // The stable VS Code file is never involved.
        Assert.False(Home.FileExists(Home.File("Library", "Application Support", "Code", "User", "chatLanguageModels.json")));
    }

    [Fact]
    public void VsCodium_WarnsAboutItsPrerequisites_VsCodeDoesNot()
    {
        var registry = ClientAdapterRegistry.CreateDefault(Home.Env, Store);
        Assert.Contains("product.json", Assert.Single(registry.Require(ClientKinds.VsCodium).Inspect().Warnings));
        Assert.Empty(registry.Require(ClientKinds.VsCodeInsiders).Inspect().Warnings);
        Assert.Empty(registry.Require(ClientKinds.VsCodeCopilot).Inspect().Warnings);
    }
}

public class DeepSeekHarnessClientAdapterTests : AdapterTestBase
{
    private const string Desktop = """
        # Your patch layer for this dsh profile
        - id: agent-default-model
          name: "@deepseek-ai/dsh-agent-default-model"
          config:
            provider: deepseek-account
            model: deepseek-flash
            reasoningEffort: high
        - id: ui-chat
          name: "@deepseek-ai/dsh-client-ui-chat"
          config:
            transcriptView: detailed
        """ + "\n";

    private readonly DeepSeekHarnessClientAdapter _adapter;
    private readonly string _desktop;
    private readonly string _web;
    private readonly string _env;

    public DeepSeekHarnessClientAdapterTests()
    {
        _adapter = new DeepSeekHarnessClientAdapter(Home.Env, Store);
        _desktop = Home.File(".dsh", "profiles", "desktop", "cordis.patch.yml");
        _web = Home.File(".dsh", "profiles", "web", "cordis.patch.yml");
        _env = Home.File(".dsh", ".env");
    }

    private static JsonNode Row(string yaml, string id) =>
        YamlEditor.ParseToJson(yaml)!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public void Enable_AddsRouteAndDefaultModel_KeepsOtherRows_DisableRestoresBytes()
    {
        Home.WriteFile(_desktop, Desktop);
        Home.WriteFile(_env, "# harness env\nOTHER=1\n");
        var desktopOriginal = Home.ReadFileBytes(_desktop);
        var envOriginal = Home.ReadFileBytes(_env);
        var extra = Hints.Extra("gpt-5.1", "glm-5");
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-1", "gpt-5.1", extra)));

        var text = Home.ReadFile(_desktop);
        var llm = Row(text, "llm-pi-ai");
        Assert.Equal("@deepseek-ai/dsh-llm-pi-ai", llm["name"]!.GetValue<string>());
        var provider = llm["config"]!["providers"]!["astra"]!;
        Assert.Equal("openai-completions", provider["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", provider["baseURL"]!.GetValue<string>());
        Assert.Equal(DeepSeekHarnessClientAdapter.KeyVariable, provider["apiKeyEnv"]!.GetValue<string>());
        Assert.DoesNotContain("astra-dsh-1", text); // no literal key in the patch file
        Assert.Equal(["gpt-5.1", "glm-5"], provider["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal(400_000, provider["models"]![0]!["contextWindow"]!.GetValue<long>());
        Assert.Equal(["text", "image"], provider["models"]![0]!["input"]!.AsArray().Select(m => m!.GetValue<string>()));
        var defaults = Row(text, "agent-default-model")["config"]!;
        Assert.Equal("astra", defaults["provider"]!.GetValue<string>());
        Assert.Equal("gpt-5.1", defaults["model"]!.GetValue<string>());
        Assert.Null(defaults["reasoningEffort"]); // a custom model declares no effort levels
        Assert.Equal("detailed", Row(text, "ui-chat")["config"]!["transcriptView"]!.GetValue<string>());
        var envText = Home.ReadFile(_env);
        Assert.Contains("ASTRA_GATEWAY_API_KEY=astra-dsh-1", envText);
        Assert.Contains("OTHER=1", envText);
        Assert.True(_adapter.Inspect().Enabled);
        Assert.False(_adapter.Inspect().DriftedKeys.Any());

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(desktopOriginal, Home.ReadFileBytes(_desktop));
        Assert.Equal(envOriginal, Home.ReadFileBytes(_env));
    }

    [Fact]
    public void ExistingRouteRow_KeepsTheUsersOwnProviders()
    {
        Home.WriteFile(_desktop, Desktop + """
            - id: llm-pi-ai
              name: "@deepseek-ai/dsh-llm-pi-ai"
              config:
                providers:
                  mine:
                    apiKeyEnv: MINE
                    api: openai-completions
                    baseURL: https://example.com/v1
                    models:
                      - id: m1
            """ + "\n");
        var original = Home.ReadFileBytes(_desktop);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-2", null, Hints.Extra("m"))));
        var providers = Row(Home.ReadFile(_desktop), "llm-pi-ai")["config"]!["providers"]!.AsObject();
        Assert.Equal(["mine", "astra"], providers.Select(p => p.Key));
        Assert.Equal("deepseek-flash", Row(Home.ReadFile(_desktop), "agent-default-model")["config"]!["model"]!.GetValue<string>()); // no model chosen
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_desktop));
        Assert.False(Home.FileExists(_env)); // created by Astra, removed again
    }

    [Fact]
    public void RowCreatedByAstra_GoesAway_UnlessTheUserAddedToIt()
    {
        Home.WriteFile(_desktop, Desktop);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-3", null, Hints.Extra("m"))));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.DoesNotContain("llm-pi-ai", Home.ReadFile(_desktop));

        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-3", null, Hints.Extra("m"))));
        var doc = YamlEditor.ParseToJson(Home.ReadFile(_desktop))!.AsArray();
        var llm = doc.Single(r => r!["id"]!.GetValue<string>() == "llm-pi-ai")!;
        llm["config"]!["providers"]!["theirs"] = new JsonObject { ["api"] = "openai-completions", ["baseURL"] = "https://x/v1", ["models"] = new JsonArray(new JsonObject { ["id"] = "x" }) };
        Home.WriteFile(_desktop, Home.ReadFile(_desktop).Replace(
            "providers:", "providers:\n      theirs:\n        api: openai-completions\n        baseURL: https://x/v1\n        models:\n          - id: x"));
        _ = Applier.Disable(_adapter.PlanDisable());
        var after = Row(Home.ReadFile(_desktop), "llm-pi-ai")["config"]!["providers"]!.AsObject();
        Assert.Equal(["theirs"], after.Select(p => p.Key));
    }

    [Fact]
    public void Disable_KeepsADefaultModelTheUserPickedAfterwards_AndBothProfilesAreWritten()
    {
        Home.WriteFile(_desktop, Desktop);
        Home.WriteFile(_web, "- id: ui-chat\n  name: x\n");
        var webOriginal = Home.ReadFileBytes(_web);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-4", "m", Hints.Extra("m"))));
        Assert.Equal("astra", Row(Home.ReadFile(_web), "agent-default-model")["config"]!["provider"]!.GetValue<string>());
        Home.WriteFile(_desktop, Home.ReadFile(_desktop).Replace("provider: \"astra\"", "provider: deepseek-account").Replace("provider: astra", "provider: deepseek-account"));
        _ = Applier.Disable(_adapter.PlanDisable());
        var defaults = Row(Home.ReadFile(_desktop), "agent-default-model")["config"]!;
        Assert.Equal("deepseek-account", defaults["provider"]!.GetValue<string>());
        Assert.Equal("m", defaults["model"]!.GetValue<string>());
        Assert.DoesNotContain("astra", Home.ReadFile(_desktop).Replace("astra-dsh", ""));
        Assert.Equal(webOriginal, Home.ReadFileBytes(_web));
    }

    [Fact]
    public void WithoutAProfile_EnableExplainsWhatToDo_AndInspectWarns()
    {
        Assert.Contains("profile", Assert.Single(_adapter.Inspect().Warnings));
        var error = Assert.Throws<EditorException>(() => _adapter.PlanEnable(TestGateway.Context("astra-dsh-5", null, Hints.Extra("m"))));
        Assert.Contains("start it once", error.Message);
        Home.WriteFile(_desktop, Desktop);
        Assert.Throws<EditorException>(() => _adapter.PlanEnable(TestGateway.Context("astra-dsh-5", null)));
    }

    [Fact]
    public void HomeOverride_And_HomeLevelPatchWarning()
    {
        var home = Home.File("elsewhere");
        Home.Variables["DSH_HOME"] = home;
        Home.WriteFile(Path.Combine(home, "profiles", "web", "cordis.patch.yml"), "# Your patch layer\n");
        Home.WriteFile(Path.Combine(home, "cordis.patch.yml"), "- id: llm-pi-ai\n  name: x\n");
        Assert.Equal([Path.Combine(home, "profiles", "web", "cordis.patch.yml"), Path.Combine(home, ".env")], _adapter.ConfigPaths());
        Assert.Contains("llm-pi-ai", Assert.Single(_adapter.Inspect().Warnings));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-6", null, Hints.Extra("m"))));
        Assert.True(Home.FileExists(Path.Combine(home, ".env")));
        Assert.True(_adapter.Inspect().Enabled);
    }
}

public class WorkBuddyClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          "models": [
            { "id": "mine", "name": "Mine", "vendor": "OpenAI", "url": "https://example.com/v1/chat/completions", "apiKey": "k" }
          ],
          "availableModels": ["mine"]
        }
        """;

    private readonly WorkBuddyClientAdapter _adapter;
    private readonly string _config;

    public WorkBuddyClientAdapterTests()
    {
        _adapter = new WorkBuddyClientAdapter(Home.Env, Store);
        _config = Home.File(".codebuddy", "models.json");
    }

    [Fact]
    public void Enable_AddsOneEntryPerModel_LeavesUserModels_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.1", "glm-5");
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-1", "gpt-5.1", extra)));

        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal(["mine"], json["availableModels"]!.AsArray().Select(e => e!.GetValue<string>()));
        var models = json["models"]!.AsArray();
        Assert.Equal(["mine", "gpt-5.1", "glm-5"], models.Select(e => e!["id"]!.GetValue<string>()));
        var ours = models[1]!;
        Assert.Equal("Astra: gpt-5.1", ours["name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1/chat/completions", ours["url"]!.GetValue<string>());
        Assert.Equal("astra-wb-1", ours["apiKey"]!.GetValue<string>());
        Assert.Equal(400_000, ours["maxInputTokens"]!.GetValue<long>());
        Assert.True(ours["supportsToolCall"]!.GetValue<bool>());
        Assert.True(ours["supportsImages"]!.GetValue<bool>());
        Assert.Null(models[2]!["supportsImages"]);
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void KeyRotationAndStaleModels_OnlyTouchOwnedEntries()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-2", null, Hints.Extra("a", "b"))));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-3", null, Hints.Extra("a"))));
        var models = Hints.Jsonc(Home.ReadFile(_config))["models"]!.AsArray();
        Assert.Equal(["mine", "a"], models.Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal("astra-wb-3", models[1]!["apiKey"]!.GetValue<string>());
        Assert.Equal("k", models[0]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-4", null, Hints.Extra("a"))));
        Assert.True(Home.FileExists(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config));
    }
}
