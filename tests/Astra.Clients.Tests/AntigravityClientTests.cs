using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Install;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The Antigravity desktop client: its settings.json keys come back on disable, its rc block sits beside the CLI client's. Golden cases cover the bytes.</summary>
public class AntigravityClientTests
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    private EnableContext Context(string key, string? model = "gpt-5") => TestGateway.Context(key, model, new JsonObject
    {
        ["models"] = new JsonObject { ["m1"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5" } },
    });

    private string Settings => _home.File(".gemini", "antigravity", "settings.json");

    [Fact]
    public void Enable_Writes_The_Gemini_Provider_And_Disable_Gives_The_Users_Keys_Back()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, "{\n  \"model\": \"gemini-3-pro\",\n  \"customModelsConfig\": { \"customModels\": { \"mine/gem\": { \"apiProvider\": \"API_PROVIDER_GOOGLE_GEMINI\", \"modelName\": \"gem\" } } }\n}\n");
        var adapter = new AntigravityClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);

        applier.Apply(adapter.PlanEnable(Context("key.desktop")));
        var enabled = JsonNode.Parse(_home.ReadFile(Settings))!;
        Assert.Equal("gemini", (string?)enabled["modelProvider"]);
        Assert.Equal("astra/gpt-5", (string?)enabled["model"]);
        var models = enabled["customModelsConfig"]!["customModels"]!;
        Assert.Equal("gpt-5", (string?)models["astra/gpt-5"]!["modelName"]);
        Assert.NotNull(models["mine/gem"]);
        var rc = _home.ReadFile(_home.File(".zshrc"));
        Assert.Contains("export GEMINI_API_KEY='key.desktop'", rc);
        Assert.Contains("export GOOGLE_GEMINI_BASE_URL=", rc);
        Assert.True(adapter.Inspect().Enabled);

        applier.Disable(adapter.PlanDisable());
        var restored = JsonNode.Parse(_home.ReadFile(Settings))!;
        Assert.Equal("gemini-3-pro", (string?)restored["model"]);
        Assert.Null(restored["modelProvider"]);
        Assert.Null(restored["customModelsConfig"]!["customModels"]!["astra/gpt-5"]);
        Assert.NotNull(restored["customModelsConfig"]!["customModels"]!["mine/gem"]);
        Assert.False(File.Exists(_home.File(".zshrc")), "the rc file Astra created goes with its last block");
        Assert.False(adapter.Inspect().Enabled);
    }

    [Fact]
    public void Resync_Drops_The_Models_Astra_Wrote_Before()
    {
        var adapter = new AntigravityClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        applier.Apply(adapter.PlanEnable(Context("key.desktop")));
        var next = TestGateway.Context("key.desktop", "gpt-6", new JsonObject
        {
            ["models"] = new JsonObject { ["m1"] = new JsonObject { ["id"] = "gpt-6", ["name"] = "GPT-6" } },
        });
        applier.Apply(adapter.PlanEnable(next));
        var models = JsonNode.Parse(_home.ReadFile(Settings))!["customModelsConfig"]!["customModels"]!.AsObject();
        Assert.Equal(["astra/gpt-6"], models.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void The_Desktop_And_The_Cli_Clients_Keep_Separate_Rc_Blocks()
    {
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var desktop = new AntigravityClientAdapter(_home.Env, _store);
        var cli = new AntigravityCliClientAdapter(_home.Env, _store);
        applier.Apply(desktop.PlanEnable(Context("key.desktop")));
        applier.Apply(cli.PlanEnable(Context("key.cli")));

        applier.Disable(desktop.PlanDisable());
        var rc = _home.ReadFile(_home.File(".zshrc"));
        Assert.DoesNotContain("export GEMINI_API_KEY", rc);
        Assert.Contains("agy()", rc);
        Assert.True(cli.Inspect().Enabled);
        Assert.False(desktop.Inspect().Enabled);
    }

    [Fact]
    public void Is_Found_By_Its_App_Or_Its_Directory_And_Has_An_Install_Spec()
    {
        var adapter = new AntigravityClientAdapter(_home.Env, _store);
        Assert.False(adapter.Detect().Detected);
        Directory.CreateDirectory(_home.File(".gemini", "antigravity"));
        Assert.True(adapter.Detect().Detected);
        Assert.Equal(ClientAvailability.Available, adapter.Availability);
        Assert.Equal(ApiProtocol.Gemini, ClientKinds.ProtocolOf(ClientKinds.Antigravity));
        Assert.NotNull(ClientInstallCatalog.Get(ClientKinds.Antigravity));
    }
}
