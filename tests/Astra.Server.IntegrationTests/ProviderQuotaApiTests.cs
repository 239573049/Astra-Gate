using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Providers.Quota;
using Astra.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Astra.Server.IntegrationTests;

/// <summary>Balance / quota queries of API-key providers: configuration, query, failure handling and the worker.</summary>
public class ProviderQuotaApiTests
{
    private sealed class Upstream : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string? NewApiUser)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("New-Api-User", out var user) ? user.Single() : null));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private static Task<TestHost> StartAsync(Upstream upstream) =>
        TestHost.StartAsync(b => b.Services.AddHttpClient(ProviderQuotaService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

    private static async Task<string> CreateAsync(TestHost host, object body)
    {
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", body);
        Assert.True(status == HttpStatusCode.OK, created?.ToJsonString());
        return created!["id"]!.GetValue<string>();
    }

    private const string DeepSeekBalance =
        """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}""";

    [Fact]
    public async Task Query_Is_Off_By_Default_Then_Stores_Snapshots_And_Keeps_The_Last_Good_One_On_Failure()
    {
        var upstream = new Upstream { Body = DeepSeekBalance };
        await using var host = await StartAsync(upstream);
        var id = await CreateAsync(host, new { templateId = "deepseek", apiKey = "sk-deepseek-secret-1" });

        var provider = await host.GetJsonAsync($"/api/providers/{id}");
        Assert.False(provider["quotaConfig"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("deepseek", provider["quotaConfig"]!["suggestedTemplate"]!.GetValue<string>());
        Assert.Equal("deepseek", provider["quotaConfig"]!["effectiveTemplate"]!.GetValue<string>());
        Assert.Equal(30, provider["quotaConfig"]!["effectiveIntervalMinutes"]!.GetValue<int>());
        Assert.Null(provider["quota"]);
        Assert.False(provider["settings"]!.AsObject().ContainsKey("quota"));

        var (status, config) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { enabled = true, intervalMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(config!["config"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(15, config["config"]!["effectiveIntervalMinutes"]!.GetValue<int>());

        var (fetchStatus, fetched) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.OK, fetchStatus);
        var (url, auth, _) = Assert.Single(upstream.Requests);
        Assert.Equal("https://api.deepseek.com/user/balance", url);
        Assert.Equal("Bearer sk-deepseek-secret-1", auth);
        var plan = fetched!["snapshot"]!["plans"]![0]!;
        Assert.Equal(110, plan["remaining"]!.GetValue<double>());
        Assert.Equal("CNY", plan["unit"]!.GetValue<string>());
        Assert.NotNull(fetched["checkedAtUtc"]);

        // The upstream now refuses the key: the snapshot keeps the balance and records the failure.
        upstream.Status = HttpStatusCode.Unauthorized;
        upstream.Body = """{"error":{"message":"invalid key sk-deepseek-secret-1"}}""";
        (fetchStatus, fetched) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.OK, fetchStatus);
        var snapshot = fetched!["snapshot"]!;
        Assert.Equal(110, snapshot["plans"]![0]!["remaining"]!.GetValue<double>());
        Assert.Equal("unauthorized", snapshot["errorCode"]!.GetValue<string>());
        Assert.Equal(1, snapshot["failures"]!.GetValue<int>());
        Assert.DoesNotContain("sk-deepseek-secret-1", fetched.ToJsonString());

        // A failed balance query says nothing about the provider itself: it stays enabled.
        provider = await host.GetJsonAsync($"/api/providers/{id}");
        Assert.True(provider["enabled"]!.GetValue<bool>());
        Assert.Equal("unauthorized", provider["quota"]!["errorCode"]!.GetValue<string>());

        // Recovery wipes the error.
        upstream.Status = HttpStatusCode.OK;
        upstream.Body = DeepSeekBalance;
        (_, fetched) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Null(fetched!["snapshot"]!["error"]);
        Assert.Null(fetched["snapshot"]!["failures"]);
    }

    [Fact]
    public async Task New_Api_Secrets_Are_Encrypted_Never_Returned_And_Survive_Settings_Edits()
    {
        var upstream = new Upstream { Body = """{"success":true,"data":{"group":"vip","quota":5000000,"used_quota":2500000}}""" };
        await using var host = await StartAsync(upstream);
        var id = await CreateAsync(host, new
        {
            name = "Relay",
            templateId = "custom-chat",
            endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://relay.example.com/v1" } },
            apiKey = "sk-relay-key-0001",
            models = Array.Empty<string>(),
        });

        var (status, saved) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new
        {
            enabled = true, template = "newapi", @params = new { userId = "42" }, secrets = new { accessToken = "sys-token-abcdef" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["accessToken"], saved!["config"]!["secrets"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        Assert.DoesNotContain("sys-token-abcdef", saved.ToJsonString());
        Assert.DoesNotContain("sys-token-abcdef", (await host.GetJsonAsync("/api/providers")).ToJsonString());
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.DoesNotContain("sys-token-abcdef", stored!.Settings.ToJsonString());
        Assert.NotNull(stored.Settings["quota"]!["secrets"]!["accessToken"]);

        // A settings edit from the UI (which never sees `quota`) keeps the stored quota config.
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { settings = new { auto_cache_control = true } });
        Assert.Equal(HttpStatusCode.OK, status);
        stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("newapi", stored!.Settings["quota"]!["template"]!.GetValue<string>());
        Assert.True(stored.Settings["auto_cache_control"]!.GetValue<bool>());

        (status, var fetched) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.OK, status);
        var request = Assert.Single(upstream.Requests);
        Assert.Equal("https://relay.example.com/api/user/self", request.Url);
        Assert.Equal("Bearer sys-token-abcdef", request.Authorization);
        Assert.Equal("42", request.NewApiUser);
        Assert.Equal(10, fetched!["snapshot"]!["plans"]![0]!["remaining"]!.GetValue<double>());
        Assert.Equal("vip", fetched["snapshot"]!["planLabel"]!.GetValue<string>());

        // Clearing a secret is an explicit empty string; omitting it keeps it.
        (_, saved) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { intervalMinutes = 0 });
        Assert.Single(saved!["config"]!["secrets"]!.AsArray());
        (_, saved) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { secrets = new { accessToken = "" } });
        Assert.Empty(saved!["config"]!["secrets"]!.AsArray());
        (status, var error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("accessToken", error!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test_Runs_An_Unsaved_Custom_Query_And_Redacts_The_Raw_Response()
    {
        var upstream = new Upstream { Body = """{"data":{"left":"7.5","owner":"sk-custom-key-7777"}}""" };
        await using var host = await StartAsync(upstream);
        var id = await CreateAsync(host, new
        {
            name = "Custom",
            endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://custom.example.com/v1" } },
            apiKey = "sk-custom-key-7777",
            models = Array.Empty<string>(),
        });

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota/test", new
        {
            template = "custom",
            request = new { method = "GET", url = "{{baseUrl}}/dashboard/balance", auth = "provider" },
            extract = new { unit = new Dictionary<string, string> { ["$const"] = "USD" }, remaining = "data.left" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("https://custom.example.com/v1/dashboard/balance", result["url"]!.GetValue<string>());
        Assert.Equal(7.5, result["snapshot"]!["plans"]![0]!["remaining"]!.GetValue<double>());
        Assert.Equal("••••", result["raw"]!["data"]!["owner"]!.GetValue<string>());

        // Nothing was saved by the test run.
        var provider = await host.GetJsonAsync($"/api/providers/{id}");
        Assert.Null(provider["quota"]);
        Assert.Null(provider["quotaConfig"]!["template"]);

        // Invalid configurations are refused with 400 before anything is sent.
        upstream.Requests.Clear();
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota/test",
            new { template = "custom", request = new { url = "{{origin}}/x" }, extract = new Dictionary<string, object> { ["remaining"] = new Dictionary<string, string> { ["$eval"] = "1" } } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { template = "no-such-template" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { template = "custom" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(upstream.Requests);

        // No built-in template matches this host, so an unconfigured query is a configuration error.
        (status, var error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("config", error!["details"]!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Subscription_Providers_Use_Their_Account_Quota_Instead()
    {
        await using var host = await StartAsync(new Upstream());
        var id = await CreateAsync(host, new { templateId = "claude-subscription" });
        var (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/quota");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, $"/api/providers/{id}/quota/config", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Worker_Queries_Only_Due_Enabled_Providers_And_Honors_The_Global_Interval()
    {
        var upstream = new Upstream { Body = DeepSeekBalance };
        await using var host = await StartAsync(upstream);
        var on = await CreateAsync(host, new { templateId = "deepseek", apiKey = "sk-on-000001" });
        var off = await CreateAsync(host, new { templateId = "deepseek", apiKey = "sk-off-00001" });
        var disabled = await CreateAsync(host, new { templateId = "deepseek", apiKey = "sk-dis-00001" });
        await host.SendAsync(HttpMethod.Put, $"/api/providers/{on}/quota/config", new { enabled = true });
        await host.SendAsync(HttpMethod.Put, $"/api/providers/{disabled}/quota/config", new { enabled = true });
        await host.SendAsync(HttpMethod.Patch, $"/api/providers/{disabled}", new { enabled = false });

        var worker = host.App.Services.GetServices<IHostedService>().OfType<ProviderQuotaWorker>().Single();
        Assert.Equal(1, await worker.RefreshDueAsync(CancellationToken.None));
        Assert.Equal("Bearer sk-on-000001", Assert.Single(upstream.Requests).Authorization);
        Assert.NotNull((await host.Db.Providers.GetAsync(on))!.Quota);
        Assert.Null((await host.Db.Providers.GetAsync(off))!.Quota);
        Assert.Null((await host.Db.Providers.GetAsync(disabled))!.Quota);

        // Just queried: not due again within the interval.
        Assert.Equal(0, await worker.RefreshDueAsync(CancellationToken.None));

        // 0 globally turns the background refresh off (and the settings API reports it).
        var (status, settings) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { quotaAutoIntervalMinutes = 0 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, settings!["quotaAutoIntervalMinutes"]!.GetValue<int>());
        await host.Db.Providers.UpdateQuotaAsync(on, null, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(0, await worker.RefreshDueAsync(CancellationToken.None));

        // ...unless the provider sets its own interval.
        await host.SendAsync(HttpMethod.Put, $"/api/providers/{on}/quota/config", new { intervalMinutes = 5 });
        Assert.Equal(1, await worker.RefreshDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Built_In_Templates_Are_Listed()
    {
        await using var host = await StartAsync(new Upstream());
        var templates = (await host.GetJsonAsync("/api/provider-quota/templates")).AsArray();
        var newApi = templates.Single(t => t!["id"]!.GetValue<string>() == "newapi")!;
        Assert.Equal("balance", newApi["kind"]!.GetValue<string>());
        Assert.Contains(newApi["params"]!.AsArray(), p => p!["name"]!.GetValue<string>() == "accessToken" && p["secret"]!.GetValue<bool>());
        Assert.Contains(templates, t => t!["id"]!.GetValue<string>() == "glm-coding" && t["kind"]!.GetValue<string>() == "plan");
    }
}
