using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class SubscriptionTokenServiceTests
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public int Calls { get; private set; }

        public StubHandler(string responseJson) => _responseJson = responseJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly SubscriptionOAuthConfig Config = SubscriptionCatalog.Find("claude-subscription")!;

    private static ProviderAccount Account(string accessToken, TimeSpan expiresIn) => new()
    {
        Id = "acc-1",
        ProviderId = "p1",
        AccessTokenEnc = new FakeProtector().Protect(accessToken),
        RefreshTokenEnc = new FakeProtector().Protect("rt-1"),
        ExpiresAtUtc = Now + expiresIn,
        Status = AccountStatus.Active,
    };

    [Fact]
    public async Task Fresh_Tokens_Are_Reused_Without_A_Network_Call()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-fresh", TimeSpan.FromHours(1)));
        var handler = new StubHandler("""{"access_token":"at-2"}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        var token = await service.GetValidAccessTokenAsync((await store.GetAsync("acc-1"))!, Config);

        Assert.Equal("at-fresh", token);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Stale_Tokens_Rotate_The_Store_Entry()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-old", TimeSpan.FromSeconds(10)));
        var handler = new StubHandler("""{"access_token":"at-new","refresh_token":null,"expires_in":3600}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        var token = await service.GetValidAccessTokenAsync((await store.GetAsync("acc-1"))!, Config);

        Assert.Equal("at-new", token);
        Assert.Equal(1, handler.Calls);
        var stored = await store.GetAsync("acc-1");
        Assert.Equal("at-new", new FakeProtector().Unprotect(stored!.AccessTokenEnc!));
        Assert.Equal("rt-1", new FakeProtector().Unprotect(stored.RefreshTokenEnc!)); // rotation kept the old refresh token
        Assert.Equal(Now + TimeSpan.FromHours(1), stored.ExpiresAtUtc);
    }

    [Fact]
    public async Task Concurrent_Callers_Refresh_Only_Once()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-old", TimeSpan.FromSeconds(10)));
        var handler = new StubHandler("""{"access_token":"at-new","expires_in":3600}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());
        var account = (await store.GetAsync("acc-1"))!;

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => service.GetValidAccessTokenAsync(account, Config)));

        Assert.All(tokens, t => Assert.Equal("at-new", t));
        Assert.Equal(1, handler.Calls); // serialized per account: one HTTP refresh
    }

    [Fact]
    public async Task Invalid_Grant_Marks_The_Account_Revoked()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-old", TimeSpan.FromSeconds(10)));
        var handler = new StubHandler("""{"error":"invalid_grant","error_description":"revoked"}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        await Assert.ThrowsAsync<SubscriptionAuthException>(
            async () => await service.GetValidAccessTokenAsync((await store.GetAsync("acc-1"))!, Config));
        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
    }

    [Fact]
    public async Task Missing_Refresh_Token_Marks_The_Account_Expired()
    {
        var store = new InMemoryAccountStore();
        var account = Account("at-old", TimeSpan.FromSeconds(10));
        account.RefreshTokenEnc = null;
        await store.InsertAsync(account);
        var handler = new StubHandler("{}");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        await Assert.ThrowsAsync<SubscriptionAuthException>(
            () => service.GetValidAccessTokenAsync(account, Config));
        Assert.Equal(AccountStatus.Expired, (await store.GetAsync("acc-1"))!.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Force_Refresh_After_Unauthorized_Rotates_Even_A_Fresh_Token()
    {
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("at-old", TimeSpan.FromHours(1)));
        var handler = new StubHandler("""{"access_token":"at-new","expires_in":3600}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        var refreshed = await service.RefreshAfterUnauthorizedAsync((await store.GetAsync("acc-1"))!, Config);

        Assert.Equal("at-new", new FakeProtector().Unprotect(refreshed.AccessTokenEnc!));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>ZCode：没有标准 refresh 端点，刷新 = 用存下的 OAuth token 重跑业务登录。</summary>
    [Fact]
    public async Task Zcode_Refresh_Reruns_The_Business_Login_And_Keeps_The_Oauth_Token()
    {
        var config = new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            Style = "zcode",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            ClientId = "client_x",
        };
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("oauth-token-1", TimeSpan.FromSeconds(10)));
        var handler = new StubHandler("""{"code":0,"data":{"access_token":"jwt-NEW"}}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        var refreshed = await service.RefreshAsync((await store.GetAsync("acc-1"))!, config, force: false);

        Assert.Equal("jwt-NEW", new FakeProtector().Unprotect(refreshed.AccessTokenEnc!));
        // 刷新槽原样保留（ZCode 生产链路里它存的是 OAuth token，刷新 = 再拿它换一次业务令牌）
        Assert.Equal("rt-1", new FakeProtector().Unprotect(refreshed.RefreshTokenEnc!));
        Assert.Equal(AccountStatus.Active, refreshed.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Zcode_Refresh_On_An_Expired_Authorization_Revokes_The_Account()
    {
        var config = new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            Style = "zcode",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            ClientId = "client_x",
        };
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("oauth-token-1", TimeSpan.FromSeconds(10)));
        var handler = new StubHandler("""{"code":2007,"msg":"http error"}""");
        var service = new SubscriptionTokenService(store, new FakeProtector(), new StubFactory(handler), new FakeClock());

        var account = (await store.GetAsync("acc-1"))!;
        await Assert.ThrowsAsync<SubscriptionAuthException>(
            () => service.RefreshAsync(account, config, force: false));

        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
    }

    private sealed class RoutedFactory(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : IHttpClientFactory
    {
        private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var (status, body) = respond(request);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        public HttpClient CreateClient(string name) => new(new Handler(respond), disposeHandler: true);
    }

    /// <summary>BigModel：刷新 = 用 OAuth token 幂等地重跑 API Key 供应；key 不是 JWT，没有 exp，有效期给一天。</summary>
    [Fact]
    public async Task BigModel_Refresh_Reprovisions_The_Api_Key_With_A_Day_Of_Validity()
    {
        var config = SubscriptionCatalog.Find("bigmodel-subscription")!;
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("old-key.old-secret", TimeSpan.FromSeconds(10)));
        var factory = new RoutedFactory(request => (HttpStatusCode.OK, request.RequestUri!.AbsolutePath switch
        {
            "/api/biz/customer/getCustomerInfo" =>
                """{"code":200,"data":{"organizations":[{"organizationId":"o","projects":[{"projectId":"p"}]}]}}""",
            "/api/biz/v1/organization/o/projects/p/api_keys" =>
                """{"code":200,"data":[{"name":"zcode-api-key","apiKey":"key-2"}]}""",
            _ => """{"code":200,"data":{"secretKey":"sec-2"}}""",
        }));
        var service = new SubscriptionTokenService(store, new FakeProtector(), factory, new FakeClock());

        var refreshed = await service.RefreshAsync((await store.GetAsync("acc-1"))!, config, force: false);

        Assert.Equal("key-2.sec-2", new FakeProtector().Unprotect(refreshed.AccessTokenEnc!));
        Assert.Equal("rt-1", new FakeProtector().Unprotect(refreshed.RefreshTokenEnc!)); // OAuth token 原样保留
        Assert.Equal(Now.AddDays(1), refreshed.ExpiresAtUtc);
        Assert.Equal(AccountStatus.Active, refreshed.Status);
    }

    [Fact]
    public async Task BigModel_Refresh_With_A_Rejected_Oauth_Token_Revokes_The_Account()
    {
        var config = SubscriptionCatalog.Find("bigmodel-subscription")!;
        var store = new InMemoryAccountStore();
        await store.InsertAsync(Account("old-key.old-secret", TimeSpan.FromSeconds(10)));
        var service = new SubscriptionTokenService(store, new FakeProtector(),
            new RoutedFactory(_ => (HttpStatusCode.Unauthorized, """{"code":1001,"msg":"invalid token"}""")), new FakeClock());

        await Assert.ThrowsAsync<SubscriptionAuthException>(
            () => service.RefreshAsync(store.GetAsync("acc-1").GetAwaiter().GetResult()!, config, force: false));

        Assert.Equal(AccountStatus.Revoked, (await store.GetAsync("acc-1"))!.Status);
    }
}
