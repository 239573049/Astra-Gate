using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;

namespace Astra.Server.IntegrationTests;

/// <summary>Profiles (<c>/api/profiles</c>) and the per-client model shortlist, through the real admin API and the client applier.</summary>
public class ProfileApiTests
{
    private const string CodexOriginal = "# user config\nmodel_provider = \"openai\"\nmodel = \"original-model\"\n";

    private static async Task<Provider> AddProvider(TestHost host, string name, params string[] models)
    {
        var provider = new Provider
        {
            Id = Ulid.NewUlid(), Name = name, AuthScheme = AuthSchemes.None,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIResponses],
        };
        await host.Db.Providers.InsertAsync(provider);
        foreach (var model in models.DefaultIfEmpty("gpt-5"))
            await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = model, SystemModelId = model });
        return provider;
    }

    private static async Task<string> WriteCodexConfig(TestHost host)
    {
        var path = Path.Combine(host.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, CodexOriginal);
        return path;
    }

    private static async Task<string> WriteOpenCodeConfig(TestHost host)
    {
        var path = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{}\n");
        return path;
    }

    private static List<string> OpenCodeModels(string config) =>
        JsonNode.Parse(File.ReadAllText(config))!["provider"]!["astra"]!["models"]!.AsObject().Select(m => m.Key).Order(StringComparer.Ordinal).ToList();

    private static JsonNode Step(JsonNode? plan, string kind) =>
        plan!["steps"]!.AsArray().Single(s => s!["kind"]!.GetValue<string>() == kind)!;

    [Fact]
    public async Task A_Profile_Switches_Clients_Through_Enable_And_Disable_And_Tracks_Drift()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A");
        var b = await AddProvider(host, "B", "glm-4.6");
        var codex = await WriteCodexConfig(host);
        var opencode = await WriteOpenCodeConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = a.Id, model = "gpt-5" });
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = a.Id });

        var (status, saved) = await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "Work" });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = saved!["id"]!.GetValue<string>();
        Assert.True(saved["active"]!.GetValue<bool>());
        Assert.False(saved["drifted"]!.GetValue<bool>());
        Assert.Equal(["codex", "opencode"], saved["clients"]!.AsArray().Select(c => c!["kind"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "work" })).Status);

        // Move on: codex off, opencode on another provider. The active profile now reads as drifted.
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/bindings", new { bindings = new[] { new { providerId = b.Id } } });
        Assert.Equal(CodexOriginal, await File.ReadAllTextAsync(codex));
        Assert.True((await host.GetJsonAsync($"/api/profiles/{id}"))["drifted"]!.GetValue<bool>());

        // The preview says what would happen and writes nothing.
        var preview = await host.GetJsonAsync($"/api/profiles/{id}/preview");
        Assert.Equal("enable", Step(preview, "codex")["action"]!.GetValue<string>());
        Assert.Contains(Step(preview, "codex")["changes"]!.AsArray(), c => c!["keyPath"]!.GetValue<string>() == "model_providers.astra");
        Assert.Equal("update", Step(preview, "opencode")["action"]!.GetValue<string>());
        Assert.Equal(CodexOriginal, await File.ReadAllTextAsync(codex));
        Assert.Equal(["glm-4.6"], OpenCodeModels(opencode));

        // Applying switches both, by the regular paths.
        (status, var applied) = await host.SendAsync(HttpMethod.Post, $"/api/profiles/{id}/apply");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("enable", Step(applied!, "codex")["action"]!.GetValue<string>());
        Assert.Contains("[model_providers.astra]", await File.ReadAllTextAsync(codex));
        Assert.Equal(["gpt-5"], OpenCodeModels(opencode));
        Assert.Equal([a.Id], (await host.Db.Clients.ListBindingsAsync("opencode")).Select(x => x.ProviderId));
        Assert.True(applied!["profile"]!["active"]!.GetValue<bool>());
        Assert.False(applied!["profile"]!["drifted"]!.GetValue<bool>());
        Assert.Equal("gpt-5", (await host.Db.Clients.GetAsync("codex"))!.SelectedModel);

        // Applying it again changes nothing.
        applied = (await host.SendAsync(HttpMethod.Post, $"/api/profiles/{id}/apply")).Body!;
        Assert.All(applied["steps"]!.AsArray(), s => Assert.Equal("unchanged", s!["action"]!.GetValue<string>()));

        // A profile of the "everything off" state restores the original bytes (the applier's invariants hold).
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/disable");
        var (_, off) = await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "Off" });
        await host.SendAsync(HttpMethod.Post, $"/api/profiles/{id}/apply");
        Assert.Contains("[model_providers.astra]", await File.ReadAllTextAsync(codex));
        await host.SendAsync(HttpMethod.Post, $"/api/profiles/{off!["id"]!.GetValue<string>()}/apply");
        Assert.Equal(CodexOriginal, await File.ReadAllTextAsync(codex));
        Assert.False((await host.Db.Clients.GetAsync("codex"))!.Enabled);
    }

    [Fact]
    public async Task Rename_Overwrite_And_Delete()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A");
        await WriteCodexConfig(host);
        var (_, saved) = await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "One" });
        var id = saved!["id"]!.GetValue<string>();
        Assert.Empty(saved["clients"]!.AsArray());
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = a.Id });

        var (status, renamed) = await host.SendAsync(HttpMethod.Put, $"/api/profiles/{id}", new { name = "Renamed", overwrite = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Renamed", renamed!["name"]!.GetValue<string>());
        Assert.Equal(["codex"], renamed["clients"]!.AsArray().Select(c => c!["kind"]!.GetValue<string>()));
        Assert.False(renamed["drifted"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, $"/api/profiles/{id}", new { name = " " })).Status);
        // Looked up by name too.
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/profiles/renamed")).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, $"/api/profiles/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, $"/api/profiles/{id}")).Status);
        Assert.Empty((await host.GetJsonAsync("/api/profiles")).AsArray());
    }

    [Fact]
    public async Task Missing_Clients_Providers_And_Tokens_Are_Reported_Not_Fatal()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A");
        var gone = await AddProvider(host, "Gone");
        await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = a.Id });
        await host.SendAsync(HttpMethod.Put, "/api/clients/codex/bindings", new { bindings = new[] { new { providerId = a.Id }, new { providerId = gone.Id } } });
        var (_, saved) = await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "Mixed" });
        var id = saved!["id"]!.GetValue<string>();

        // A client that is not installed here, and one the profile knows that does not exist any more, are skipped.
        var doc = await host.Db.Settings.GetAsync<ClientProfiles>(ClientProfiles.SettingsKey);
        doc!.Profiles[0].Clients.Add(new ClientProfileEntry
        {
            Kind = ClientKinds.GeminiCli, Enabled = true, Bindings = [new ClientProfileBinding { ProviderId = a.Id }],
        });
        doc.Profiles[0].Clients.Add(new ClientProfileEntry { Kind = "no-such-client", Enabled = true, Bindings = [new ClientProfileBinding { ProviderId = a.Id }] });
        await host.Db.Settings.SetAsync(ClientProfiles.SettingsKey, doc);
        // One provider disappears.
        await host.SendAsync(HttpMethod.Delete, $"/api/providers/{gone.Id}");

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/profiles/{id}/apply");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("skipped", Step(result!, ClientKinds.GeminiCli)["action"]!.GetValue<string>());
        Assert.Equal("not-installed", Step(result, ClientKinds.GeminiCli)["reason"]!.GetValue<string>());
        Assert.Equal("unknown-client", Step(result, "no-such-client")["reason"]!.GetValue<string>());
        Assert.Contains(result!["dropped"]!.AsArray(), d => d!["what"]!.GetValue<string>() == "provider" && d["id"]!.GetValue<string>() == gone.Id);
        Assert.Equal([a.Id], (await host.Db.Clients.ListBindingsAsync("codex")).Select(x => x.ProviderId));
        Assert.True((await host.Db.Clients.GetAsync("codex"))!.Enabled);
    }

    [Fact]
    public async Task A_Client_Left_Without_Any_Provider_Is_Disabled_By_The_Profile()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A");
        var b = await AddProvider(host, "B");
        var codex = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = b.Id });
        var doc = new ClientProfiles { Profiles = [new ClientProfile { Id = "p1", Name = "Dead", Clients =
            [new ClientProfileEntry { Kind = "codex", Enabled = true, Bindings = [new ClientProfileBinding { ProviderId = "vanished" }] }] }] };
        await host.Db.Settings.SetAsync(ClientProfiles.SettingsKey, doc);
        var (_, result) = await host.SendAsync(HttpMethod.Post, "/api/profiles/p1/apply");
        Assert.Equal("disable", Step(result!, "codex")["action"]!.GetValue<string>());
        Assert.Equal("no-provider", Step(result, "codex")["reason"]!.GetValue<string>());
        Assert.Equal(CodexOriginal, await File.ReadAllTextAsync(codex));
        _ = a;
    }

    [Fact]
    public async Task The_Shortlist_Narrows_The_Models_A_Client_Lists_And_Its_Config()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A", "gpt-5", "glm-4.6", "kimi-k2");
        var config = await WriteOpenCodeConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = a.Id });
        Assert.Equal(["glm-4.6", "gpt-5", "kimi-k2"], OpenCodeModels(config));

        var (status, info) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/shortlist", new { models = new[] { "kimi-k2", " gpt-5 ", "kimi-k2" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["kimi-k2", "gpt-5"], info!["extras"]!["modelShortlist"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal(["gpt-5", "kimi-k2"], OpenCodeModels(config));
        Assert.Equal(["gpt-5", "kimi-k2"], (await host.GetJsonAsync("/api/clients/opencode/models")).AsArray().Select(m => m!.GetValue<string>()).Order());
        Assert.Equal(3, (await host.GetJsonAsync("/api/clients/opencode/models?all=true")).AsArray().Count);

        // An enable that sends extras without the shortlist keeps it.
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = a.Id, extras = new { options = new { } } });
        Assert.Equal(["gpt-5", "kimi-k2"], OpenCodeModels(config));

        (status, info) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/shortlist", new { models = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(info!["extras"]!["modelShortlist"]);
        Assert.Equal(["glm-4.6", "gpt-5", "kimi-k2"], OpenCodeModels(config));
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, "/api/clients/nope/shortlist", new { models = new[] { "x" } })).Status);
    }

    [Fact]
    public async Task A_Profile_Carries_The_Shortlist()
    {
        await using var host = await TestHost.StartAsync();
        var a = await AddProvider(host, "A", "gpt-5", "glm-4.6");
        var config = await WriteOpenCodeConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = a.Id });
        await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/shortlist", new { models = new[] { "glm-4.6" } });
        var (_, saved) = await host.SendAsync(HttpMethod.Post, "/api/profiles", new { name = "Short" });
        Assert.Equal(["glm-4.6"], saved!["clients"]![0]!["modelShortlist"]!.AsArray().Select(m => m!.GetValue<string>()));
        await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/shortlist", new { models = Array.Empty<string>() });
        Assert.Equal(2, OpenCodeModels(config).Count);
        await host.SendAsync(HttpMethod.Post, $"/api/profiles/{saved["id"]!.GetValue<string>()}/apply");
        Assert.Equal(["glm-4.6"], OpenCodeModels(config));
    }

    [Fact]
    public async Task The_Gateway_Lists_Only_The_Shortlist_To_That_Client()
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json("{}"));
        await using var _gw = gw;
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "extra-model" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "other-model" });
        async Task<List<string>> ListAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
            var body = JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!["data"]!.AsArray();
            return body.Select(m => m!["id"]!.GetValue<string>()).ToList();
        }
        var all = await ListAsync();
        Assert.Contains("extra-model", all);
        const string keep = "other-model";
        Assert.Contains(keep, all);
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Put, "/api/clients/opencode/shortlist", new { models = new[] { keep } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([keep], await ListAsync());
    }
}
