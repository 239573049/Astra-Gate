using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;

namespace Astra.Clients.Tests;

/// <summary>Codex's "modelCatalog" option: the bound providers' models appended to a copy of Codex's own catalog.</summary>
public class CodexModelCatalogTests : IDisposable
{
    private const string OriginalConfig = "approval_policy = \"never\"\n";

    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;
    private readonly CodexClientAdapter _adapter;

    public CodexModelCatalogTests()
    {
        _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        _adapter = new CodexClientAdapter(_home.Env, _store);
        _home.WriteFile(ConfigFile, OriginalConfig);
    }

    public void Dispose() => _home.Dispose();

    private string ConfigFile => _home.File(".codex", "config.toml");

    private string CatalogFile => _home.File(".codex", CodexClientAdapter.CatalogFileName);

    /// <summary>A catalog entry the way Codex writes one (only the fields these tests look at).</summary>
    private static JsonObject Entry(string slug, int priority, string? instructions = "You are Codex.") => new()
    {
        ["slug"] = slug,
        ["display_name"] = slug,
        ["visibility"] = "list",
        ["priority"] = priority,
        ["supported_reasoning_levels"] = new JsonArray(new JsonObject { ["effort"] = "high", ["description"] = "High" }),
        ["default_reasoning_level"] = "medium",
        ["context_window"] = 272000,
        ["support_verbosity"] = true,
        ["apply_patch_tool_type"] = "freeform",
        ["model_messages"] = instructions is null ? null : new JsonObject { ["instructions_template"] = instructions },
    };

    private void WriteCatalog(string path, params JsonObject[] entries) =>
        _home.WriteFile(path, new JsonObject { ["models"] = new JsonArray(entries) }.ToJsonString());

    private static EnableContext Ctx(bool catalog = true)
    {
        var models = new JsonObject
        {
            ["deepseek-chat"] = new JsonObject { ["id"] = "deepseek-chat", ["name"] = "DeepSeek Chat", ["contextWindow"] = 128000, ["vision"] = false, ["reasoning"] = false },
            ["gpt-5.5"] = new JsonObject { ["id"] = "gpt-5.5", ["name"] = "GPT-5.5" },
        };
        var options = new JsonObject();
        if (catalog) options["modelCatalog"] = true;
        return TestGateway.Context("astra-codex-x", null, new JsonObject { ["models"] = models, ["options"] = options });
    }

    private JsonArray Written() => (JsonArray)JsonNode.Parse(_home.ReadFile(CatalogFile))!["models"]!;

    private static JsonObject Model(JsonArray models, string slug) =>
        models.OfType<JsonObject>().Single(m => m["slug"]!.GetValue<string>() == slug);

    [Fact]
    public void AppendsAstraModelsToCodexsCachedCatalog_AndRestoresOnDisable()
    {
        WriteCatalog(_home.File(".codex", "models_cache.json"), Entry("gpt-5.5", 1), Entry("gpt-5.4", 2));

        _applier.Apply(_adapter.PlanEnable(Ctx()));

        var models = Written();
        Assert.Equal(["gpt-5.5", "gpt-5.4", "deepseek-chat"], models.Select(m => m!["slug"]!.GetValue<string>()));
        Assert.Equal(272000, Model(models, "gpt-5.5")["context_window"]!.GetValue<int>()); // Codex's own entry is kept as is
        var added = Model(models, "deepseek-chat");
        Assert.Equal("DeepSeek Chat", added["display_name"]!.GetValue<string>());
        Assert.Equal("You are Codex.", added["model_messages"]!["instructions_template"]!.GetValue<string>());
        Assert.Equal(3, added["priority"]!.GetValue<int>());
        Assert.Equal(128000, added["context_window"]!.GetValue<int>());
        Assert.Equal(["text"], added["input_modalities"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Empty(added["supported_reasoning_levels"]!.AsArray());
        Assert.Null(added["default_reasoning_level"]);
        Assert.False(added["support_verbosity"]!.GetValue<bool>());
        Assert.Null(added["apply_patch_tool_type"]);
        Assert.Contains($"model_catalog_json = \"{CatalogFile}\"", _home.ReadFile(ConfigFile));
        Assert.Contains(CatalogFile, _adapter.ConfigPaths());

        _applier.Disable(_adapter.PlanDisable());
        Assert.False(_home.FileExists(CatalogFile));
        Assert.Equal(OriginalConfig, _home.ReadFile(ConfigFile));
    }

    [Fact]
    public void KeepsTheUsersOwnCatalog_AndPointsBackAtItOnDisable()
    {
        var mine = _home.File(".codex", "mine.json");
        WriteCatalog(mine, Entry("my-model", 5));
        WriteCatalog(_home.File(".codex", "models_cache.json"), Entry("gpt-5.5", 1));
        var config = "model_catalog_json = \"mine.json\"\n";
        _home.WriteFile(ConfigFile, config);

        _applier.Apply(_adapter.PlanEnable(Ctx()));
        Assert.Equal(["my-model", "deepseek-chat", "gpt-5.5"], Written().Select(m => m!["slug"]!.GetValue<string>()));

        // Re-planning once Astra's file is in place still starts from the user's catalog, not from Astra's own.
        Assert.Empty(_adapter.PlanEnable(Ctx()).Diffs);

        _applier.Disable(_adapter.PlanDisable());
        Assert.Equal(config, _home.ReadFile(ConfigFile));
        Assert.False(_home.FileExists(CatalogFile));
        Assert.Equal("my-model", Model((JsonArray)JsonNode.Parse(_home.ReadFile(mine))!["models"]!, "my-model")["slug"]!.GetValue<string>());
    }

    [Fact]
    public void EntriesWithoutInstructionsGetTheTemplates_SoCodexCanLoadTheFile()
    {
        WriteCatalog(_home.File(".codex", "models_cache.json"), Entry("bare", 1, instructions: null), Entry("gpt-5.5", 2));

        _applier.Apply(_adapter.PlanEnable(Ctx()));

        Assert.Equal("You are Codex.", Model(Written(), "bare")["base_instructions"]!.GetValue<string>());
    }

    [Fact]
    public void WritesNothing_WithoutACodexCatalogToStartFrom_OrWithTheOptionOff()
    {
        Assert.DoesNotContain(_adapter.PlanEnable(Ctx()).Changes, c => c.KeyPath is "model_catalog_json" or "models");

        WriteCatalog(_home.File(".codex", "models_cache.json"), Entry("gpt-5.5", 1));
        Assert.DoesNotContain(_adapter.PlanEnable(Ctx(catalog: false)).Changes, c => c.KeyPath is "model_catalog_json" or "models");
        Assert.Equal([ConfigFile], _adapter.ConfigPaths());
    }
}
