using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Core.Models;
using Astra.Core.Plugins;
using Astra.Core.Requests;
using Astra.Server.Alerts;
using Astra.Server.PluginsV2;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// The v2 host events about providers, accounts and alerts, and the request data views a plugin reads
/// (design/plugin-v2 09 §4.1, §4.2), through a real plugin.
/// </summary>
public class PluginV2HostEventsTests
{
    private const string Main = """
        import { definePlugin, events, storage, commands, data, env } from 'astra';
        const names = ['provider.changed', 'account.changed', 'alert.fired'];
        export default definePlugin({
          activate() {
            for (const n of names) events.on(n, async (e) => { const l = (await storage.get(n)) ?? []; l.push(e); await storage.set(n, l); });
            commands.register('seen', async (a) => (await storage.get(a.name)) ?? []);
            commands.register('stats', (a) => data.requests.stats(a));
            commands.register('query', (a) => data.requests.query(a));
            commands.register('zone', () => ({ timeZone: env.timeZone, utcOffsetMinutes: env.utcOffsetMinutes, locale: env.locale }));
          },
        });
        """;

    private static readonly string[] Permissions = ["storage", "events", "data.requests:read", "data.providers:read"];

    private static async Task<TestHost> StartAsync()
    {
        PluginDataEvents.Window = TimeSpan.FromMilliseconds(50);
        var host = await TestHost.StartAsync();
        var dir = Path.Combine(host.Root, "plugin-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), Main);
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), $$"""
            {
              "manifestVersion": 2, "id": "acme.watch", "version": "2.0.0", "name": "Watch", "main": "main.js",
              "permissions": [{{string.Join(", ", Permissions.Select(p => $"\"{p}\""))}}],
              "contributes": { "commands": [{ "id": "seen" }, { "id": "stats" }, { "id": "query" }, { "id": "zone" }] }
            }
            """);
        await host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "acme.watch", Source = PluginSources.Path });
        host.App.Services.GetRequiredService<Astra.Plugins.PluginCatalog>().Invalidate();
        await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.watch/grant", new { permissions = Permissions });
        return host;
    }

    private static async Task<JsonNode> RunAsync(TestHost host, string command, object args)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/plugins/v2/acme.watch/commands/{command}", new { args });
        Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
        return body!["result"]!;
    }

    private static async Task<JsonArray> SeenAsync(TestHost host, string name, int atLeast)
    {
        // generous: under a loaded test run the debounce, the event and each command round trip are slow
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var seen = (await RunAsync(host, "seen", new { name })).AsArray();
            if (seen.Count >= atLeast) return seen;
            await Task.Delay(50);
        }
        throw new TimeoutException($"{name}: fewer than {atLeast} events arrived");
    }

    [Fact]
    public async Task Provider_and_account_changes_reach_a_subscribed_plugin_once_per_burst()
    {
        await using var host = await StartAsync();
        await RunAsync(host, "seen", new { name = "provider.changed" }); // activates the plugin: its listeners are on

        await host.Db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Upstream", AuthScheme = AuthSchemes.OAuthSubscription });
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "a1", ProviderId = "p1", DisplayName = "display-secret", AccessTokenEnc = "secret-token" });
        await host.Db.Accounts.SetEnabledAsync("a1", false);
        await host.Db.Accounts.SetCooldownAsync("a1", DateTimeOffset.UtcNow.AddMinutes(5), "429");

        var provider = Assert.Single(await SeenAsync(host, "provider.changed", 1))!;
        Assert.Equal("p1", provider["providerId"]!.GetValue<string>());
        Assert.Equal("created", provider["change"]!.GetValue<string>());
        Assert.Equal("Upstream", provider["name"]!.GetValue<string>());

        // three writes in one burst: one event, "created" wins, the payload is the row as it is now
        var account = Assert.Single(await SeenAsync(host, "account.changed", 1))!;
        Assert.Equal("created", account["change"]!.GetValue<string>());
        Assert.Equal("p1", account["providerId"]!.GetValue<string>());
        Assert.False(account["enabled"]!.GetValue<bool>());
        Assert.NotNull(account["cooldownUntil"]);
        Assert.DoesNotContain("secret-token", account.ToJsonString());
        Assert.DoesNotContain("display-secret", account.ToJsonString());

        // a token refresh is not a change plugins hear about; a deletion is
        await host.Db.Accounts.UpdateTokensAsync("a1", "new-secret", null, DateTimeOffset.UtcNow.AddHours(1));
        await host.Db.Accounts.DeleteAsync("a1");
        var after = await SeenAsync(host, "account.changed", 2);
        Assert.Equal(2, after.Count);
        Assert.Equal("deleted", after[1]!["change"]!.GetValue<string>());
        Assert.Equal("a1", after[1]!["accountId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_alert_that_notifies_reaches_the_plugin_without_a_webhook()
    {
        await using var host = await StartAsync();
        await RunAsync(host, "seen", new { name = "alert.fired" });
        var (status, rule) = await host.SendAsync(HttpMethod.Post, "/api/alerts/rules", new
        {
            name = "Low balance", @event = AlertEventIds.ProviderBalanceLow, condition = new { value = 5 }, notifyResolved = true, cooldownMinutes = 0,
        });
        Assert.True(status == HttpStatusCode.OK, rule?.ToJsonString());
        var engine = host.App.Services.GetRequiredService<AlertEngine>();
        static AlertSignal Balance(double remaining) => new(AlertSubjectTypes.Provider, "p1", "DeepSeek", new JsonObject
        {
            ["quota"] = new JsonObject
            {
                ["isValid"] = true,
                ["plans"] = new JsonArray(new JsonObject { ["name"] = "CNY", ["unit"] = "CNY", ["remaining"] = remaining }),
            },
        });

        await engine.ProcessAsync(Balance(3), CancellationToken.None);
        await engine.ProcessAsync(Balance(50), CancellationToken.None);
        var fired = await SeenAsync(host, "alert.fired", 2);
        Assert.Equal("firing", fired[0]!["state"]!.GetValue<string>());
        Assert.Equal("resolved", fired[1]!["state"]!.GetValue<string>());
        Assert.Equal(rule!["id"]!.GetValue<string>(), fired[0]!["ruleId"]!.GetValue<string>());
        Assert.Equal(AlertEventIds.ProviderBalanceLow, fired[0]!["event"]!.GetValue<string>());
        Assert.Equal("DeepSeek", fired[0]!["subject"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(fired[0]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Request_data_has_times_cache_tokens_and_groups()
    {
        await using var host = await StartAsync();
        var day = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        RequestRecord Row(string id, DateTimeOffset at, string model, long cost) => new()
        {
            Id = id, StartedAtUtc = at, InboundProtocol = "anthropic", RequestedModel = model, ClientKind = "claude-code", Stream = true,
            TotalInputTokens = 100, TotalOutputTokens = 10, CacheReadTokens = 80, CacheWriteTokens = 5, CostNanoUsd = cost,
            ClientIp = "203.0.113.9", UserAgent = "secret-agent", ErrorMessage = "upstream said no",
        };
        await host.Db.Requests.InsertBatchAsync([
            Row("r1", day, "big", 3_000), Row("r2", day.AddHours(1), "small", 1_000), Row("r3", day.AddDays(1), "big", 5_000),
        ]);
        var range = new { from = day.AddDays(-1).ToString("O"), to = day.AddDays(2).ToString("O") };

        var one = await RunAsync(host, "query", new { id = "r2" });
        Assert.Equal(1, one["total"]!.GetValue<int>());
        var row = one["items"]![0]!;
        Assert.Equal(day.AddHours(1), DateTimeOffset.Parse(row["at"]!.GetValue<string>()));
        Assert.True(row["stream"]!.GetValue<bool>());
        Assert.Equal(80, row["cacheReadTokens"]!.GetValue<long>());
        foreach (var hidden in new[] { "203.0.113.9", "secret-agent", "upstream said no" }) Assert.DoesNotContain(hidden, one.ToJsonString());
        Assert.Equal(0, (await RunAsync(host, "query", new { id = "missing" }))["total"]!.GetValue<int>());

        var byDay = await RunAsync(host, "stats", new { range.from, range.to, groupBy = "day" });
        Assert.Equal(9_000, byDay["costNanoUsd"]!.GetValue<long>());
        Assert.Equal(240, byDay["cacheReadTokens"]!.GetValue<long>());
        var series = byDay["series"]!.AsArray();
        Assert.Equal(["2026-11-10", "2026-11-11"], series.Select(s => s!["bucket"]!.GetValue<string>()));
        Assert.Equal(4_000, series[0]!["costNanoUsd"]!.GetValue<long>());
        Assert.Equal(2, series[0]!["requests"]!.GetValue<long>());

        var byModel = (await RunAsync(host, "stats", new { range.from, range.to, groupBy = "model" }))["groups"]!.AsArray();
        Assert.Equal(["big", "small"], byModel.Select(g => g!["key"]!.GetValue<string>()));
        Assert.Equal(8_000, byModel[0]!["costNanoUsd"]!.GetValue<long>());

        var (bad, refused) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.watch/commands/stats", new { args = new { groupBy = "week" } });
        Assert.NotEqual(HttpStatusCode.OK, bad);
        Assert.Contains("groupBy", refused!.ToJsonString());
    }

    [Fact]
    public async Task Env_tells_the_usage_time_zone_and_language_and_follows_the_settings()
    {
        await using var host = await StartAsync();
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { usageTimeZone = "Asia/Shanghai" });
        var zone = await RunAsync(host, "zone", new { });
        Assert.Equal("Asia/Shanghai", zone["timeZone"]!.GetValue<string>());
        Assert.Equal(480, zone["utcOffsetMinutes"]!.GetValue<int>());
        Assert.Equal("zh", zone["locale"]!.GetValue<string>());
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { usageTimeZone = "UTC", locale = "en" });
        zone = await RunAsync(host, "zone", new { });
        Assert.Equal(0, zone["utcOffsetMinutes"]!.GetValue<int>());
        Assert.Equal("en", zone["locale"]!.GetValue<string>());
    }
}

/// <summary>v2 plugins' http.fetch through a proxy on this machine (a common setup): the hop to the proxy is allowed.</summary>
public class PluginV2ProxyHopTests
{
    [Fact]
    public void Only_the_configured_proxy_for_that_request_counts_as_a_proxy_hop()
    {
        var proxy = new System.Net.WebProxy("http://127.0.0.1:52429") { BypassList = [@"^https://bypassed\.example"] };
        var target = new Uri("https://status.claude.com/api/v2/summary.json");
        Assert.True(PluginV2HostServices.IsProxyHop(target, new System.Net.DnsEndPoint("127.0.0.1", 52429), proxy));
        Assert.False(PluginV2HostServices.IsProxyHop(target, new System.Net.DnsEndPoint("127.0.0.1", 8080), proxy));
        Assert.False(PluginV2HostServices.IsProxyHop(target, new System.Net.DnsEndPoint("10.0.0.1", 52429), proxy));
        Assert.False(PluginV2HostServices.IsProxyHop(new Uri("https://bypassed.example/x"), new System.Net.DnsEndPoint("127.0.0.1", 52429), proxy));
        Assert.False(PluginV2HostServices.IsProxyHop(target, new System.Net.DnsEndPoint("127.0.0.1", 52429), null));

        // an https request's CONNECT: the connection being opened is to the proxy itself, even when NO_PROXY lists 127.0.0.1
        var env = new System.Net.WebProxy("http://127.0.0.1:52429") { BypassList = [@"^https?://127\.0\.0\.1"] };
        Assert.True(PluginV2HostServices.IsProxyHop(new Uri("http://127.0.0.1:52429/"), new System.Net.DnsEndPoint("127.0.0.1", 52429), env));
        // a plugin asking for some other address on this machine is not a proxy hop
        Assert.False(PluginV2HostServices.IsProxyHop(new Uri("http://127.0.0.1:6379/"), new System.Net.DnsEndPoint("127.0.0.1", 6379), env));
    }
}
