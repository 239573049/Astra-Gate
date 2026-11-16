using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data.Backup;
using Astra.Server.Api;
using Astra.Server.Backup;
using Astra.Server.Hosting;
using Astra.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Whole-installation backup and restore through the admin API, between two hosts that do not share DataProtection keys.</summary>
public sealed class BackupRestoreApiTests
{
    private const string Pass = "correct horse battery";
    private const string ProviderKey = "sk-seed-provider-key-1";
    private const string Secret = "seed-secret-value";

    private static async Task ExecAsync(TestHost host, string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = await host.Db.Factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Protect(TestHost host, string plain) => host.App.Services.GetRequiredService<ISecretProtector>().Protect(plain);

    private const string Now = "2026-11-10T00:00:00.000Z";

    /// <summary>One row in every configuration table (the guard below fails when a spec table is left without one).</summary>
    private static async Task SeedAsync(TestHost host)
    {
        var p = (string v) => Protect(host, v);
        await ExecAsync(host, "INSERT INTO providers(id, name, endpoints_json, api_key_enc, created_at, updated_at, quota_json) VALUES ('p1','Alpha','[]',$k,$n,$n,'{\"cached\":1}')", ("$k", p(ProviderKey)), ("$n", Now));
        await ExecAsync(host, "INSERT INTO provider_models(provider_id, model_id) VALUES ('p1','gpt-5'), ('p1','gpt-5-mini')");
        await ExecAsync(host, "INSERT INTO provider_accounts(id, provider_id, kind, display_name, access_token_enc, created_at, updated_at, last_error) VALUES ('k1','p1','api_key','Key 1',$k,$n,$n,'transient')", ("$k", p("sk-second-key")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO provider_accounts(id, provider_id, kind, display_name, access_token_enc, refresh_token_enc, created_at, updated_at) VALUES ('s1','p1','oauth','Me',$a,$r,$n,$n)", ("$a", p("access-1")), ("$r", p("refresh-1")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO tokens(id, name, key_enc, key_hash, key_prefix, provider_id, created_at, updated_at, last_used_at) VALUES ('t1','Team',$k,'hash1','ast-abc','p1',$n,$n,$n)", ("$k", p("ast-team-key")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO routing_groups(id, slug, name, created_at, updated_at) VALUES ('g1','fast','Fast',$n,$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO webhooks(id, name, url, secret_enc, headers_enc, created_at, updated_at) VALUES ('w1','Hook','https://hook.example.invalid',$s,$h,$n,$n)", ("$s", p(Secret)), ("$h", p("{\"x\":\"y\"}")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO alert_rules(id, name, event, created_at, updated_at) VALUES ('r1','Rule','request.failed',$n,$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO alert_rule_webhooks(rule_id, webhook_id) VALUES ('r1','w1')");
        await ExecAsync(host, "INSERT INTO mcp_servers(id, slug, name, transport, url, headers_enc, status, created_at, updated_at) VALUES ('m1','docs','Docs','http','https://mcp.example.invalid',$h,'ok',$n,$n)", ("$h", p("{\"Authorization\":\"Bearer abc\"}")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO mcp_oauth(server_id, tokens_enc, updated_at) VALUES ('m1',$t,$n)", ("$t", p("{\"access\":\"oa\"}")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO mcp_local_servers(id, name, env_enc, projects_json, created_at, updated_at) VALUES ('l1','files',$e,'[\"/home/me/proj\"]',$n,$n)", ("$e", p("{\"TOKEN\":\"t\"}")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO plugins(spec, name, source, added_at_utc, updated_at_utc, options_json) VALUES ('pkg-a','Pkg A','npm',$n,$n,'{\"a\":1}')", ("$n", Now));
        await ExecAsync(host, "INSERT INTO plugin_provider_prefs(provider_id, spec) VALUES ('pp1','pkg-a')");
        await ExecAsync(host, "INSERT INTO plugin_kv(plugin_id, key, value_json, updated_at_utc) VALUES ('com.a','k','1',$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO plugin_secrets(plugin_id, name, value_protected, updated_at_utc) VALUES ('com.a','tok',$v,$n)", ("$v", p("plugin-secret")), ("$n", Now));
        await ExecAsync(host, "INSERT INTO plugin_grants(plugin_id, permissions_json, updated_at_utc) VALUES ('com.a','[\"http.fetch\"]',$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO plugin_successor_dismissals(spec, version) VALUES ('pkg-old','1.0.0')");
        await ExecAsync(host, "INSERT INTO models(id, display_name, source, created_at, updated_at) VALUES ('my-model','My model','user',$n,$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO model_prices(model_id, price_key, pricing_json, source, user_modified, updated_at) VALUES ('my-model','custom','{}','user',1,$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO library_items(id, kind, name, body, created_at, updated_at) VALUES ('i1','instruction','style','be brief',$n,$n)", ("$n", Now));
        await ExecAsync(host, "INSERT INTO token_usage_totals(token_id, requests) VALUES ('t1', 7)");
        await ExecAsync(host, "INSERT INTO token_usage_daily(token_id, day, requests) VALUES ('t1','2026-11-09', 3)");
        await ExecAsync(host, "INSERT INTO provider_account_usage_totals(provider_id, account_id, requests) VALUES ('p1','k1', 2)");

        // settings: the app document carries this machine's own proxy; two others carry secrets
        await ExecAsync(host, "INSERT INTO settings(key, value_json) VALUES ('app', $v) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json",
            ("$v", $$"""{"locale":"en","proxy_mode":"custom","proxy_url":"http://127.0.0.1:1","proxy_password_protected":"{{p("proxy-pass")}}","display_currency":"EUR"}"""));
        await ExecAsync(host, "INSERT INTO settings(key, value_json) VALUES ('smart_routing', $v) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json", ("$v", $$"""{"enabled":false,"api_key_protected":"{{p("judge-key")}}"}"""));
        await ExecAsync(host, "INSERT INTO settings(key, value_json) VALUES ('tools', $v) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json", ("$v", $$"""{"search":[{"name":"s","key_enc":"{{p("search-key")}}"}]}"""));
        await ExecAsync(host, "INSERT INTO settings(key, value_json) VALUES ('privacy_placeholder_key', $v) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json", ("$v", "\"machine-own\""));
    }

    private static async Task<(string Content, JsonNode Body)> ExportAsync(TestHost host, object body)
    {
        var (status, res) = await host.SendAsync(HttpMethod.Post, "/api/backup/export", body);
        Assert.Equal(HttpStatusCode.OK, status);
        return (res!["content"]!.GetValue<string>(), res);
    }

    private static async Task<List<BackupDoc>> DocsAsync(TestHost host, bool keys, IEnumerable<string>? sections = null) =>
        await host.App.Services.GetRequiredService<ConfigDocs>().ReadAsync(sections ?? BackupSections.All, new ReadOptions(keys), null, default);

    [Fact]
    public async Task Every_configuration_table_round_trips_into_a_fresh_host_and_secrets_are_protected_again()
    {
        await using var a = await TestHost.StartAsync();
        await SeedAsync(a);
        // the guard: no spec table may go untested
        foreach (var spec in BackupCatalog.Specs)
        {
            await using var conn = await a.Db.Factory.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {spec.Table}" + (spec.Where is null ? "" : $" WHERE {spec.Where}");
            Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0, $"seed a row for {spec.Name} ({spec.Table}) in SeedAsync");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(a.Paths.PluginAuthFile)!);
        await File.WriteAllTextAsync(a.Paths.PluginAuthFile, """{"github-copilot":{"type":"oauth","refresh":"gho_x"}}""");

        var (content, res) = await ExportAsync(a, new { includeKeys = true, includeUsage = true, passphrase = Pass });
        Assert.True(res["encrypted"]!.GetValue<bool>());
        foreach (var plain in new[] { ProviderKey, Secret, "ast-team-key", "judge-key", "refresh-1", "gho_x" })
            Assert.DoesNotContain(plain, content); // nothing readable in the file

        await using var b = await TestHost.StartAsync();
        // a plugin row only exists where the plugin is installed: B has it (with other options)
        await ExecAsync(b, "INSERT INTO plugins(spec, name, source, added_at_utc, updated_at_utc, options_json) VALUES ('pkg-a','Pkg A','npm',$n,$n,'{\"a\":2}')", ("$n", Now));

        var (status, plan) = await b.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "file", content, passphrase = Pass });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(plan!["hasKeys"]!.GetValue<bool>());
        var providers = plan["sections"]!.AsArray().Single(s => s!["section"]!.GetValue<string>() == "providers")!;
        Assert.Equal(1, providers["added"]!.GetValue<int>());
        Assert.Empty(await b.Db.Providers.ListAsync()); // a preview writes nothing

        (status, var done) = await b.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "file", content, passphrase = Pass, sections = BackupSections.All.ToArray(), mode = "merge" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(done!["errors"]!.AsArray());

        // every document, secrets included, is the same on both hosts
        var from = (await DocsAsync(a, true)).ToDictionary(d => d.Section + d.Id, d => d.Hash());
        var to = (await DocsAsync(b, true)).ToDictionary(d => d.Section + d.Id, d => d.Hash());
        Assert.Equal(from.Keys.Order().ToList(), to.Keys.Order().ToList());
        foreach (var (key, hash) in from) Assert.True(hash == to[key], $"{key} differs after the restore");

        // re-protected with B's own keys
        var protector = b.App.Services.GetRequiredService<ISecretProtector>();
        var provider = (await b.Db.Providers.GetAsync("p1"))!;
        Assert.Equal(ProviderKey, protector.Unprotect(provider.ApiKeyEnc!));
        Assert.Equal(["gpt-5", "gpt-5-mini"], (await b.Db.Providers.ListModelsAsync("p1")).Select(m => m.ModelId).Order().ToList());
        var token = (await b.Db.Tokens.GetAsync("t1"))!;
        Assert.Equal("ast-team-key", protector.Unprotect(token.KeyEnc!));
        Assert.Equal("hash1", token.KeyHash);
        Assert.Equal("Alpha", provider.Name);

        // settings: preferences came, this machine's proxy and server key did not
        var app = JsonNode.Parse((await b.Db.Settings.GetAllAsync())["app"])!;
        Assert.Equal("en", app["locale"]!.GetValue<string>());
        Assert.Null(app["proxy_url"]);
        Assert.Null(app["proxy_password_protected"]);
        Assert.DoesNotContain("machine-own", (await b.Db.Settings.GetAllAsync()).GetValueOrDefault("privacy_placeholder_key") ?? ""); // the server's own key never travels
        Assert.Equal("judge-key", protector.Unprotect(JsonNode.Parse((await b.Db.Settings.GetAllAsync())["smart_routing"])!["api_key_protected"]!.GetValue<string>()));
        // machine-local columns were not exported: the runtime state of A stayed on A
        Assert.Null(await ScalarAsync(b, "SELECT quota_json FROM providers WHERE id = 'p1'"));
        Assert.Equal("[]", await ScalarAsync(b, "SELECT projects_json FROM mcp_local_servers WHERE id = 'l1'"));
        Assert.Null(await ScalarAsync(b, "SELECT last_error FROM provider_accounts WHERE id = 'k1'"));
        Assert.Equal("{\"a\":1}", await ScalarAsync(b, "SELECT options_json FROM plugins WHERE spec = 'pkg-a'"));
        Assert.Contains("gho_x", await File.ReadAllTextAsync(b.Paths.PluginAuthFile));
    }

    private static async Task<string?> ScalarAsync(TestHost host, string sql)
    {
        await using var conn = await host.Db.Factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync() is { } v and not DBNull ? Convert.ToString(v) : null;
    }

    [Fact]
    public async Task A_backup_without_keys_holds_no_secret_and_leaves_the_keys_here_alone()
    {
        await using var a = await TestHost.StartAsync();
        await SeedAsync(a);
        var (content, res) = await ExportAsync(a, new { });
        Assert.False(res["encrypted"]!.GetValue<bool>());
        Assert.False(res["hasKeys"]!.GetValue<bool>());
        foreach (var plain in new[] { ProviderKey, Secret, "ast-team-key", "judge-key", "refresh-1", "sk-second-key", "hash1", "ast-abc", "$secrets" })
            Assert.DoesNotContain(plain, content);
        Assert.Contains("\"kdf\":\"none\"", content);

        // keys need a passphrase, and a passphrase-less file cannot be asked to carry them
        Assert.Equal(HttpStatusCode.BadRequest, (await a.SendAsync(HttpMethod.Post, "/api/backup/export", new { includeKeys = true })).Status);

        await using var c = await TestHost.StartAsync();
        await ExecAsync(c, "INSERT INTO providers(id, name, endpoints_json, api_key_enc, created_at, updated_at) VALUES ('p1','Old name','[]',$k,$n,$n)", ("$k", Protect(c, "my-own-key")), ("$n", Now));
        var (status, _) = await c.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "file", content });
        Assert.Equal(HttpStatusCode.OK, status);
        var provider = (await c.Db.Providers.GetAsync("p1"))!;
        Assert.Equal("Alpha", provider.Name);
        Assert.Equal("my-own-key", c.App.Services.GetRequiredService<ISecretProtector>().Unprotect(provider.ApiKeyEnc!));
        // a token that arrives without its key cannot authenticate anything
        var token = (await c.Db.Tokens.GetAsync("t1"))!;
        Assert.Null(token.KeyEnc);
        Assert.Null(token.KeyHash);
    }

    [Fact]
    public async Task Preview_counts_per_section_and_replace_removes_what_the_backup_lacks_after_keeping_the_old_setup()
    {
        await using var a = await TestHost.StartAsync();
        await SeedAsync(a);
        var (content, _) = await ExportAsync(a, new { });

        await using var b = await TestHost.StartAsync();
        await ExecAsync(b, "INSERT INTO providers(id, name, endpoints_json, created_at, updated_at) VALUES ('p1','Alpha','[]',$n,$n), ('extra','Extra','[]',$n,$n)", ("$n", Now));
        await ExecAsync(b, "INSERT INTO routing_groups(id, slug, name, created_at, updated_at) VALUES ('gx','mine','Mine',$n,$n)", ("$n", Now));

        var (_, merge) = await b.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "file", content, mode = "merge" });
        Section(merge!, "providers", out var added, out var changed, out var removed, out _);
        Assert.Equal((0, 1, 0), (added, changed, removed)); // p1 differs (models, key list, notes); nothing is removed in a merge
        var (_, replace) = await b.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "file", content, mode = "replace" });
        Section(replace!, "providers", out _, out _, out removed, out _);
        Assert.Equal(1, removed);
        Section(replace!, "routing", out _, out _, out removed, out _);
        Assert.Equal(1, removed); // the group the backup does not have

