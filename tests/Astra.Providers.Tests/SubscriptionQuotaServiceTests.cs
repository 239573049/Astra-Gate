using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class SubscriptionQuotaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]));
    }

    private sealed class InMemoryAccountStore : IProviderAccountStore
    {
        public ConcurrentDictionary<string, ProviderAccount> Rows { get; } = new();

        public Task<IReadOnlyList<ProviderAccount>> ListAsync(string? providerId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>(
                Rows.Values.Where(a => providerId is null || a.ProviderId == providerId).ToList());

        public Task<ProviderAccount?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Rows.TryGetValue(id, out var account) ? account : null);

        public Task<IReadOnlyList<ProviderAccount>> ListExpiringAsync(TimeSpan within, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>(Rows.Values
                .Where(a => a.Status == AccountStatus.Active && a.ExpiresAtUtc is { } exp && exp - Now <= within)
                .ToList());

        public Task InsertAsync(ProviderAccount account, CancellationToken ct = default)
        {
            Rows[account.Id] = account;
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(ProviderAccount account, CancellationToken ct = default)
        {
            Rows[account.Id] = account;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateTokensAsync(
            string id, string accessTokenEnc, string? refreshTokenEnc, DateTimeOffset expiresAtUtc, CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(id, out var account)) return Task.FromResult(false);
            account.AccessTokenEnc = accessTokenEnc;
            account.RefreshTokenEnc = refreshTokenEnc ?? account.RefreshTokenEnc;
            account.ExpiresAtUtc = expiresAtUtc;
            account.Status = AccountStatus.Active;
            account.LastRefreshAtUtc = Now;
            return Task.FromResult(true);
        }

        public Task<bool> SetStatusAsync(string id, string status, CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(id, out var account)) return Task.FromResult(false);
            account.Status = status;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Rows.TryRemove(id, out _));
    }

    /// <summary>Routes by URL: the OAuth token endpoint always refreshes; probes answer by bearer token.</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers) headers[name] = string.Join(",", values);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(),
                headers.TryGetValue("Authorization", out var auth) ? auth : null, headers));
            return respond(request, body);
        }
    }

    private sealed class StubFactory(RoutingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private const string RefreshResponse = """{"access_token":"at-NEW","refresh_token":null,"expires_in":3600}""";

    private static Provider Provider(string templateId) => new()
    {
        Id = "p1",
        Name = "订阅",
        TemplateId = templateId,
        AuthScheme = AuthSchemes.OAuthSubscription,
    };

    private static ProviderAccount Account(string accessToken = "at-fresh") => new()
    {
        Id = "acc-1",
        ProviderId = "p1",
        DisplayName = "me@example.com",
        AccessTokenEnc = new FakeProtector().Protect(accessToken),
        RefreshTokenEnc = new FakeProtector().Protect("rt-1"),
        ExpiresAtUtc = Now + TimeSpan.FromHours(1),
        Status = AccountStatus.Active,
    };

    private static SubscriptionQuotaService Service(InMemoryAccountStore store, RoutingHandler handler) =>
        new(store, new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock()),
            new FakeProtector(), new StubFactory(handler), new FakeClock());

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Claude_Usage_Is_Normalized_From_The_Nested_Shape_And_Persisted()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"five_hour":{"utilization":12.5,"resets_at":"2026-10-06T17:00:00Z"},"seven_day":{"utilization":43.4}}"""));
        var service = Service(store, handler);

        var (account, quota) = await service.FetchAsync(Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!);

        Assert.Equal(12.5, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal("2026-10-06T17:00:00Z", quota["session"]!["resetsAtUtc"]!.GetValue<string>());
        Assert.Equal(43.4, quota["weekly"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(quota.ToJsonString(), account.Extra["quota"]!.ToJsonString()); // persisted into Extra["quota"]
    }

    [Fact]
    public async Task Claude_Legacy_Flat_Fractions_Are_Scaled_To_Percent()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"five_hour_utilization":0.25,"seven_day_utilization":0.9}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!);

        Assert.Equal(25, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(90, quota["weekly"]!["usedPercent"]!.GetValue<double>());
    }

    [Fact]
    public async Task Codex_Rate_Limit_Windows_Map_To_Session_And_Weekly()
    {
        var store = new InMemoryAccountStore();
        var account = Account("eyJhbGciOiJub25lIn0.eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2N0LXh4In0.sig"); // JWT with chatgpt_account_id
        await store.InsertAsync(account);
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"rate_limits":{"primary_window":{"used_percent":10,"window_minutes":300,"resets_in_seconds":600},"secondary_window":{"used_percent":43,"window_minutes":10080,"resets_in_seconds":86400}}}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("openai-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("openai-subscription")!);

        Assert.Equal(10, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(300, quota["session"]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal(Now.AddSeconds(600).ToString("o"), quota["session"]!["resetsAtUtc"]!.GetValue<string>());
        Assert.Equal(43, quota["weekly"]!["usedPercent"]!.GetValue<double>());
        // the probe carries the fixed Codex identity headers, including the account id from the token
        var probe = handler.Requests.Single(r => r.Url.Contains("/codex/usage"));
        Assert.Equal("Bearer eyJhbGciOiJub25lIn0.eyJjaGF0Z3B0X2FjY291bnRfaWQiOiJhY2N0LXh4In0.sig", probe.Authorization);
    }

    [Fact]
    public async Task Grok_Billing_And_User_Responses_Map_To_Credits_And_Plan()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth/token")) return Json(RefreshResponse);
            return request.RequestUri!.PathAndQuery.Contains("/billing")
                ? Json("""{"creditUsagePercent":30.5,"config":{"monthlyLimit":1000,"prepaidBalance":5.5}}""")
                : Json("""{"subscription":{"plan_name":"SuperGrok"}}""");
        });
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("grok-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("grok-subscription")!);

        Assert.Equal(30.5, quota!["credits"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(1000, quota["credits"]!["monthlyLimit"]!.GetValue<double>());
        Assert.Equal(5.5, quota["credits"]!["prepaidBalance"]!.GetValue<double>());
        Assert.Equal("SuperGrok", quota["planLabel"]!.GetValue<string>());
        var probe = handler.Requests.Single(r => r.Url.Contains("/billing"));
        Assert.Null(probe.Authorization!.EndsWith("xai-grok-cli", StringComparison.Ordinal) ? probe.Authorization : null);
    }

    [Fact]
    public async Task An_Unauthorized_Probe_Force_Refreshes_Once_And_Retries()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-STALE"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : request.Headers.GetValues("Authorization").First().EndsWith("at-STALE")
                ? Json("""{"error":"expired"}""", HttpStatusCode.Unauthorized)
                : Json("""{"five_hour":{"utilization":7}}"""));
        var service = Service(store, handler);

        var (account, quota) = await service.FetchAsync(Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!);

        Assert.Equal(7, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(AccountStatus.Active, account.Status); // recovered, not disabled
    }

    [Fact]
    public async Task A_Persistently_Rejected_Account_Is_Disabled_As_Revoked()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-DEAD"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"error":"forbidden"}""", HttpStatusCode.Forbidden));
        var service = Service(store, handler);

        await Assert.ThrowsAsync<SubscriptionAuthException>(async () => await service.FetchAsync(
            Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!));
        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
    }

    [Fact]
    public async Task Zcode_Quota_Maps_The_Five_Hour_And_Weekly_Windows()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("api-key-jwt"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"success":true,"code":0,"data":{"level":"glm-coding-pro","limits":[{"type":"CREDIT_LIMIT","unit":3,"number":5,"percentage":12.5,"nextResetTime":1791230400000},{"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":40,"nextResetTime":1791835200000},{"type":"TIME_LIMIT","unit":5,"number":1,"percentage":1}]}}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("zcode-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("zcode-subscription")!);

        Assert.Equal(12.5, quota!["session"]!["usedPercent"]!.GetValue<double>()); // type CREDIT_LIMIT + unit 3 × number 5 = 5h
        Assert.Equal(300, quota["session"]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal(40, quota["weekly"]!["usedPercent"]!.GetValue<double>());     // unit 6 = weekly
        Assert.Equal("glm-coding-pro", quota["planLabel"]!.GetValue<string>());    // level 当套餐名
        // TIME_LIMIT（工具月额度）不认；鉴权头是裸值（无 Bearer）——biz/monitor API 只认这个形状
        var probe = handler.Requests.Single(r => r.Url.Contains("/monitor/usage/quota/limit"));
        Assert.Equal("api-key-jwt", probe.Authorization);
    }

    [Fact]
    public async Task Zcode_Quota_Falls_Back_To_Remaining_Over_Number_And_Drops_Incomplete_Windows()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("k"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"success":true,"code":200,"data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"remaining":1,"nextResetTime":1791230400000},{"type":"TOKENS_LIMIT","unit":6,"percentage":9}]}}"""));
        var service = Service(store, handler);

        // 第一条：percentage 缺席时用 remaining/number 反推已用百分比（照 NextCoWork 的公式）
        // → 100 - 1/5×100 = 80。
        var (_, quota) = await service.FetchAsync(Provider("zcode-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("zcode-subscription")!);

        Assert.Equal(80, quota!["session"]!["usedPercent"]!.GetValue<double>());
        // 第二条缺 nextResetTime → 整条丢弃（绝不编一个重置时间），weekly 缺席
        Assert.Null(quota["weekly"]);
    }

    [Fact]
    public async Task Zcode_Quota_Envelope_Rejection_Yields_An_Empty_Snapshot_Not_An_Error()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("k"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"success":false,"code":1001,"msg":"Header中未收到Authorization参数"}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("zcode-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("zcode-subscription")!);

        Assert.NotNull(quota);
        Assert.Null(quota!["session"]);
    }

    [Fact]
    public async Task Unknown_Families_Return_Null_Instead_Of_Throwing()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((_, _) => Json("{}"));
        var service = Service(store, handler);

        var (account, quota) = await service.FetchAsync(Provider("custom-thing"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!);

        Assert.Null(quota);
        Assert.Null(account.Extra["quota"]);
    }
}
