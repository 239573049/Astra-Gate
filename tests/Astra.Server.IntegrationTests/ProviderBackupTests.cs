using System.Net;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Provider backup (issue #33): export, preview and import of providers and their models.</summary>
public sealed class ProviderBackupTests
{
    private const string Key = "sk-backup-secret-0123456789";

    private static async Task<string> CreateAsync(TestHost host, string name, string url, string? key = Key)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name, apiKey = key, models = new[] { "gpt-5", "gpt-5-mini" },
            endpoints = new[] { new { protocol = "openai-chat", baseUrl = url } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    private static async Task<JsonObject> ExportAsync(TestHost host, object body)
    {
        var (status, file) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/export", body);
        Assert.Equal(HttpStatusCode.OK, status);
        return file!.AsObject();
    }

    [Fact]
    public async Task Default_Export_Carries_No_Key_And_Keeps_Models_And_Overrides()
    {
        await using var host = await TestHost.StartAsync();
        var id = await CreateAsync(host, "Alpha", "https://a.example.invalid/v1");
        var model = (await host.Db.Providers.ListModelsAsync(id)).First(m => m.ModelId == "gpt-5");
        var (patch, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}/models/{model.Id}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, patch);

        var file = await ExportAsync(host, new { });

        Assert.Equal("astra-providers", file["format"]!.GetValue<string>());
        Assert.Equal("none", file["secrets"]!.GetValue<string>());
        Assert.DoesNotContain(Key, file.ToJsonString());
        var item = file["providers"]![0]!;
        Assert.Equal("Alpha", item["name"]!.GetValue<string>());
        Assert.Null(item["api_key"]);
        Assert.Null(item["api_key_sealed"]);
        var models = item["models"]!.AsArray();
        Assert.Equal(2, models.Count);
        Assert.False(models.Single(m => m!["model_id"]!.GetValue<string>() == "gpt-5")!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Plaintext_Backup_Round_Trips_Into_An_Empty_Host_With_The_Key_Protected()
    {
        JsonObject file;
        await using (var source = await TestHost.StartAsync())
        {
            await CreateAsync(source, "Alpha", "https://a.example.invalid/v1");
            file = await ExportAsync(source, new { secrets = "plaintext" });
            Assert.Equal(Key, file["providers"]![0]!["api_key"]!.GetValue<string>());
        }

        await using var target = await TestHost.StartAsync();
        var (status, preview) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file });
        Assert.Equal(HttpStatusCode.OK, status);
        var shown = preview!["items"]![0]!;
        Assert.Equal("new", shown["status"]!.GetValue<string>());
        Assert.True(shown["hasKey"]!.GetValue<bool>());
        Assert.DoesNotContain(Key, preview.ToJsonString()); // only a mask ever comes back

        (status, var done) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file });
        Assert.Equal(HttpStatusCode.OK, status);
        var newId = done!["added"]![0]!["id"]!.GetValue<string>();
        var provider = (await target.Db.Providers.GetAsync(newId))!;
        Assert.Equal("Alpha", provider.Name);
        Assert.NotNull(provider.ApiKeyEnc);
        Assert.Equal(Key, target.App.Services.GetRequiredService<ISecretProtector>().Unprotect(provider.ApiKeyEnc!));
        Assert.Equal(["gpt-5", "gpt-5-mini"], (await target.Db.Providers.ListModelsAsync(newId)).Select(m => m.ModelId).Order().ToList());

        // the same file again: same key and address, so nothing is created twice
        (_, var again) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file });
        Assert.Empty(again!["added"]!.AsArray());
        Assert.Equal("same", again["skipped"]![0]!["reason"]!.GetValue<string>());
        Assert.Single(await target.Db.Providers.ListAsync());
    }

    [Fact]
    public async Task Passphrase_Backup_Hides_The_Key_And_Needs_The_Right_Passphrase()
    {
        JsonObject file;
        await using (var source = await TestHost.StartAsync())
        {
            await CreateAsync(source, "Alpha", "https://a.example.invalid/v1");
            file = await ExportAsync(source, new { secrets = "passphrase", passphrase = "correct horse battery" });
        }
        Assert.DoesNotContain(Key, file.ToJsonString());
        Assert.StartsWith("v1:", file["providers"]![0]!["api_key_sealed"]!.GetValue<string>());

        await using var target = await TestHost.StartAsync();
        var (status, _) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file });
        Assert.Equal(HttpStatusCode.BadRequest, status); // no passphrase
        (status, _) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file, passphrase = "wrong passphrase!" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(await target.Db.Providers.ListAsync());

        (status, var done) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file, passphrase = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, status);
        var provider = (await target.Db.Providers.GetAsync(done!["added"]![0]!["id"]!.GetValue<string>()))!;
        Assert.Equal(Key, target.App.Services.GetRequiredService<ISecretProtector>().Unprotect(provider.ApiKeyEnc!));
    }

    [Fact]
    public async Task A_Short_Passphrase_And_An_Unknown_Mode_Are_Refused()
    {
        await using var host = await TestHost.StartAsync();
        await CreateAsync(host, "Alpha", "https://a.example.invalid/v1");
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/export", new { secrets = "passphrase", passphrase = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/export", new { secrets = "everything" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Keys_Are_Never_Exported_To_A_Caller_That_Is_Not_This_Machine()
    {
        await using var host = await TestHost.StartAsync();
        await CreateAsync(host, "Alpha", "https://a.example.invalid/v1");
        var backup = host.App.Services.GetRequiredService<ProviderBackupService>();

        var e = await Assert.ThrowsAsync<AdminApiException>(() => backup.ExportAsync(new JsonObject { ["secrets"] = "plaintext" }, local: false, default));
        Assert.Equal(403, e.StatusCode);
        e = await Assert.ThrowsAsync<AdminApiException>(() => backup.ExportAsync(new JsonObject { ["secrets"] = "passphrase", ["passphrase"] = "correct horse battery" }, local: false, default));
        Assert.Equal(403, e.StatusCode);
        // a file without keys is fine from anywhere
        var file = await backup.ExportAsync(new JsonObject(), local: false, default);
        Assert.DoesNotContain(Key, file.ToJsonString());
    }

    [Fact]
    public async Task Import_Names_Clashes_Apart_And_Reports_A_Broken_Entry_Without_Stopping_The_Rest()
    {
        await using var host = await TestHost.StartAsync();
        await CreateAsync(host, "Alpha", "https://a.example.invalid/v1", key: null);
        var file = new JsonObject
        {
            ["format"] = "astra-providers", ["version"] = 1, ["secrets"] = "none",
            ["providers"] = new JsonArray(
                new JsonObject { ["name"] = "Alpha", ["endpoints"] = new JsonArray(new JsonObject { ["protocol"] = "openai-chat", ["base_url"] = "https://b.example.invalid/v1" }) },
                new JsonObject { ["name"] = "Broken", ["endpoints"] = new JsonArray() },
                new JsonObject { ["name"] = "Odd", ["auth_scheme"] = "plugin", ["endpoints"] = new JsonArray(new JsonObject { ["protocol"] = "openai-chat", ["base_url"] = "https://c.example.invalid/v1" }) }),
        };

        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file });
        var statuses = preview!["items"]!.AsArray().Select(i => i!["status"]!.GetValue<string>()).ToList();
        Assert.True(statuses is ["new", "invalid", "invalid"], preview.ToJsonString());

        var (status, done) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Alpha (2)"], done!["added"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToList());
        Assert.Equal(2, done["skipped"]!.AsArray().Count);
        Assert.Equal(2, (await host.Db.Providers.ListAsync()).Count);
    }

    [Fact]
    public async Task A_File_That_Is_Not_A_Backup_Or_Comes_From_The_Future_Is_Refused()
    {
        await using var host = await TestHost.StartAsync();
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file = new { format = "something-else", version = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file = new { format = "astra-providers", version = 99, providers = Array.Empty<object>() } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Plugin_And_Unified_Gateway_Providers_Are_Not_Exported()
    {
        await using var host = await TestHost.StartAsync();
        await CreateAsync(host, "Alpha", "https://a.example.invalid/v1");
        var id = await CreateAsync(host, "Corp gateway", "https://corp.example.invalid/v1");
        var gateway = (await host.Db.Providers.GetAsync(id))!;
        gateway.Settings["unified_gateway"] = true;
        await host.Db.Providers.UpdateAsync(gateway);

        var file = await ExportAsync(host, new { });
        Assert.Equal(["Alpha"], file["providers"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList());
    }
}