        // restoring only the chosen sections
        var (status, only) = await b.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "file", content, mode = "replace", sections = new[] { "routing" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["g1"], (await ScalarListAsync(b, "SELECT id FROM routing_groups")));
        Assert.Equal(2, (await b.Db.Providers.ListAsync()).Count); // providers untouched
        Assert.NotNull(only!["safetyBackup"]);

        // the setup that was there is kept first: a backup file and a copy of the database
        var kept = Directory.EnumerateFiles(b.Paths.AutoBackupsDir, "*-restore.astra-backup").ToList();
        Assert.Single(kept);
        Assert.NotEmpty(Directory.EnumerateFiles(b.Paths.DbBackupsDir, "pre-restore-*.db"));
        Assert.Contains("routing_groups/gx", File.ReadAllText(kept[0]));
    }

    private static async Task<List<string>> ScalarListAsync(TestHost host, string sql)
    {
        await using var conn = await host.Db.Factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private static void Section(JsonNode plan, string name, out int added, out int changed, out int removed, out int unchanged)
    {
        var s = plan["sections"]!.AsArray().Single(x => x!["section"]!.GetValue<string>() == name)!;
        added = s["added"]!.GetValue<int>();
        changed = s["changed"]!.GetValue<int>();
        removed = s["removed"]!.GetValue<int>();
        unchanged = s["unchanged"]!.GetValue<int>();
    }

    [Fact]
    public async Task Clients_come_back_through_the_enable_flow_and_Skills_through_the_library_engine()
    {
        await using var a = await TestHost.StartAsync();
        var provider = new Provider
        {
            Id = "p1", Name = "Alpha", AuthScheme = AuthSchemes.None,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIResponses],
        };
        await a.Db.Providers.InsertAsync(provider);
        await a.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = "p1", ModelId = "gpt-5", SystemModelId = "gpt-5" });
        var codexA = Path.Combine(a.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(codexA)!);
        await File.WriteAllTextAsync(codexA, "model = \"orig\"\n");
        Assert.Equal(HttpStatusCode.OK, (await a.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = "p1", model = "gpt-5" })).Status);

        var source = Path.Combine(a.Root, "source", "pdf");
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: pdf\ndescription: Does pdf\n---\nbody\n");
        await File.WriteAllTextAsync(Path.Combine(source, "scripts", "run.sh"), "echo hi\n");
        var (_, probe) = await a.SendAsync(HttpMethod.Post, "/api/library/skills/probe", new { kind = "local", source });
        var (st, install) = await a.SendAsync(HttpMethod.Post, "/api/library/skills/install", new { probeId = probe!["probeId"]!.GetValue<string>(), names = new[] { "pdf" }, clients = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.True(install![0]!["ok"]!.GetValue<bool>());

        var (content, _) = await ExportAsync(a, new { });

        await using var b = await TestHost.StartAsync();
        var codexB = Path.Combine(b.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(codexB)!);
        await File.WriteAllTextAsync(codexB, "model = \"b-orig\"\n");
        var (status, done) = await b.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "file", content });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(done!["errors"]!.AsArray());

        // the client was switched on by the ordinary enable path: its file now points at Astra and the original was recorded for restore
        Assert.True((await b.Db.Clients.GetAsync("codex"))!.Enabled);
        var config = await File.ReadAllTextAsync(codexB);
        Assert.Contains("astra", config, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(b.Db.ClientConfigState.List("codex"));
        // the Skill landed in the library with its files, and no client link was made behind the engine's back
        Assert.True(File.Exists(Path.Combine(b.Paths.LibrarySkillsDir, "pdf", "scripts", "run.sh")));
        Assert.NotNull(await b.Db.Library.GetSkillAsync("pdf"));
        Assert.Empty(await b.Db.Library.ListPlacementsAsync("pdf"));
    }

    [Fact]
    public async Task The_older_providers_only_backup_still_restores_through_the_new_endpoint()
    {
        await using var a = await TestHost.StartAsync();
        var (_, created) = await a.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Legacy", apiKey = "sk-legacy", models = new[] { "gpt-5" }, endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://l.example.invalid/v1" } },
        });
        Assert.NotNull(created);
        var (_, file) = await a.SendAsync(HttpMethod.Post, "/api/providers/backup/export", new { secrets = "plaintext" });

        await using var b = await TestHost.StartAsync();
        var (status, plan) = await b.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "file", content = file!.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(plan!["legacy"]!.GetValue<bool>());
        (status, var done) = await b.SendAsync(HttpMethod.Post, "/api/backup/restore", new { source = "file", content = file.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, done!["written"]!.GetValue<int>());
        Assert.Equal("Legacy", Assert.Single(await b.Db.Providers.ListAsync()).Name);
    }

    [Fact]
    public async Task Automatic_backups_are_kept_pruned_and_skipped_when_nothing_changed()
    {
        await using var host = await TestHost.StartAsync();
        await SeedAsync(host);
        var auto = host.App.Services.GetRequiredService<AutoBackupService>();
        await auto.SaveAsync(new AutoBackupSettings { Keep = 2, LastVersion = ServerOptions.Version }, default);

        Assert.NotNull(await auto.CreateAsync("auto", force: false, default));
        Assert.Null(await auto.CreateAsync("auto", force: false, default)); // same setup as the newest one
        Assert.NotNull(await auto.CreateAsync("auto", force: true, default));
        Assert.NotNull(await auto.CreateAsync("auto", force: true, default));
        Assert.Equal(2, auto.List().Count(f => f.Kind == "auto")); // the oldest went
        foreach (var f in auto.List()) Assert.DoesNotContain("ast-team-key", await File.ReadAllTextAsync(Path.Combine(auto.Dir, f.Name))); // no passphrase: no secret at all

        // a stranger's file in the folder is never listed, restored or pruned
        await File.WriteAllTextAsync(Path.Combine(auto.Dir, "notes.txt"), "mine");
        await auto.CreateAsync("auto", force: true, default);
        Assert.True(File.Exists(Path.Combine(auto.Dir, "notes.txt")));

        var newest = auto.List().First();
        var (status, plan) = await host.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "auto", name = newest.Name });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(plan);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/backup/preview", new { source = "auto", name = "../../etc/passwd" })).Status);
    }

    [Fact]
    public async Task Everything_that_writes_or_reads_a_backup_is_refused_off_this_machine()
    {
        await using var host = await TestHost.StartAsync();
        host.App.Services.GetRequiredService<ServerOptions>().AdminPasswordHash = PasswordHasher.Hash("correct-horse");
        await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { allowAdmin = true });
        var login = await host.Client.SendAsync(Remote(HttpMethod.Post, "/api/auth/login", new { password = "correct-horse" }));
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        async Task<HttpStatusCode> Send(HttpMethod method, string url, object? body = null)
        {
            using var req = Remote(method, url, body);
            req.Headers.Add("Cookie", cookie);
            return (await host.Client.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Get, "/api/backup/info"));
        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Get, "/api/sync/status"));
        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Post, "/api/backup/export", new { })); // a file without keys may be taken from anywhere
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/backup/export", new { includeKeys = true, passphrase = Pass }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/backup/preview", new { source = "file", content = "{}" }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/backup/restore", new { source = "file", content = "{}" }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Put, "/api/backup/auto", new { enabled = false }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/backup/auto/run"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Put, "/api/sync/config", new { target = new { kind = "folder", url = "/tmp/x" }, passphrase = Pass }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/sync/test"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/sync/now"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, "/api/sync/backup", new { passphrase = Pass }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Delete, "/api/sync/config"));
    }

    private static HttpRequestMessage Remote(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cf-Connecting-IP", "203.0.113.7");
        req.Headers.Add("Cf-Ray", "8a1b2c3d4e5f-LAX");
        req.Headers.Add("Cf-Visitor", "{\"scheme\":\"https\"}");
        if (body is not null) req.Content = JsonContent.Create(body, options: TestHost.JsonOptions);
        return req;
    }
}
