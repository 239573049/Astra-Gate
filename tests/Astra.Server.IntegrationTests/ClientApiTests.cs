using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Astra.Server.IntegrationTests;

public class ClientApiTests
{
    private const string Original = "# user config\nmodel_provider = \"openai\"\nmodel = \"original-model\"\n\n[projects.\"/work\"]\ntrust_level = \"trusted\"\n";

    [Fact]
    public async Task Listing_Supported_Clients_Does_Not_Configure_Any_Of_Them()
    {
        await using var host = await TestHost.StartAsync();
        var clients = (await host.GetJsonAsync("/api/clients")).AsArray();
        Assert.Equal(ClientKinds.All, clients.Select(c => c!["kind"]!.GetValue<string>()));
        Assert.All(clients, c => Assert.False(c!["enabled"]!.GetValue<bool>()));
        Assert.Empty(await host.Db.Clients.ListAsync());
        Assert.False(Directory.Exists(host.ClientHome));
    }

    [Fact]
    public async Task Preview_Enable_Is_Read_Only_And_Returns_A_Real_Diff()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        var (status, preview) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/preview-enable", new { providerId = provider.Id, model = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(preview!["changes"]!.AsArray(), c => c!["keyPath"]!.GetValue<string>() == "model_providers.astra");
        Assert.Contains("[model_providers.astra]", preview["diffs"]![0]!["unifiedDiff"]!.GetValue<string>());
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
        Assert.Empty(await host.Db.Clients.ListAsync());
        Assert.Empty(host.Db.ClientConfigState.List("codex"));
        Assert.Empty((await host.GetJsonAsync("/api/clients/codex/backups")).AsArray());
    }

    [Fact]
    public async Task Enable_Disable_Round_Trips_Original_Bytes_And_Leaves_Auth_File_Alone()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        var authFile = Path.Combine(Path.GetDirectoryName(config)!, "auth.json");
        await File.WriteAllTextAsync(authFile, "{\"user\":\"untouched\"}\n");
        var (status, enabled) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = provider.Id, model = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(enabled!["enabled"]!.GetValue<bool>());
        Assert.Equal("enabled", enabled["status"]!.GetValue<string>());
        Assert.Equal(provider.Id, enabled["providerId"]!.GetValue<string>());
        var text = await File.ReadAllTextAsync(config);
        Assert.Contains("[model_providers.astra]", text);
        Assert.Contains("http://127.0.0.1:17321/v1", text);
        Assert.Contains("[projects.\"/work\"]", text);
        Assert.Contains("# user config", text);
        Assert.Equal("{\"user\":\"untouched\"}\n", await File.ReadAllTextAsync(authFile));
        var record = await host.Db.Clients.GetAsync("codex");
        Assert.Equal(TokenIds.Default, record!.TokenId);
        Assert.Equal(TokenIds.Default, enabled["tokenId"]!.GetValue<string>());
        // The config holds "<default token>.codex"; the API response never echoes the token.
        var token = await DefaultTokenAsync(host);
        Assert.Contains($"experimental_bearer_token = \"{token}.codex\"", text);
        Assert.DoesNotContain(token, enabled.ToJsonString());

        var backups = (await host.GetJsonAsync("/api/clients/codex/backups")).AsArray();
        Assert.Contains(backups, b => b!["firstWrite"]!.GetValue<bool>());
        (status, var disabled) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(disabled!["client"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(disabled["drifted"]!.AsArray());
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Binding_Is_Independent_Of_Enablement_And_Does_Not_Rewrite_Config()
    {
        await using var host = await TestHost.StartAsync();
        var first = await AddProvider(host, "First");
        var second = await AddProvider(host, "Second");
        var config = await WriteCodexConfig(host);
        var (status, bound) = await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = first.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(bound!["enabled"]!.GetValue<bool>());
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { model = "gpt-5" });
        var applied = await File.ReadAllTextAsync(config);

        (status, bound) = await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = second.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(bound!["enabled"]!.GetValue<bool>());
        Assert.Equal(second.Id, bound["providerId"]!.GetValue<string>());
        Assert.Equal(applied, await File.ReadAllTextAsync(config));
        Assert.Equal(TokenIds.Default, (await host.Db.Clients.GetAsync("codex"))!.TokenId);
    }

    [Fact]
    public async Task Enable_With_A_Chosen_Token_Writes_It_And_Rotate_Key_Is_Gone()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Work" });
        Assert.Equal(HttpStatusCode.OK, status);
        var tokenId = created!["id"]!.GetValue<string>();
        (status, var info) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id, tokenId });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(tokenId, info!["tokenId"]!.GetValue<string>());
        var (_, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{tokenId}/reveal");
        Assert.Contains($"{secret!["token"]!.GetValue<string>()}.codex", await File.ReadAllTextAsync(config));

        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { tokenId = "missing" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/rotate-key");
        Assert.NotEqual(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Token_Reset_Rewrites_Only_Astra_Config_Of_Its_Clients()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id, model = "gpt-5" });
        var before = await DefaultTokenAsync(host);
        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{TokenIds.Default}/reset");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["codex"], result!["rewritten"]!.AsArray().Select(k => k!.GetValue<string>()));
        var after = await DefaultTokenAsync(host);
        Assert.NotEqual(before, after);
        var text = await File.ReadAllTextAsync(config);
        Assert.Contains($"{after}.codex", text);
        Assert.Contains("# user config", text);
        Assert.Contains("trust_level = \"trusted\"", text);
        Assert.Empty(await OutdatedKinds(host));
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Deleting_A_Token_Moves_Its_Clients_To_The_Default_Token()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Temp" });
        var tokenId = created!["id"]!.GetValue<string>();
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id, tokenId });

        var (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/tokens/{TokenIds.Default}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        (status, var result) = await host.SendAsync(HttpMethod.Delete, $"/api/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["codex"], result!["rewritten"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(TokenIds.Default, (await host.Db.Clients.GetAsync("codex"))!.TokenId);
        Assert.Contains($"{await DefaultTokenAsync(host)}.codex", await File.ReadAllTextAsync(config));
        Assert.Null(await host.Db.Tokens.GetAsync(tokenId));
    }

    [Fact]
    public async Task Migration_Marker_Rewrites_Enabled_Clients_Once_And_Leaves_Drifted_Ones()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var codex = await WriteCodexConfig(host);
        var opencode = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(opencode)!);
        await File.WriteAllTextAsync(opencode, "{}\n");
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id });
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = p.Id });
        // Simulate configs written before tokens existed: the old per-client key is what Astra recorded and wrote.
        var token = await DefaultTokenAsync(host);
        foreach (var file in new[] { codex, opencode })
            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace($"{token}.", "astra-legacy-"));
        foreach (var e in host.Db.ClientConfigState.List("codex").Concat(host.Db.ClientConfigState.List("opencode")))
        {
            e.AppliedValueJson = e.AppliedValueJson?.Replace($"{token}.", "astra-legacy-");
            host.Db.ClientConfigState.Upsert(e);
        }
        // The user edited OpenCode's Astra entry: it is drifted and must not be touched.
        var drifted = (await File.ReadAllTextAsync(opencode)).Replace("\"Astra\"", "\"My Astra\"");
        await File.WriteAllTextAsync(opencode, drifted);
        await host.Db.Settings.SetAsync(TokenMigrationWorker.MarkerKey, new { pending = true });

        var worker = host.App.Services.GetServices<IHostedService>().OfType<TokenMigrationWorker>().Single();
        var result = await worker.RunAsync(CancellationToken.None);
        Assert.Equal(["codex"], result!.Rewritten);
        Assert.Equal(["opencode"], result.Skipped);
        Assert.Contains($"{token}.codex", await File.ReadAllTextAsync(codex));
        Assert.Equal(drifted, await File.ReadAllTextAsync(opencode));
        Assert.Null(await worker.RunAsync(CancellationToken.None)); // marker cleared
    }

    private static async Task<string> DefaultTokenAsync(TestHost host)
    {
        var (_, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{TokenIds.Default}/reveal");
        return secret!["token"]!.GetValue<string>();
    }

    [Fact]
    public async Task Drifted_Config_Is_Not_Overwritten_On_Disable_Or_Token_Reset()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id });
        var edited = (await File.ReadAllTextAsync(config)).Replace("model_provider = \"astra\"", "model_provider = \"some-other-provider\"");
        await File.WriteAllTextAsync(config, edited);
        var (status, reset) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{TokenIds.Default}/reset");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["codex"], reset!["skipped"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(edited, await File.ReadAllTextAsync(config));
        (status, var body) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(body!["drifted"]!.AsArray(), k => k!.GetValue<string>().EndsWith(":model_provider"));
        Assert.Contains("some-other-provider", await File.ReadAllTextAsync(config));
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/force-restore");
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Unsetting_Default_Model_Restores_The_Original_Selection()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id, model = "gpt-5" });
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new JsonObject { ["model"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["selectedModel"]);
        Assert.Contains("model = \"original-model\"", await File.ReadAllTextAsync(config));
        Assert.Equal("enabled", body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Offline_Restore_All_Works_Without_Starting_A_Second_Server()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id });
        var result = await Commands.RestoreAllAsync(new AstraPaths(host.Root), false, new ClientEnvironment(host.ClientHome, "osx"));
        Assert.Equal(0, result);
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
        Assert.False((await host.Db.Clients.GetAsync("codex"))!.Enabled);
    }

    [Fact]
    public async Task Invalid_Client_Or_Provider_Does_Not_Create_Config_And_Returns_JSON_Error()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/unknown/enable", new { providerId = "missing" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.NotNull(body!["error"]);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = "missing" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Empty(await host.Db.Clients.ListAsync());
        Assert.False(Directory.Exists(host.ClientHome));
    }

    [Fact]
    public async Task OpenCode_Model_List_Follows_The_Binding_And_The_Providers_Models()
    {
        await using var host = await TestHost.StartAsync();
        var first = await AddProvider(host, "First");
        var second = await AddProvider(host, "Second");
        await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "glm-4.6" });
        var config = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        const string original = "{\n  // mine\n  \"theme\": \"tokyonight\"\n}\n";
        await File.WriteAllTextAsync(config, original);

        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = first.Id, model = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5"], OpenCodeModels(config));

        // A model added to the bound provider shows up in the client config.
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{first.Id}/models", new { modelIds = new[] { "gpt-5-mini" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5", "gpt-5-mini"], OpenCodeModels(config));

        // Changes to a provider OpenCode is not bound to leave the file alone.
        var before = await File.ReadAllTextAsync(config);
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{second.Id}/models", new { modelIds = new[] { "glm-4.5-air" } });
        Assert.Equal(before, await File.ReadAllTextAsync(config));

        // Switching providers rewrites the list; the user's own settings stay.
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/binding", new { providerId = second.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["glm-4.5-air", "glm-4.6", "gpt-5"], OpenCodeModels(config)); // AddProvider gives every provider gpt-5
        Assert.Contains("// mine", await File.ReadAllTextAsync(config));

        // Still a clean, Astra-owned entry: disabling restores the original bytes.
        (status, var disabled) = await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/disable");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(disabled!["drifted"]!.AsArray());
        Assert.Equal(original, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task OpenCode_Model_Sync_Never_Overwrites_A_User_Edit()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var config = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "{}\n");
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = provider.Id });
        var edited = (await File.ReadAllTextAsync(config)).Replace("\"Astra\"", "\"My Astra\"");
        Assert.Contains("My Astra", edited);
        await File.WriteAllTextAsync(config, edited);

        var (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{provider.Id}/models", new { modelIds = new[] { "gpt-5-mini" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(edited, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Copilot_And_MiniMax_Model_Lists_Follow_The_Providers_Models()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var providers = Path.Combine(host.ClientHome, ".copilot", "providers.json");
        var minimax = Path.Combine(host.ClientHome, ".minimax", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(minimax)!);
        const string minimaxOriginal = "logLevel: info\n";
        await File.WriteAllTextAsync(minimax, minimaxOriginal);

        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/copilot-cli/enable", new { providerId = provider.Id, model = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/minimax-code/enable", new { providerId = provider.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5"], CopilotModels(providers));

        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{provider.Id}/models", new { modelIds = new[] { "gpt-5-mini" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5", "gpt-5-mini"], CopilotModels(providers));
        var yaml = Astra.Clients.Editing.YamlEditor.ParseToJson(await File.ReadAllTextAsync(minimax))!;
        Assert.Equal(["gpt-5", "gpt-5-mini"], yaml["custom_provider"]!["astra"]!["models"]!.AsObject().Select(m => m.Key).Order(StringComparer.Ordinal));

        foreach (var kind in new[] { "copilot-cli", "minimax-code" })
        {
            (status, var disabled) = await host.SendAsync(HttpMethod.Post, $"/api/clients/{kind}/disable");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Empty(disabled!["drifted"]!.AsArray());
        }
        Assert.False(File.Exists(providers)); // created by Astra, removed again
        Assert.Equal(minimaxOriginal, await File.ReadAllTextAsync(minimax));
    }

    [Fact]
    public async Task New_Clients_Enable_Follow_Model_Changes_And_Disable_Cleanly()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var crush = Path.Combine(host.ClientHome, ".config", "crush", "crush.json");
        var kimi = Path.Combine(host.ClientHome, ".kimi-code", "config.toml");
        var omp = Path.Combine(host.ClientHome, ".omp", "agent", "models.yml");
        var originals = new Dictionary<string, string>
        {
            [crush] = "{\n  \"options\": { \"debug\": false }\n}\n",
            [kimi] = "# kimi\ndefault_model = \"kimi/k2\"\n",
            [omp] = "providers:\n  ollama:\n    baseUrl: http://localhost:11434/v1\n",
        };
        foreach (var (path, text) in originals)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
        }

        foreach (var kind in new[] { "crush", "kimi-code", "omp" })
        {
            var (status, enabled) = await host.SendAsync(HttpMethod.Post, $"/api/clients/{kind}/enable", new { providerId = provider.Id, model = "gpt-5" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("enabled", enabled!["status"]!.GetValue<string>());
        }
        Assert.Contains("\"gpt-5\"", await File.ReadAllTextAsync(crush));
        Assert.Contains("[models.astra-gpt-5]", await File.ReadAllTextAsync(kimi));
        Assert.Contains("astra/gpt-5", await File.ReadAllTextAsync(Path.Combine(host.ClientHome, ".omp", "agent", "config.yml")));

        // Adding a model to the provider rewrites the model lists of the enabled clients.
        var (added, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{provider.Id}/models", new { modelIds = new[] { "gpt-5-mini" } });
        Assert.Equal(HttpStatusCode.OK, added);
        Assert.Contains("gpt-5-mini", await File.ReadAllTextAsync(crush));
        Assert.Contains("[models.astra-gpt-5-mini]", await File.ReadAllTextAsync(kimi));
        Assert.Contains("gpt-5-mini", await File.ReadAllTextAsync(omp));

        foreach (var kind in new[] { "crush", "kimi-code", "omp" })
        {
            var (status, disabled) = await host.SendAsync(HttpMethod.Post, $"/api/clients/{kind}/disable");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Empty(disabled!["drifted"]!.AsArray());
        }
        foreach (var (path, text) in originals) Assert.Equal(text, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(host.ClientHome, ".omp", "agent", "config.yml")));

        // Every registered client is listed, in the fixed order.
        var (listed, list) = await host.SendAsync(HttpMethod.Get, "/api/clients");
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Equal(ClientKinds.All, list!.AsArray().Select(c => c!["kind"]!.GetValue<string>()));
    }

    private static List<string> CopilotModels(string providers) =>
        JsonNode.Parse(File.ReadAllText(providers))!["models"]!.AsArray()
            .Where(m => m!["provider"]!.GetValue<string>() == "astra")
            .Select(m => m!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task After_The_Port_Changes_Enabled_Clients_Are_Flagged_And_Rewritten_In_One_Go()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var codex = await WriteCodexConfig(host);
        var opencode = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(opencode)!);
        await File.WriteAllTextAsync(opencode, "{\n  \"theme\": \"tokyonight\"\n}\n");
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = provider.Id, model = "gpt-5" });
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = provider.Id });

        // Freshly enabled clients are current.
        Assert.Empty(await OutdatedKinds(host));

        // Astra comes back on another port (the configured one was busy).
        host.App.Services.GetRequiredService<ServerOptions>().Port = 17399;
        Assert.Equal(["codex", "opencode"], await OutdatedKinds(host));

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/reapply");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["codex", "opencode"], body!["updated"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Contains("http://127.0.0.1:17399/v1", await File.ReadAllTextAsync(codex));
        Assert.Contains("http://127.0.0.1:17399/v1", await File.ReadAllTextAsync(opencode));
        Assert.Empty(await OutdatedKinds(host));
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/reapply");
        Assert.Empty(body!["updated"]!.AsArray()); // nothing left to do

        // The rewrite is still Astra-owned: disabling restores the user's original file.
        (status, var disabled) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        Assert.Empty(disabled!["drifted"]!.AsArray());
        Assert.Equal(Original, await File.ReadAllTextAsync(codex));
    }

    [Fact]
    public async Task Reapply_Leaves_Drifted_Clients_Alone()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var codex = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = provider.Id });
        var edited = (await File.ReadAllTextAsync(codex)).Replace("model_provider = \"astra\"", "model_provider = \"openai\"");
        await File.WriteAllTextAsync(codex, edited);
        host.App.Services.GetRequiredService<ServerOptions>().Port = 17399;

        Assert.Empty(await OutdatedKinds(host)); // drift is reported as drift, not as "outdated"
        var (_, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/reapply");
        Assert.Empty(body!["updated"]!.AsArray());
        Assert.Equal(edited, await File.ReadAllTextAsync(codex));
    }

    private static async Task<List<string>> OutdatedKinds(TestHost host) =>
        (await host.GetJsonAsync("/api/clients")).AsArray()
            .Where(c => c!["configOutdated"]!.GetValue<bool>())
            .Select(c => c!["kind"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task Claude_Desktop_Requires_At_Least_One_Role_Mapping()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-desktop/preview-enable", new { providerId = provider.Id });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("role", body!["error"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-desktop/preview-enable",
            new { providerId = provider.Id, extras = new { roleMap = new { sonnet = "gpt-5" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(await host.Db.Clients.ListAsync());
    }

    private static List<string> OpenCodeModels(string config)
    {
        var json = JsonNode.Parse(File.ReadAllText(config), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        return json["provider"]!["astra"]!["models"]!.AsObject().Select(m => m.Key).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Claude Code model slots: only the chosen ones are written, clearing one reverts it, and an empty
    /// selection never touches the file (the "defaults to empty" contract of the client page).</summary>
    [Fact]
    public async Task Claude_Code_Model_Slots_Are_Optional_And_Clearing_One_Reverts_It()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var settings = Path.Combine(host.ClientHome, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, """
            {
              "env": {
                "EDITOR": "vim"
              }
            }
            """);

        var (status, info) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/enable",
            EnableBody(provider.Id, new JsonObject { ["ANTHROPIC_MODEL"] = "gpt-5" }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("gpt-5", EnvOf(settings)["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Null(EnvOf(settings)["ANTHROPIC_DEFAULT_HAIKU_MODEL"]); // an empty slot writes nothing
        Assert.Equal("gpt-5", info!["extras"]!["models"]!["ANTHROPIC_MODEL"]!.GetValue<string>());

        // Pick a tier and clear the default model at the same time.
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/enable",
            EnableBody(null, new JsonObject { ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "gpt-5-mini" }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(EnvOf(settings)["ANTHROPIC_MODEL"]);
        Assert.Equal("gpt-5-mini", EnvOf(settings)["ANTHROPIC_DEFAULT_HAIKU_MODEL"]!.GetValue<string>());

        // Unsetting the last slot leaves the file as it was before Astra touched it.
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/enable", EnableBody(null, new JsonObject()));
        Assert.Equal(HttpStatusCode.OK, status);
        var env = EnvOf(settings);
        Assert.Null(env["ANTHROPIC_MODEL"]);
        Assert.Null(env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("vim", env["EDITOR"]!.GetValue<string>());
    }

    /// <summary>An enable body; the Claude Code model slots keep their exact env-var names, so they go in as JSON.</summary>
    private static JsonObject EnableBody(string? providerId, JsonObject models)
    {
        var body = new JsonObject { ["extras"] = new JsonObject { ["models"] = models } };
        if (providerId is not null) body["providerId"] = providerId;
        return body;
    }

    private static JsonNode EnvOf(string settingsFile) =>
        JsonNode.Parse(File.ReadAllText(settingsFile), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!["env"]!;

    private static async Task<string> WriteCodexConfig(TestHost host)
    {
        var path = Path.Combine(host.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Original);
        return path;
    }

    [Fact]
    public async Task Several_Providers_Fill_The_Model_List_In_Drag_Order()
    {
        await using var host = await TestHost.StartAsync();
        var first = await AddProvider(host, "First");
        var second = await AddProvider(host, "Second");
        var third = await AddProvider(host, "Third");
        await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "glm-4.6" });
        var config = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "{}\n");
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = first.Id });
        Assert.Equal(HttpStatusCode.OK, status);

        // The full list replaces the bindings in order and rewrites the model list with every provider's models.
        (status, var info) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/bindings",
            new { bindings = new[] { new { providerId = second.Id }, new { providerId = first.Id } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(second.Id, info!["providerId"]!.GetValue<string>());
        Assert.Equal([second.Id, first.Id], info["bindings"]!.AsArray().Select(b => b!["providerId"]!.GetValue<string>()));
        Assert.Equal(["glm-4.6", "gpt-5"], OpenCodeModels(config));
        Assert.Equal(["gpt-5", "glm-4.6"], (await host.GetJsonAsync("/api/clients/opencode/models")).AsArray().Select(m => m!.GetValue<string>()));

        // Duplicates, an empty list and a disabled provider that is not bound yet are rejected.
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/bindings",
            new { bindings = new[] { new { providerId = first.Id }, new { providerId = first.Id } } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/bindings", new { bindings = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        third.Enabled = false;
        await host.Db.Providers.UpdateAsync(third);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/bindings",
            new { bindings = new[] { new { providerId = second.Id }, new { providerId = third.Id } } });
        Assert.Equal(HttpStatusCode.Conflict, status);

        // Choosing a primary replaces only slot 0; the other bindings keep their order.
        (status, info) = await host.SendAsync(HttpMethod.Put, "/api/clients/opencode/binding", new { providerId = first.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([first.Id], info!["bindings"]!.AsArray().Select(b => b!["providerId"]!.GetValue<string>()));
        Assert.Equal(["gpt-5"], OpenCodeModels(config));
    }

    [Fact]
    public async Task Model_Mapping_Is_Validated_Kept_By_Settings_Writes_And_Listed_For_Clients()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await AddProvider(host);
        var config = Path.Combine(host.ClientHome, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "{}\n");
        await host.SendAsync(HttpMethod.Post, "/api/clients/opencode/enable", new { providerId = provider.Id });

        var (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{provider.Id}/model-map", new { map = new Dictionary<string, string> { ["x"] = "not-a-model" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{provider.Id}/model-map", new { map = new Dictionary<string, string> { ["gpt-5"] = "gpt-5" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, var dto) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{provider.Id}/model-map",
            new { map = new Dictionary<string, string> { [" deepseek-v4.1 "] = "gpt-5" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("gpt-5", dto!["settings"]!["model_map"]!["deepseek-v4.1"]!.GetValue<string>());
        // The write filter re-synced the bound client's model list: the mapped id is offered too.
        Assert.Equal(["deepseek-v4.1", "gpt-5"], OpenCodeModels(config));
        Assert.Equal(["gpt-5", "deepseek-v4.1"], (await host.GetJsonAsync("/api/clients/opencode/models")).AsArray().Select(m => m!.GetValue<string>()));

        // A settings write never replaces the mapping (it has its own validated endpoint).
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{provider.Id}", new { settings = new { } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("gpt-5", (await host.Db.Providers.GetAsync(provider.Id))!.MapModel("deepseek-v4.1"));

        (status, dto) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{provider.Id}/model-map", new { map = new { } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(dto!["settings"]!["model_map"]);
        Assert.Equal(["gpt-5"], OpenCodeModels(config));
    }

    private static async Task<Provider> AddProvider(TestHost host, string name = "Mock provider")
    {
        var provider = new Provider { Id = Ulid.NewUlid(), Name = name, AuthScheme = AuthSchemes.None,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIResponses] };
        await host.Db.Providers.InsertAsync(provider);
        await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = "gpt-5", SystemModelId = "gpt-5" });
        return provider;
    }
}
