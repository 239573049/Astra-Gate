using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Clients.Install;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The file-backed CLI and desktop agents added from magpie (batch 3). Golden cases cover the file output.</summary>
public class MagpieBatch3Tests
{
    private static readonly string[] Kinds =
    [
        ClientKinds.Reasonix, ClientKinds.Morph, ClientKinds.Jcode, ClientKinds.Vibe, ClientKinds.Empryo, ClientKinds.Nanobot, ClientKinds.MiniQ,
        ClientKinds.Paimon, ClientKinds.Cline, ClientKinds.GrokApp, ClientKinds.AtomCode, ClientKinds.Snow, ClientKinds.Ante,
        ClientKinds.CraftAgents, ClientKinds.TuanjieCowork,
    ];

    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    private EnableContext Context(string? model = "gpt-5") => TestGateway.Context("key.local", model, new JsonObject
    {
        ["models"] = new JsonObject { ["m1"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5" } },
    });

    [Fact]
    public void Every_Batch_Kind_Has_An_Adapter_A_Name_And_An_Install_Spec()
    {
        var registry = ClientAdapterRegistry.CreateDefault(_home.Env, _store);
        foreach (var kind in Kinds)
        {
            Assert.Equal(ClientAvailability.Available, registry.Require(kind).Availability);
            Assert.Equal(ClientMode.Coexist, registry.Require(kind).Mode);
            Assert.NotNull(ClientInstallCatalog.Get(kind));
        }
    }

    [Fact]
    public void Mister_Morph_Speaks_Responses()
    {
        Assert.Equal(ApiProtocol.OpenAIResponses, ClientKinds.ProtocolOf(ClientKinds.Morph));
        Assert.Equal([ApiProtocol.OpenAIResponses], ClientProtocols.For(ClientKinds.Morph));
    }

    [Theory]
    [InlineData("[\"a\"]", "[\"a\", \"astra\"]")]
    [InlineData("[]", "[\"astra\"]")]
    [InlineData("[ \"a\", \"b\", ]", "[\"a\", \"b\", \"astra\"]")]
    [InlineData("[\"astra\", \"a\"]", null)]
    [InlineData("\"not an array\"", null)]
    public void Reasonix_Provider_Access_Gets_Astra_Appended_Once(string array, string? expected)
    {
        var widened = ReasonixClientAdapter.AppendToArray(array, "astra");
        Assert.Equal(expected, widened);
    }

    [Fact]
    public void Craft_Agents_Refuses_A_Config_Craft_Has_Not_Made()
    {
        var adapter = new CraftAgentsClientAdapter(_home.Env, _store);
        Assert.Throws<EditorException>(() => adapter.PlanEnable(Context()));
        _home.WriteFile(_home.File(".craft-agent", "config.json"), "{ \"llmConnections\": [] }");
        Assert.Throws<EditorException>(() => adapter.PlanEnable(Context()));
        _home.WriteFile(_home.File(".craft-agent", "config.json"), "{ \"workspaces\": [] }");
        Assert.NotEmpty(adapter.PlanEnable(Context()).Changes);
    }

    [Fact]
    public void A_Model_The_User_Picked_After_Enabling_Stays_On_Disable()
    {
        var adapter = new VibeClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var file = _home.File(".vibe", "config.toml");
        applier.Apply(adapter.PlanEnable(Context()));
        Assert.Contains("active_model = \"astra/gpt-5\"", _home.ReadFile(file));
        _home.WriteFile(file, _home.ReadFile(file).Replace("active_model = \"astra/gpt-5\"", "active_model = \"mine\""));
        var report = applier.Disable(adapter.PlanDisable());
        Assert.Contains(report.Keys, k => k.KeyPath == "active_model" && k.Outcome == RestoreKeyOutcome.Drifted);
        var text = _home.ReadFile(file);
        Assert.Contains("active_model = \"mine\"", text);
        Assert.DoesNotContain("astra", text);
    }

    [Fact]
    public void Keyless_Clients_Warn_That_The_Gateway_Key_Requirement_Must_Be_Off()
    {
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var empryo = new EmpryoClientAdapter(_home.Env, _store);
        applier.Apply(empryo.PlanEnable(Context()));
        var status = empryo.Inspect();
        Assert.True(status.Enabled);
        Assert.Contains(status.Warnings, w => w.Contains("require gateway key", StringComparison.Ordinal));
    }

    [Fact]
    public void Snow_Carries_The_Active_Profiles_Settings_And_Drops_Its_Oauth()
    {
        _home.WriteFile(_home.File(".snow", "profiles", "work.json"), "{ \"snowcfg\": { \"apiKey\": \"mine\", \"oauth\": { \"t\": 1 }, \"extra\": true } }");
        _home.WriteFile(_home.File(".snow", "active-profile.json"), "{ \"activeProfile\": \"work\" }");
        var adapter = new SnowClientAdapter(_home.Env, _store);
        new ClientConfigApplier(_store, _home.Env, _home.BackupRoot).Apply(adapter.PlanEnable(Context()));
        var profile = JsonNode.Parse(_home.ReadFile(_home.File(".snow", "profiles", "astra.json")))!["snowcfg"]!;
        Assert.Equal(true, (bool?)profile["extra"]);
        Assert.Null(profile["oauth"]);
        Assert.Equal("key.local", (string?)profile["apiKey"]);
        Assert.Equal("gpt-5", (string?)profile["advancedModel"]);
        Assert.Equal("astra", (string?)JsonNode.Parse(_home.ReadFile(_home.File(".snow", "active-profile.json")))!["activeProfile"]);
    }

    [Fact]
    public void Grok_App_Never_Sets_The_Route()
    {
        var adapter = new GrokAppClientAdapter(_home.Env, _store);
        new ClientConfigApplier(_store, _home.Env, _home.BackupRoot).Apply(adapter.PlanEnable(Context()));
        var text = _home.ReadFile(_home.File("Library", "Application Support", "com.grokapp.grok-app", "agent-home", "config.toml"));
        Assert.Contains("[model.astra]", text);
        Assert.DoesNotContain("[models]", text);
        Assert.DoesNotContain("default =", text);
    }
}
