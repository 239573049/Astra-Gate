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
    public async Task A_Transient_Handshake_Failure_Is_Retried_Once_On_A_Fresh_Connection()
    {
        // 真实事故：代理节点偶发在 TLS 握手阶段掐连接（"unexpected EOF"），下一次新连接就通了。
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var calls = 0;
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth/token")) return Json(RefreshResponse);
            if (++calls == 1) throw new HttpRequestException("The SSL connection could not be established", new IOException("unexpected EOF"));
            return Json("""{"five_hour":{"utilization":7,"resets_at":"2026-10-06T17:00:00Z"}}""");
        });
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!);

        Assert.Equal(7, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(2, calls);
        // 重试带着同样的授权头。
        Assert.All(handler.Requests.Where(r => r.Url.Contains("/api/oauth/usage")), r => Assert.Equal("Bearer at-fresh", r.Authorization));
    }

    [Fact]
    public async Task A_Persistent_Connection_Failure_Becomes_A_Clear_Error_Not_An_Exception_Page()
    {
        // 重试仍失败：不能炸成 500 + 异常页，要报成一条指向"代理 / 网络"的 OAuthProtocolException，
        // 且账号状态不变（网络不通不是授权失效，不能把账号禁用）。
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var calls = 0;
        var handler = new RoutingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth/token")) return Json(RefreshResponse);
            calls++;
            throw new HttpRequestException("The SSL connection could not be established", new IOException("unexpected EOF"));
        });
        var service = Service(store, handler);

        var stored = (await store.GetAsync("acc-1"))!;
        var error = await Assert.ThrowsAsync<OAuthProtocolException>(() => service.FetchAsync(
            Provider("claude-subscription"), stored, SubscriptionCatalog.Find("claude-subscription")!));

        Assert.Equal("network", error.Error);
        Assert.Contains("api.anthropic.com", error.Message);
        Assert.Contains("代理", error.Message);
        Assert.Equal(2, calls); // 一次 + 一次重试，不无限重试
        Assert.Equal(AccountStatus.Active, (await store.GetAsync("acc-1"))!.Status);
    }

    [Fact]
    public async Task Cancellation_Is_Not_Swallowed_Into_A_Network_Error()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        // 调用方取消了请求：HttpClient 会抛 TaskCanceledException。这必须原样向上传播，
        // 不能被当成"网络不通"吞成一条 502 错误（那会让用户关掉页面后还看到报错）。
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : throw new TaskCanceledException());
        var service = Service(store, handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stored = (await store.GetAsync("acc-1"))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(
            Provider("claude-subscription"), stored, SubscriptionCatalog.Find("claude-subscription")!, cts.Token));
    }

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
        // 真实 codex 令牌的账号 id 在命名空间 claim 里；探测必须把它当 chatgpt-account-id 发出去。
        const string token = "eyJhbGciOiJub25lIn0.eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoiYWNjdC14eCJ9fQ.sig";
        var account = Account(token);
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
        Assert.Equal($"Bearer {token}", probe.Authorization);
        Assert.Equal("acct-xx", probe.Headers["chatgpt-account-id"]);
        Assert.Equal("codex_cli_rs", probe.Headers["originator"]);
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
        var probes = handler.Requests.Where(r => r.Url.StartsWith("https://cli-chat-proxy.grok.com/", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["https://cli-chat-proxy.grok.com/v1/billing?format=credits", "https://cli-chat-proxy.grok.com/v1/user?include=subscription"],
            probes.Select(r => r.Url));
        Assert.All(probes, probe =>
        {
            Assert.Equal("Bearer at-fresh", probe.Authorization);
            Assert.Equal("xai-grok-cli", probe.Headers["x-grok-client-identifier"]);
            Assert.Equal("1.0.13", probe.Headers["x-grok-client-version"]);
        });
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

        var error = await Assert.ThrowsAsync<SubscriptionAuthException>(async () => await service.FetchAsync(
            Provider("claude-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("claude-subscription")!));
        Assert.Contains("额度查询", error.Message);
        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
    }

    /// <summary>
    /// WAF/Cloudflare 会因为出口 IP 或缺少浏览器指纹直接回 403 + HTML；那不是授权失效。
    /// 以前这里会把 403 当成"令牌死了"→ 强制刷新 → 仍 403 → 把账号标成 revoked，
    /// 结果一个好好的账号被废掉（实测 chatgpt.com 对裸 HTTP 客户端如此）。
    /// </summary>
    [Fact]
    public async Task A_Non_Json_Forbidden_Is_Treated_As_Policy_Not_As_A_Dead_Grant()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-fresh"));
        var handler = new RoutingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>Just a moment…</html>", Encoding.UTF8, "text/html") });
        var service = Service(store, handler);

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await service.FetchAsync(
            Provider("openai-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("openai-subscription")!));

        Assert.Equal("http_403", error.Error);
        Assert.Equal(AccountStatus.Active, (await store.GetAsync("acc-1"))!.Status); // 账号没有被禁用
        // 两个端点都试过；都没有浪费一次令牌刷新（WAF 403 不等于授权失效）。
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("/usage")));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/oauth/token"));
    }

    /// <summary>
    /// codex/usage 被 WAF 挡（403 + HTML）时不能整条判定失败：wham/usage 是同一个额度的备用端点，
    /// 实测前者 403、后者 200。这里校验会继续试第二个端点并解析出单数形态的窗口。
    /// </summary>
    [Fact]
    public async Task OpenAi_Probe_Falls_Through_A_Blocked_Endpoint_To_The_Wham_Shape()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-fresh"));
        var handler = new RoutingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/codex/usage")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>Just a moment…</html>", Encoding.UTF8, "text/html") }
                : Json("""{"plan_type":"promax","rate_limit":{"allowed":true,"primary_window":{"used_percent":6,"limit_window_seconds":604800,"reset_at":1791948516}}}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("openai-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("openai-subscription")!);

        Assert.Equal(6, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(604800 / 60, quota["session"]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal("promax", quota["planLabel"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791948516).ToString("o"), quota["session"]!["resetsAtUtc"]!.GetValue<string>());
        // 官方身份头（含 UA）跟着探针一起发出去
        var probe = handler.Requests.First(r => r.Url.Contains("/usage"));
        Assert.Equal("codex_cli_rs", probe.Headers["originator"]);
        Assert.StartsWith("codex_cli_rs/", probe.Headers["User-Agent"]);
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
        Assert.Equal("api.z.ai", new Uri(probe.Url).Host);
    }

    [Fact]
    public async Task BigModel_Quota_Uses_The_BigModel_Host_With_The_Bare_Api_Key()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("key-id.key-secret"));
        var handler = new RoutingHandler((request, _) =>
            Json("""{"success":true,"code":200,"data":{"level":"lite","limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":20,"nextResetTime":1791230400000}]}}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("bigmodel-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("bigmodel-subscription")!);

        Assert.Equal(20, quota!["session"]!["usedPercent"]!.GetValue<double>());
        var probe = handler.Requests.Single(r => r.Url.Contains("/monitor/usage/quota/limit"));
        Assert.Equal("https://bigmodel.cn/api/monitor/usage/quota/limit", probe.Url);
        Assert.Equal("key-id.key-secret", probe.Authorization); // 裸 API Key，无 Bearer
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

    /// <summary>
    /// GitHub Copilot 按"高级请求"计费：额度来自 /copilot_internal/user 的 quota_snapshots
    /// （entitlement / percent_remaining / overage_*）。探针用 GitHub token 认证，不碰 Copilot 短时令牌。
    /// </summary>
    [Fact]
    public async Task Copilot_Quota_Maps_Premium_Request_Entitlement_To_A_Used_Percent()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("copilot-short-lived")); // 访问令牌本身不参与额度查询
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath == "/copilot_internal/user"
            ? Json("""
                   {"login":"octocat","copilot_plan":"individual","quota_reset_date":"2026-11-01T00:00:00Z",
                    "quota_snapshots":{"chat":{"entitlement":0,"percent_remaining":100,"unlimited":true},
                      "premium_models":{"quota_id":"premium","entitlement":300,"remaining":225,"percent_remaining":75,
                        "overage_count":0,"overage_permitted":false,"unlimited":false}}}
                   """)
            : Json("{}"));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("github-copilot-subscription"),
            (await store.GetAsync("acc-1"))!, SubscriptionCatalog.Find("github-copilot-subscription")!);

        // 75% 剩余 → 25% 已用（卡片按已用画条）
        Assert.Equal(25, quota!["credits"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal("2026-11-01T00:00:00.0000000+00:00", quota["credits"]!["resetsAtUtc"]!.GetValue<string>());
        Assert.Equal(300, quota["entitlement"]!.GetValue<long>());
        Assert.Equal(225, quota["remaining"]!.GetValue<long>());
        Assert.Equal("individual", quota["planLabel"]!.GetValue<string>());
        Assert.Equal("octocat", quota["account"]!.GetValue<string>());

        // 认证用 GitHub token（刷新槽）而不是 Copilot 短时令牌
        var probe = handler.Requests.Single(r => r.Url.Contains("/copilot_internal/user"));
        Assert.Equal("token rt-1", probe.Authorization);
        Assert.Equal("vscode-chat", probe.Headers["Copilot-Integration-Id"]);
    }

    [Fact]
    public async Task Codex_Reset_Credits_Are_Listed_And_Consumed_By_Id()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-fresh"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/backend-api/wham/rate-limit-reset-credits" =>
                Json("""
                     {"credits":[{"id":"RateLimitResetCredit_x","reset_type":"codex_rate_limits","status":"available",
                       "is_supported_by_plan":true,"expires_at":"2026-11-06T23:19:30Z","title":"Full reset",
                       "description":"one free rate limit reset"}],
                      "available_count":1,"total_earned_count":0,"history_enabled":true}
                     """),
            "/backend-api/wham/rate-limit-reset-credits/consume" => Json("""{"redeemed":true}"""),
            _ => Json("{}"),
        });
        var service = Service(store, handler);
        var provider = Provider("openai-subscription");
        var config = SubscriptionCatalog.Find("openai-subscription")!;

        var credits = await service.ListResetCreditsAsync(provider, (await store.GetAsync("acc-1"))!, config);
        Assert.Equal(1, credits!["available_count"]!.GetValue<int>());
        Assert.Equal("available", credits["credits"]![0]!["status"]!.GetValue<string>());
        // 快照落库（extra.credits），列表每次实时拉
        var stored = await store.GetAsync("acc-1");
        Assert.Equal("Full reset", stored!.Extra["credits"]!["credits"]![0]!["title"]!.GetValue<string>());

        await service.ConsumeResetCreditAsync(provider, stored, config, "RateLimitResetCredit_x");

        // 消费请求带幂等键 + credit_id，并带 codex 身份头
        var consume = handler.Requests.Single(r => r.Url.EndsWith("/consume"));
        Assert.Equal("Bearer at-fresh", consume.Authorization);
        Assert.Equal("codex_cli_rs", consume.Headers["originator"]);
        Assert.StartsWith("codex_cli_rs/", consume.Headers["User-Agent"]);
        // 卡就地标成 redeemed，可用数减一
        var after = await store.GetAsync("acc-1");
        Assert.Equal("redeemed", after!.Extra["credits"]!["credits"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(0, after.Extra["credits"]!["available_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Codex_Reset_Credit_Without_Anything_To_Reset_Is_A_Clear_Error()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-fresh"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/consume")
            ? Json("""{"code":"nothing_to_reset","message":"no active rate limit"}""")
            : Json("{}"));
        var service = Service(store, handler);

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await service.ConsumeResetCreditAsync(
            Provider("openai-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("openai-subscription")!, "c1"));

        Assert.Equal("nothing_to_reset", error.Error);
        Assert.Contains("不需要重置", error.Message);
    }

    [Fact]
    public void Only_The_ChatGpt_Subscription_Has_Reset_Credits()
    {
        Assert.True(SubscriptionQuotaService.SupportsResetCredits(Provider("openai-subscription")));
        Assert.False(SubscriptionQuotaService.SupportsResetCredits(Provider("claude-subscription")));
    }

    /// <summary>
    /// Kimi Code：GET /coding/v1/usages。数字是字符串，顶层 usage 是周额度，limits[] 里 300 分钟窗口是 5 小时会话。
    /// </summary>
    [Fact]
    public async Task Kimi_Usages_Map_Session_And_Weekly_Windows_From_String_Numbers()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("kimi-at"));
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"usage":{"limit":"100","remaining":"74","resetTime":"2026-10-11T17:32:50.757941Z"},"limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},"detail":{"limit":"100","remaining":"85","resetTime":"2026-10-06T17:00:00Z"}}],"user":{"membership":{"level":"LEVEL_INTERMEDIATE"}}}"""));
        var service = Service(store, handler);

        var (account, quota) = await service.FetchAsync(Provider("kimi-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("kimi-subscription")!);

        Assert.Equal(15, quota!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(300, quota["session"]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T17:00:00Z"), DateTimeOffset.Parse(quota["session"]!["resetsAtUtc"]!.GetValue<string>()));
        Assert.Equal(26, quota["weekly"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(10_080, quota["weekly"]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal("intermediate", quota["planLabel"]!.GetValue<string>());
        Assert.Equal(26, account.Extra["quota"]!["weekly"]!["usedPercent"]!.GetValue<double>()); // 已持久化

        var probe = handler.Requests.Single(r => r.Url == "https://api.kimi.com/coding/v1/usages");
        Assert.Equal("Bearer kimi-at", probe.Authorization);
        Assert.Equal("kimi_code_cli", probe.Headers["X-Msh-Platform"]);
    }

    [Fact]
    public async Task Kimi_Exhausted_Weekly_Quota_Uses_Usage_Not_The_Contradicting_Usages_Ratio()
    {
        // kimi-code#3951：usage.used=100/100（周额度耗尽，上游也确实 403），而 usages.limit_7d.used_ratio 却是 0。
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"usage":{"limit":"100","used":"100","resetTime":"2026-09-24T02:09:07.465054Z"},"limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},"detail":{"limit":"100","remaining":"100","resetTime":"2026-09-21T01:09:07Z"}}],"usages":{"limit_5h":{"used_ratio":0},"limit_7d":{"used_ratio":0}}}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("kimi-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("kimi-subscription")!);

        Assert.Equal(100, quota!["weekly"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(0, quota["session"]!["usedPercent"]!.GetValue<double>()); // 满额剩余 → 0% 已用，不是缺失
        Assert.Null(quota["planLabel"]);
    }

    [Fact]
    public async Task Kimi_Drops_Incomplete_Or_Unrecognized_Windows_Instead_Of_Inventing_Zero()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        // usage 缺 used/remaining；limits 里一条是小时窗口（不是 300 分钟）且单位不认识，一条 limit 为 0。
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"usage":{"limit":"100","resetTime":"2026-10-11T00:00:00Z"},"limits":[{"window":{"duration":1,"timeUnit":"TIME_UNIT_FORTNIGHT"},"detail":{"limit":"10","remaining":"1"}},{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":"0","remaining":"0"}}]}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("kimi-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("kimi-subscription")!);

        Assert.NotNull(quota);
        Assert.Null(quota!["weekly"]);
        Assert.Null(quota["session"]);
    }

    [Fact]
    public async Task Kimi_Hour_Window_Is_Recognized_As_The_Five_Hour_Session_And_Reset_Is_Optional()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"limits":[{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":200,"remaining":150}}]}"""));
        var service = Service(store, handler);

        var (_, quota) = await service.FetchAsync(Provider("kimi-subscription"), (await store.GetAsync("acc-1"))!,
            SubscriptionCatalog.Find("kimi-subscription")!);

        Assert.Equal(25, quota!["session"]!["usedPercent"]!.GetValue<double>()); // 裸数字也认
        Assert.Null(quota["session"]!["resetsAtUtc"]);
    }

    [Fact]
    public async Task Kimi_Persistently_Rejected_Account_Is_Disabled_As_Revoked()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account());
        var handler = new RoutingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/oauth/token")
            ? Json(RefreshResponse)
            : Json("""{"error":{"message":"invalid token"}}""", HttpStatusCode.Unauthorized));
        var service = Service(store, handler);

        var stored = (await store.GetAsync("acc-1"))!;
        await Assert.ThrowsAsync<SubscriptionAuthException>(() => service.FetchAsync(
            Provider("kimi-subscription"), stored, SubscriptionCatalog.Find("kimi-subscription")!));

        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
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
