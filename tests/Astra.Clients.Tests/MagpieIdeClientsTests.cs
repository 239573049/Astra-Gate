using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Clients.Install;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The IDE, editor and extension clients added from magpie (IDE batch). Golden cases cover the plain file output.</summary>
public class MagpieIdeClientsTests
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    private static readonly string[] BatchKinds =
    [
        ClientKinds.ZooCode, ClientKinds.JetBrainsAir, ClientKinds.AirIde, ClientKinds.CopilotJetBrains, ClientKinds.CodeBuddy,
        ClientKinds.Qoder, ClientKinds.QoderCn, ClientKinds.T3Code, ClientKinds.Pencil, ClientKinds.OpenPencil, ClientKinds.Raycast,
        ClientKinds.Obsidian,
    ];

    private EnableContext Context(string key = "key", string? model = "gpt-5") => TestGateway.Context(key, model, new JsonObject
    {
        ["models"] = new JsonObject
        {
            ["m1"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5", ["contextWindow"] = 400000, ["maxOutputTokens"] = 128000, ["reasoning"] = true },
            ["m2"] = new JsonObject { ["id"] = "deepseek-chat", ["name"] = "DeepSeek Chat" },
        },
    });

    private ClientConfigApplier Applier() => new(_store, _home.Env, _home.BackupRoot);

    [Fact]
    public void Every_Batch_Kind_Has_An_Adapter_A_Name_And_An_Install_Spec()
    {
        var registry = ClientAdapterRegistry.CreateDefault(_home.Env, _store);
        foreach (var kind in BatchKinds)
        {
            Assert.Equal(ClientAvailability.Available, registry.Require(kind).Availability);
            Assert.NotNull(ClientInstallCatalog.Get(kind));
            Assert.Contains(kind, ClientKinds.All);
            Assert.Contains(kind, ClientKinds.WithModelList);
        }
        Assert.Equal(ClientInstallMethod.Npm, ClientInstallCatalog.Get(ClientKinds.CodeBuddy)!.Method);
        Assert.Equal("@qoder-ai/qodercli", ClientInstallCatalog.Get(ClientKinds.Qoder)!.Package);
        Assert.Equal("@qodercn-ai/qoderclicn", ClientInstallCatalog.Get(ClientKinds.QoderCn)!.Package);
        Assert.Equal(ApiProtocol.Anthropic, ClientKinds.ProtocolOf(ClientKinds.T3Code));
    }

    [Fact]
    public void A_Dotted_Key_Is_Addressed_With_An_Escaped_Dot()
    {
        var text = "{\n  // c\n  \"a.b\": 1,\n  \"a\": { \"b\": 2 }\n}\n";
        var editor = new JsoncEditor(text);
        Assert.Equal("1", editor.Get(@"a\.b")!.ToJsonString());
        Assert.Equal("2", editor.Get("a.b")!.ToJsonString());
        var set = editor.SetRaw(@"c\.d", "\"x\"");
        Assert.Contains("\"c.d\": \"x\"", set.Text);
        Assert.Equal(text, set.Remove(@"c\.d").Text);
        Assert.Equal(["a.b"], JsoncEditor.SplitKeyPath(@"a\.b"));
    }

    [Fact]
    public void Nested_Yaml_Rows_Are_Added_Replaced_And_Removed_Without_Touching_The_Rest()
    {
        const string text = "# mine\nproviders:\n  - id: mine\n    name: Mine\n\n# tools\ntools:\n  - id: web\n";
        const string selector = "providers.[id=astra]";
        var yaml = new YamlEditor(text);
        Assert.False(yaml.Has(selector));
        var added = yaml.SetRaw(selector, """{"id":"astra","name":"Astra","models":[{"id":"m","abilities":{"tools":{"supported":true}}}]}""");
        Assert.True(added.Has(selector));
        Assert.StartsWith("# mine\nproviders:\n  - id: mine\n    name: Mine\n  - id: \"astra\"", added.Text);
        Assert.Contains("# tools\ntools:\n  - id: web\n", added.Text);
        Assert.Contains("      - id: \"m\"\n        abilities:\n          tools:\n            supported: true\n", added.Text);
        var replaced = added.SetRaw(selector, """{"id":"astra","name":"Other"}""");
        Assert.Contains("name: \"Other\"", replaced.Text);
        Assert.DoesNotContain("abilities", replaced.Text);
        Assert.Equal(text, replaced.Remove(selector).Text);

        var empty = new YamlEditor("providers: []\n").SetRaw(selector, """{"id":"astra"}""");
        Assert.Equal("providers:\n  - id: \"astra\"\n", empty.Text);
        Assert.Equal("", empty.Remove(selector).Text);
        var fresh = new YamlEditor("").SetRaw(selector, """{"id":"astra"}""");
        Assert.Equal("providers:\n  - id: \"astra\"\n", fresh.Text);
    }

    [Fact]
    public void A_Model_Hidden_In_Copilot_Stays_Hidden_And_Is_Not_Drift()
    {
        var adapter = new CopilotJetBrainsClientAdapter(_home.Env, _store);
        var file = _home.File(".config", "github-copilot", "byok.json");
        Applier().Apply(adapter.PlanEnable(Context()));
        // Manage Models hides a model: isRegistered flips to false in the file.
        _home.WriteFile(file, _home.ReadFile(file).Replace("\"isRegistered\":true", "\"isRegistered\":false", StringComparison.Ordinal));
        Assert.Empty(adapter.Inspect().DriftedKeys);

        var again = adapter.PlanEnable(Context(model: "gpt-5"));
        var models = JsonNode.Parse(again.Changes.Single(c => c.KeyPath == "astra-models-config").After!)!.AsObject();
        Assert.False((bool)models["gpt-5"]!["isRegistered"]!);
        Assert.False((bool)models["deepseek-chat"]!["isRegistered"]!);
        Applier().Apply(again);
        var report = Applier().Disable(adapter.PlanDisable());
        Assert.All(report.Keys, k => Assert.Equal(RestoreKeyOutcome.Restored, k.Outcome));
        Assert.False(_home.FileExists(file));
    }

    [Fact]
    public void Qoder_Keeps_What_The_User_Adds_To_Its_Provider()
    {
        var adapter = new QoderClientAdapter(_home.Env, _store, QoderFlavour.Qoder);
        var file = _home.File(".qoder", "settings.json");
        Applier().Apply(adapter.PlanEnable(Context()));
        var text = _home.ReadFile(file).Replace("\"displayName\":\"Astra\"", "\"displayName\":\"Mine\",\"routing\":{\"compact\":\"x\"}", StringComparison.Ordinal);
        _home.WriteFile(file, text);
        Assert.Empty(adapter.Inspect().DriftedKeys);

        var provider = JsonNode.Parse(adapter.PlanEnable(Context()).Changes.Single(c => c.KeyPath == "providers.astra").After!)!.AsObject();
        Assert.Equal("Mine", (string?)provider["displayName"]);
        Assert.Equal("x", (string?)provider["routing"]!["compact"]);
        Assert.Equal("astra/gpt-5", (string?)JsonNode.Parse(_home.ReadFile(file))!["model"]!["name"]);
    }

    [Fact]
    public void Qoder_Is_Not_Detected_From_The_JetBrains_Plugins_Settings_Alone()
    {
        _home.WriteFile(_home.File(".qoder", "settings.json"), "{\"enabledPlugins\":{\"p\":true}}\n");
        Assert.False(new QoderClientAdapter(_home.Env, _store, QoderFlavour.Qoder).Detect().Detected);
        _home.WriteFile(_home.File(".qoder", "settings.json"), "{\"enabledPlugins\":{},\"theme\":\"dark\"}\n");
        Assert.True(new QoderClientAdapter(_home.Env, _store, QoderFlavour.Qoder).Detect().Detected);
    }

    [Fact]
    public void OpenPencil_Leaves_A_File_Of_Another_Version_Alone_And_Keeps_The_Users_Name_And_Switch()
    {
        var adapter = new OpenPencilClientAdapter(_home.Env, _store);
        var file = _home.File("Library", "Application Support", "openpencil", "settings.json");
        _home.WriteFile(file, "{\"version\":2}\n");
        Assert.Empty(adapter.PlanEnable(Context()).Changes);
        Assert.Contains(adapter.Inspect().Warnings, w => w.Contains("version 1", StringComparison.Ordinal));

        _home.WriteFile(file, "{\"version\":1}\n");
        Applier().Apply(adapter.PlanEnable(Context()));
        _home.WriteFile(file, _home.ReadFile(file).Replace("\"display_name\":\"Astra\"", "\"display_name\":\"Mine\"", StringComparison.Ordinal)
            .Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal));
        Assert.Empty(adapter.Inspect().DriftedKeys);
        var row = JsonNode.Parse(adapter.PlanEnable(Context()).Changes.Single(c => c.KeyPath.StartsWith("builtin_agents", StringComparison.Ordinal)).After!)!;
        Assert.Equal("Mine", (string?)row["display_name"]);
        Assert.False((bool)row["enabled"]!);
        Applier().Disable(adapter.PlanDisable());
        Assert.Equal("{\"version\":1}\n", _home.ReadFile(file));
    }

    [Fact]
    public void Obsidian_Wires_Only_Vaults_With_Copilot_Four_And_Brings_The_Users_Default_Back()
    {
        var obsidian = _home.File("Library", "Application Support", "obsidian", "obsidian.json");
        var notes = _home.File("Notes");
        var old = _home.File("Old");
        _home.WriteFile(obsidian, $"{{\"vaults\":{{\"a\":{{\"path\":{Json.EncodeString(notes)}}},\"b\":{{\"path\":{Json.EncodeString(old)}}}}}}}");
        _home.WriteFile(Path.Combine(notes, ".obsidian", "plugins", "copilot", "manifest.json"), "{\"version\":\"4.0.19\"}");
        _home.WriteFile(Path.Combine(old, ".obsidian", "plugins", "copilot", "manifest.json"), "{\"version\":\"3.2.0\"}");
        var data = Path.Combine(notes, ".obsidian", "plugins", "copilot", "data.json");
        _home.WriteFile(data, "{\"defaultModelKey\":\"mine\",\"backends\":{\"chat\":{\"enabledModels\":[\"mine\"]}}}\n");

        var adapter = new ObsidianClientAdapter(_home.Env, _store);
        Assert.Equal([data], adapter.ConfigPaths());
        Assert.Contains(adapter.Inspect().Warnings, w => w.Contains("Copilot 3", StringComparison.Ordinal));
        Applier().Apply(adapter.PlanEnable(Context()));
        var written = JsonNode.Parse(_home.ReadFile(data))!;
        Assert.NotEqual("mine", (string?)written["defaultModelKey"]);
        Assert.Equal("mine", (string?)written["backends"]!["chat"]!["enabledModels"]![0]);
        Assert.Equal(3, written["backends"]!["chat"]!["enabledModels"]!.AsArray().Count);
        Assert.False(_home.FileExists(Path.Combine(old, ".obsidian", "plugins", "copilot", "data.json")));

        // The user picks another default in Copilot: a re-apply leaves it.
        written["defaultModelKey"] = "picked";
        _home.WriteFile(data, written.ToJsonString());
        var again = adapter.PlanEnable(Context());
        Assert.DoesNotContain(again.Changes, c => c.KeyPath == "defaultModelKey");

        Applier().Disable(adapter.PlanDisable());
        var restored = JsonNode.Parse(_home.ReadFile(data))!;
        Assert.Null(restored["providers"]?["astra"]);
        Assert.Single(restored["backends"]!["chat"]!["enabledModels"]!.AsArray());
    }

    [Fact]
    public void T3Code_Keeps_What_The_User_Set_On_Its_Instance_And_Drops_Cloud_Switches()
    {
        var adapter = new T3CodeClientAdapter(_home.Env, _store);
        var file = _home.File(".t3", "userdata", "settings.json");
        Applier().Apply(adapter.PlanEnable(Context()));
        var instance = JsonNode.Parse(_home.ReadFile(file))!["providerInstances"]!["astra"]!.AsObject();
        Assert.Equal("claudeAgent", (string?)instance["driver"]);
        Assert.Equal(TestGateway.BaseUrl, (string?)instance["environment"]![0]!["value"]);
        // gpt-5 reasons: it gets T3's Reasoning and Thinking picks; deepseek-chat none.
        Assert.NotNull(instance["config"]!["customModels"]![0]!["capabilities"]);
        Assert.Null(instance["config"]!["customModels"]![1]!["capabilities"]);

        var edited = _home.ReadFile(file)
            .Replace("\"displayName\":\"Astra\"", "\"displayName\":\"My Astra\",\"accentColor\":\"blue\"", StringComparison.Ordinal)
            .Replace("\"environment\":[", "\"environment\":[{\"name\":\"MY_VAR\",\"value\":\"1\"},{\"name\":\"CLAUDE_CODE_USE_BEDROCK\",\"value\":\"1\"},", StringComparison.Ordinal);
        _home.WriteFile(file, edited);
        Assert.Empty(adapter.Inspect().DriftedKeys);
        var next = JsonNode.Parse(adapter.PlanEnable(Context()).Changes.Single().After!)!.AsObject();
        Assert.Equal("My Astra", (string?)next["displayName"]);
        Assert.Equal("blue", (string?)next["accentColor"]);
        var names = next["environment"]!.AsArray().Select(e => (string?)e!["name"]).ToList();
        Assert.Contains("MY_VAR", names);
        Assert.DoesNotContain("CLAUDE_CODE_USE_BEDROCK", names);
    }

    [Fact]
    public void Zoo_Code_Points_Every_Profile_With_Settings_Of_Its_Own_At_The_Import_File()
    {
        var user = _home.File("Library", "Application Support", "Code", "User");
        _home.WriteFile(Path.Combine(user, "globalStorage", "storage.json"),
            "{\"userDataProfiles\":[{\"name\":\"W\",\"location\":\"abc\"},{\"name\":\"S\",\"location\":\"def\",\"useDefaultFlags\":{\"settings\":true}},{\"name\":\"Gone\",\"location\":\"zzz\"}]}");
        _home.WriteFile(Path.Combine(user, "profiles", "abc", "settings.json"), "{}\n");
        _home.WriteFile(Path.Combine(user, "profiles", "def", "settings.json"), "{}\n");

        var plan = new ZooCodeClientAdapter(_home.Env, _store).PlanEnable(Context());
        var settings = plan.Changes.Where(c => c.KeyPath == @"zoo-code\.autoImportSettingsPath").Select(c => c.File).ToList();
        Assert.Equal([Path.Combine(user, "settings.json"), Path.Combine(user, "profiles", "abc", "settings.json")], settings);
    }

    [Fact]
    public void Air_Writes_The_Agent_With_The_Opencode_It_Finds_And_Its_Own_Config_Beside_Acp_Json()
    {
        var adapter = new AirClientAdapter(_home.Env, _store, AirFlavour.Plugin);
        Applier().Apply(adapter.PlanEnable(Context()));
        var acp = JsonNode.Parse(_home.ReadFile(_home.File(".jetbrains", "acp.json")))!["agent_servers"]!["Astra"]!;
        Assert.Equal("opencode", (string?)acp["command"]);
        Assert.Equal(_home.File(".jetbrains", "astra-opencode.json"), (string?)acp["env"]!["OPENCODE_CONFIG"]);
        var config = JsonNode.Parse(_home.ReadFile(_home.File(".jetbrains", "astra-opencode.json")))!;
        Assert.Equal("astra/gpt-5", (string?)config["model"]);
        Assert.Equal("astra", (string?)config["enabled_providers"]![0]);
        Assert.Contains(adapter.Inspect().Warnings, w => w.Contains("not installed", StringComparison.Ordinal));
    }
}
