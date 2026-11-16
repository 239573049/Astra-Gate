using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Clients.Install;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The desktop apps, chat apps and bots added from magpie (batch 5). Golden cases cover the file output.</summary>
public class MagpieBatch5Tests
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    private EnableContext Context(string key, string? model = "gpt-5", params string[] ids) => TestGateway.Context(key, model, new JsonObject
    {
        ["models"] = new JsonObject(ids.DefaultIfEmpty("gpt-5").Select(id =>
            KeyValuePair.Create(id, (JsonNode?)new JsonObject { ["id"] = id, ["name"] = id.ToUpperInvariant() }))),
    });

    [Fact]
    public void Every_Batch_Kind_Has_An_Adapter_And_An_Install_Spec()
    {
        var registry = ClientAdapterRegistry.CreateDefault(_home.Env, _store);
        foreach (var kind in new[]
        {
            ClientKinds.LiveAgent, ClientKinds.OpenMausBot, ClientKinds.AstrBot, ClientKinds.QwenPaw, ClientKinds.OpenClaw, ClientKinds.OpenBot,
        })
        {
            Assert.Equal(ClientAvailability.Available, registry.Require(kind).Availability);
            Assert.NotNull(ClientInstallCatalog.Get(kind));
        }
        Assert.Equal(ClientInstallMethod.Npm, ClientInstallCatalog.Get(ClientKinds.OpenClaw)!.Method);
    }

    [Fact]
    public void QwenPaw_Keeps_The_Key_It_Encrypted_Until_The_Key_Rotates()
    {
        var adapter = new QwenPawClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var provider = _home.File(".qwenpaw.secret", "providers", "custom", "astra.json");
        applier.Apply(adapter.PlanEnable(Context("key-1")));

        // QwenPaw rewrites a plaintext key as ciphertext at its next start.
        _home.WriteFile(provider, _home.ReadFile(provider).Replace("\"key-1\"", "\"ENC:abc\"", StringComparison.Ordinal));
        Assert.Empty(adapter.Inspect().DriftedKeys);
        var same = adapter.PlanEnable(Context("key-1"));
        Assert.DoesNotContain(same.Changes, c => c.KeyPath == "api_key");
        var rotated = adapter.PlanEnable(Context("key-2"));
        Assert.Contains(rotated.Changes, c => c.KeyPath == "api_key" && c.After == "\"key-2\"");

        // A restore treats the ciphertext as Astra's own and removes the file it created.
        var report = applier.Disable(adapter.PlanDisable());
        Assert.False(_home.FileExists(provider));
        Assert.Contains(report.Keys, k => k.KeyPath == "api_key" && k.Outcome == RestoreKeyOutcome.Restored);
    }

    [Fact]
    public void OpenBot_Writes_No_Key_And_Keeps_The_Secret_The_User_Gave_Its_Entry()
    {
        var adapter = new OpenBotClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var file = _home.File("Library", "Application Support", "OpenBot", OpenBotClientAdapter.FileName);
        applier.Apply(adapter.PlanEnable(Context("key-1")));
        var entry = JsonNode.Parse(_home.ReadFile(file))!["providers"]![0]!;
        Assert.Null(entry["secret"]);
        Assert.DoesNotContain("key-1", _home.ReadFile(file), StringComparison.Ordinal);

        _home.WriteFile(file, _home.ReadFile(file).Replace("\"secret\":null", "\"secret\":\"enc:xyz\"", StringComparison.Ordinal));
        applier.Apply(adapter.PlanEnable(Context("key-1", "gpt-5", "gpt-5", "gpt-4o")));
        entry = JsonNode.Parse(_home.ReadFile(file))!["providers"]![0]!;
        Assert.Equal("enc:xyz", (string?)entry["secret"]);
        Assert.Equal(2, entry["models"]!.AsArray().Count);
        Assert.Contains(adapter.Inspect().Warnings, w => w.Contains("require gateway key", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenMausBot_Refuses_A_Model_List_Its_Picker_Would_Reject()
    {
        var adapter = new OpenMausBotClientAdapter(_home.Env, _store);
        Assert.Throws<EditorException>(() => adapter.PlanEnable(Context("key", "bad model!", "bad model!")));
    }

    [Fact]
    public void AstrBot_Keeps_The_Byte_Order_Mark_Of_Its_Config_Through_Enable_And_Disable()
    {
        var adapter = new AstrBotClientAdapter(_home.Env, _store);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        var file = _home.File(".astrbot", "data", "cmd_config.json");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("{\n  \"provider_sources\": [],\n  \"provider\": []\n}\n")).ToArray();
        _home.WriteFileBytes(file, original);

        applier.Apply(adapter.PlanEnable(Context("key-1")));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, _home.ReadFileBytes(file)[..3]);
        Assert.Contains("astra/gpt-5", _home.ReadFile(file), StringComparison.Ordinal);

        applier.Disable(adapter.PlanDisable());
        Assert.Equal(original, _home.ReadFileBytes(file));
    }

    [Fact]
    public void AstrBot_Skips_Model_Ids_That_The_Selector_Syntax_Cannot_Address()
    {
        var adapter = new AstrBotClientAdapter(_home.Env, _store);
        var plan = adapter.PlanEnable(Context("key", "gpt-5", "gpt-5", "odd[1m]"));
        Assert.Contains(plan.Changes, c => c.KeyPath.Contains("astra/gpt-5", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Changes, c => c.KeyPath.Contains("odd", StringComparison.Ordinal));
    }
}
