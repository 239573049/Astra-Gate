using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;
using Astra.Server.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class ProviderApiTests
{
    [Fact]
    public async Task Catalog_And_Price_Keys_Are_Available_Without_Configuring_Providers()
    {
        await using var host = await TestHost.StartAsync();
        var templates = (await host.GetJsonAsync("/api/provider-templates")).AsArray();
        Assert.Equal(25, templates.Count); // + github-copilot-subscription
        var claudeSub = templates.Single(t => t!["id"]!.GetValue<string>() == "claude-subscription")!;
        Assert.Equal("oauth-subscription", claudeSub["authScheme"]!.GetValue<string>());
        Assert.False(claudeSub["requiresApiKey"]!.GetValue<bool>());
        var openai = templates.Single(t => t!["id"]!.GetValue<string>() == "openai")!;
        Assert.Contains(openai["models"]!.AsArray(), m => m!.GetValue<string>() == "gpt-5");
        var routin = templates.Single(t => t!["id"]!.GetValue<string>() == "routin")!;
        Assert.Equal(["openai-responses", "anthropic"],
            routin["endpoints"]!.AsArray().Select(e => e!["protocol"]!.GetValue<string>()).ToList());
        Assert.All(routin["endpoints"]!.AsArray(), e => Assert.Equal("https://api.routin.ai/plan/v1", e!["baseUrl"]!.GetValue<string>()));
        var ollama = templates.Single(t => t!["id"]!.GetValue<string>() == "ollama")!;
        Assert.False(ollama["requiresApiKey"]!.GetValue<bool>());
        var prices = (await host.GetJsonAsync("/api/price-keys")).AsArray();
        Assert.Contains(prices, p => p!["priceKey"]!.GetValue<string>() == "siliconflow-cn");
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());
    }

    [Fact]
    public async Task Provider_Key_Is_Encrypted_And_Never_Returned_In_Plaintext()
    {
        await using var host = await TestHost.StartAsync();
        const string key = "sk-a-long-test-secret-key-1234";
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai", apiKey = key, models = new[] { "gpt-5" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("sk-…1234", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain(key, body.ToJsonString());
        var id = body["id"]!.GetValue<string>();
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.NotEqual(key, stored!.ApiKeyEnc);
        Assert.NotNull(stored.TemplateSnapshotJson);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(stored.ApiKeyEnc, (await host.Db.Providers.GetAsync(id))!.ApiKeyEnc);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { apiKey = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Null((await host.Db.Providers.GetAsync(id))!.ApiKeyEnc);
    }

    [Fact]
    public async Task Provider_Model_Only_Needs_Id_And_Inherits_System_Fields_And_Provider_Price()
    {
        await using var host = await TestHost.StartAsync();
        var p = await Create(host, "siliconflow");
        var (status, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{p}/models", new { modelIds = new[] { "deepseek-ai/DeepSeek-V4-Flash-0731" } });
        Assert.Equal(HttpStatusCode.OK, status);
        var m = added!.AsArray().Single()!;
        Assert.Equal("deepseek-v4-flash", m["systemModelId"]!.GetValue<string>());
        Assert.Equal("inherited", m["origins"]!["contextWindow"]!.GetValue<string>());
        Assert.Equal("provider_price", m["pricingSource"]!.GetValue<string>());
        Assert.Equal("siliconflow", m["priceKey"]!.GetValue<string>());
        Assert.Equal(0.22m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(1_000_000, m["effective"]!["contextWindow"]!.GetValue<long>());
        Assert.Equal("{}", m["overrides"]!.ToJsonString());
    }

    [Fact]
    public async Task Price_Override_Does_Not_Multiply_And_Null_Resets_To_Inherited()
    {
        await using var host = await TestHost.StartAsync();
        var p = await Create(host, "siliconflow");
        await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}", new { priceMultiplier = 2 });
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{p}/models", new { modelIds = new[] { "deepseek-v4-flash" } });
        var pmId = added![0]!["id"]!.GetValue<long>();
        var (_, overridden) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}/models/{pmId}", new
        {
            overrides = new { pricing = new { currency = "USD", unit = "per_1m_tokens", @base = new { input = 7, output = 14 } } },
        });
        Assert.Equal("provider_override", overridden!["pricingSource"]!.GetValue<string>());
        Assert.Equal(1, overridden["multiplier"]!.GetValue<decimal>());
        var (status, inherited) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}/models/{pmId}", new JsonObject
        { ["overrides"] = new JsonObject { ["pricing"] = null } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("provider_price", inherited!["pricingSource"]!.GetValue<string>());
        Assert.Equal(2, inherited["multiplier"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_Model_From_Another_Provider_Cannot_Be_Edited_Or_Removed()
    {
        await using var host = await TestHost.StartAsync();
        var a = await Create(host);
        var b = await Create(host);
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{a}/models", new { modelIds = new[] { "gpt-5" } });
        var pm = added![0]!["id"]!.GetValue<long>();
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{b}/models/{pm}", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{b}/models/{pm}");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.True((await host.Db.Providers.GetModelByIdAsync(pm))!.Enabled);
    }

    [Fact]
    public async Task Bound_Provider_Deletion_Is_Blocked_And_Duplicate_Has_Independent_Models()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "gpt-5" } });
        await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = id });
        var (status, error) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{id}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("codex", error!["details"]!["boundClients"]![0]!.GetValue<string>());
        (status, var copy) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);
        var copyId = copy!["id"]!.GetValue<string>();
        Assert.NotEqual(id, copyId);
        Assert.Empty(copy["boundClients"]!.AsArray());
        var copiedModels = (await host.GetJsonAsync($"/api/providers/{copyId}/models")).AsArray();
        Assert.Equal("gpt-5", copiedModels.Single()!["modelId"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{copyId}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.NotNull(await host.Db.Providers.GetAsync(id));
    }

    [Fact]
    public async Task Subscription_Template_Is_Single_Instance_And_Cannot_Be_Duplicated()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();

        // a second provider from the same subscription template is refused with the existing instance named
        (status, var error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(id, error!["details"]!["existingProviderId"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription", variantId = "" });
        Assert.NotEqual(HttpStatusCode.Conflict, status); // a different subscription family is fine

        // duplicating the subscription provider is refused too; a normal provider still duplicates
        (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.Conflict, status);
        var regular = await Create(host);
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{regular}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);

        // deleting the instance frees the template again
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", body!["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Account_Quota_Is_Fetched_Persisted_And_Served_By_The_Accounts_Api()
    {
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse(
                    """{"five_hour":{"utilization":12.5,"resets_at":"2026-10-06T17:00:00Z"},"seven_day":{"utilization":43}}""")))));
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-q",
            ProviderId = providerId,
            DisplayName = "me@example.com",
            AccessTokenEnc = protector.Protect("at-fresh"),
            RefreshTokenEnc = protector.Protect("rt-1"),
            ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            Status = AccountStatus.Active,
        });

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/provider-accounts/acc-q/quota", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(12.5, body!["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(43, body["quota"]!["weekly"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal("2026-10-06T17:00:00Z", body["quota"]!["session"]!["resetsAtUtc"]!.GetValue<string>());

        // the snapshot is persisted: plain account listings serve it without touching upstream again
        var accounts = (await host.GetJsonAsync($"/api/providers/{providerId}/accounts")).AsArray();
        Assert.Equal(12.5, accounts.Single()!["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
    }

    [Fact]
    public async Task Codex_Subscription_Login_Uses_The_Browser_Pkce_Flow()
    {
        var requests = new List<(string Path, string Body, string? ContentType)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            lock (requests) requests.Add((request.RequestUri!.AbsolutePath, body, request.Content?.Headers.ContentType?.MediaType));
            return JsonResponse("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""");
        });
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{providerId}",
            new { settings = new { subscription_oauth = new { verified = true } } });
        Assert.Equal(HttpStatusCode.OK, status);

        // codex-cli 的默认登录：回环回调必须是它的白名单端口（1455/1457）+ /auth/callback，
        // 授权页带它固定的三个额外参数与完整 scope 集合。
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login", new { });
        Assert.True(
            status == HttpStatusCode.OK || status == HttpStatusCode.Conflict,
            $"HTTP {(int)status}: {body?.ToJsonString()}");
        if (status == HttpStatusCode.Conflict)
            return; // 端口被本机上另一个 codex 占用：这不是失败，登录会要求改用 deviceauth。
        Assert.Equal("pkce", body!["mode"]!.GetValue<string>());
        var state = body["state"]!.GetValue<string>();
        var authorizeUrl = body["authorizeUrl"]!.GetValue<string>();
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?", authorizeUrl);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback", authorizeUrl);
        Assert.Contains("code_challenge_method=S256", authorizeUrl);
        Assert.Contains("originator=codex_cli_rs", authorizeUrl);
        Assert.Contains("codex_cli_simplified_flow=true", authorizeUrl);
        Assert.Contains("id_token_add_organizations=true", authorizeUrl);
        Assert.Contains("scope=" + Uri.EscapeDataString("openid profile email offline_access api.connectors.read api.connectors.invoke"), authorizeUrl);

        // 上游把浏览器 302 到 codex 的回环端口；Astra 的接收器转给本机回调完成换码。
        var receiver = new HttpClient();
        var received = await receiver.GetAsync($"http://localhost:1455/auth/callback?code=the-code&state={state}");
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);

        // 换码是异步转发的，轮询到落库为止。
        for (var i = 0; i < 50 && requests.Count == 0; i++) await Task.Delay(100);
        var exchange = Assert.Single(requests);
        Assert.Equal("/oauth/token", exchange.Path);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
        Assert.Contains("code=the-code", exchange.Body);
        Assert.Contains("code_verifier=", exchange.Body);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback", exchange.Body);

        for (var i = 0; i < 50 && (await host.Db.Accounts.ListAsync(providerId)).Count == 0; i++) await Task.Delay(100);
        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.NotNull(account.AccessTokenEnc);

        // 回调完成后接收器释放端口，别的进程可以再用 codex 的默认端口。
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 1455);
        probe.Start();
        probe.Stop();
    }

    [Fact]
    public async Task Codex_Login_Can_Be_Imported_From_The_Machines_Codex_Cli()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse("""{"plan_type":"promax","rate_limit":{"primary_window":{"used_percent":12,"limit_window_seconds":604800,"reset_at":1791948516}}}""")));
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse("{}"))));
        });
        // 假 home：写入一份 codex 的登录文件，绝不碰真实 ~/.codex。
        static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var access = $"{Segment("""{"alg":"none"}""")}.{Segment("""{"https://api.openai.com/auth":{"chatgpt_account_id":"ws-1","chatgpt_plan_type":"promax"},"exp":4102444800}""")}.sig";
        var idToken = $"{Segment("""{"alg":"none"}""")}.{Segment("""{"email":"me@example.com"}""")}.sig";
        Directory.CreateDirectory(Path.Combine(host.Root, "client-home", ".codex"));
        File.WriteAllText(Path.Combine(host.Root, "client-home", ".codex", "auth.json"),
            """{"auth_mode":"chatgpt","tokens":{"id_token":"ID","access_token":"ACCESS","refresh_token":"rt-9","account_id":"ws-1"}}"""
                .Replace("ID", idToken).Replace("ACCESS", access));

        // 探测：只回展示信息，不出凭据。
        var probe = await host.GetJsonAsync("/api/subscription/codex/local-login");
        Assert.True(probe["available"]!.GetValue<bool>());
        // 邮箱来自 id_token、套餐名来自 access_token 的命名空间 claim（payload 长度 %4==3 也必须解得出）。
        Assert.Equal("me@example.com", probe["accountEmail"]!.GetValue<string>());
        Assert.Equal("promax", probe["plan"]!.GetValue<string>());
        Assert.DoesNotContain("rt-9", probe.ToJsonString());
        Assert.DoesNotContain(access, probe.ToJsonString());

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/import-codex", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("active", body!["account"]!["status"]!.GetValue<string>());
        Assert.Equal("me@example.com", body["account"]!["accountEmail"]!.GetValue<string>());

        // 两个令牌都加密入库：访问令牌 = 本机 access_token，刷新槽 = 本机 refresh_token。
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal(access, protector.Unprotect(account.AccessTokenEnc!));
        Assert.Equal("rt-9", protector.Unprotect(account.RefreshTokenEnc!));
        // 导入时顺手验一次：额度快照落库（wham 形态的窗口）。
        Assert.Equal(12, account.Extra["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
    }

    [Fact]
    public async Task Import_Codex_Reports_A_Missing_Local_Login()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        // 空 home 里没有 .codex/auth.json。
        Assert.False((await host.GetJsonAsync("/api/subscription/codex/local-login"))["available"]!.GetValue<bool>());
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/import-codex", new { });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("Codex", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Copilot_Subscription_Login_Runs_The_Device_Flow_Then_Exchanges_A_Copilot_Token()
    {
        var requests = new List<(string Path, string Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            lock (requests) requests.Add((path, body));
            return path switch
            {
                "/login/device/code" => JsonResponse("""{"device_code":"dc-1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""),
                "/login/oauth/access_token" => JsonResponse("""{"access_token":"gho_1","token_type":"bearer","scope":"repo workflow"}"""),
                "/copilot_internal/v2/token" => JsonResponse("""{"token":"copilot-tok","expires_at":4102444800,"refresh_in":1800,"sku":"copilot_individual","endpoints":{"api":"https://api.githubcopilot.com"}}"""),
                _ => JsonResponse("{}"),
            };
        });
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "github-copilot-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("device", body!["mode"]!.GetValue<string>());       // GitHub device flow
        Assert.Equal("ABCD-1234", body["userCode"]!.GetValue<string>());
        Assert.Equal("https://github.com/login/device", body["verificationUrl"]!.GetValue<string>());
        var state = body["state"]!.GetValue<string>();

        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login/{state}/poll", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("done", body!["status"]!.GetValue<string>());

        // 设备码申请 → 换 GitHub token（form + client_id + scope）→ 换 Copilot 短时令牌
        Assert.Equal(["/login/device/code", "/login/oauth/access_token", "/copilot_internal/v2/token"], requests.Select(r => r.Path));
        Assert.Contains("client_id=Iv1.b507a08c87ecfe98", requests[0].Body);
        Assert.Contains("scope=repo+workflow", requests[0].Body);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", requests[1].Body);

        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Equal("copilot-tok", protector.Unprotect(account.AccessTokenEnc!));   // 访问令牌 = Copilot 短时令牌
        Assert.Equal("gho_1", protector.Unprotect(account.RefreshTokenEnc!));        // 刷新槽 = GitHub token
        Assert.Equal("copilot_individual", account.Plan);
    }

    [Fact]
    public async Task Codex_Device_Code_Login_Stays_Available_For_The_Headless_Path()
    {
        var requests = new List<(string Path, string Body, string? ContentType)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            lock (requests) requests.Add((path, body, request.Content?.Headers.ContentType?.MediaType));
            return path switch
            {
                "/api/accounts/deviceauth/usercode" => JsonResponse("""{"device_auth_id":"da-1","user_code":"ABCD-1234","interval":"5"}"""),
                "/api/accounts/deviceauth/token" => JsonResponse("""{"authorization_code":"ac-1","code_challenge":"ch-1","code_verifier":"cv-1"}"""),
                "/oauth/token" => JsonResponse("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}"""),
                _ => JsonResponse("{}"),
            };
        });
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();
        // 切到 device code 备选：codex login --device-auth（headless 环境）。
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{providerId}",
            new { settings = new { subscription_oauth = new { verified = true, style = "deviceauth", use_pkce = false } } });
        Assert.Equal(HttpStatusCode.OK, status);

        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("device", body!["mode"]!.GetValue<string>());
        Assert.Equal("ABCD-1234", body["userCode"]!.GetValue<string>());
        Assert.Equal("https://auth.openai.com/codex/device", body["verificationUrl"]!.GetValue<string>());
        var state = body["state"]!.GetValue<string>();

        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login/{state}/poll", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("done", body!["status"]!.GetValue<string>());

        // 申请（JSON）→ 轮询（JSON）→ 用上游给的授权码 + verifier 走标准换码。
        Assert.Equal(
            ["/api/accounts/deviceauth/usercode", "/api/accounts/deviceauth/token", "/oauth/token"],
            requests.Select(r => r.Path));
        Assert.All(requests.Take(2), r => Assert.Equal("application/json", r.ContentType));
        Assert.Contains("grant_type=authorization_code", requests[2].Body);
        Assert.Contains("code_verifier=cv-1", requests[2].Body);

        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public async Task Zcode_Subscription_Login_Uses_The_Server_Mediated_Cli_Flow()
    {
        var requests = new List<(string Method, string Path, string Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            lock (requests) requests.Add((request.Method.Method, path, body));
            return path switch
            {
                "/api/v1/oauth/cli/init" => JsonResponse("""{"code":0,"data":{"flow_id":"flow-1","authorize_url":"https://chat.z.ai/api/oauth/authorize?client_id=x","poll_token":"poll-1"}}"""),
                "/api/v1/oauth/cli/poll/flow-1" => JsonResponse("""{"code":0,"data":{"accessToken":"oauth-1"}}"""),
                "/api/auth/z/login" => JsonResponse("""{"code":0,"data":{"access_token":"jwt-1"}}"""),
                _ => JsonResponse("{}"),
            };
        });
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "zcode-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{providerId}",
            new { settings = new { subscription_oauth = new { verified = true } } });
        Assert.Equal(HttpStatusCode.OK, status);

        // 发起：服务端调 CLI init，回一个上游给的授权链接（没有回调地址）。
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("cli", body!["mode"]!.GetValue<string>());
        Assert.StartsWith("https://chat.z.ai/api/oauth/authorize", body["authorizeUrl"]!.GetValue<string>());
        var state = body["state"]!.GetValue<string>();

        // 轮询：拿 OAuth token → 第四跳换业务 JWT → 落库。
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/login/{state}/poll", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("done", body!["status"]!.GetValue<string>());
        Assert.Equal("active", body["account"]!["status"]!.GetValue<string>());

        Assert.Equal(
            [("POST", "/api/v1/oauth/cli/init"), ("GET", "/api/v1/oauth/cli/poll/flow-1"),
             ("POST", "/api/auth/z/login"), ("GET", "/api/oauth/userinfo")],
            requests.Select(r => (r.Method, r.Path)));
        Assert.Contains("\"provider\":\"zai\"", requests[0].Body);

        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal("jwt-1", protector.Unprotect(account.AccessTokenEnc!)); // 访问令牌 = 业务 JWT
        Assert.Equal("oauth-1", protector.Unprotect(account.RefreshTokenEnc!)); // 刷新槽 = OAuth token
    }

    [Fact]
    public async Task Template_Variant_Preserves_Price_Key_And_Endpoint_On_Upgrade()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { templateId = "siliconflow", variantId = "cn", apiKey = "sk-test-long-enough", models = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        Assert.Equal("siliconflow-cn", body["priceKey"]!.GetValue<string>());
        Assert.Equal("https://api.siliconflow.cn/v1", body["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        catalog.Get("siliconflow")!.Version++;
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow-cn", body!["priceKey"]!.GetValue<string>());
        Assert.Equal("https://api.siliconflow.cn/v1", body["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(2, body["templateVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Template_Update_Merges_Unmodified_Defaults_Without_Overwriting_User_Fields()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host, "openrouter");
        await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "My provider", priceMultiplier = 1.5 });
        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("openrouter")!;
        template.Version = 3; // the embedded catalog already ships v2; only a newer version triggers the merge
        template.DefaultHeaders["X-Title"] = "Astra v2";
        template.DefaultHeaders["X-New"] = "default";
        var (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("My provider", body!["name"]!.GetValue<string>());
        Assert.Equal(1.5m, body["priceMultiplier"]!.GetValue<decimal>());
        Assert.Equal("Astra v2", body["extraHeaders"]!["X-Title"]!.GetValue<string>());
        Assert.Equal("default", body["extraHeaders"]!["X-New"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_Provider_Settings_Return_400_Without_Persisting_Partial_Changes()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Changed", priceMultiplier = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Test", (await host.Db.Providers.GetAsync(id))!.Name);
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { endpoints = new[] { new { protocol = "openai-chat", baseUrl = "file:///etc/passwd" } } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Remote_Model_Discovery_Uses_Upstream_Credentials_And_Auto_Links()
    {
        var requests = new List<string>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                requests.Add(request.RequestUri!.AbsolutePath);
                Assert.Equal("upstream-test-key", request.Headers.Authorization!.Parameter);
                Assert.False(request.Headers.Contains("X-Astra-Admin"));
                await Task.CompletedTask;
                return JsonResponse("{\"data\":[{\"id\":\"gpt-5\"},{\"id\":\"unknown-model\"}]}");
            })));
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { name = "Mock", apiKey = "upstream-test-key", endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal("gpt-5", remote.Single(m => m!["id"]!.GetValue<string>() == "gpt-5")!["linkedSystemModelId"]!.GetValue<string>());
        Assert.Equal(["/v1/models"], requests);
    }

    [Fact]
    public async Task Connection_Test_Logs_Actual_Reported_Usage_And_Cost()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("gpt-5", body["model"]!.GetValue<string>());
                return JsonResponse("{\"model\":\"gpt-5-2026-05-01\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":60},\"completion_tokens_details\":{\"reasoning_tokens\":10}}}");
            })));
        var id = await Create(host);
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "gpt-5" } });
        var result = await host.TestAsync(id, new { modelId = "gpt-5", stream = false });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var r = records.Items.Single();
        Assert.Equal("gpt-5", r.RequestedModel);
        Assert.Equal("gpt-5-2026-05-01", r.ResponseModel);
        Assert.Equal(100, r.TotalInputTokens);
        Assert.Equal(20, r.TotalOutputTokens);
        Assert.Equal(60, r.CacheReadTokens);
        Assert.Equal(10, r.ReasoningTokens);
        Assert.Equal("reported", r.UsageSource);
        Assert.True(r.CostNanoUsd > 0);
    }

    [Theory]
    [InlineData("openai-chat", "model")]
    [InlineData("openai-responses", "model")]
    [InlineData("anthropic", "model")]
    [InlineData("gemini", "modelVersion")]
    [InlineData("gemini", "model_version")]
    public async Task Connection_Test_Records_Only_The_Model_Id_Declared_By_The_Api(string protocol, string modelField)
    {
        foreach (var reported in new[] { true, false })
        {
            var upstreamBody = new JsonObject();
            if (reported) upstreamBody[modelField] = "actual-reported-model";
            await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse(upstreamBody.ToJsonString())))));
            var id = await CreateCustomAsync(host, protocol,
                protocol == "gemini" ? "https://example.invalid/v1beta" : "https://example.invalid/v1", "none");
            var result = await host.TestAsync(id, new { modelId = "requested-alias", stream = false });
            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.True(result.Done!["ok"]!.GetValue<bool>());
            await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
            var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
            var record = Assert.Single(records.Items);
            Assert.Equal("requested-alias", record.RequestedModel);
            Assert.Equal("requested-alias", record.UpstreamModel);
            Assert.Equal(reported ? "actual-reported-model" : null, record.ResponseModel);
        }
    }

    [Fact]
    public async Task Malformed_Provider_Payloads_Return_400_Json_And_Never_Persist_Partially()
    {
        await using var host = await TestHost.StartAsync();

        // create with a duplicate protocol endpoint leaves nothing behind
        var (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Broken",
            endpoints = new[]
            {
                new { protocol = "openai-chat", baseUrl = "https://a.example.invalid/v1" },
                new { protocol = "openai-chat", baseUrl = "https://b.example.invalid/v1" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        var id = await Create(host);

        // wrong JSON field types and explicit nulls are rejected as 400 JSON errors
        foreach (var bad in new JsonObject[]
        {
            new() { ["name"] = 123 },
            new() { ["name"] = "NewName", ["settings"] = "not-an-object" },
            new() { ["name"] = "NewName", ["endpoints"] = "not-an-array" },
            new() { ["endpoints"] = null },
            new() { ["extraHeaders"] = null },
            new() { ["priceMultiplier"] = "expensive" },
        })
        {
            (status, error) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", bad);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotNull(error!["error"]);
        }

        // reserved / malformed / case-insensitively duplicate extra headers are rejected,
        // together with the valid "name" field of the same request
        foreach (var headers in new[]
        {
            new JsonObject { ["Host"] = "evil.example" },
            new JsonObject { ["X-Astra-Admin"] = "1" },
            new JsonObject { ["Bad Header"] = "x" },
            new JsonObject { ["X-Dup"] = "a", ["x-dup"] = "b" },
            new JsonObject { ["X-Ok"] = "a\u0007b" },
        })
        {
            (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}",
                new JsonObject { ["extraHeaders"] = headers.DeepClone(), ["name"] = "NewName" });
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }

        // apiKey must be a string; explicit null is refused (only "" clears it)
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["apiKey"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // none of the failed writes persisted their valid fields either
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        Assert.Empty(stored.ExtraHeaders);
    }

    [Fact]
    public async Task Short_Api_Keys_Are_Masked_As_Bullets_Only()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Short key",
            endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } },
            apiKey = "abc",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("••••", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain("abc", body.ToJsonString());
        Assert.Equal("••••", (await host.GetJsonAsync("/api/providers")).AsArray().Single()!["apiKeyMasked"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adding_Models_Trims_Dedupes_And_Returns_Only_New_Rows()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);

        // invalid model id payloads are rejected before anything is written
        var (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new JsonObject { ["modelIds"] = "not-an-array" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "   " } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray());

        // same id and whitespace-only-padded ids collapse into one row each; only new rows are returned
        (status, var added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "gpt-5", " gpt-5 ", "gpt-5", " gpt-6" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5", "gpt-6"], added!.AsArray().Select(m => m!["modelId"]!.GetValue<string>()).ToList());

        // re-adding everything (still padded) adds nothing and returns []
        (status, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "gpt-5", "gpt-6", " gpt-6 " } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(added!.AsArray());
        Assert.Equal(2, (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Count);
    }

    [Fact]
    public async Task Provider_Order_Requires_Every_Id_Exactly_Once_And_Keeps_The_Old_Order_On_Errors()
    {
        await using var host = await TestHost.StartAsync();
        var a = await Create(host);
        var b = await Create(host);
        var c = await Create(host);
        Assert.Equal([a, b, c], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());

        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/providers/order", new { ids = new[] { c, a, b } });
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal([c, a, b], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());

        foreach (var bad in new[]
        {
            new[] { a, b, c, "unknown-id" }, // unknown id
            new[] { c, c, a },               // duplicate id
            new[] { a, b },                  // missing id
        })
        {
            (status, var error) = await host.SendAsync(HttpMethod.Put, "/api/providers/order", new { ids = bad });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotNull(error!["error"]);
            Assert.Equal([c, a, b], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());
        }
    }

    [Fact]
    public async Task Provider_Creation_With_Duplicate_Model_Ids_Rolls_Back_The_Whole_Transaction()
    {
        await using var host = await TestHost.StartAsync(); // temp data dir, never the user's real one
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Tx",
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        var models = new List<ProviderModel>
        {
            new() { ProviderId = provider.Id, ModelId = "same-model" },
            new() { ProviderId = provider.Id, ModelId = "same-model" },
        };

        await Assert.ThrowsAsync<SqliteException>(() => host.Db.Providers.InsertWithModelsAsync(provider, models));
        Assert.Null(await host.Db.Providers.GetAsync(provider.Id));
        Assert.Empty(await host.Db.Providers.ListModelsAsync(provider.Id));
    }

    [Fact]
    public async Task Connection_Test_Uses_Protocol_Specific_Paths_Bodies_And_Credentials()
    {
        var sent = new List<Sent>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                return JsonResponse(request.RequestUri!.AbsolutePath switch
                {
                    "/v1/responses" => """{"id":"resp_test","status":"completed","output":[]}""",
                    "/anthropic/v1/messages" => """{"id":"msg_test","role":"assistant","content":[{"type":"text","text":"OK"}]}""",
                    "/v1beta/models/gemini-test:generateContent" => """{"candidates":[{"content":{"parts":[{"text":"OK"}]}}]}""",
                    _ => """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""",
                });
            })));

        var chat = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "sk-chat-key-0001");
        var responses = await CreateCustomAsync(host, "openai-responses", "https://example.invalid/v1", "bearer", "sk-resp-key-0002");
        var anthropic = await CreateCustomAsync(host, "anthropic", "https://example.invalid/anthropic", "x-api-key", "sk-ant-key-0003");
        var gemini = await CreateCustomAsync(host, "gemini", "https://example.invalid/v1beta", "x-goog-api-key", "gm-key-0004");
        var full = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/custom/endpoint?flag=1", "bearer", "sk-full-key-0005", fullUrl: true);

        var result = await host.TestAsync(chat, new { modelId = "chat-model-x", stream = false });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        var r = sent.Single(s => s.Path == "/v1/chat/completions");
        Assert.Equal("https://example.invalid/v1/chat/completions", r.Url);
        Assert.Equal("Bearer sk-chat-key-0001", r.Authorization);
        Assert.Equal("application/json", r.Accept);
        var body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("chat-model-x", body["model"]!.GetValue<string>());
        Assert.Equal(256, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Reply with OK.", body["messages"]![0]!["content"]!.GetValue<string>());

        result = await host.TestAsync(responses, new { modelId = "resp-model-x", stream = false });
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/v1/responses");
        Assert.Equal("Bearer sk-resp-key-0002", r.Authorization);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("resp-model-x", body["model"]!.GetValue<string>());
        // The codex backend only accepts the array form of input (same shape GatewayPipeline forces);
        // the probe sends a single user text message.
        var input = Assert.Single(body["input"]!.AsArray())!;
        Assert.Equal("user", input["role"]!.GetValue<string>());
        var inputText = Assert.Single(input["content"]!.AsArray())!;
        Assert.Equal("Reply with OK.", inputText["text"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal(256, body["max_output_tokens"]!.GetValue<int>());

        result = await host.TestAsync(anthropic, new { modelId = "claude-model-x", stream = false });
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/anthropic/v1/messages");
        Assert.Equal("https://example.invalid/anthropic/v1/messages", r.Url);
        Assert.Equal("sk-ant-key-0003", r.XApiKey);
        Assert.Null(r.Authorization);
        Assert.Equal("2023-06-01", r.AnthropicVersion);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("claude-model-x", body["model"]!.GetValue<string>());
        Assert.Equal(256, body["max_tokens"]!.GetValue<int>());

        result = await host.TestAsync(gemini, new { modelId = "gemini-test", stream = false });
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/v1beta/models/gemini-test:generateContent");
        Assert.Equal("gm-key-0004", r.XGoogApiKey);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("Reply with OK.", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(256, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());

        // fullUrl endpoints are requested verbatim; no path is appended
        result = await host.TestAsync(full, new { modelId = "full-model-x", stream = false });
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Url == "https://example.invalid/custom/endpoint?flag=1");
        Assert.Equal("full-model-x", JsonNode.Parse(r.Body!)!["model"]!.GetValue<string>());
        Assert.Equal(5, sent.Count);

        // the explicit protocol wins over the preferred one: the very same provider is tested over Anthropic next
        var compatible = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "sk-chat-key-0001");
        Assert.Equal("openai-chat", (await host.GetJsonAsync($"/api/providers/{compatible}"))["preferredUpstreamProtocols"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.TestAsync(compatible, new { modelId = "x", protocol = "gemini" })).Status);
    }

    [Fact]
    public async Task Remote_Models_Follow_Gemini_Page_Tokens_Keep_The_Base_Query_And_Encode_The_Query_Key()
    {
        var sent = new List<Sent>();
        var page = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                page++;
                return JsonResponse(page == 1
                    ? """{"models":[{"name":"models/gemini-test","displayName":"Gemini Test"},{"name":"models/plain-2"}],"nextPageToken":"tok 2"}"""
                    : """{"models":[{"name":"models/gemini-2"}]}""");
            })));

        const string key = "gk+1/2=&q";
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Gemini",
            endpoints = new[] { new { protocol = "gemini", baseUrl = "https://example.invalid/v1beta?env=prod" } },
            authScheme = "query-key",
            apiKey = key,
            models = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created!["id"]!.GetValue<string>();

        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal(["gemini-2", "gemini-test", "plain-2"], remote.Select(m => m!["id"]!.GetValue<string>()).ToList());
        Assert.Equal("Gemini Test", remote.Single(m => m!["id"]!.GetValue<string>() == "gemini-test")!["displayName"]!.GetValue<string>());
        Assert.All(remote, m => Assert.False(m!["id"]!.GetValue<string>().StartsWith("models/", StringComparison.Ordinal)));

        Assert.Equal(2, sent.Count);
        Assert.StartsWith("https://example.invalid/v1beta/models?", sent[0].Url);
        Assert.Contains("env=prod", sent[0].Query);              // base-URL query survives
        Assert.Contains("key=gk%2B1%2F2%3D%26q", sent[0].Query); // query-key auth is URI-encoded
        Assert.DoesNotContain("pageToken", sent[0].Query);
        Assert.Contains("pageToken=tok%202", sent[1].Query);
        Assert.Contains("env=prod", sent[1].Query);
        Assert.Contains("key=gk%2B1%2F2%3D%26q", sent[1].Query);
    }

    [Fact]
    public async Task Remote_Models_Paginate_Anthropic_With_After_Id()
    {
        var sent = new List<Sent>();
        var page = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                page++;
                return JsonResponse(page == 1
                    ? """{"data":[{"id":"claude-b"},{"id":"claude-a"}],"has_more":true,"last_id":"claude-b"}"""
                    : """{"data":[{"id":"claude-c"}],"has_more":false}""");
            })));

        var id = await CreateCustomAsync(host, "anthropic", "https://example.invalid/v1", "x-api-key", "sk-ant-remote-0006");
        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal(["claude-a", "claude-b", "claude-c"], remote.Select(m => m!["id"]!.GetValue<string>()).ToList());

        Assert.Equal(2, sent.Count);
        Assert.EndsWith("/v1/models", sent[0].Url);
        Assert.Equal("https://example.invalid/v1/models?after_id=claude-b", sent[1].Url);
        Assert.Equal("sk-ant-remote-0006", sent[1].XApiKey);
    }

    [Fact]
    public async Task Remote_Models_Non_Advancing_Pagination_Fails_With_502_Instead_Of_A_Partial_List()
    {
        var sent = new List<Sent>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                await Task.CompletedTask;
                return JsonResponse("""{"data":[{"id":"m1"}],"has_more":true,"last_id":"m1"}""");
            })));

        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");
        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("pagination", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Remote_Models_Upstream_Failures_Map_To_502_Json()
    {
        Func<HttpResponseMessage> respond = () => JsonResponse("{}");
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return respond();
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");

        async Task Assert502Async(string fragment)
        {
            var (status, error) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
            Assert.Equal(HttpStatusCode.BadGateway, status);
            Assert.NotNull(error!["error"]);
            Assert.Contains(fragment, error["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }

        respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{not json", Encoding.UTF8, "text/plain") };
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("[]"); // a JSON array is not a model-list object
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("""{"data":{"id":"wrong-shape"}}""");
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("""{"data":[17]}"""); // rows must be objects
        await Assert502Async("invalid model list");
        respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        { Content = new StringContent("boom", Encoding.UTF8, "text/plain") };
        await Assert502Async("Upstream returned HTTP 500");
        respond = () => throw new HttpRequestException("connection refused");
        await Assert502Async("could not connect");
    }

    [Fact]
    public async Task Remote_Models_Timeout_Returns_504_Without_A_Real_Wait()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMilliseconds(400))
            .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler()));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");

        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Contains("timed out", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upstream_Error_Echoing_The_Api_Key_Is_Redacted_In_Response_And_Request_Log()
    {
        const string key = "sk-echo-test-key-1234";
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("{\"error\":{\"message\":\"Invalid API key provided: " + key + "\"}}", Encoding.UTF8, "application/json") };
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", key);

        var result = await host.TestAsync(id, new { modelId = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.False(result.Done!["ok"]!.GetValue<bool>());
        Assert.Equal(401, result.Done["httpStatus"]!.GetValue<int>());
        Assert.Contains("[redacted]", result.Done["error"]!.GetValue<string>());
        Assert.DoesNotContain(key, result.Text);

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var record = records.Items.Single();
        Assert.Equal("upstream_error", record.Status);
        Assert.Equal(401, record.HttpStatus);
        Assert.Contains("[redacted]", record.ErrorMessage);
        Assert.DoesNotContain(key, record.ErrorMessage);
    }

    [Fact]
    public async Task Remote_Models_Upstream_Error_Redacts_Echoed_Credentials_In_The_Json_Error()
    {
        const string bearerKey = "sk-remote-echo-key-5678";
        const string queryKey = "gq+secret=&1";
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                var message = request.RequestUri!.AbsolutePath.Contains("v1beta", StringComparison.Ordinal)
                    ? "{\"error\":{\"message\":\"API key not valid: " + queryKey + "\"}}"
                    : "{\"error\":{\"message\":\"Invalid API key: " + bearerKey + "\"}}";
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent(message, Encoding.UTF8, "application/json") };
            })));
        var openai = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", bearerKey);
        var gemini = await CreateCustomAsync(host, "gemini", "https://example.invalid/v1beta", "query-key", queryKey);

        foreach (var id in new[] { openai, gemini })
        {
            var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
            Assert.Equal(HttpStatusCode.BadGateway, status);
            var text = body!.ToJsonString();
            Assert.Contains("[redacted]", text);
            Assert.DoesNotContain(bearerKey, text);
            Assert.DoesNotContain(queryKey, text);
            Assert.DoesNotContain(Uri.EscapeDataString(queryKey), text);
        }
    }

    [Fact]
    public async Task Connection_Test_Reports_Failure_When_Upstream_Returns_Invalid_Json()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("not json at all", Encoding.UTF8, "text/plain") };
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "k");

        var result = await host.TestAsync(id, new { modelId = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.False(result.Done!["ok"]!.GetValue<bool>());
        Assert.Equal(200, result.Done["httpStatus"]!.GetValue<int>());
        Assert.Contains("invalid JSON", result.Done["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var record = records.Items.Single();
        Assert.Equal("upstream_error", record.Status);
        Assert.Equal(200, record.HttpStatus);
        Assert.Contains("invalid JSON", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Template_Update_Keeps_User_Header_And_Setting_Customs_While_Untouched_Fields_Follow()
    {
        await using var host = await TestHost.StartAsync();
        var modified = await Create(host, "anthropic");
        var untouched = await Create(host, "anthropic");

        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{modified}", new
        {
            extraHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2099-01-01" },
            settings = new JsonObject { ["auto_cache_control"] = "never" },
        });
        Assert.Equal(HttpStatusCode.OK, status);

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("anthropic")!;
        template.Version = 2;
        template.DefaultHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2024-10-22", ["x-new"] = "v2" };
        template.Settings = new JsonObject { ["auto_cache_control"] = false };

        (status, var a) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{modified}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, a!["templateVersion"]!.GetValue<int>());
        Assert.Equal("2099-01-01", a["extraHeaders"]!["anthropic-version"]!.GetValue<string>()); // user value kept
        Assert.Equal("v2", a["extraHeaders"]!["x-new"]!.GetValue<string>());
        Assert.Equal("never", a["settings"]!["auto_cache_control"]!.GetValue<string>());          // user value kept

        (status, var b) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{untouched}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, b!["templateVersion"]!.GetValue<int>());
        Assert.Equal("2024-10-22", b["extraHeaders"]!["anthropic-version"]!.GetValue<string>());  // followed the template
        Assert.Equal("v2", b["extraHeaders"]!["x-new"]!.GetValue<string>());
        Assert.False(b["settings"]!["auto_cache_control"]!.GetValue<bool>());                     // followed the template
    }

    [Fact]
    public async Task Template_Update_Appends_New_Default_Models_Without_Duplicates_Or_ReEnabling()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host, "custom-chat"); // template without default models
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "user-kept", "gpt-5" } });
        Assert.Equal(2, added!.AsArray().Count);
        var gpt = added.AsArray().Single(m => m!["modelId"]!.GetValue<string>() == "gpt-5")!;
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}/models/{gpt["id"]!.GetValue<long>()}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("custom-chat")!;
        template.Version = 2;
        template.Models = ["gpt-5", "brand-new-default"];

        (status, var body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!["templateVersion"]!.GetValue<int>());

        var models = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray();
        Assert.Equal(["user-kept", "gpt-5", "brand-new-default"], models.Select(m => m!["modelId"]!.GetValue<string>()).ToList());
        Assert.False(models.Single(m => m!["modelId"]!.GetValue<string>() == "gpt-5")!["enabled"]!.GetValue<bool>());
        Assert.True(models.Single(m => m!["modelId"]!.GetValue<string>() == "brand-new-default")!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Explicit_Empty_PriceKey_Uses_The_Official_System_Default_And_Survives_Every_Write()
    {
        await using var host = await TestHost.StartAsync();

        // an omitted or explicit-null price key keeps the historical meaning: inherit the template default
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { templateId = "siliconflow", name = "Inherit", apiKey = "sk-test-long-enough", models = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow", body!["priceKey"]!.GetValue<string>());
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        { ["templateId"] = "siliconflow", ["name"] = "Null key", ["apiKey"] = "sk-test-long-enough", ["priceKey"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow", body!["priceKey"]!.GetValue<string>());

        // an explicit empty price key is a real value: only the system model's official default price
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["templateId"] = "siliconflow",
            ["name"] = "Official default",
            ["apiKey"] = "sk-test-long-enough",
            ["priceKey"] = "",
            ["models"] = new JsonArray { "deepseek-v4-flash" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>());
        var id = body["id"]!.GetValue<string>();
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey); // the DB keeps the empty string too
        var m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("deepseek-v4-flash", m["modelId"]!.GetValue<string>());
        Assert.Equal("system_default", m["pricingSource"]!.GetValue<string>());
        Assert.Null(m["priceKey"]);
        Assert.Equal(0.15m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>()); // official, not the SF 0.22

        var (simStatus, sim) = await host.SendAsync(HttpMethod.Post, "/api/billing/simulate", new
        {
            providerId = id,
            modelId = "deepseek-v4-flash",
            usage = new { tokens = new { input = 1_000_000 } },
            requestTimeUtc = "2026-10-04T12:00:00Z", // a Sunday: no Mon-Fri UTC peak window, so the off-peak base price
        });
        Assert.Equal(HttpStatusCode.OK, simStatus);
        Assert.Equal("system_default", sim!["pricingSource"]!.GetValue<string>());
        Assert.Equal(150_000_000L, sim["totalNanos"]!.GetValue<long>()); // 1M × $0.15 / 1M tokens

        // patching other fields, duplicating, and a template update all keep the explicit empty string
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Renamed", priceMultiplier = 2 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>());
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        (status, var copy) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", copy!["priceKey"]!.GetValue<string>());
        Assert.Equal("", (await host.Db.Providers.GetAsync(copy["id"]!.GetValue<string>()))!.PriceKey);

        // null restores the historical inherit; the provider price table takes over again
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["priceKey"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["priceKey"]);
        Assert.Null((await host.Db.Providers.GetAsync(id))!.PriceKey);
        m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("provider_price", m["pricingSource"]!.GetValue<string>());
        Assert.Equal("siliconflow", m["priceKey"]!.GetValue<string>());
        Assert.Equal(0.22m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());

        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["priceKey"] = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("system_default", m["pricingSource"]!.GetValue<string>());
        Assert.Equal(0.15m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        catalog.Get("siliconflow")!.Version++;
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>()); // not reset to the template price
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        Assert.Equal(2, body["templateVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Provider_Input_Validation_Rejects_Null_Endpoint_Rows_Bad_Protocols_And_Control_Char_Api_Keys()
    {
        await using var host = await TestHost.StartAsync();

        // a null endpoint row is refused on create and leaves nothing behind
        var (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Broken",
            ["endpoints"] = new JsonArray { null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        // a non-string protocol is refused too
        (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Broken",
            ["endpoints"] = new JsonArray { new JsonObject { ["protocol"] = 123, ["baseUrl"] = "https://example.invalid/v1" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        // an apiKey with an interior control character is refused, and the valid name of the same body is not saved
        (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Would Have Persisted",
            ["endpoints"] = new JsonArray { new JsonObject { ["protocol"] = "openai-chat", ["baseUrl"] = "https://example.invalid/v1" } },
            ["apiKey"] = "sk-a\u0007b",
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        var id = await Create(host);

        // the same control-character rule applies on PATCH and drops the valid "name" of the same request
        (status, error) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject
        { ["name"] = "NewName", ["apiKey"] = "sk-a\u0007b" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        Assert.Null(stored.ApiKeyEnc);

        // a null endpoint row on PATCH is refused and the original configuration is left completely intact
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject
        { ["name"] = "NewName", ["endpoints"] = new JsonArray { null } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        var endpoint = Assert.Single(stored.Endpoints);
        Assert.Equal(ApiProtocol.OpenAIChat, endpoint.Protocol);
        Assert.Equal("https://example.invalid/v1", endpoint.BaseUrl);
    }

    private static async Task<string> Create(TestHost host, string? template = null)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            templateId = template, name = "Test", apiKey = template is null ? null : "sk-test-long-enough", models = Array.Empty<string>(),
            endpoints = template is null ? new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } } : null,
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    // ---------- streamed connection test ----------

    [Fact]
    public async Task Connection_Test_Streams_Metrics_Deltas_And_Usage_Then_Logs_The_Run()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
            {
                Assert.Equal("text/event-stream", request.Headers.Accept.Single().MediaType);
                var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
                Assert.True(body["stream"]!.GetValue<bool>());
                Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
                return Task.FromResult(SseResponse(
                    """{"model":"gpt-5-2026-05-01","choices":[{"delta":{"role":"assistant"}}]}""",
                    "",
                    """{"choices":[{"delta":{"content":"O"}}]}""",
                    """{"choices":[{"delta":{"content":"K"}}]}""",
                    """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":2}}"""));
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "sk-stream-key-1");

        var result = await host.TestAsync(id, new { modelId = "gpt-5", prompt = "Say OK." });

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(["start", "headers", "delta", "delta", "done"], result.Names);
        var done = result.Done!;
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.Equal("OK", done["text"]!.GetValue<string>());
        Assert.Equal("gpt-5-2026-05-01", done["responseModel"]!.GetValue<string>());
        Assert.NotNull(done["httpMs"]);
        Assert.True(done["ttftMs"]!.GetValue<long>() >= done["httpMs"]!.GetValue<long>());
        Assert.Contains("data:", done["raw"]!.GetValue<string>());
        Assert.Contains("[DONE]", done["raw"]!.GetValue<string>());
        Assert.Equal(12, done["usage"]!["inputTokens"]!.GetValue<long>());
        Assert.Equal(2, done["usage"]!["outputTokens"]!.GetValue<long>());

        // The upstream response headers travel with the "headers" event and in the done payload.
        var headers = result.Events[1].Data["headers"]!;
        Assert.Contains("text/event-stream", headers["Content-Type"]!.GetValue<string>());
        Assert.Contains("text/event-stream", done["headers"]!["Content-Type"]!.GetValue<string>());

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var record = (await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id })).Items.Single();
        Assert.Equal("success", record.Status);
        Assert.True(record.Stream);
        Assert.NotNull(record.TtftMs);
        Assert.Equal(12, record.TotalInputTokens);
        Assert.Equal(2, record.TotalOutputTokens);
        Assert.Equal("reported", record.UsageSource);
    }

    [Fact]
    public async Task Connection_Test_Gemini_Streams_From_The_Alt_Sse_Url()
    {
        var urls = new List<string>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
            {
                urls.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(SseResponse("""{"candidates":[{"content":{"parts":[{"text":"OK"}]}}],"modelVersion":"gemini-3-pro-001"}"""));
            })));
        var id = await CreateCustomAsync(host, "gemini", "https://example.invalid/v1beta", "query-key", "gm-key");

        var result = await host.TestAsync(id, new { modelId = "gemini-test" });

        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        Assert.Equal("https://example.invalid/v1beta/models/gemini-test:streamGenerateContent?alt=sse&key=gm-key", urls.Single());
        Assert.Equal("gemini-3-pro-001", result.Done["responseModel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connection_Test_Rejects_Bad_Parameters_Before_Streaming()
    {
        await using var host = await TestHost.StartAsync();
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "none");
        foreach (var body in new object[]
        {
            new { modelId = "m", protocol = "gemini" },   // no such endpoint on this provider
            new { modelId = "m", maxOutputTokens = 0 },
            new { modelId = "m", maxOutputTokens = 4097 },
            new { modelId = "m", prompt = new string('x', 4001) },
        })
        {
            var result = await host.TestAsync(id, body);
            Assert.Equal(HttpStatusCode.BadRequest, result.Status);
            Assert.Equal([], result.Names); // nothing streamed: the error is plain JSON
            Assert.NotNull(result.Done!["error"]);
        }
    }

    [Fact]
    public async Task Connection_Test_Does_Not_Retry_On_Unauthorized()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("""{"error":{"message":"bad key"}}""", Encoding.UTF8, "application/json") });
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "sk-retry-key-1");

        var result = await host.TestAsync(id, new { modelId = "gpt-5" });

        Assert.Equal(1, calls);
        Assert.False(result.Done!["ok"]!.GetValue<bool>());
        Assert.Equal(401, result.Done["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public async Task Connection_Test_Redacts_Echoed_Credentials_In_Response_Headers()
    {
        const string key = "sk-header-echo-key-9999";
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
            {
                // Some gateways echo what they received (and the CORS-ish mirrors below) in their own headers.
                var response = JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
                response.Headers.TryAddWithoutValidation("x-echoed-authorization", $"Bearer {key}");
                response.Headers.TryAddWithoutValidation("access-control-expose-headers", "authorization");
                response.Headers.TryAddWithoutValidation("x-request-id", "req-42");
                return Task.FromResult(response);
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", key);

        var result = await host.TestAsync(id, new { modelId = "gpt-5", stream = false });

        var headers = result.Done!["headers"]!;
        Assert.Equal("req-42", headers["X-Request-ID"]!.GetValue<string>());
        // A header echoing the credential we sent is redacted; header names are kept verbatim.
        Assert.Equal("[redacted]", headers["x-echoed-authorization"]!.GetValue<string>());
        Assert.Equal("authorization", headers["Access-Control-Expose-Headers"]!.GetValue<string>());
        Assert.DoesNotContain(key, result.Text);
    }

    [Fact]
    public async Task Connection_Test_Applies_The_Privacy_Guard_And_Reports_It_In_The_Request_Log()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
            {
                calls++;
                var body = request.Content!.ReadAsStringAsync().Result;
                // Redacted: the placeholder goes upstream instead of the card number.
                Assert.DoesNotContain("4111111111111111", body);
                Assert.Contains("[REDACTED:credit_card#1]", body);
                return Task.FromResult(JsonResponse("""{"choices":[{"message":{"content":"[REDACTED:credit_card#1] seen"}}]}"""));
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "none");
        await EnablePrivacyAsync(host, PrivacyActions.Redact);

        var result = await host.TestAsync(id, new { modelId = "gpt-5", stream = false, prompt = "card 4111111111111111" });

        Assert.Equal(1, calls);
        var done = result.Done!;
        Assert.True(done["ok"]!.GetValue<bool>());
        // The response is restored before it reaches the caller, and the raw view is the client-facing body too:
        // the upstream itself only ever saw the placeholder (asserted in the stub above).
        Assert.Equal("4111111111111111 seen", done["text"]!.GetValue<string>());
        Assert.Contains("4111111111111111", done["raw"]!.GetValue<string>());

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var record = (await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id })).Items.Single();
        Assert.NotNull(record.PrivacyJson);
        Assert.Equal("success", record.Status);
    }

    [Fact]
    public async Task Connection_Test_Is_Blocked_By_The_Privacy_Guard_Without_Calling_Upstream()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
            {
                calls++;
                return Task.FromResult(JsonResponse("""{"choices":[]}"""));
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "none");
        await EnablePrivacyAsync(host, PrivacyActions.Block);

        var result = await host.TestAsync(id, new { modelId = "gpt-5", stream = false, prompt = "card 4111111111111111" });

        Assert.Equal(0, calls);
        Assert.Equal(JsonValueKind.Object, result.Done!.GetValueKind());
        Assert.False(result.Done["ok"]!.GetValue<bool>());
        Assert.Contains("privacy guard", result.Done["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var record = (await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id })).Items.Single();
        Assert.Equal("blocked", record.Status);
        Assert.NotNull(record.PrivacyJson);
    }

    [Fact]
    public async Task Connection_Test_Decodes_A_Json_Body_When_The_Upstream_Ignores_The_Stream_Flag()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
                Task.FromResult(JsonResponse("""{"model":"gpt-5-2026-05-01","choices":[{"message":{"content":"OK"}}]}""")))));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "none");

        var result = await host.TestAsync(id, new { modelId = "gpt-5" });

        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        Assert.Equal("OK", result.Done["text"]!.GetValue<string>());
        Assert.Equal("gpt-5-2026-05-01", result.Done["responseModel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connection_Test_Truncates_A_Huge_Raw_Body()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"OK"}}],"padding":""" + "\"" + new string('x', 300 * 1024) + "\"}")))));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "none");

        var result = await host.TestAsync(id, new { modelId = "gpt-5", stream = false });

        var done = result.Done!;
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.True(done["rawTruncated"]!.GetValue<bool>());
        Assert.True(done["raw"]!.GetValue<string>().Length < 270 * 1024);
    }

    /// <summary>Enables the privacy guard with one action applied to every category (built-in rules included).</summary>
    private static async Task EnablePrivacyAsync(TestHost host, string action)
    {
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/privacy", new
        {
            enabled = true,
            dryRun = false,
            restoreResponses = true,
            recordSamples = false,
            defaultAction = action,
            categoryActions = new Dictionary<string, string>(),
            clientDefaults = new Dictionary<string, string>(),
            customRules = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A streamed JSON-Lines fake upstream: one SSE frame per line (blank lines become the keep-alive comment).</summary>
    private static HttpResponseMessage SseResponse(params string[] payloads)
    {
        var content = new StringBuilder();
        foreach (var payload in payloads)
            content.Append(payload.Length == 0 ? ": keep-alive\n\n" : "data: " + payload + "\n\n");
        content.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static async Task<string> CreateCustomAsync(TestHost host, string protocol, string baseUrl,
        string? authScheme = null, string? apiKey = null, bool fullUrl = false)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Probe",
            endpoints = new[] { new { protocol, baseUrl, fullUrl } },
            authScheme,
            apiKey,
            models = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    /// <summary>The values of one upstream request, copied out before the message is disposed.</summary>
    private sealed record Sent(
        HttpMethod Method, string Url, string Path, string Query, string? Accept,
        string? Authorization, string? XApiKey, string? XGoogApiKey, string? AnthropicVersion, string? Body);

    private static async Task<Sent> RecordAsync(HttpRequestMessage request)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
        return new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.RequestUri.AbsolutePath, request.RequestUri.Query,
            Header("Accept"), Header("Authorization"), Header("x-api-key"), Header("x-goog-api-key"), Header("anthropic-version"),
            request.Content is null ? null : await request.Content.ReadAsStringAsync());
    }

    /// <summary>Hangs until the (short) named-client timeout cancels the request — no real upstream wait.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK); // never reached; the delay is always cancelled
        }
    }
}
