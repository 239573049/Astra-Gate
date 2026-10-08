using System.Net;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;

namespace Astra.Gateway.Tests;

public class UpstreamAuthResolverTests
{
    private static async Task<AstraDatabase> NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-gw-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return await AstraDatabase.InitializeAsync(new AstraPaths(dir));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["enc:".Length..]));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        private readonly string _responseJson;

        public StubHandler(string responseJson) => _responseJson = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (AstraDatabase Db, StubHandler Handler, SubscriptionTokenService Tokens, UpstreamAuthResolver Resolver)
        Build(AstraDatabase db, string responseJson)
    {
        var handler = new StubHandler(responseJson);
        var tokens = new SubscriptionTokenService(db.Accounts, new FakeProtector(), new StubFactory(handler),
            new FixedClock(Now));
        var resolver = new UpstreamAuthResolver(db, new FakeProtector(), tokens);
        return (db, handler, tokens, resolver);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Chatgpt_Subscription_Tokens_Also_Resolve_The_Account_Header()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        // codex 真实令牌把账号 id 放在命名空间 claim 里（https://api.openai.com/auth）。
        await db.Accounts.InsertAsync(Account(
            FakeJwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"ws-1"},"exp":9999999999}"""),
            TimeSpan.FromHours(1)));
        var (_, _, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Equal("chatgpt-account-id", auth!.ExtraHeaderName);
        Assert.Equal("ws-1", auth.ExtraHeaderValue);
    }

    [Fact]
    public async Task Chatgpt_Account_Header_Also_Accepts_The_Flat_Claim()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account(FakeJwt("""{"chatgpt_account_id":"ws-flat","exp":9999999999}"""), TimeSpan.FromHours(1)));
        var (_, _, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Equal("ws-flat", auth!.ExtraHeaderValue);
    }

    /// <summary>
    /// base64url 段长度 %4==3 时要补一个 '='（不是两个）：补错的实现会抛异常、claim 静默变 null
    /// —— 大约四分之一的真实 JWT 段落落在这个余数上。两个 vector 分别是 %4==3 与 %4==2。
    /// </summary>
    [Theory]
    [InlineData("eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoid3MtMSJ9LCJwYWQiOiJ4In0", "ws-1")]
    [InlineData("eyJodHRwczovL2FwaS5vcGVuYWkuY29tL2F1dGgiOnsiY2hhdGdwdF9hY2NvdW50X2lkIjoid3MtMSJ9LCJwYWQiOiIifQ", "ws-1")]
    public async Task Chatgpt_Account_Header_Decodes_Every_Base64Url_Padding_Length(string payload, string accountId)
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account($"eyJhbGciOiJub25lIn0.{payload}.sig", TimeSpan.FromHours(1)));
        var (_, _, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Equal(accountId, auth!.ExtraHeaderValue);
    }

    [Fact]
    public async Task Non_Chatgpt_Tokens_Carry_No_Account_Header()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account("at-OLD", TimeSpan.FromHours(1))); // opaque token, not a JWT
        var (_, _, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Null(auth!.ExtraHeaderName);
        Assert.Null(auth.ExtraHeaderValue);
    }

    [Fact]
    public void Apply_Writes_The_Bearer_And_The_Account_Header()
    {
        var auth = new UpstreamAuth(AuthSchemes.Bearer, "Authorization", "Bearer jwt", null, null, "chatgpt-account-id", "ws-1");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses");

        auth.Apply(request);

        Assert.Equal("Bearer jwt", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("ws-1", request.Headers.GetValues("chatgpt-account-id").Single());
    }

    /// <summary>A syntactically valid (unsigned) JWT; only the payload claims matter here.</summary>
    private static string FakeJwt(string payloadJson)
    {
        static string Segment(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment("""{"alg":"none"}""")}.{Segment(payloadJson)}.sig";
    }

    private static async Task InsertSubscriptionProviderAsync(AstraDatabase db)
    {
        var provider = new Provider
        {
            Id = "p-claude-sub",
            Name = "Claude 订阅",
            TemplateId = "claude-subscription",
            AuthScheme = AuthSchemes.OAuthSubscription,
            Settings = System.Text.Json.Nodes.JsonNode.Parse(
                """{"subscription_oauth":{"verified":true,"client_id":"test-client"}}""")!.AsObject(),
        };
        await db.Providers.InsertAsync(provider);
    }

    private static ProviderAccount Account(string token, TimeSpan expiresIn) => new()
    {
        Id = "acc-1",
        ProviderId = "p-claude-sub",
        DisplayName = "me@example.com",
        AccessTokenEnc = new FakeProtector().Protect(token),
        RefreshTokenEnc = new FakeProtector().Protect("refresh-token-1"),
        ExpiresAtUtc = Now + expiresIn,
        Status = AccountStatus.Active,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private const string RefreshResponse = """{"access_token":"at-NEW","refresh_token":null,"expires_in":3600}""";

    [Fact]
    public async Task Static_Bearer_Keys_Decrypt_Into_The_Right_Header()
    {
        var db = await NewDb();
        await db.Providers.InsertAsync(new Provider
        {
            Id = "p1",
            Name = "Static",
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = new FakeProtector().Protect("sk-static"),
        });
        var (_, _, _, resolver) = Build(db, "{}");

        var auth = await resolver.ResolveAsync("p1", null);

        Assert.NotNull(auth);
        Assert.Equal("Authorization", auth.HeaderName);
        Assert.Equal("Bearer sk-static", auth.HeaderValue);
        Assert.Null(auth.QueryName);
    }

    [Fact]
    public async Task Query_Key_Scheme_Resolves_As_A_Query_Parameter()
    {
        var db = await NewDb();
        await db.Providers.InsertAsync(new Provider
        {
            Id = "p-gemini",
            Name = "Gemini",
            AuthScheme = AuthSchemes.QueryKey,
            ApiKeyEnc = new FakeProtector().Protect("AIzaSyD-secret"),
        });
        var (_, _, _, resolver) = Build(db, "{}");

        var auth = await resolver.ResolveAsync("p-gemini", null);

        Assert.Equal("key", auth!.QueryName);
        Assert.Equal("AIzaSyD-secret", auth.QueryValue);
    }

    [Fact]
    public async Task Fresh_Subscription_Token_Is_Reused_Without_A_Network_Call()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account("at-OLD", TimeSpan.FromHours(1)));
        var (_, handler, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Equal("Bearer at-OLD", auth!.HeaderValue);
        Assert.Empty(handler.Bodies); // no refresh happened
    }

    [Fact]
    public async Task Stale_Subscription_Token_Triggers_Rotation_And_Persists()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account("at-OLD", TimeSpan.FromSeconds(10)));
        var (_, handler, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAsync("p-claude-sub", null);

        Assert.Equal("Bearer at-NEW", auth!.HeaderValue);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=refresh_token", body);
        var stored = await db.Accounts.GetAsync("acc-1");
        Assert.Equal("at-NEW", new FakeProtector().Unprotect(stored!.AccessTokenEnc!));
        Assert.Equal(AccountStatus.Active, stored.Status);
    }

    [Fact]
    public async Task After_Unauthorized_The_Token_Is_Force_Refreshed()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        await db.Accounts.InsertAsync(Account("at-OLD", TimeSpan.FromHours(1)));
        var (_, handler, _, resolver) = Build(db, RefreshResponse);

        var auth = await resolver.ResolveAfterUnauthorizedAsync("p-claude-sub", "acc-1");

        Assert.Equal("Bearer at-NEW", auth!.HeaderValue);
        Assert.NotEmpty(handler.Bodies);
    }

    [Fact]
    public async Task A_Dead_Pinned_Account_Is_Auto_Disabled_And_An_Active_One_Takes_Over()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        var dead = Account("at-DEAD", TimeSpan.FromHours(1));
        dead.Id = "acc-dead";
        dead.Status = AccountStatus.Revoked;
        await db.Accounts.InsertAsync(dead);
        var alive = Account("at-LIVE", TimeSpan.FromHours(1));
        alive.Id = "acc-live";
        await db.Accounts.InsertAsync(alive);
        var (_, handler, _, resolver) = Build(db, RefreshResponse);

        // The client binding pins the revoked account; resolution disables it automatically
        // and serves the request with the first active account instead.
        var auth = await resolver.ResolveAsync("p-claude-sub", "acc-dead");

        Assert.Equal("Bearer at-LIVE", auth!.HeaderValue);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Missing_Account_Is_A_Clear_Subscription_Error()
    {
        var db = await NewDb();
        await InsertSubscriptionProviderAsync(db);
        var (_, _, _, resolver) = Build(db, RefreshResponse);

        var error = await Assert.ThrowsAsync<SubscriptionAuthException>(
            () => resolver.ResolveAsync("p-claude-sub", null));
        Assert.Contains("尚未登录", error.Message);

        // Unknown provider ids resolve to null (callers decide how to respond).
        Assert.Null(await resolver.ResolveAsync("ghost", null));
    }

    [Fact]
    public async Task Deleted_Provider_Resolves_To_Null()
    {
        var db = await NewDb();
        var (_, _, _, resolver) = Build(db, "{}");
        Assert.Null(await resolver.ResolveAsync("nope", null));
    }
}
