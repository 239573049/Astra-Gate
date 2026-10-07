using System.Net;
using System.Text;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class OAuthClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;
        public List<(string Url, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = [];

        public StubHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers) headers[name] = string.Join(",", values);
            Requests.Add((request.RequestUri!.ToString(), body, headers));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly SubscriptionOAuthConfig PkceConfig = new()
    {
        ProviderKey = "claude",
        AuthorizeUrl = "https://claude.ai/oauth/authorize",
        TokenUrl = "https://claude.ai/v1/oauth/token",
        ClientId = "client-1",
        Scopes = ["org:create_api_key", "user:inference"],
        UsePkce = true,
        Verified = true,
    };

    [Fact]
    public void Authorize_Url_Carries_Pkce_State_And_Scopes()
    {
        var url = OAuthClient.BuildAuthorizeUrl(PkceConfig, "http://127.0.0.1:17321/api/oauth/callback",
            "state-123", "challenge-456");

        Assert.StartsWith(PkceConfig.AuthorizeUrl, url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=client-1", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A17321%2Fapi%2Foauth%2Fcallback", url);
        Assert.Contains("state=state-123", url);
        Assert.Contains("scope=" + Uri.EscapeDataString("org:create_api_key user:inference"), url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=challenge-456", url);
    }

    [Fact]
    public async Task Exchange_Code_Posts_The_Pkce_Form_And_Parses_Tokens()
    {
        var handler = new StubHandler(_ => """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600,"scope":"openid"}""");
        var client = new OAuthClient(new StubFactory(handler));

        var token = await client.ExchangeCodeAsync(PkceConfig, "the-code", "the-verifier", "http://127.0.0.1/cb");

        Assert.Equal("at-1", token.AccessToken);
        Assert.Equal("rt-1", token.RefreshToken);
        Assert.Equal(3600, token.ExpiresIn);
        var (url, body, _) = Assert.Single(handler.Requests);
        Assert.Equal(PkceConfig.TokenUrl, url);
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("client_id=client-1", body);
        Assert.Contains("code_verifier=the-verifier", body);
    }

    [Fact]
    public async Task Refresh_Posts_The_Refresh_Grant()
    {
        var handler = new StubHandler(_ => """{"access_token":"at-2","expires_in":7200}""");
        var client = new OAuthClient(new StubFactory(handler));

        var token = await client.RefreshAsync(PkceConfig, "rt-old");

        Assert.Equal("at-2", token.AccessToken);
        Assert.Null(token.RefreshToken); // rotation optional
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=rt-old", body);
    }

    [Fact]
    public async Task OAuth_Errors_Surface_As_Protocol_Exceptions()
    {
        var handler = new StubHandler(_ => """{"error":"invalid_grant","error_description":"expired"}""");
        var client = new OAuthClient(new StubFactory(handler));

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(
            () => client.RefreshAsync(PkceConfig, "rt-bad"));

        Assert.Equal("invalid_grant", error.Error);
        Assert.True(error.IsInvalidGrant);
    }

    [Fact]
    public async Task Device_Flow_Starts_Polls_And_Reports_Pending()
    {
        var deviceConfig = new SubscriptionOAuthConfig
        {
            ProviderKey = "grok",
            TokenUrl = "https://auth.x.ai/oauth2/token",
            DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code",
            ClientId = "gc",
            Scopes = ["openid", "api:access"],
            ExtraDeviceParams = new Dictionary<string, string> { ["referrer"] = "grok-build" },
            UsePkce = false,
            Verified = true,
        };
        var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("grant_type")
                ? """{"error":"authorization_pending"}"""
                : """{"device_code":"dc-1","user_code":"ABCD-1234","verification_uri":"https://x.ai/activate","expires_in":600,"interval":5}""";
        });
        var client = new OAuthClient(new StubFactory(handler));

        var start = await client.StartDeviceAsync(deviceConfig);
        Assert.Equal("dc-1", start.DeviceCode);
        Assert.Equal("ABCD-1234", start.UserCode);
        Assert.Equal("https://x.ai/activate", start.VerificationUrl);
        Assert.Equal(5, start.Interval);

        var pending = await client.PollDeviceAsync(deviceConfig, start.DeviceCode);
        Assert.Equal(OAuthClient.DevicePollKind.Pending, pending.Kind);

        // the device request carries the scope set and the fixed provider quirks; the token poll does not
        var deviceBody = handler.Requests[0].Body;
        Assert.Contains("client_id=gc", deviceBody);
        Assert.Contains("scope=openid+api%3Aaccess", deviceBody);
        Assert.Contains("referrer=grok-build", deviceBody);
        Assert.DoesNotContain("scope", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Zcode_Exchange_Posts_The_Json_Envelope_And_Business_Login_Reruns_The_Hop()
    {
        var config = new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            Style = "zcode",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            ClientId = "client_x",
            ExtraHeaders = new Dictionary<string, string> { ["User-Agent"] = "ZCode/3.10.2" },
        };
        var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("\"token\"")
                ? """{"code":0,"data":{"access_token":"jwt-1"}}"""
                : """{"code":0,"data":{"zai":{"access_token":"oauth-1"}}}""";
        });
        var client = new OAuthClient(new StubFactory(handler));

        var oauthToken = await client.ExchangeZcodeCodeAsync(config, "zai", "the-code", "http://127.0.0.1:1/cb", "state-1");
        Assert.Equal("oauth-1", oauthToken); // 渠道键 zai 下取到的 OAuth token

        var apiToken = await client.ZcodeBusinessLoginAsync(config, oauthToken);
        Assert.Equal("jwt-1", apiToken); // 第四跳换出的业务 JWT

        // 换码是 JSON body {provider, code, redirect_uri, state}——没有 grant_type / client_id
        var (url, body, headers) = handler.Requests[0];
        Assert.Equal(config.TokenUrl, url);
        Assert.Contains("\"provider\":\"zai\"", body);
        Assert.Contains("\"code\":\"the-code\"", body);
        Assert.Contains("\"state\":\"state-1\"", body);
        Assert.DoesNotContain("grant_type", body);
        Assert.DoesNotContain("client_id", body);
        Assert.Equal("ZCode/3.10.2", headers["User-Agent"]); // 渠道要求 ZCode 身份头

        // 业务登录发的是 {token: <oauth token>}
        Assert.Contains("\"token\":\"oauth-1\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Zcode_Business_Error_Codes_Surface_As_Protocol_Exceptions()
    {
        var config = new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            Style = "zcode",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            ClientId = "client_x",
        };
        var handler = new StubHandler(_ => """{"code":2007,"msg":"http error"}""");
        var client = new OAuthClient(new StubFactory(handler));

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(
            () => client.ExchangeZcodeCodeAsync(config, "zai", "expired-code", "http://127.0.0.1:1/cb", "s"));
        Assert.Equal("zcode_2007", error.Error);
        Assert.Contains("http error", error.Message);
    }
}
