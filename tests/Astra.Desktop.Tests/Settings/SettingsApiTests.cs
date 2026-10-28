using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Tests.Api;

namespace Astra.Desktop.Tests.Settings;

public class SettingsApiTests
{
    private const string SettingsJson = """
        {"locale":"zh","debugBodies":false,"bodyRetentionDays":7,"requestRetentionDays":90,"effortBudgets":{"low":1,"medium":2,"high":3},
         "streamIdleTimeoutSec":120,"updateChannel":"stable","updateAutoCheck":true,"port":1,"host":"127.0.0.1","dataDir":"/d","gatewayBaseUrl":"http://127.0.0.1:1",
         "quotaAutoIntervalMinutes":30,"subscriptionQuotaIntervalMinutes":30,"proxyMode":"system","proxyUrl":null,"proxyUsername":null,"hasProxyPassword":false,
         "proxyBypass":null,"requireGatewayKey":true,"pluginMarketUrl":null,"pluginMarketDefaultUrl":"https://x","usageTimeZone":null,"effectiveUsageTimeZone":"UTC",
         "requestCompression":"default"}
        """;

    private static (SettingsApi Api, QueryClient Queries) Make(FakeServer server)
    {
        var client = new ApiClient(server.Url);
        var queries = new QueryClient();
        return (new SettingsApi(client, queries, new SettingsStore(queries, client), new ProvidersStore(queries, client), new TokensStore(queries, client)), queries);
    }

    [Fact]
    public async Task Patch_settings_sends_only_the_given_keys_with_the_admin_header_and_caches_the_answer()
    {
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, SettingsJson.Replace("\"debugBodies\":false", "\"debugBodies\":true")));
        var (api, _) = Make(server);
        var saved = await api.PatchSettingsAsync(JsonPatch.Of(("debugBodies", true), ("requestRetentionDays", null)));
        Assert.True(saved.DebugBodies);
        var seen = Assert.Single(server.Requests);
        Assert.Equal("PATCH", seen.Method);
        Assert.Equal("/api/settings", seen.Url);
        Assert.Equal("1", seen.Admin);
        Assert.Equal("""{"debugBodies":true,"requestRetentionDays":null}""", seen.Body);
        await Task.Delay(50);
        Assert.True(api.Settings.Data?.DebugBodies);
    }

    [Fact]
    public async Task Tunnel_toggle_posts_start_or_stop_and_caches_the_status()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(TunnelLogicTests.Status(enabled: true, state: "starting"), Astra.Core.JsonContexts.Info<TunnelStatusDto>(Astra.Core.Json.Api));
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, body));
        var (api, _) = Make(server);
        var status = await api.ToggleTunnelAsync(true);
        Assert.Equal("starting", status.State);
        await api.ToggleTunnelAsync(false);
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.Url == "/api/tunnel/start");
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.Url == "/api/tunnel/stop");
    }

    [Fact]
    public async Task Webhook_writes_refresh_the_alert_queries()
    {
        var hook = """{"id":"w1","name":"Ops","url":"https://h","format":"generic","enabled":true,"timeoutSec":10,"useProxy":true,"hasSecret":true,"headerNames":[],"createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T00:00:00Z"}""";
        var listCalls = 0;
        using var server = new FakeServer(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (ctx.Request.HttpMethod == "GET" && path == "/api/webhooks") { Interlocked.Increment(ref listCalls); return FakeServer.Json(ctx, 200, $"[{hook}]"); }
            if (ctx.Request.HttpMethod == "POST" && path == "/api/webhooks") return FakeServer.Json(ctx, 200, $$"""{"webhook":{{hook}},"secret":"s3cret"}""");
            return FakeServer.Json(ctx, 404, "{}");
        });
        var (api, _) = Make(server);
        using var observe = api.Webhooks.Activate();
        await api.Webhooks.RefetchAsync();
        Assert.Single(api.Webhooks.Data!);
        var created = await api.CreateWebhookAsync(new WebhookDraft(null) { Name = "Ops", Url = "https://h" }.CreateBody());
        Assert.Equal("s3cret", created.Secret);
        await Task.Delay(300);
        Assert.True(Volatile.Read(ref listCalls) >= 2, "the list was refetched after the create");
    }

    [Fact]
    public async Task Deliveries_and_accounts_queries_use_the_web_keys_and_paths()
    {
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, "[]"));
        var (api, _) = Make(server);
        var d = api.Deliveries("a b");
        await d.RefetchAsync();
        await api.Accounts("prov").RefetchAsync();
        Assert.Contains(server.Requests, r => r.Url == "/api/webhooks/a%20b/deliveries");
        Assert.Contains(server.Requests, r => r.Url == "/api/providers/prov/accounts");
        Assert.Same(d, api.Deliveries("a b"));
    }
}
