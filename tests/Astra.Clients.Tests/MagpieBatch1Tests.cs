using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Clients.Install;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>The OpenCode / Pi families and the simple CLI agents added from magpie (batch 1). Golden cases cover the file output.</summary>
public class MagpieBatch1Tests
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();

    private EnableContext Context(string key, string? model = "gpt-5") => TestGateway.Context(key, model, new JsonObject
    {
        ["models"] = new JsonObject { ["m1"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5" } },
    });

    [Fact]
    public void TomlArrayEntries_Replace_And_Remove_Only_The_Matching_Entries()
    {
        const string text = "[[models]]\nprovider = \"mine\"\nmodel_id = \"a\"\n\n[x]\nk = 1\n\n[[models]]\nprovider = \"astra\"\nmodel_id = \"old\"\n";
        const string selector = "models[provider=astra]";
        Assert.True(TomlArrayEntries.Has(text, selector));
        var replaced = TomlArrayEntries.Set(text, selector,
            [[new("provider", "\"astra\""), new("model_id", "\"new\"")]]);
        Assert.Contains("model_id = \"new\"", replaced);
        Assert.DoesNotContain("model_id = \"old\"", replaced);
        Assert.Contains("model_id = \"a\"", replaced);
        var removed = TomlArrayEntries.Remove(replaced, selector);
        Assert.False(TomlArrayEntries.Has(removed, selector));
        Assert.Contains("model_id = \"a\"", removed);
    }

    [Fact]
    public void Mu_And_StepCode_Are_Found_By_Their_Directory_Only()
    {
        // `mu` and `step` are other tools' commands too, so a PATH hit must never count.
        Assert.False(new PiClientAdapter(_home.Env, _store, PiFlavour.Mu).Detect().Detected);
        Assert.False(new PiClientAdapter(_home.Env, _store, PiFlavour.StepCode).Detect().Detected);
        Directory.CreateDirectory(_home.File(".stepcode"));
        Assert.True(new PiClientAdapter(_home.Env, _store, PiFlavour.StepCode).Detect().Detected);
    }

    [Fact]
    public void OpenChamber_Writes_Its_Own_Provider_And_Leaves_OpenCodes_Block_Alone()
    {
        var opencode = new OpenCodeClientAdapter(_home.Env, _store);
        var chamber = new OpenCodeClientAdapter(_home.Env, _store, OpenCodeFlavour.OpenChamber);
        var applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);
        applier.Apply(opencode.PlanEnable(Context("key.opencode")));
        applier.Apply(chamber.PlanEnable(Context("key.openchamber")));

        var config = JsonNode.Parse(_home.ReadFile(_home.File(".config", "opencode", "opencode.json")))!;
        Assert.Equal("key.opencode", (string?)config["provider"]!["astra"]!["options"]!["apiKey"]);
        Assert.Equal("key.openchamber", (string?)config["provider"]!["astra-openchamber"]!["options"]!["apiKey"]);
        Assert.Equal("astra/gpt-5", (string?)config["model"]);
        var settings = JsonNode.Parse(_home.ReadFile(_home.File(".config", "openchamber", "settings.json")))!;
        Assert.Equal("astra-openchamber/gpt-5", (string?)settings["defaultModel"]);

        applier.Disable(chamber.PlanDisable());
        config = JsonNode.Parse(_home.ReadFile(_home.File(".config", "opencode", "opencode.json")))!;
        Assert.NotNull(config["provider"]!["astra"]);
        Assert.Null(config["provider"]!["astra-openchamber"]);
    }

    [Fact]
    public void Every_Batch_Kind_Has_An_Adapter_A_Name_And_An_Install_Spec()
    {
        var registry = ClientAdapterRegistry.CreateDefault(_home.Env, _store);
        foreach (var kind in new[]
        {
            ClientKinds.OpenChamber, ClientKinds.DevEcoCode, ClientKinds.StepCode, ClientKinds.Aside, ClientKinds.OmO, ClientKinds.PrimeAgent,
            ClientKinds.Mu, ClientKinds.MothX, ClientKinds.PenguinHarness, ClientKinds.AntigravityCli, ClientKinds.Dim, ClientKinds.CommandCode,
            ClientKinds.Fx, ClientKinds.Goose,
        })
        {
            Assert.Equal(ClientAvailability.Available, registry.Require(kind).Availability);
            Assert.NotNull(ClientInstallCatalog.Get(kind));
        }
    }
}
