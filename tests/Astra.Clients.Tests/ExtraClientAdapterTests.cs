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
    public void ModelsGoToAProviderOfTheirNativeProtocol_WhichGoesAgainWhenEmpty()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "grok-4.7", "claude-sonnet-5", "gemini-3-pro", "glm-5");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "grok-4.7", "openai-responses"); // Crush's openai type would send it over Chat anyway
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini");
        TestGateway.Native(extra, "glm-5", "openai-chat");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-4", "claude-sonnet-5", extra)));

        var providers = Hints.Jsonc(Home.ReadFile(_config))["providers"]!;
        string[] Ids(string provider) => providers[provider]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(["grok-4.7", "glm-5"], Ids("astra"));
        Assert.Equal(["gpt-5.5"], Ids("astra-responses"));
        Assert.Equal("openai", providers["astra-responses"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", providers["astra-responses"]!["base_url"]!.GetValue<string>());
        Assert.Equal(["claude-sonnet-5"], Ids("astra-anthropic"));
        Assert.Equal("anthropic", providers["astra-anthropic"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", providers["astra-anthropic"]!["base_url"]!.GetValue<string>());
        Assert.Equal(["gemini-3-pro"], Ids("astra-gemini"));
        Assert.Equal("google", providers["astra-gemini"]!["type"]!.GetValue<string>());
        Assert.Equal("astra-anthropic", Hints.Jsonc(Home.ReadFile(_config))["models"]!["large"]!["provider"]!.GetValue<string>());
        Assert.True(_adapter.Inspect().Enabled);

        // Everything moves to chat: the extra providers go, and astra no longer discovers models on its own.
        var chatOnly = Hints.Extra("gpt-5.5", "glm-5");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-4", "glm-5", chatOnly)));
        providers = Hints.Jsonc(Home.ReadFile(_config))["providers"]!;
        Assert.Null(providers["astra-responses"]);
        Assert.Null(providers["astra-anthropic"]);
        Assert.Null(providers["astra-gemini"]);
        Assert.Equal(["gpt-5.5", "glm-5"], Ids("astra"));
        Assert.Empty(_adapter.Inspect().DriftedKeys);

        // Only Responses models: astra stays, without models and without discovery.
        var responsesOnly = TestGateway.Native(Hints.Extra("gpt-5.5"), "gpt-5.5", "openai-responses");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-crush-4", "gpt-5.5", responsesOnly)));
        providers = Hints.Jsonc(Home.ReadFile(_config))["providers"]!;
        Assert.Null(providers["astra"]!["models"]);
        Assert.False(providers["astra"]!["discover_models"]!.GetValue<bool>());
        Assert.Equal("astra-responses", Hints.Jsonc(Home.ReadFile(_config))["models"]!["large"]!["provider"]!.GetValue<string>());

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
    public void Models_GoToTheirNativeProtocolsArray_AndMoveWhenItChanges()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "gemini-3-pro", "glm-5");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini");
        TestGateway.Native(extra, "glm-5", "openai-chat", "anthropic");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-4", "claude-sonnet-5", extra)));

        var json = Hints.Jsonc(Home.ReadFile(_config));
        var openai = json["modelProviders"]!["openai"]!.AsArray().ToDictionary(e => e!["id"]!.GetValue<string>(), e => e!);
        Assert.Equal(["mine", "gpt-5.5", "glm-5"], openai.Keys);
        Assert.Equal("responses", openai["gpt-5.5"]["wireApi"]!.GetValue<string>());
        Assert.Null(openai["glm-5"]["wireApi"]);
        var anthropic = Assert.Single(json["modelProviders"]!["anthropic"]!.AsArray())!;
        Assert.Equal("claude-sonnet-5", anthropic["id"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", anthropic["baseUrl"]!.GetValue<string>());
        Assert.Equal(QwenCodeClientAdapter.KeyVariable, anthropic["envKey"]!.GetValue<string>());
        var gemini = Assert.Single(json["modelProviders"]!["gemini"]!.AsArray())!;
        Assert.Equal("http://127.0.0.1:17321", gemini["baseUrl"]!.GetValue<string>());
        Assert.Equal("anthropic", json["security"]!["auth"]!["selectedType"]!.GetValue<string>());
        Assert.Equal("claude-sonnet-5", json["model"]!["name"]!.GetValue<string>());

        // The user moves the Claude model to chat in Astra: it leaves the anthropic array for the openai one.
        TestGateway.Override(extra, "claude-sonnet-5", "openai-chat");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-qwen-4", "claude-sonnet-5", extra)));
        json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Empty(json["modelProviders"]!["anthropic"]?.AsArray() ?? []);
        Assert.Contains(json["modelProviders"]!["openai"]!.AsArray(), e => e!["id"]!.GetValue<string>() == "claude-sonnet-5");
        Assert.Equal("openai", json["security"]!["auth"]!["selectedType"]!.GetValue<string>());

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
    public void EachEntryNamesItsNativeProviderType_AndTheLimitsAstraKnows()
    {
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "deepseek-v4");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat");
        ((JsonObject)extra["models"]!["claude-sonnet-5"]!)["contextWindow"] = 200_000;
        ((JsonObject)extra["models"]!["claude-sonnet-5"]!)["maxOutputTokens"] = 64_000;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-droid-5", null, extra)));

        var models = Hints.Jsonc(Home.ReadFile(_config))["customModels"]!.AsArray().ToDictionary(e => e!["model"]!.GetValue<string>(), e => e!);
        Assert.Equal("openai", models["gpt-5.5"]["provider"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", models["gpt-5.5"]["baseUrl"]!.GetValue<string>());
        Assert.Equal("anthropic", models["claude-sonnet-5"]["provider"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", models["claude-sonnet-5"]["baseUrl"]!.GetValue<string>());
        Assert.Equal(200_000, models["claude-sonnet-5"]["maxContextLimit"]!.GetValue<long>());
        Assert.Equal(64_000, models["claude-sonnet-5"]["maxOutputTokens"]!.GetValue<long>());
        Assert.Equal("generic-chat-completion-api", models["deepseek-v4"]["provider"]!.GetValue<string>());
        Assert.Null(models["deepseek-v4"]["maxContextLimit"]);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
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
    public void Models_NameTheirNativeProtocol_AndAStaleOneIsTakenOutAgain()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "glm-5");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "glm-5", "openai-chat", "anthropic");
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["maxOutputTokens"] = 128_000;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-7", null, extra)));

        var tables = TomlTables(Home.ReadFile(_config));
        Assert.Equal("\"openai_responses\"", tables["models.astra-gpt-5-5"]["protocol"]);
        Assert.Equal("128000", tables["models.astra-gpt-5-5"]["max_output_size"]);
        Assert.Equal("\"GPT-5.5\"", tables["models.astra-gpt-5-5"]["display_name"]);
        Assert.Equal("\"anthropic\"", tables["models.astra-claude-sonnet-5"]["protocol"]);
        Assert.False(tables["models.astra-glm-5"].ContainsKey("protocol"));

        // The provider later serves the model over chat only: the protocol Astra wrote goes away again.
        TestGateway.Native(extra, "gpt-5.5", "openai-chat");
        ((JsonObject)extra["models"]!["gpt-5.5"]!).Remove("maxOutputTokens");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-kimi-7", null, extra)));
        tables = TomlTables(Home.ReadFile(_config));
        Assert.False(tables["models.astra-gpt-5-5"].ContainsKey("protocol"));
        Assert.False(tables["models.astra-gpt-5-5"].ContainsKey("max_output_size"));
        Assert.Equal("\"anthropic\"", tables["models.astra-claude-sonnet-5"]["protocol"]);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    /// <summary>The simple <c>key = value</c> lines of each <c>[table]</c>, values as raw TOML text.</summary>
    private static Dictionary<string, Dictionary<string, string>> TomlTables(string text)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>();
        var current = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('[')) tables[line.Trim('[', ']', ' ')] = current = new Dictionary<string, string>();
            else if (line.IndexOf(" = ", StringComparison.Ordinal) is var eq and > 0) current[line[..eq]] = line[(eq + 3)..];
        }
        return tables;
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
    public void ResponsesNativeModels_LeaveChatCompletions()
    {
        var extra = Hints.Extra("gpt-5.5", "deepseek-v4", "claude-sonnet-5");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses", "openai-chat");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic"); // no Anthropic provider written: chat, translated
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("k", null, extra)));

        var models = Hints.Jsonc(Home.ReadFile(_config))["language_models"]!["openai_compatible"]!["astra"]!["available_models"]!.AsArray()
            .ToDictionary(m => m!["name"]!.GetValue<string>(), m => m!["capabilities"]!);
        Assert.False(models["gpt-5.5"]["chat_completions"]!.GetValue<bool>());
        Assert.Null(models["deepseek-v4"]["chat_completions"]);
        Assert.Null(models["claude-sonnet-5"]["chat_completions"]);
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
    public void Models_CarryTheirNativeApi_AndTheProviderUsesPlainApiKeyAuth()
    {
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "gemini-3-pro", "deepseek-v4");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat", "anthropic");
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["maxOutputTokens"] = 128_000;
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["reasoning"] = true;
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-omp-4", null, extra)));

        var ours = YamlEditor.ParseToJson(Home.ReadFile(_models))!["providers"]!["astra"]!;
        Assert.Equal("apiKey", ours["auth"]!.GetValue<string>());
        var models = ours["models"]!.AsArray().ToDictionary(m => m!["id"]!.GetValue<string>(), m => m!);
        Assert.Equal("openai-responses", models["gpt-5.5"]["api"]!.GetValue<string>());
        Assert.Null(models["gpt-5.5"]["baseUrl"]);
        Assert.Equal(400_000, models["gpt-5.5"]["contextWindow"]!.GetValue<long>());
        Assert.Equal(128_000, models["gpt-5.5"]["maxTokens"]!.GetValue<long>());
        Assert.True(models["gpt-5.5"]["reasoning"]!.GetValue<bool>());
        Assert.Equal(["text", "image"], models["gpt-5.5"]["input"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("anthropic-messages", models["claude-sonnet-5"]["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", models["claude-sonnet-5"]["baseUrl"]!.GetValue<string>());
        Assert.Equal("google-generative-ai", models["gemini-3-pro"]["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1beta", models["gemini-3-pro"]["baseUrl"]!.GetValue<string>());
        Assert.Null(models["deepseek-v4"]["api"]);
        // Unknown limits stay unset: omp takes them from its catalog for a known id.
        Assert.Null(models["deepseek-v4"]["contextWindow"]);
        Assert.Null(models["deepseek-v4"]["maxTokens"]);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
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
        Assert.Null(defaults["reasoningEffort"]); // an effort the model does not declare is refused
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
    public void ThinkingModelsDeclareReasoningEfforts_OthersDeclareNothing()
    {
        // GitHub issue 7: a model without `reasoningEfforts` is a non-reasoning one to Harness, which then offers no effort picker.
        Home.WriteFile(_desktop, Desktop);
        var extra = Hints.Extra("thinks", "plain", "unknown");
        ((JsonObject)extra["models"]!["thinks"]!)["reasoning"] = true;
        ((JsonObject)extra["models"]!["plain"]!)["reasoning"] = false;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-9", null, extra)));

        var models = Row(Home.ReadFile(_desktop), "llm-pi-ai")["config"]!["providers"]!["astra"]!["models"]!.AsArray();
        JsonNode Model(string id) => models.Single(m => m!["id"]!.GetValue<string>() == id)!;
        var efforts = Model("thinks")["reasoningEfforts"]!.AsObject();
        Assert.Equal(["low", "medium", "high"], efforts.Select(p => p.Key)); // only `off` may be valueless, and it is not offered
        Assert.All(efforts, p => Assert.Equal(p.Key, p.Value!.GetValue<string>()));
        Assert.Null(Model("thinks")["reasoning"]); // the route's `reasoning` is a default selection, not a capability
        Assert.Null(Model("plain")["reasoningEfforts"]);
        Assert.Null(Model("unknown")["reasoningEfforts"]);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
    }

    [Fact]
    public void ModelsAreListedUnderARouteOfTheirNativeApi_AndTheDefaultNamesThatRoute()
    {
        Home.WriteFile(_desktop, Desktop);
        var original = Home.ReadFileBytes(_desktop);
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "deepseek-v4");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat", "anthropic");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-6", "claude-sonnet-5", extra)));

        var text = Home.ReadFile(_desktop);
        var providers = Row(text, "llm-pi-ai")["config"]!["providers"]!;
        string[] Ids(string route) => providers[route]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(["deepseek-v4"], Ids("astra"));
        Assert.Null(providers["astra"]!["models"]![0]!["contextWindow"]); // unknown: Harness's own default
        Assert.Equal(["gpt-5.5"], Ids("astra-responses"));
        Assert.Equal("openai-responses", providers["astra-responses"]!["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", providers["astra-responses"]!["baseURL"]!.GetValue<string>());
        Assert.Equal(["claude-sonnet-5"], Ids("astra-anthropic"));
        Assert.Equal("anthropic-messages", providers["astra-anthropic"]!["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", providers["astra-anthropic"]!["baseURL"]!.GetValue<string>());
        Assert.Equal(DeepSeekHarnessClientAdapter.KeyVariable, providers["astra-anthropic"]!["apiKeyEnv"]!.GetValue<string>());
        Assert.Equal("astra-anthropic", Row(text, "agent-default-model")["config"]!["provider"]!.GetValue<string>());

        // The Anthropic model is no longer offered: its route goes, the default follows the new choice.
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-6", "gpt-5.5", TestGateway.Native(Hints.Extra("gpt-5.5", "deepseek-v4"), "gpt-5.5", "openai-responses"))));
        text = Home.ReadFile(_desktop);
        Assert.Null(Row(text, "llm-pi-ai")["config"]!["providers"]!["astra-anthropic"]);
        Assert.Equal("astra-responses", Row(text, "agent-default-model")["config"]!["provider"]!.GetValue<string>());
        Assert.Empty(_adapter.Inspect().DriftedKeys);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_desktop));
    }

    [Fact]
    public void Disable_AfterHarnessFoldedTheModelItems_RestoresBytes()
    {
        Home.WriteFile(_desktop, Desktop);
        var original = Home.ReadFileBytes(_desktop);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-dsh-7", null, Hints.Extra("m1", "m2"))));
        // Harness's own save spreads each flow item over several lines; the content stays the same.
        var text = Home.ReadFile(_desktop);
        var folded = System.Text.RegularExpressions.Regex.Replace(text, "- \\{\"id\":\"(\\w+)\",\"name\":\"(\\w+)\"\\}",
            "- {\n              \"id\": \"$1\",\n              \"name\": \"$2\"\n            }");
        Assert.Contains("\"id\": \"m2\",\n", folded);
        Home.WriteFile(_desktop, folded);
        Assert.Empty(_adapter.Inspect().DriftedKeys);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_desktop));
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
        _config = Home.File(".workbuddy", "models.json");
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

    [Fact]
    public void BareEmptyArrayFile_IsTreatedAsAnEmptyConfig()
    {
        // WorkBuddy creates ~/.workbuddy/models.json as "[]"; its daemon reads that as no models.
        Home.WriteFile(_config, "[]");
        var plan = _adapter.PlanEnable(TestGateway.Context("astra-wb-6", null, Hints.Extra("a")));
        Assert.NotEmpty(plan.Diffs); // the preview works too
        Applier.Apply(plan);
        var json = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal(["a"], json["models"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.True(_adapter.Inspect().Enabled);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Empty(Assert.IsType<JsonObject>(Hints.Jsonc(Home.ReadFile(_config)))); // no Astra entries left; still an empty config
    }

    [Theory]
    [InlineData("[ ]")]
    [InlineData("[\n]\n")]
    [InlineData("// models go here\n[]\n")]
    public void EmptyArrayFileInAnyLayout_IsTreatedAsAnEmptyConfig(string text)
    {
        // GitHub issue 5: only the exact text "[]" used to be recognised, the rest failed with "parent is missing".
        Home.WriteFile(_config, text);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-7", null, Hints.Extra("a"))));
        Assert.Equal(["a"], Hints.Jsonc(Home.ReadFile(_config))["models"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.True(_adapter.Inspect().Enabled);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Empty(Assert.IsType<JsonObject>(Hints.Jsonc(Home.ReadFile(_config))));
    }

    [Fact]
    public void BareArrayHoldingModels_IsRefusedWithAClearMessage_AndLeftAlone()
    {
        const string text = """[{"id":"mine","name":"Mine","vendor":"OpenAI","url":"https://example.com/v1/chat/completions","apiKey":"k"}]""";
        Home.WriteFile(_config, text);
        var ex = Assert.Throws<EditorException>(() =>
            Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-8", null, Hints.Extra("a")))));
        Assert.Contains("top level is an array", ex.Message);
        Assert.Equal(text, Home.ReadFile(_config));
    }

    [Fact]
    public void LegacyCodeBuddyEntries_AreRemovedOnTheNextApply()
    {
        // Astra ≤ 0.3.0 wrote WorkBuddy's models into ~/.codebuddy/models.json (CodeBuddy's file).
        var legacy = Home.File(".codebuddy", "models.json");
        const string entry = """{"id":"a","name":"Astra: a","vendor":"Astra","url":"http://127.0.0.1:1/v1/chat/completions","apiKey":"old"}""";
        Applier.Apply(new ConfigChangePlan(_adapter.Kind,
            [new ConfigChange(legacy, ConfigFileFormat.Json, "models.[name=Astra: a]", null, entry)], []));
        Assert.True(_adapter.Inspect().Enabled); // entries recorded there still count, until they are migrated

        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-wb-5", null, Hints.Extra("a"))));
        Assert.Null(Hints.Jsonc(Home.ReadFile(legacy))["models"]); // the array Astra created goes with its last entry
        Assert.DoesNotContain(Store.List(_adapter.Kind), e => e.FilePath == legacy);
        Assert.Equal(["a"], Hints.Jsonc(Home.ReadFile(_config))["models"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
    }

    [Fact]
    public void InternationalBuild_IsItsOwnClient_WritingOnlyWorkBuddyAiModelsJson()
    {
        // WorkBuddy AI (the international build) keeps its data in ~/.workbuddy-ai; it is a client of its own.
        var intlAdapter = new WorkBuddyClientAdapter(Home.Env, Store, international: true);
        var intl = Home.File(".workbuddy-ai", "models.json");
        Assert.Equal(ClientKinds.WorkBuddyAi, intlAdapter.Kind);
        Assert.False(intlAdapter.Detect().Detected);
        Home.WriteFile(intl, Original);
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(intl);
        Assert.True(intlAdapter.Detect().Detected);
        Assert.Equal([intl], intlAdapter.ConfigPaths());

        Applier.Apply(intlAdapter.PlanEnable(TestGateway.Context("astra-wb-7", null, Hints.Extra("a", "b"))));
        Applier.Apply(intlAdapter.PlanEnable(TestGateway.Context("astra-wb-7", null, Hints.Extra("a"))));
        Assert.Equal(["mine", "a"], Hints.Jsonc(Home.ReadFile(intl))["models"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal(["mine"], Hints.Jsonc(Home.ReadFile(_config))["models"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.True(intlAdapter.Inspect().Enabled);
        Assert.False(_adapter.Inspect().Enabled); // the China build's client is untouched

        Assert.False(Applier.Disable(intlAdapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(intl));
    }
}

public class NextCoWorkClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          "version": 1,
          "providers": {
            "mine": { "name": "Mine", "protocol": "anthropic", "baseUrl": "https://example.com", "apiKey": "k" }
          }
        }
        """;

    private readonly NextCoWorkClientAdapter _adapter;
    private readonly string _config;

    public NextCoWorkClientAdapterTests()
    {
        _adapter = new NextCoWorkClientAdapter(Home.Env, Store);
        _config = Home.File(".next-cowork", "providers.json");
    }

    [Fact]
    public void Enable_AddsOneProvider_LeavesUserProviders_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.1", "glm-5");
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["contextWindow"] = 400_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["maxOutputTokens"] = 32_000;
        ((JsonObject)extra["models"]!["gpt-5.1"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-1", "gpt-5.1", extra)));

        var providers = Hints.Jsonc(Home.ReadFile(_config))["providers"]!.AsObject();
        Assert.Equal(["mine", "astra"], providers.Select(p => p.Key));
        Assert.Equal("k", providers["mine"]!["apiKey"]!.GetValue<string>());

        var ours = providers["astra"]!;
        Assert.Equal("openai-chat", ours["protocol"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("astra-nc-1", ours["apiKey"]!.GetValue<string>());
        // Defaults (priority 50, enabled) are NextCoWork's: leaving them out keeps the entry untouched by its normaliser.
        Assert.Null(ours["priority"]);
        Assert.Null(ours["enabled"]);

        var models = ours["models"]!.AsObject();
        Assert.Equal(["gpt-5.1", "glm-5"], models.Select(m => m.Key));
        Assert.Equal("gpt-5.1", models["gpt-5.1"]!["upstreamModel"]!.GetValue<string>());
        Assert.Equal(400_000, models["gpt-5.1"]!["contextWindow"]!.GetValue<long>());
        Assert.Equal(32_000, models["gpt-5.1"]!["maxOutputTokens"]!.GetValue<long>());
        Assert.True(models["gpt-5.1"]!["capabilities"]!["vision"]!.GetValue<bool>());
        Assert.Null(models["glm-5"]!["capabilities"]);
        Assert.Null(models["gpt-5.1"]!["capabilities"]!["thinking"]);
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
        Assert.False(_adapter.Inspect().Enabled);
    }

    [Fact]
    public void EachModelUsesItsNativeProtocol_UnlessTheUserChoseAnother()
    {
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "deepseek-v4", "gemini-3-pro", "kimi-k3");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini"); // NextCoWork has no Gemini protocol: the gateway translates
        TestGateway.Native(extra, "kimi-k3", "openai-responses", "openai-chat");
        TestGateway.Override(extra, "kimi-k3", "openai-chat");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-8", null, extra)));

        var ours = Hints.Jsonc(Home.ReadFile(_config))["providers"]!["astra"]!;
        Assert.Equal("openai-chat", ours["protocol"]!.GetValue<string>());
        var models = ours["models"]!.AsObject();
        Assert.Equal("openai-responses", models["gpt-5.5"]!["protocolOverride"]!.GetValue<string>());
        Assert.Equal("anthropic", models["claude-sonnet-5"]!["protocolOverride"]!.GetValue<string>());
        Assert.Null(models["deepseek-v4"]!["protocolOverride"]);
        Assert.Null(models["gemini-3-pro"]!["protocolOverride"]);
        Assert.Null(models["kimi-k3"]!["protocolOverride"]);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
    }

    [Fact]
    public void WhatNextCoWorkAddsIsNotDrift_SurvivesARewrite_AndItsProtocolChoiceIsReported()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "deepseek-v4");
        TestGateway.Native(extra, "gpt-5.5", "openai-chat", "openai-responses");
        ((JsonObject)extra["models"]!["gpt-5.5"]!)["vision"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-10", null, extra)));
        Assert.Empty(_adapter.ChangedProtocolChoices());

        // The user edits gpt-5.5 in NextCoWork: it writes the resolved alias back (thinking settings, the full capability
        // set, catalogOverrides) with the protocol they picked, and adds the provider defaults.
        var json = Hints.Jsonc(Home.ReadFile(_config));
        var ours = json["providers"]!["astra"]!.AsObject();
        ours["priority"] = 50;
        ours["enabled"] = true;
        var gpt = ours["models"]!["gpt-5.5"]!.AsObject();
        gpt["protocolOverride"] = "openai-responses";
        gpt["thinkingConfig"] = new JsonObject { ["mode"] = "effort", ["defaultEnabled"] = true, ["defaultEffort"] = "high" };
        gpt["catalogOverrides"] = new JsonArray("thinkingConfig");
        gpt["capabilities"]!["tools"] = true;
        gpt["capabilities"]!["caching"] = false;
        Home.WriteFile(_config, json.ToJsonString());

        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Empty(status.DriftedKeys);
        Assert.Equal(Astra.Core.ApiProtocol.OpenAIResponses, Assert.Single(_adapter.ChangedProtocolChoices()).Value);

        // The server records the choice and rewrites (e.g. a refreshed model list): NextCoWork's fields stay.
        TestGateway.Override(extra, "gpt-5.5", "openai-responses");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-10", null, extra)));
        ours = Hints.Jsonc(Home.ReadFile(_config))["providers"]!["astra"]!.AsObject();
        Assert.Equal(50, ours["priority"]!.GetValue<int>());
        gpt = ours["models"]!["gpt-5.5"]!.AsObject();
        Assert.Equal("openai-responses", gpt["protocolOverride"]!.GetValue<string>());
        Assert.Equal("high", gpt["thinkingConfig"]!["defaultEffort"]!.GetValue<string>());
        Assert.True(gpt["capabilities"]!["vision"]!.GetValue<bool>());
        Assert.False(gpt["capabilities"]!["caching"]!.GetValue<bool>());
        Assert.Empty(_adapter.ChangedProtocolChoices());

        // Removing the override in NextCoWork means the provider's protocol (chat).
        json = Hints.Jsonc(Home.ReadFile(_config));
        json["providers"]!["astra"]!["models"]!["gpt-5.5"]!.AsObject().Remove("protocolOverride");
        Home.WriteFile(_config, json.ToJsonString());
        Assert.Equal(Astra.Core.ApiProtocol.OpenAIChat, Assert.Single(_adapter.ChangedProtocolChoices()).Value);

        // An Astra-owned value changed in NextCoWork is still drift; NextCoWork-only changes do not block the restore.
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void MaxOutputNeverExceedsTheWindow_AndPdfMapsToFileInput()
    {
        var extra = Hints.Extra("a", "b");
        ((JsonObject)extra["models"]!["a"]!)["contextWindow"] = 100_000;
        ((JsonObject)extra["models"]!["a"]!)["maxOutputTokens"] = 128_000;
        ((JsonObject)extra["models"]!["a"]!)["pdf"] = true;
        ((JsonObject)extra["models"]!["b"]!)["maxOutputTokens"] = 384_000; // no window: NextCoWork assumes 200k
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-9", null, extra)));

        var models = Hints.Jsonc(Home.ReadFile(_config))["providers"]!["astra"]!["models"]!;
        Assert.Equal(100_000, models["a"]!["maxOutputTokens"]!.GetValue<long>());
        Assert.True(models["a"]!["capabilities"]!["fileInput"]!.GetValue<bool>());
        Assert.Null(models["a"]!["capabilities"]!["vision"]);
        Assert.Equal(200_000, models["b"]!["maxOutputTokens"]!.GetValue<long>());
    }

    [Fact]
    public void ModelsOnlyAppearWhenTheProviderHasThem_AndSelectedModelIsAlwaysListed()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-2", "only-selected", null)));
        var models = Hints.Jsonc(Home.ReadFile(_config))["providers"]!["astra"]!["models"]!.AsObject();
        Assert.Equal(["only-selected"], models.Select(m => m.Key));

        Applier.Disable(_adapter.PlanDisable());
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-3", null, null)));
        Assert.Null(Hints.Jsonc(Home.ReadFile(_config))["providers"]!["astra"]!["models"]);
    }

    [Fact]
    public void KeyRotationAndRefreshedModelList_ReplaceOnlyOurProvider()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-4", null, Hints.Extra("a", "b"))));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-5", null, Hints.Extra("a"))));
        var providers = Hints.Jsonc(Home.ReadFile(_config))["providers"]!.AsObject();
        Assert.Equal("astra-nc-5", providers["astra"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(["a"], providers["astra"]!["models"]!.AsObject().Select(m => m.Key));
        Assert.Equal("k", providers["mine"]!["apiKey"]!.GetValue<string>());
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-6", null, Hints.Extra("a"))));
        Assert.True(Home.FileExists(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config));
    }

    [Fact]
    public void EntryEditedInNextCoWork_IsDriftAndIsNotOverwrittenByDisable()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-nc-7", null, Hints.Extra("a"))));
        // NextCoWork re-saves the provider with explicit defaults when the user touches it in its settings.
        var edited = Home.ReadFile(_config).Replace("\"name\":\"Astra\"", "\"name\":\"My Astra\"");
        Assert.NotEqual(Home.ReadFile(_config), edited);
        Home.WriteFile(_config, edited);

        Assert.Contains("providers.astra", _adapter.Inspect().DriftedKeys);
        Assert.True(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Contains("My Astra", Home.ReadFile(_config));
    }

    [Fact]
    public void Detect_FollowsTheConfigDirectory_AndPathsAreFixed()
    {
        Assert.False(_adapter.Detect().Detected);
        Home.WriteFile(_config, Original);
        Assert.True(_adapter.Detect().Detected);
        Assert.Equal([_config], _adapter.ConfigPaths());
        Assert.Equal(ClientMode.Coexist, _adapter.Mode);
        Assert.Equal(Astra.Core.ApiProtocol.OpenAIChat, ClientKinds.ProtocolOf(_adapter.Kind));
    }
}

public class PaseoClientAdapterTests : AdapterTestBase
{
    private const string Original = """
        {
          "$schema": "https://paseo.sh/schemas/paseo.config.v1.json",
          "version": 1,
          "daemon": { "listen": "127.0.0.1:6767" },
          "agents": {
            "providers": {
              "copilot": { "enabled": false },
              "zai": { "extends": "claude", "label": "ZAI", "env": { "ANTHROPIC_AUTH_TOKEN": "k" } }
            }
          }
        }
        """;

    private readonly PaseoClientAdapter _adapter;
    private readonly string _config;

    public PaseoClientAdapterTests()
    {
        _adapter = new PaseoClientAdapter(Home.Env, Store);
        _config = Home.File(".paseo", "config.json");
    }

    [Fact]
    public void Enable_AddsOneClaudeProfile_LeavesUserProviders_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-1", "glm-5", Hints.Extra("claude-opus-4-6", "glm-5"))));

        var root = Hints.Jsonc(Home.ReadFile(_config));
        Assert.Equal("127.0.0.1:6767", root["daemon"]!["listen"]!.GetValue<string>());
        var providers = root["agents"]!["providers"]!.AsObject();
        Assert.Equal(["copilot", "zai", "astra"], providers.Select(p => p.Key));
        Assert.Equal("k", providers["zai"]!["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());

        var ours = providers["astra"]!.AsObject();
        // Paseo's schema is strict: only documented provider fields.
        Assert.Equal(["extends", "label", "description", "env", "disallowedTools", "models"], ours.Select(p => p.Key));
        Assert.Equal("claude", ours["extends"]!.GetValue<string>());
        Assert.Equal("Astra", ours["label"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", ours["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("astra-paseo-1", ours["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal(["WebSearch"], ours["disallowedTools"]!.AsArray().Select(t => t!.GetValue<string>()));

        var models = ours["models"]!.AsArray();
        Assert.Equal(["claude-opus-4-6", "glm-5"], models.Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal("GLM-5", models[1]!["label"]!.GetValue<string>());
        Assert.Null(models[0]!["isDefault"]);
        Assert.True(models[1]!["isDefault"]!.GetValue<bool>());
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
        Assert.False(_adapter.Inspect().Enabled);
    }

    [Fact]
    public void ModelLabelFallsBackToId_FirstModelIsDefault_NoModelsLeavesPaseoList()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-2", "only-selected", null)));
        var models = Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!["astra"]!["models"]!.AsArray();
        Assert.Single(models);
        Assert.Equal("only-selected", models[0]!["label"]!.GetValue<string>());
        Assert.True(models[0]!["isDefault"]!.GetValue<bool>());

        Applier.Disable(_adapter.PlanDisable());
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-3", null, Hints.Extra("a", "b"))));
        models = Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!["astra"]!["models"]!.AsArray();
        Assert.True(models[0]!["isDefault"]!.GetValue<bool>());

        Applier.Disable(_adapter.PlanDisable());
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-4", null, null)));
        Assert.Null(Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!["astra"]!["models"]);
    }

    [Fact]
    public void ResponsesNativeModels_GetACodexEntry_ThatGoesAgainWhenEmpty()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("claude-sonnet-5", "gpt-5.5", "deepseek-v4");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "deepseek-v4", "openai-chat"); // Claude Code reaches it, translated
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-7", "gpt-5.5", extra)));

        var providers = Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!;
        Assert.Equal(["claude-sonnet-5", "deepseek-v4"], providers["astra"]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        var codex = providers["astra-codex"]!;
        Assert.Equal("codex", codex["extends"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", codex["env"]!["OPENAI_BASE_URL"]!.GetValue<string>());
        Assert.Equal("astra-paseo-7", codex["env"]!["OPENAI_API_KEY"]!.GetValue<string>());
        var codexModel = Assert.Single(codex["models"]!.AsArray())!;
        Assert.Equal("gpt-5.5", codexModel["id"]!.GetValue<string>());
        Assert.True(codexModel["isDefault"]!.GetValue<bool>());

        // Only Responses models: the Claude entry still lists them (translated), never empty.
        var responsesOnly = TestGateway.Native(Hints.Extra("gpt-5.5"), "gpt-5.5", "openai-responses");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-7", null, responsesOnly)));
        providers = Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!;
        Assert.Equal(["gpt-5.5"], providers["astra"]!["models"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.NotNull(providers["astra-codex"]);

        // No Responses models any more: the Codex entry goes.
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-7", null, Hints.Extra("deepseek-v4"))));
        Assert.Null(Hints.Jsonc(Home.ReadFile(_config))["agents"]!["providers"]!["astra-codex"]);
        Assert.Empty(_adapter.Inspect().DriftedKeys);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-5", null, Hints.Extra("a"))));
        Assert.True(Home.FileExists(_config));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config));
    }

    [Fact]
    public void EntryEditedInPaseo_IsDriftAndIsNotOverwrittenByDisable()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-paseo-6", null, Hints.Extra("a"))));
        var edited = Home.ReadFile(_config).Replace("\"label\":\"Astra\"", "\"label\":\"My Astra\"");
        Assert.NotEqual(Home.ReadFile(_config), edited);
        Home.WriteFile(_config, edited);

        Assert.Contains("agents.providers.astra", _adapter.Inspect().DriftedKeys);
        Assert.True(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Contains("My Astra", Home.ReadFile(_config));
    }

    [Fact]
    public void PaseoHome_MovesTheConfig()
    {
        Home.Variables["PASEO_HOME"] = Home.File("custom-paseo");
        var moved = Home.File("custom-paseo", "config.json");
        Assert.Equal([moved], _adapter.ConfigPaths());
        Assert.False(_adapter.Detect().Detected);
        Home.WriteFile(moved, "{}");
        Assert.True(_adapter.Detect().Detected);
    }

    [Fact]
    public void Detect_FollowsTheConfigDirectory_SpeaksAnthropic()
    {
        Assert.False(_adapter.Detect().Detected);
        Home.WriteFile(_config, Original);
        Assert.True(_adapter.Detect().Detected);
        Assert.Equal([_config], _adapter.ConfigPaths());
        Assert.Equal(ClientMode.Coexist, _adapter.Mode);
        Assert.Equal(Astra.Core.ApiProtocol.Anthropic, ClientKinds.ProtocolOf(_adapter.Kind));
        Assert.Contains(ClientKinds.Paseo, ClientKinds.WithModelList);
    }
}

public class ZCodeClientAdapterTests : AdapterTestBase
{
    // provider_config.json as ZCode 3.15 writes it: one provider the user added, a model rule of theirs.
    private const string Original = """
        {
          "schemaVersion": 1,
          "config": {
            "providerOrder": [
              "mine"
            ],
            "providerConfigRules": {
              "providerRules": [
                {
                  "providerId": "mine",
                  "providerName": "Mine",
                  "enabled": true,
                  "config": {
                    "group": "standard-personal",
                    "access": {
                      "type": "api-key",
                      "apiKey": "k"
                    },
                    "api": {
                      "type": "openai-responses",
                      "baseUrl": "https://example.com/v1"
                    },
                    "personalModelIds": [
                      "m1"
                    ],
                    "modelOrder": [
                      "m1"
                    ]
                  }
                }
              ]
            },
            "modelConfigRules": {
              "providerModelRules": [
                {
                  "modelId": "m1",
                  "config": {
                    "properties": {
                      "contextWindow": 1000000
                    }
                  },
                  "providerId": "mine"
                }
              ],
              "manualProviderModelRules": []
            }
          }
        }
        """;

    private readonly ZCodeClientAdapter _adapter;
    private readonly string _config;

    public ZCodeClientAdapterTests()
    {
        _adapter = new ZCodeClientAdapter(Home.Env, Store);
        _config = Home.File(".zcode", "v2", "provider_config.json");
    }

    private JsonNode Config => Hints.Jsonc(Home.ReadFile(_config))["config"]!;

    private JsonArray Rules => Config["providerConfigRules"]!["providerRules"]!.AsArray();

    private JsonArray ModelRules => Config["modelConfigRules"]!["providerModelRules"]!.AsArray();

    private JsonNode Rule(string id) => Rules.Single(r => r!["providerId"]!.GetValue<string>() == id)!;

    [Fact]
    public void Enable_AddsAProviderRuleAndModelRules_LeavesTheUsersOwn_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("glm-5.3", "deepseek-v4");
        ((JsonObject)extra["models"]!["glm-5.3"]!)["contextWindow"] = 1_000_000;
        ((JsonObject)extra["models"]!["glm-5.3"]!)["vision"] = true;
        ((JsonObject)extra["models"]!["glm-5.3"]!)["pdf"] = false;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-1", "glm-5.3", extra)));

        Assert.Equal(["mine", "astra"], Rules.Select(r => r!["providerId"]!.GetValue<string>()));
        Assert.Equal("k", Rule("mine")["config"]!["access"]!["apiKey"]!.GetValue<string>());
        var ours = Rule("astra");
        Assert.Equal("Astra", ours["providerName"]!.GetValue<string>());
        Assert.True(ours["enabled"]!.GetValue<bool>());
        var config = ours["config"]!;
        Assert.Equal("standard-personal", config["group"]!.GetValue<string>());
        Assert.Equal("api-key", config["access"]!["type"]!.GetValue<string>());
        Assert.Equal("astra-zc-1", config["access"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("openai-chat-completions", config["api"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", config["api"]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(["glm-5.3", "deepseek-v4"], config["personalModelIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["glm-5.3", "deepseek-v4"], config["modelOrder"]!.AsArray().Select(n => n!.GetValue<string>()));
        // providerOrder is ZCode's: providers it does not name are listed after the ones it does.
        Assert.Equal(["mine"], Config["providerOrder"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Equal(3, ModelRules.Count);
        var glm = ModelRules.Single(r => r!["modelId"]!.GetValue<string>() == "glm-5.3")!;
        Assert.Equal("astra", glm["providerId"]!.GetValue<string>());
        var properties = glm["config"]!["properties"]!;
        Assert.Equal(1_000_000, properties["contextWindow"]!.GetValue<long>());
        Assert.True(properties["inputFormat"]!["supportsImage"]!.GetValue<bool>());
        Assert.False(properties["inputFormat"]!["supportsPdf"]!.GetValue<bool>());
        Assert.False(properties["supportsNativeWebSearch"]!.GetValue<bool>());
        var deepseek = ModelRules.Single(r => r!["modelId"]!.GetValue<string>() == "deepseek-v4")!;
        Assert.Null(deepseek["config"]!["properties"]!["contextWindow"]);
        Assert.Empty(Config["modelConfigRules"]!["manualProviderModelRules"]!.AsArray());
        Assert.True(_adapter.Inspect().Enabled);
        Assert.Empty(_adapter.Inspect().Repairs);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
        Assert.False(_adapter.Inspect().Enabled);
    }

    [Fact]
    public void ModelsGoToOneProviderPerProtocol_CreatedOnlyWhenNeeded()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "deepseek-v4", "gemini-3-pro");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini"); // ZCode has no Gemini protocol: the gateway translates
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-2", null, extra)));

        Assert.Equal(["mine", "astra", "astra-responses", "astra-anthropic"], Rules.Select(r => r!["providerId"]!.GetValue<string>()));
        Assert.Equal(["deepseek-v4", "gemini-3-pro"], Rule("astra")["config"]!["personalModelIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        var responses = Rule("astra-responses")["config"]!;
        Assert.Equal("openai-responses", responses["api"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", responses["api"]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(["gpt-5.5"], responses["personalModelIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        var anthropic = Rule("astra-anthropic")["config"]!;
        Assert.Equal("anthropic-messages", anthropic["api"]!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321", anthropic["api"]!["baseUrl"]!.GetValue<string>()); // ZCode adds /v1/messages
        Assert.Equal("Astra (Anthropic)", Rule("astra-anthropic")["providerName"]!.GetValue<string>());
        Assert.Contains(ModelRules, r => r!["modelId"]!.GetValue<string>() == "claude-sonnet-5" && r["providerId"]!.GetValue<string>() == "astra-anthropic");

        // The user moves Claude to chat: the Anthropic provider and its model rule go, the model rule moves to "astra".
        TestGateway.Override(extra, "claude-sonnet-5", "openai-chat");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-2", null, extra)));
        Assert.Equal(["mine", "astra", "astra-responses"], Rules.Select(r => r!["providerId"]!.GetValue<string>()));
        var claude = Assert.Single(ModelRules, r => r!["modelId"]!.GetValue<string>() == "claude-sonnet-5")!;
        Assert.Equal("astra", claude["providerId"]!.GetValue<string>());

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void WhatZCodeChangesIsNotDrift_AndSurvivesARewrite()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-3", null, Hints.Extra("a", "b", "c"))));

        // In ZCode's settings the user switches the provider off, drags c to the top and toggles model a; ZCode then saves
        // the whole file in its canonical form.
        var json = Hints.Jsonc(Home.ReadFile(_config));
        var rules = json["config"]!["providerConfigRules"]!["providerRules"]!.AsArray();
        var ours = rules.Single(r => r!["providerId"]!.GetValue<string>() == "astra")!.AsObject();
        ours["enabled"] = false;
        ours["config"]!["modelOrder"] = new JsonArray("c", "a", "b");
        var a = json["config"]!["modelConfigRules"]!["providerModelRules"]!.AsArray()
            .Single(r => r!["modelId"]!.GetValue<string>() == "a")!["config"]!.AsObject();
        a["enabled"] = true;
        Home.WriteFile(_config, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Empty(status.DriftedKeys);

        // A rotated key and a refreshed model list (b left, d came) rewrite the rules: ZCode's choices stay.
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-4", null, Hints.Extra("a", "c", "d"))));
        var rule = Rule("astra");
        Assert.False(rule["enabled"]!.GetValue<bool>());
        Assert.Equal("astra-zc-4", rule["config"]!["access"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(["a", "c", "d"], rule["config"]!["personalModelIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["c", "a", "d"], rule["config"]!["modelOrder"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(ModelRules.Single(r => r!["modelId"]!.GetValue<string>() == "a")!["config"]!["enabled"]!.GetValue<bool>());
        Assert.DoesNotContain(ModelRules, r => r!["modelId"]!.GetValue<string>() == "b");
        Assert.Empty(_adapter.Inspect().DriftedKeys);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(["mine"], Rules.Select(r => r!["providerId"]!.GetValue<string>()));
        Assert.Equal(["m1"], ModelRules.Select(r => r!["modelId"]!.GetValue<string>()));
        Assert.Equal(Hints.Jsonc(System.Text.Encoding.UTF8.GetString(original)).ToJsonString(), Hints.Jsonc(Home.ReadFile(_config)).ToJsonString());
    }

    [Fact]
    public void AnAstraValueEditedInZCode_IsDriftAndIsNotRemovedByDisable()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-5", null, Hints.Extra("a"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("http://127.0.0.1:17321/v1", "http://127.0.0.1:9999/v1"));

        Assert.Contains("config.providerConfigRules.providerRules.[providerId=astra]", _adapter.Inspect().DriftedKeys);
        Assert.False(_adapter.Inspect().Enabled);
        Assert.True(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Contains("127.0.0.1:9999", Home.ReadFile(_config));
    }

    [Fact]
    public void WithoutTheFile_EnableRefuses_SoZCodeStillImportsItsOlderConfig()
    {
        Home.WriteFile(Home.File(".zcode", "v2", "config.json"), """{"provider":{}}""");
        Assert.True(_adapter.Detect().Detected);
        var error = Assert.Throws<EditorException>(() => _adapter.PlanEnable(TestGateway.Context("astra-zc-6", null, Hints.Extra("a"))));
        Assert.Contains("start ZCode once", error.Message);
        Assert.False(Home.FileExists(_config));
    }

    [Fact]
    public void AModelIdASelectorCannotAddress_IsListedWithoutAModelRule()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-zc-7", null, Hints.Extra("a&b", "c"))));
        Assert.Equal(["a&b", "c"], Rule("astra")["config"]!["personalModelIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain(ModelRules, r => r!["modelId"]!.GetValue<string>() == "a&b");
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
    }

    [Fact]
    public void Detect_FollowsZCodesHome_AStrictParserIsAssumed_AndPathsAreFixed()
    {
        Assert.False(_adapter.Detect().Detected);
        Home.WriteFile(_config, Original.Replace("\"schemaVersion\": 1,", "// mine\n  \"schemaVersion\": 1,"));
        Assert.True(_adapter.Detect().Detected);
        Assert.Single(_adapter.Inspect().Repairs);
        Assert.Equal([_config], _adapter.ConfigPaths());
        Assert.Equal(ClientMode.Coexist, _adapter.Mode);
        Assert.Contains(ClientKinds.ZCode, ClientKinds.WithModelList);
        Assert.True(ClientProtocols.IsPerModel(ClientKinds.ZCode));

        var elsewhere = Path.Combine(Home.Path, "zhome");
        Home.Variables["ZCODE_DESKTOP_HOME_DIR"] = elsewhere;
        Assert.False(_adapter.Detect().Detected);
        Assert.Equal([Path.Combine(elsewhere, ".zcode", "v2", "provider_config.json")], _adapter.ConfigPaths());
    }
}

public class TKCodeClientAdapterTests : AdapterTestBase
{
    // settings.json as a TKCode user keeps it: JSONC with a comment, a built-in provider, a custom one and other settings.
    private const string Original = """
        {
          "$schema": "./settings.schema.json",
          // default for new chats
          "model": "anthropic/claude-opus-5-5",
          "language": "en-US",
          "providers": {
            "anthropic": { "apiKey": "{env:ANTHROPIC_API_KEY}" },
            "mine": { "api": "chat", "baseUrl": "https://example.com/v1", "apiKey": "k", "models": { "m1": {} } }
          },
          "mcpServers": { "fs": { "command": "npx" } }
        }
        """;

    private readonly TKCodeClientAdapter _adapter;
    private readonly string _config;

    public TKCodeClientAdapterTests()
    {
        _adapter = new TKCodeClientAdapter(Home.Env, Store);
        _config = Home.File(".tkcode", "settings.json");
    }

    private JsonNode Settings => Hints.Jsonc(Home.ReadFile(_config));

    private JsonNode Ours => Settings["providers"]!["astra"]!;

    private JsonNode Model(string id) => Ours["models"]![id]!;

    private static string[] Keys(JsonNode node) => [.. node.AsObject().Select(p => p.Key)];

    [Fact]
    public void Enable_AddsAProviderAndSelectsTheModel_LeavesTheUsersOwn_DisableRestoresBytes()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("glm-5.3", "deepseek-v4");
        var glm = (JsonObject)extra["models"]!["glm-5.3"]!;
        glm["contextWindow"] = 1_000_000;
        glm["maxOutputTokens"] = 128_000;
        glm["vision"] = true;
        glm["reasoning"] = true;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-1", "glm-5.3", extra)));

        var text = Home.ReadFile(_config);
        Assert.Contains("// default for new chats", text);
        Assert.Equal(["anthropic", "mine", "astra"], Keys(Settings["providers"]!));
        Assert.Equal("{env:ANTHROPIC_API_KEY}", Settings["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("npx", Settings["mcpServers"]!["fs"]!["command"]!.GetValue<string>());
        Assert.Equal("astra/glm-5.3", Settings["model"]!.GetValue<string>());

        Assert.Equal("Astra", Ours["name"]!.GetValue<string>());
        Assert.Equal("chat", Ours["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", Ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("astra-tk-1", Ours["apiKey"]!.GetValue<string>());
        Assert.Equal(["glm-5.3", "deepseek-v4"], Keys(Ours["models"]!));
        Assert.Equal("GLM-5.3", Model("glm-5.3")["name"]!.GetValue<string>());
        Assert.Equal(1_000_000, Model("glm-5.3")["contextWindow"]!.GetValue<int>());
        Assert.Equal(128_000, Model("glm-5.3")["maxTokens"]!.GetValue<int>());
        Assert.True(Model("glm-5.3")["images"]!.GetValue<bool>());
        Assert.True(Model("glm-5.3")["reasoning"]!.GetValue<bool>());
        // Nothing is invented for a model Astra knows nothing about: TKCode's catalog and defaults apply.
        Assert.Equal(["name"], Keys(Model("deepseek-v4")));
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
        Assert.False(_adapter.Inspect().Enabled);
    }

    [Fact]
    public void AModelGoesOverItsNativeProtocol_NamedOnTheModelNotTheProvider()
    {
        Home.WriteFile(_config, Original);
        var original = Home.ReadFileBytes(_config);
        var extra = Hints.Extra("gpt-5.5", "claude-sonnet-5", "deepseek-v4", "gemini-3-pro");
        TestGateway.Native(extra, "gpt-5.5", "openai-responses");
        TestGateway.Native(extra, "claude-sonnet-5", "anthropic");
        TestGateway.Native(extra, "gemini-3-pro", "gemini"); // TKCode has no Gemini protocol: the gateway translates
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-2", null, extra)));

        // One /v1 base serves all three: TKCode drops it again for messages and adds /v1/messages itself.
        Assert.Equal("chat", Ours["api"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", Ours["baseUrl"]!.GetValue<string>());
        Assert.Equal("responses", Model("gpt-5.5")["api"]!.GetValue<string>());
        Assert.Equal("messages", Model("claude-sonnet-5")["api"]!.GetValue<string>());
        Assert.Null(Model("deepseek-v4")["api"]);
        Assert.Null(Model("gemini-3-pro")["api"]);
        Assert.Equal("anthropic/claude-opus-5-5", Settings["model"]!.GetValue<string>()); // no model selected: the user's stays

        // The user moves Claude to chat: its own api goes.
        TestGateway.Override(extra, "claude-sonnet-5", "openai-chat");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-2", null, extra)));
        Assert.Null(Model("claude-sonnet-5")["api"]);
        Assert.Equal("responses", Model("gpt-5.5")["api"]!.GetValue<string>());

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_config));
    }

    [Fact]
    public void WhatTKCodeChangesIsNotDrift_AndSurvivesARewrite()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-3", null, Hints.Extra("a", "b", "c"))));

        // In TKCode's settings the user switches the provider off and overrides model a's efforts; TKCode then saves the file.
        var json = Settings;
        var ours = json["providers"]!["astra"]!.AsObject();
        ours["enabled"] = false;
        ours["headers"] = new JsonObject { ["x-team"] = "me" };
        ours["models"]!["a"]!["efforts"] = new JsonArray("low", "high");
        Home.WriteFile(_config, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var status = _adapter.Inspect();
        Assert.True(status.Enabled);
        Assert.Empty(status.DriftedKeys);

        // A rotated key and a refreshed model list (b left, d came) rewrite the entry: TKCode's choices stay.
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-4", null, Hints.Extra("a", "c", "d"))));
        Assert.False(Ours["enabled"]!.GetValue<bool>());
        Assert.Equal("me", Ours["headers"]!["x-team"]!.GetValue<string>());
        Assert.Equal("astra-tk-4", Ours["apiKey"]!.GetValue<string>());
        Assert.Equal(["a", "c", "d"], Keys(Ours["models"]!));
        Assert.Equal(["low", "high"], Model("a")["efforts"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(_adapter.Inspect().DriftedKeys);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(["anthropic", "mine"], Keys(Settings["providers"]!));
        Assert.Equal(Hints.Jsonc(Original).ToJsonString(), Settings.ToJsonString());
    }

    [Fact]
    public void AProtocolChangedInTKCode_IsTheUsersChoice_NotDrift()
    {
        Home.WriteFile(_config, Original);
        var extra = Hints.Extra("a", "b");
        TestGateway.Native(extra, "a", "openai-responses");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-5", null, extra)));
        Assert.Empty(_adapter.ChangedProtocolChoices());

        // In the model's editor the user picks chat for a (the provider's own) and messages for b.
        var json = Settings;
        var models = json["providers"]!["astra"]!["models"]!.AsObject();
        models["a"]!.AsObject().Remove("api");
        models["b"]!["api"] = "messages";
        Home.WriteFile(_config, json.ToJsonString());

        Assert.Empty(_adapter.Inspect().DriftedKeys);
        var changed = _adapter.ChangedProtocolChoices();
        Assert.Equal(Astra.Core.ApiProtocol.OpenAIChat, changed["a"]);
        Assert.Equal(Astra.Core.ApiProtocol.Anthropic, changed["b"]);

        // The server records them as overrides, so the rewrite keeps them instead of putting the native protocol back.
        TestGateway.Override(extra, "a", "openai-chat");
        TestGateway.Override(extra, "b", "anthropic");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-5", null, extra)));
        Assert.Null(Model("a")["api"]);
        Assert.Equal("messages", Model("b")["api"]!.GetValue<string>());
        Assert.Empty(_adapter.ChangedProtocolChoices());
    }

    [Fact]
    public void AnAstraValueEditedInTKCode_IsDriftAndIsNotRemovedByDisable()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-6", null, Hints.Extra("a", "b"))));
        Home.WriteFile(_config, Home.ReadFile(_config).Replace("http://127.0.0.1:17321/v1", "http://127.0.0.1:9999/v1"));

        Assert.Contains("providers.astra", _adapter.Inspect().DriftedKeys);
        Assert.False(_adapter.Inspect().Enabled);
        Assert.True(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Contains("127.0.0.1:9999", Home.ReadFile(_config));
    }

    [Fact]
    public void ModelsSwitchedOffInTKCode_AreDrift()
    {
        Home.WriteFile(_config, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-7", null, Hints.Extra("a", "b"))));
        var json = Settings;
        json["providers"]!["astra"]!["models"]!.AsObject().Remove("b");
        Home.WriteFile(_config, json.ToJsonString());

        Assert.Contains("providers.astra", _adapter.Inspect().DriftedKeys);
    }

    [Fact]
    public void ANumberTKCodeCannotReadIsLeftOut_SoItNeverRejectsTheFile()
    {
        Home.WriteFile(_config, Original);
        var extra = Hints.Extra("a");
        ((JsonObject)extra["models"]!["a"]!)["contextWindow"] = 3_000_000_000L;
        ((JsonObject)extra["models"]!["a"]!)["maxOutputTokens"] = 8192;
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-8", null, extra)));

        Assert.Null(Model("a")["contextWindow"]);
        Assert.Equal(8192, Model("a")["maxTokens"]!.GetValue<int>());
    }

    [Fact]
    public void WithoutAFile_EnableCreatesIt_AndDisableRemovesIt()
    {
        Home.WriteFile(Home.File(".tkcode", "state.json"), "{}");
        Assert.True(_adapter.Detect().Detected);
        Assert.False(Home.FileExists(_config));

        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-9", "a", Hints.Extra("a"))));
        Assert.Equal("astra/a", Settings["model"]!.GetValue<string>());
        Assert.Equal(["astra"], Keys(Settings["providers"]!));

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_config));
    }

    [Fact]
    public void Detect_FollowsTKCodesHome_AndAModelSelectionOfTheUsersIsNotTouched()
    {
        Assert.False(_adapter.Detect().Detected);
        Home.WriteFile(_config, Original);
        Assert.True(_adapter.Detect().Detected);
        Assert.Equal([_config], _adapter.ConfigPaths());
        Assert.Equal(ClientMode.Coexist, _adapter.Mode);
        Assert.Contains(ClientKinds.TKCode, ClientKinds.WithModelList);
        Assert.True(ClientProtocols.IsPerModel(ClientKinds.TKCode));
        Assert.Equal(
            [Astra.Core.ApiProtocol.OpenAIChat, Astra.Core.ApiProtocol.OpenAIResponses, Astra.Core.ApiProtocol.Anthropic],
            ClientProtocols.For(ClientKinds.TKCode));

        // Disabling leaves the default model alone when it is not Astra's.
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-tk-10", null, Hints.Extra("a"))));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal("anthropic/claude-opus-5-5", Settings["model"]!.GetValue<string>());

        var elsewhere = Path.Combine(Home.Path, "tkhome");
        Home.Variables["TKCODE_HOME"] = elsewhere;
        Assert.False(_adapter.Detect().Detected);
        Assert.Equal([Path.Combine(elsewhere, "settings.json")], _adapter.ConfigPaths());
    }
}
