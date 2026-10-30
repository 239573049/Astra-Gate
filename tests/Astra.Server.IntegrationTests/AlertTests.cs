using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Server.Alerts;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>A webhook endpoint: records every call and answers with <see cref="Status"/>.</summary>
public sealed class WebhookReceiver : HttpMessageHandler
{
    public sealed record Call(Uri Url, Dictionary<string, string> Headers, string Body)
    {
        public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
    }

    public List<Call> Calls { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Response body; null = "ok:&lt;status&gt;" as text.</summary>
    public string? ResponseBody { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.Concat(request.Content!.Headers)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = await request.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add(new Call(request.RequestUri!, headers, body));
        return new HttpResponseMessage(Status)
        {
            Content = ResponseBody is { } json
                ? new StringContent(json, Encoding.UTF8, "application/json")
                : new StringContent("ok:" + Status, Encoding.UTF8, "text/plain"),
        };
    }

    public int Count
    {
        get
        {
            lock (Calls) return Calls.Count;
        }
    }
}

public class AlertTests
{
    private static async Task<(TestHost Host, WebhookReceiver Receiver)> StartAsync(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? more = null)
    {
        var receiver = new WebhookReceiver();
        var host = await TestHost.StartAsync(b =>
        {
            b.Services.AddHttpClient(WebhookDispatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            b.Services.AddHttpClient(WebhookDispatcher.DirectHttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            more?.Invoke(b);
        });
        return (host, receiver);
    }

    private static async Task<(string Id, string? Secret)> WebhookAsync(TestHost host, object? body = null)
    {
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/webhooks", body ?? new { name = "Ops", url = "https://hooks.test/astra" });
        Assert.True(status == HttpStatusCode.OK, created?.ToJsonString());
        return (created!["webhook"]!["id"]!.GetValue<string>(), created["secret"]?.GetValue<string>());
    }

    private static async Task<string> RuleAsync(TestHost host, object body)
    {
        var (status, rule) = await host.SendAsync(HttpMethod.Post, "/api/alerts/rules", body);
        Assert.True(status == HttpStatusCode.OK, rule?.ToJsonString());
        return rule!["id"]!.GetValue<string>();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    private static AlertSignal Provider(string id, double remaining) => new(AlertSubjectTypes.Provider, id, "DeepSeek",
        new JsonObject
        {
            ["quota"] = new JsonObject
            {
                ["isValid"] = true,
                ["plans"] = new JsonArray(new JsonObject { ["name"] = "CNY", ["unit"] = "CNY", ["remaining"] = remaining }),
            },
        });

    [Fact]
    public async Task Webhook_Api_Never_Echoes_Secrets_Or_Header_Values()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (id, secret) = await WebhookAsync(host, new { name = "Ops", url = "https://hooks.test/a", headers = new Dictionary<string, string> { ["Authorization"] = "Bearer hook-credential" } });
        Assert.StartsWith("whsec_", secret);
        var listed = (await host.GetJsonAsync("/api/webhooks")).AsArray();
        var hook = Assert.Single(listed)!;
        Assert.True(hook["hasSecret"]!.GetValue<bool>());
        Assert.Equal("Authorization", hook["headerNames"]![0]!.GetValue<string>());
        Assert.DoesNotContain("hook-credential", listed.ToJsonString());
        Assert.DoesNotContain(secret!, listed.ToJsonString());

        foreach (var bad in new object[]
                 {
                     new { name = "x", url = "ftp://hooks.test" },
                     new { name = "", url = "https://hooks.test" },
                     new { name = "x", url = "https://hooks.test", headers = new Dictionary<string, string> { ["X-Astra-Event"] = "spoof" } },
                     new { name = "x", url = "https://hooks.test", headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" } },
                     new { name = "x", url = "https://hooks.test", timeoutSec = 0 },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/webhooks", bad)).Status);

        // An empty header value keeps the stored one; signed=false drops the secret.
        var (status, patched) = await host.SendAsync(HttpMethod.Patch, $"/api/webhooks/{id}",
            new { headers = new Dictionary<string, string> { ["Authorization"] = "" }, signed = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(patched!["hasSecret"]!.GetValue<bool>());
        var stored = (await host.Db.Alerts.GetWebhookAsync(id))!;
        Assert.Equal("Bearer hook-credential", host.App.Services.GetRequiredService<WebhookDispatcher>().CustomHeaders(stored)["Authorization"]);

        var (_, reset) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/secret");
        Assert.StartsWith("whsec_", reset!["secret"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, $"/api/webhooks/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Delete, $"/api/webhooks/{id}")).Status);
    }

    [Fact]
    public async Task A_Test_Delivery_Is_Signed_And_Not_Retried()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        var (id, secret) = await WebhookAsync(host, new { name = "Ops", url = "https://hooks.test/a", headers = new Dictionary<string, string> { ["X-Team"] = "infra" } });

        var (status, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("success", delivery!["status"]!.GetValue<string>());
        Assert.Equal(200, delivery["httpStatus"]!.GetValue<int>());
        var call = Assert.Single(receiver.Calls);
        Assert.Equal("test", call.Headers["x-astra-event"]);
        Assert.Equal(delivery["id"]!.GetValue<string>(), call.Headers["x-astra-delivery"]);
        Assert.Equal("infra", call.Headers["x-team"]);
        Assert.StartsWith("astra/", call.Headers["user-agent"]);
        var timestamp = long.Parse(call.Headers["x-astra-timestamp"]);
        Assert.Equal(WebhookDispatcher.Sign(secret!, timestamp, call.Body), call.Headers["x-astra-signature"]);
        Assert.Equal("test", call.Json["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(call.Json["message"]!.GetValue<string>()));

        receiver.Status = HttpStatusCode.InternalServerError;
        (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Equal("failed", delivery!["status"]!.GetValue<string>());
        Assert.Equal(1, delivery["attempts"]!.GetValue<int>());
        Assert.Equal(2, (await host.GetJsonAsync($"/api/webhooks/{id}/deliveries")).AsArray().Count);
    }

    [Fact]
    public async Task A_Rule_Notifies_Once_Then_Resolves()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        var (hook, _) = await WebhookAsync(host);
        var ruleId = await RuleAsync(host, new
        {
            name = "Low balance", @event = AlertEventIds.ProviderBalanceLow, condition = new { value = 5 },
            webhookIds = new[] { hook }, notifyResolved = true, cooldownMinutes = 0,
        });
        var engine = host.App.Services.GetRequiredService<AlertEngine>();

        await engine.ProcessAsync(Provider("p1", 3.2), CancellationToken.None);
        await engine.ProcessAsync(Provider("p1", 2), CancellationToken.None); // still firing: no second notification
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 1), "the firing notification");
        var firing = receiver.Calls[0].Json;
        Assert.Equal(AlertEventIds.ProviderBalanceLow, firing["event"]!.GetValue<string>());
        Assert.Equal("firing", firing["status"]!.GetValue<string>());
        Assert.Equal(ruleId, firing["rule"]!["id"]!.GetValue<string>());
        Assert.Equal("p1", firing["subject"]!["id"]!.GetValue<string>());
        Assert.Contains("低于阈值", firing["message"]!.GetValue<string>());

        var rule = Assert.Single((await host.GetJsonAsync("/api/alerts/rules")).AsArray())!;
        Assert.Equal(1, rule["firing"]!.GetValue<int>());
        var state = Assert.Single((await host.GetJsonAsync("/api/alerts/states?firing=true")).AsArray())!;
        Assert.Equal("DeepSeek", state["subjectName"]!.GetValue<string>());
        Assert.Equal("provider", state["subjectType"]!.GetValue<string>());

        await engine.ProcessAsync(Provider("p1", 50), CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 2), "the resolved notification");
        Assert.Equal("resolved", receiver.Calls[1].Json["status"]!.GetValue<string>());
        Assert.Empty((await host.GetJsonAsync("/api/alerts/states?firing=true")).AsArray());
        // Undecidable observations keep the state (a provider without a snapshot says nothing).
        await engine.ProcessAsync(new AlertSignal(AlertSubjectTypes.Provider, "p1", "DeepSeek", new JsonObject { ["quota"] = null }), CancellationToken.None);
        Assert.Equal("ok", Assert.Single((await host.GetJsonAsync("/api/alerts/states")).AsArray())!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cooldown_Mutes_Flapping_And_Repeat_Renotifies()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        var (hook, _) = await WebhookAsync(host);
        await RuleAsync(host, new { name = "Low", @event = AlertEventIds.ProviderBalanceLow, condition = new { value = 5 }, webhookIds = new[] { hook }, cooldownMinutes = 60 });
        var engine = host.App.Services.GetRequiredService<AlertEngine>();
        await engine.ProcessAsync(Provider("p1", 1), CancellationToken.None);
        await engine.ProcessAsync(Provider("p1", 9), CancellationToken.None);
        await engine.ProcessAsync(Provider("p1", 1), CancellationToken.None); // within the cooldown
        await WaitUntilAsync(() => Task.FromResult(receiver.Count >= 1), "the first notification");
        await Task.Delay(300);
        Assert.Equal(1, receiver.Count);

        // Repeat: a firing notified longer ago than repeatMinutes goes out again.
        var rule = Assert.Single(await host.Db.Alerts.ListRulesAsync());
        await host.SendAsync(HttpMethod.Patch, $"/api/alerts/rules/{rule.Id}", new { repeatMinutes = 1 });
        await engine.ProcessAsync(Provider("p1", 1), CancellationToken.None); // the edit is not a condition change: state kept
        var stored = Assert.Single(await host.Db.Alerts.ListStatesAsync());
        stored.ChangedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        stored.LastNotifiedAt = stored.ChangedAt;
        await host.Db.Alerts.UpsertStateAsync(stored);
        engine.Invalidate();
        await engine.ProcessAsync(Provider("p1", 1), CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 2), "the repeat");
        Assert.True(receiver.Calls[1].Json["repeat"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Failed_Deliveries_Back_Off_And_Can_Be_Redelivered()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        receiver.Status = HttpStatusCode.BadGateway;
        var (hook, _) = await WebhookAsync(host);
        await RuleAsync(host, new { name = "Low", @event = AlertEventIds.ProviderBalanceLow, condition = new { value = 5 }, webhookIds = new[] { hook } });
        await host.App.Services.GetRequiredService<AlertEngine>().ProcessAsync(Provider("p1", 1), CancellationToken.None);
        await WaitUntilAsync(async () => (await host.Db.Alerts.ListDeliveriesAsync(hook, 10)).Single().Attempts == 1, "the first attempt");
        var delivery = (await host.Db.Alerts.ListDeliveriesAsync(hook, 10)).Single();
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(502, delivery.HttpStatus);
        Assert.InRange((delivery.NextAttemptAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 30, 61);

        receiver.Status = HttpStatusCode.OK;
        var (status, copy) = await host.SendAsync(HttpMethod.Post, $"/api/webhook-deliveries/{delivery.Id}/redeliver");
        Assert.Equal(HttpStatusCode.OK, status);
        var copyId = copy!["id"]!.GetValue<string>();
        await WaitUntilAsync(async () => (await host.Db.Alerts.GetDeliveryAsync(copyId))!.Status == WebhookDeliveryStatus.Success, "the redelivery");
        Assert.Equal(copyId, receiver.Calls[^1].Json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rules_Are_Validated_And_Events_Are_Listed()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var events = (await host.GetJsonAsync("/api/alerts/events")).AsArray();
        Assert.Equal(AlertEvents.All.Count, events.Count);
        Assert.Contains(events, e => e!["id"]!.GetValue<string>() == AlertEventIds.TokenUsageHigh && e["subjectType"]!.GetValue<string>() == "token");

        var (hook, _) = await WebhookAsync(host);
        foreach (var bad in new object[]
                 {
                     new { name = "x", @event = "nope" },
                     new { name = "x", @event = AlertEventIds.ProviderBalanceLow }, // value required
                     new { name = "x", @event = AlertEventIds.AccountQuotaHigh, condition = new { window = "daily" } },
                     new { name = "x", @event = AlertEventIds.AccountQuotaHigh, webhookIds = new[] { "missing" } },
                     new { name = "x", @event = AlertEventIds.AccountQuotaHigh, repeatMinutes = 0 },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/alerts/rules", bad)).Status);

        var id = await RuleAsync(host, new { name = "Accounts", @event = AlertEventIds.AccountQuotaHigh, subjects = new[] { "a1" }, webhookIds = new[] { hook } });
        var rule = Assert.Single((await host.GetJsonAsync("/api/alerts/rules")).AsArray())!;
        Assert.Equal(90, rule["condition"]!["value"]!.GetValue<double>());
        Assert.Equal("a1", rule["subjects"]![0]!.GetValue<string>());
        // Deleting the webhook unlinks it from the rule.
        await host.SendAsync(HttpMethod.Delete, $"/api/webhooks/{hook}");
        Assert.Empty((await host.Db.Alerts.GetRuleAsync(id))!.WebhookIds);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, $"/api/alerts/rules/{id}")).Status);
    }

    [Fact]
    public async Task The_Sweep_Reads_Stored_Provider_Snapshots()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        var provider = new Provider { Id = "p-sweep", Name = "Sweep", AuthScheme = AuthSchemes.Bearer };
        await host.Db.Providers.InsertAsync(provider);
        await host.Db.Providers.UpdateQuotaAsync(provider.Id,
            JsonNode.Parse("""{"isValid":true,"plans":[{"name":"USD","unit":"USD","remaining":0.5}]}""")!.AsObject(), DateTimeOffset.UtcNow);
        var (hook, _) = await WebhookAsync(host);
        await RuleAsync(host, new { name = "Low", @event = AlertEventIds.ProviderBalanceLow, condition = new { value = 1 }, webhookIds = new[] { hook } });

        await host.App.Services.GetRequiredService<AlertEngine>().SweepAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 1), "the sweep's notification");
        Assert.Equal("Sweep", receiver.Calls[0].Json["subject"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Token_Limit_Refusal_Fires_Right_Away()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(
            """{"id":"c","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""));
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{TokenIds.Default}",
            new { limits = new { budgets = Array.Empty<object>(), rpm = 1 } });
        Assert.Equal(HttpStatusCode.OK, status);
        await RuleAsync(gw.Host, new { name = "Limited", @event = AlertEventIds.TokenLimitReached });

        const string body = """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""";
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await gw.PostAsync("/v1/chat/completions", body)).StatusCode);
        await WaitUntilAsync(async () => (await gw.Host.Db.Alerts.ListStatesAsync()).Any(s => s.State == AlertStates.Firing), "the token alert");
        var state = Assert.Single(await gw.Host.Db.Alerts.ListStatesAsync());
        Assert.Equal(TokenIds.Default, state.SubjectId);
        Assert.Contains("rpm", state.ValueJson);
    }

    [Fact]
    public async Task Subscription_Accounts_Sync_In_The_Background_And_Count_Failures()
    {
        var fail = true;
        var calls = 0;
        var (host, receiver) = await StartAsync(b => b.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FakeUpstream(_ =>
            {
                Interlocked.Increment(ref calls);
                return fail
                    ? FakeUpstream.Json("""{"error":"overloaded"}""", HttpStatusCode.InternalServerError)
                    : FakeUpstream.Json("""{"five_hour":{"utilization":97},"seven_day":{"utilization":40}}""");
            })));
        await using var _host = host;
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = created!["id"]!.GetValue<string>();
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-sync", ProviderId = providerId, DisplayName = "me@example.com", Status = AccountStatus.Active,
            AccessTokenEnc = protector.Protect("at"), RefreshTokenEnc = protector.Protect("rt"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        });

        // The interval has a floor (rate-limited upstreams) and 0 turns it off.
        var (_, settings) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { subscriptionQuotaIntervalMinutes = 3 });
        Assert.Equal(SubscriptionQuotaSync.MinIntervalMinutes, settings!["subscriptionQuotaIntervalMinutes"]!.GetValue<int>());
        var sync = host.App.Services.GetRequiredService<SubscriptionQuotaSync>();

        Assert.Equal(1, await sync.RefreshDueAsync(10, CancellationToken.None));
        var account = (await host.Db.Accounts.GetAsync("acc-sync"))!;
        Assert.Equal(1, SubscriptionQuotaSync.Failures(account));
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Equal(0, await sync.RefreshDueAsync(10, CancellationToken.None)); // just tried

        var (hook, _) = await WebhookAsync(host);
        await RuleAsync(host, new { name = "Sync", @event = AlertEventIds.AccountQuotaFailed, condition = new { value = 1 }, webhookIds = new[] { hook } });
        await RuleAsync(host, new { name = "High", @event = AlertEventIds.AccountQuotaHigh, webhookIds = new[] { hook } });
        var engine = host.App.Services.GetRequiredService<AlertEngine>();
        await engine.SweepAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 1), "the sync-failure alert");
        Assert.Equal(AlertEventIds.AccountQuotaFailed, receiver.Calls[0].Json["event"]!.GetValue<string>());

        // Due again later; a success clears the failure state and the quota alert sees the fresh snapshot.
        fail = false;
        account.Extra[SubscriptionQuotaSync.SyncKey]!["checkedAtUtc"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        await host.Db.Accounts.UpdateAsync(account);
        Assert.Equal(1, await sync.RefreshDueAsync(10, CancellationToken.None));
        account = (await host.Db.Accounts.GetAsync("acc-sync"))!;
        Assert.Equal(0, SubscriptionQuotaSync.Failures(account));
        Assert.Equal(97, account.Extra["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
        await engine.SweepAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(receiver.Count == 2), "the quota alert");
        Assert.Equal(AlertEventIds.AccountQuotaHigh, receiver.Calls[1].Json["event"]!.GetValue<string>());
        Assert.True(calls >= 2);

        (_, settings) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { subscriptionQuotaIntervalMinutes = 0 });
        Assert.Equal(0, settings!["subscriptionQuotaIntervalMinutes"]!.GetValue<int>());
        Assert.Equal(0, await sync.RefreshDueAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Cooling_Accounts_Are_Rechecked_And_Released_Once_The_Quota_Has_Room()
    {
        var session = 100;
        var calls = 0;
        var (host, _) = await StartAsync(b => b.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FakeUpstream(_ =>
            {
                Interlocked.Increment(ref calls);
                return FakeUpstream.Json("{\"five_hour\":{\"utilization\":" + session + "},\"seven_day\":{\"utilization\":40}}");
            })));
        await using var _host = host;
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription" });
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-cool", ProviderId = created!["id"]!.GetValue<string>(), DisplayName = "me@example.com", Status = AccountStatus.Active,
            AccessTokenEnc = protector.Protect("at"), RefreshTokenEnc = protector.Protect("rt"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var sync = host.App.Services.GetRequiredService<SubscriptionQuotaSync>();

        // Not cooling: nothing to look at.
        Assert.Equal(0, await sync.RecheckCooledAsync(10, CancellationToken.None));

        await host.Db.Accounts.SetCooldownAsync("acc-cool", DateTimeOffset.UtcNow.AddHours(3), "429", CancellationToken.None);
        Assert.Equal(1, await sync.RecheckCooledAsync(10, CancellationToken.None));
        Assert.NotNull((await host.Db.Accounts.GetAsync("acc-cool"))!.CooldownUntilUtc); // the five hours are still used up
        Assert.Equal(0, await sync.RecheckCooledAsync(10, CancellationToken.None)); // just looked

        // The window has room again (a reset credit): the next look, five minutes on, lifts the cooldown.
        session = 20;
        var account = (await host.Db.Accounts.GetAsync("acc-cool"))!;
        account.Extra["quota"]!["fetchedAtUtc"] = DateTimeOffset.UtcNow.Subtract(SubscriptionQuotaSync.CooledRecheck).AddSeconds(-1).ToString("o");
        await host.Db.Accounts.UpdateAsync(account);
        Assert.Equal(1, await sync.RecheckCooledAsync(10, CancellationToken.None));
        account = (await host.Db.Accounts.GetAsync("acc-cool"))!;
        Assert.Null(account.CooldownUntilUtc);
        Assert.Equal(20, account.Extra["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(0, await sync.RecheckCooledAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Background_Quota_Rounds_Never_Renew_A_Token()
    {
        var quotaCalls = 0;
        var refreshCalls = 0;
        var (host, _) = await StartAsync(b =>
        {
            b.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeUpstream(_ =>
            {
                Interlocked.Increment(ref quotaCalls);
                return FakeUpstream.Json("""{"five_hour":{"utilization":10},"seven_day":{"utilization":40}}""");
            }));
            b.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeUpstream(_ =>
            {
                Interlocked.Increment(ref refreshCalls);
                return FakeUpstream.Json("""{"access_token":"at-renewed","refresh_token":"rt-renewed","expires_in":3600}""");
            }));
        });
        await using var _host = host;
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription" });
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-idle", ProviderId = created!["id"]!.GetValue<string>(), DisplayName = "me@example.com", Status = AccountStatus.Active,
            AccessTokenEnc = protector.Protect("at"), RefreshTokenEnc = protector.Protect("rt"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
        });
        await host.Db.Accounts.SetCooldownAsync("acc-idle", DateTimeOffset.UtcNow.AddHours(3), "429", CancellationToken.None);
        var sync = host.App.Services.GetRequiredService<SubscriptionQuotaSync>();

        // The access token is about to lapse: neither round renews it just to look at the quota, and skipping is no failure.
        Assert.Equal(0, await sync.RecheckCooledAsync(10, CancellationToken.None));
        Assert.Equal(0, await sync.RefreshDueAsync(10, CancellationToken.None));
        var account = (await host.Db.Accounts.GetAsync("acc-idle"))!;
        Assert.Equal((0, 0), (refreshCalls, quotaCalls));
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Equal("at", protector.Unprotect(account.AccessTokenEnc!));
        Assert.Equal(0, SubscriptionQuotaSync.Failures(account));

        // A request renewed it (the gateway refreshes before it calls): the next round looks at the quota with that token.
        await host.Db.Accounts.UpdateTokensAsync("acc-idle", protector.Protect("at-2"), account.RefreshTokenEnc, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(1, await sync.RecheckCooledAsync(10, CancellationToken.None));
        Assert.Equal(0, await sync.RefreshDueAsync(10, CancellationToken.None)); // just looked
        Assert.Equal((0, 1), (refreshCalls, quotaCalls));
    }

    [Fact]
    public async Task Feishu_Webhooks_Get_A_Signed_Text_Message_And_Error_Codes_Fail()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/webhooks",
            new { name = "飞书群", url = "https://open.feishu.cn/open-apis/bot/v2/hook/abc", format = "feishu", secret = "fs-secret" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(created!["secret"]); // the bot's own secret is never echoed (nor generated)
        Assert.Equal("feishu", created["webhook"]!["format"]!.GetValue<string>());
        Assert.True(created["webhook"]!["hasSecret"]!.GetValue<bool>());
        var id = created["webhook"]!["id"]!.GetValue<string>();

        receiver.ResponseBody = """{"code":0,"msg":"success"}""";
        var (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Equal("success", delivery!["status"]!.GetValue<string>());
        var call = Assert.Single(receiver.Calls);
        var body = call.Json;
        Assert.Equal("text", body["msg_type"]!.GetValue<string>());
        Assert.Contains("【Astra】测试通知", body["content"]!["text"]!.GetValue<string>());
        var ts = body["timestamp"]!.GetValue<string>();
        var expected = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes($"{ts}\nfs-secret"), Array.Empty<byte>()));
        Assert.Equal(expected, body["sign"]!.GetValue<string>());
        Assert.False(call.Headers.ContainsKey("x-astra-signature"));
        // The delivery log keeps Astra's generic payload.
        Assert.Equal("test", delivery["payload"]!["status"]!.GetValue<string>());

        receiver.ResponseBody = """{"code":19021,"msg":"sign match fail or timestamp is not within one hour from current time"}""";
        (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Equal("failed", delivery!["status"]!.GetValue<string>());
        Assert.Contains("19021", delivery["error"]!.GetValue<string>());

        // Secrets: generic ones are generated; a bot secret is entered and can be cleared; formats without signing take none.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/secret")).Status);
        var (_, patched) = await host.SendAsync(HttpMethod.Patch, $"/api/webhooks/{id}", new { secret = "" });
        Assert.False(patched!["hasSecret"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/webhooks",
            new { name = "x", url = "https://hooks.test", format = "wecom", secret = "s" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/webhooks",
            new { name = "x", url = "https://hooks.test", secret = "s" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/webhooks",
            new { name = "x", url = "https://hooks.test", format = "telegram" })).Status);
    }

    [Fact]
    public async Task DingTalk_Signs_The_Url_And_Other_Bots_Get_Their_Shapes()
    {
        var (host, receiver) = await StartAsync();
        await using var _host = host;
        receiver.ResponseBody = """{"errcode":0,"errmsg":"ok"}""";
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/webhooks",
            new { name = "钉钉", url = "https://oapi.dingtalk.com/robot/send?access_token=t1", format = "dingtalk", secret = "SECabc" });
        var id = created!["webhook"]!["id"]!.GetValue<string>();
        var (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Equal("success", delivery!["status"]!.GetValue<string>());
        var call = receiver.Calls[^1];
        var query = System.Web.HttpUtility.ParseQueryString(call.Url.Query);
        Assert.Equal("t1", query["access_token"]);
        var ms = query["timestamp"]!;
        var sign = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("SECabc"), Encoding.UTF8.GetBytes($"{ms}\nSECabc")));
        Assert.Equal(sign, query["sign"]);
        Assert.Equal("text", call.Json["msgtype"]!.GetValue<string>());
        Assert.Contains("测试通知", call.Json["text"]!["content"]!.GetValue<string>());

        receiver.ResponseBody = """{"errcode":310000,"errmsg":"keywords not in content"}""";
        (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{id}/test");
        Assert.Contains("310000", delivery!["error"]!.GetValue<string>());

        receiver.ResponseBody = null;
        foreach (var (format, check) in new (string, Func<JsonObject, bool>)[]
                 {
                     ("wecom", b => b["msgtype"]!.GetValue<string>() == "text" && b["text"]!["content"] is not null),
                     ("slack", b => b["text"] is not null),
                     ("discord", b => b["content"] is not null),
                 })
        {
            (_, created) = await host.SendAsync(HttpMethod.Post, "/api/webhooks", new { name = format, url = $"https://{format}.test/hook", format });
            Assert.False(created!["webhook"]!["hasSecret"]!.GetValue<bool>());
            (_, delivery) = await host.SendAsync(HttpMethod.Post, $"/api/webhooks/{created["webhook"]!["id"]!.GetValue<string>()}/test");
            Assert.Equal("success", delivery!["status"]!.GetValue<string>());
            Assert.True(check(receiver.Calls[^1].Json), format);
        }

        // Switching format drops the old secret.
        var (_, patched) = await host.SendAsync(HttpMethod.Patch, $"/api/webhooks/{id}", new { format = "generic" });
        Assert.Equal("generic", patched!["format"]!.GetValue<string>());
        Assert.False(patched["hasSecret"]!.GetValue<bool>());
    }
}
