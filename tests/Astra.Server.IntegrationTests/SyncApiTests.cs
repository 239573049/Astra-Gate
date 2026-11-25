using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Server.Backup;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Multi-machine sync end to end: two hosts (with their own DataProtection keys) and one shared folder as the target.</summary>
public sealed class SyncApiTests
{
    private const string Pass = "sync passphrase 1";
    private const string Key = "sk-sync-provider-key";

    private static async Task Configure(TestHost host, string dir, string passphrase = Pass, bool keys = true)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Put, "/api/sync/config", new
        {
            target = new { kind = "folder", url = dir, folder = "astra-sync" }, passphrase, includeKeys = keys, enabled = true, intervalMinutes = 3,
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["configured"]!.GetValue<bool>());
        Assert.True(body["hasPassphrase"]!.GetValue<bool>());
        Assert.DoesNotContain(passphrase, body.ToJsonString());
    }

    private static async Task<JsonNode> SyncNow(TestHost host, string query = "")
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/sync/now" + query);
        Assert.True(status == HttpStatusCode.OK, status + " " + body);
        return body!;
    }

    private static async Task AddProvider(TestHost host, string id, string name)
    {
        await host.Db.Providers.InsertAsync(new Provider
        {
            Id = id, Name = name, AuthScheme = AuthSchemes.Bearer,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
            ApiKeyEnc = host.App.Services.GetRequiredService<ISecretProtector>().Protect(Key),
        });
    }

    private static async Task<List<string>> Names(TestHost host) => (await host.Db.Providers.ListAsync()).Select(p => p.Name).Order().ToList();

    [Fact]
    public async Task Two_machines_converge_through_one_encrypted_file_and_machine_local_settings_never_travel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var a = await TestHost.StartAsync();
            await using var b = await TestHost.StartAsync();
            await Configure(a, dir);
            await Configure(b, dir);
            await AddProvider(a, "p1", "Alpha");
            await ExecAsync(a, """INSERT INTO settings(key, value_json) VALUES ('app', '{"locale":"en","proxy_url":"http://127.0.0.1:9"}') ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json""");

            var dry = await SyncNow(a, "?dryRun=true");
            Assert.True(dry["dryRun"]!.GetValue<bool>());
            Assert.False(File.Exists(Path.Combine(dir, "astra-sync", SyncService.SyncFile))); // a plan writes nothing

            var first = await SyncNow(a);
            Assert.True(first["pushed"]!.GetValue<int>() > 0);
            var file = await File.ReadAllBytesAsync(Path.Combine(dir, "astra-sync", SyncService.SyncFile));
            var text = Encoding.UTF8.GetString(file);
            Assert.DoesNotContain(Key, text);
            Assert.DoesNotContain("Alpha", text); // ciphertext only
            var opened = BackupEnvelope.Open(file, Pass).ToJsonString();
            Assert.Contains("Alpha", opened);
            Assert.Contains(Key, opened); // inside the envelope only
            Assert.DoesNotContain("proxy_url", opened); // this machine's own

            var joined = await SyncNow(b);
            Assert.True(joined["firstSync"]!.GetValue<bool>());
            Assert.Equal(["Alpha"], await Names(b));
            Assert.Equal(Key, b.App.Services.GetRequiredService<ISecretProtector>().Unprotect((await b.Db.Providers.GetAsync("p1"))!.ApiKeyEnc!));
            Assert.Equal("en", JsonNode.Parse((await b.Db.Settings.GetAllAsync())["app"])!["locale"]!.GetValue<string>());
            Assert.Null(JsonNode.Parse((await b.Db.Settings.GetAllAsync())["app"])!["proxy_url"]);

            // B renames it; the change comes back to A
            Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(HttpMethod.Patch, "/api/providers/p1", new { name = "Alpha renamed" })).Status);
            await SyncNow(b);
            await SyncNow(a);
            Assert.Equal(["Alpha renamed"], await Names(a));

            // A deletes it; B follows, and the next syncs do not bring it back
            Assert.Equal(HttpStatusCode.NoContent, (await a.SendAsync(HttpMethod.Delete, "/api/providers/p1")).Status);
            await SyncNow(a);
            await SyncNow(b);
            Assert.Empty(await Names(b));
            await SyncNow(a);
            await SyncNow(b);
            Assert.Empty(await Names(a));

            var (_, status) = await a.SendAsync(HttpMethod.Get, "/api/sync/status");
            Assert.NotNull(status!["lastSyncAt"]);
            Assert.Null(status["lastError"]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task ExecAsync(TestHost host, string sql)
    {
        await using var conn = await host.Db.Factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_wrong_passphrase_or_a_different_keys_setting_is_reported_and_changes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var a = await TestHost.StartAsync();
            await using var c = await TestHost.StartAsync();
            await Configure(a, dir);
            await AddProvider(a, "p1", "Alpha");
            await SyncNow(a);

            await Configure(c, dir, "another passphrase");
            var (status, body) = await c.SendAsync(HttpMethod.Post, "/api/sync/now");
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("Wrong passphrase", body!["error"]!.GetValue<string>());
            Assert.Empty(await Names(c));
            var (_, st) = await c.SendAsync(HttpMethod.Get, "/api/sync/status");
            Assert.Contains("Wrong passphrase", st!["lastError"]!.GetValue<string>());

            await using var d = await TestHost.StartAsync();
            await Configure(d, dir, Pass, keys: false);
            (status, body) = await d.SendAsync(HttpMethod.Post, "/api/sync/now");
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Contains("include keys", body!["error"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task The_configuration_validates_the_target_hides_the_secret_and_can_be_forgotten()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { target = new { kind = "github", url = "nonsense" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { passphrase = "short" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { enabled = true })).Status); // no target, no passphrase

        var (status, body) = await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { target = new { kind = "webdav", url = "https://dav.example.invalid/dav", username = "me", secret = "dav-password" }, passphrase = Pass });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["target"]!["hasSecret"]!.GetValue<bool>());
        Assert.DoesNotContain("dav-password", body.ToJsonString());
        Assert.DoesNotContain("dav-password", string.Join(" ", (await host.Db.Settings.GetAllAsync()).Values)); // stored protected

        // an unchanged target keeps its secret; moving it drops the secret
        (_, body) = await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { target = new { kind = "webdav", url = "https://dav.example.invalid/dav", username = "me" } });
        Assert.True(body!["target"]!["hasSecret"]!.GetValue<bool>());
        (_, body) = await host.SendAsync(HttpMethod.Put, "/api/sync/config", new { target = new { kind = "webdav", url = "https://other.example.invalid/dav", username = "me" } });
        Assert.False(body!["target"]!["hasSecret"]!.GetValue<bool>());

        (_, body) = await host.SendAsync(HttpMethod.Delete, "/api/sync/config");
        Assert.False(body!["configured"]!.GetValue<bool>());
        Assert.False(body["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Remote_restore_reads_sync_file_when_no_manual_upload_exists_and_uses_saved_passphrase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-remote-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var a = await TestHost.StartAsync();
            await using var b = await TestHost.StartAsync();
            await Configure(a, dir);
            await AddProvider(a, "p1", "CloudAlpha");
            await SyncNow(a); // writes astra-sync.astra-backup, not astra-backup-latest.astra-backup

            // On a second machine with the same sync target configured, preview & restore from "remote" works even without retyping the passphrase.
            await Configure(b, dir);
            var (status, preview) = await b.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "remote", mode = "merge" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("providers", preview!["available"]!.AsArray().Select(n => n!.GetValue<string>()));

            (status, var restored) = await b.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "remote", mode = "merge", sections = new[] { "providers" } });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(restored!["written"]!.GetValue<int>() >= 1);
            Assert.Equal(["CloudAlpha"], await Names(b));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
