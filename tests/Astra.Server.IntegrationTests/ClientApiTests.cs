using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.NotNull(record!.LocalKeyEnc);
        Assert.NotNull(record.LocalKeyHash);
        Assert.DoesNotContain(record.LocalKeyEnc!, enabled.ToJsonString());
        Assert.DoesNotContain(record.LocalKeyHash!, enabled.ToJsonString());

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
        var keyHash = (await host.Db.Clients.GetAsync("codex"))!.LocalKeyHash;

        (status, bound) = await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = second.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(bound!["enabled"]!.GetValue<bool>());
        Assert.Equal(second.Id, bound["providerId"]!.GetValue<string>());
        Assert.Equal(applied, await File.ReadAllTextAsync(config));
        Assert.Equal(keyHash, (await host.Db.Clients.GetAsync("codex"))!.LocalKeyHash);
    }

    [Fact]
    public async Task Rotate_Key_Changes_Only_Astra_Config_And_Updates_Key_Hash()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id, model = "gpt-5" });
        var before = await host.Db.Clients.GetAsync("codex");
        var (status, info) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/rotate-key");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("enabled", info!["status"]!.GetValue<string>());
        var after = await host.Db.Clients.GetAsync("codex");
        Assert.NotEqual(before!.LocalKeyHash, after!.LocalKeyHash);
        var text = await File.ReadAllTextAsync(config);
        Assert.Contains("# user config", text);
        Assert.Contains("trust_level = \"trusted\"", text);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/disable");
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task Drifted_Config_Is_Not_Overwritten_On_Disable_Or_Key_Rotation()
    {
        await using var host = await TestHost.StartAsync();
        var p = await AddProvider(host);
        var config = await WriteCodexConfig(host);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = p.Id });
        var edited = (await File.ReadAllTextAsync(config)).Replace("model_provider = \"astra\"", "model_provider = \"some-other-provider\"");
        await File.WriteAllTextAsync(config, edited);
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/rotate-key");
        Assert.Equal(HttpStatusCode.Conflict, status);
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

    private static async Task<string> WriteCodexConfig(TestHost host)
    {
        var path = Path.Combine(host.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Original);
        return path;
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
