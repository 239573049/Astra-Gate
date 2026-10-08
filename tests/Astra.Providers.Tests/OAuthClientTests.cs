using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class OAuthClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;
        private readonly HttpStatusCode _status;
        public List<(string Url, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = [];

        public StubHandler(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK)
        {
            _respond = respond;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers) headers[name] = string.Join(",", values);
            Requests.Add((request.RequestUri!.ToString(), body, headers));
            return new HttpResponseMessage(_status)
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

    /// <summary>
    /// OpenAI（Codex）的 device code 备选路径（codex login --device-auth）：申请用 JSON body，
    /// 轮询给的是授权码 + 上游配对的 PKCE 对。默认登录走 PKCE 回环，这条由实例把
    /// style 改成 deviceauth、use_pkce 关掉后才生效。
    /// </summary>
    [Fact]
    public async Task Deviceauth_Requests_A_User_Code_And_Polls_For_The_Authorization_Grant()
    {
        var config = SubscriptionCatalog.Effective(SubscriptionCatalog.Find("openai-subscription")!, JsonNode.Parse(
            """{"subscription_oauth":{"style":"deviceauth","use_pkce":false}}""")!.AsObject());
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/accounts/deviceauth/usercode" =>
                """{"device_auth_id":"da-1","user_code":"ABCD-1234","interval":"5"}""",
            "/api/accounts/deviceauth/token" =>
                """{"authorization_code":"ac-1","code_challenge":"ch-1","code_verifier":"cv-1"}""",
            _ => """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""",
        });
        var client = new OAuthClient(new StubFactory(handler));

        var start = await client.StartDeviceAsync(config);
        Assert.Equal("da-1", start.DeviceCode); // 设备授权 id 放在 DeviceCode 槽里传给轮询
        Assert.Equal("ABCD-1234", start.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", start.VerificationUrl);
        Assert.Equal(5, start.Interval); // interval 是字符串

        var poll = await client.PollDeviceAuthAsync(config, start.DeviceCode, start.UserCode);
        Assert.Equal(OAuthClient.DevicePollKind.Success, poll.Kind);
        Assert.Null(poll.Token); // 这一步没有令牌
        Assert.Equal("ac-1", poll.Grant!.AuthorizationCode);
        Assert.Equal("cv-1", poll.Grant.CodeVerifier);

        // 未授权时上游是 403/404，不解析正文，继续等。
        var pendingHandler = new StubHandler(_ => "", HttpStatusCode.Forbidden);
        var pending = await new OAuthClient(new StubFactory(pendingHandler))
            .PollDeviceAuthAsync(config, "da-1", "ABCD-1234");
        Assert.Equal(OAuthClient.DevicePollKind.Pending, pending.Kind);

        // 申请是 JSON body（带 client_id），没有 scope；轮询是 device_auth_id + user_code。
        var (usercodeUrl, usercodeBody, _) = handler.Requests[0];
        Assert.Equal(config.DeviceCodeUrl, usercodeUrl);
        Assert.Contains("\"client_id\":\"app_EMoamEEZ73f0CkXaXp7hrann\"", usercodeBody);
        Assert.DoesNotContain("scope", usercodeBody);
        var (tokenUrl, tokenBody, _) = handler.Requests[1];
        Assert.Equal(config.DeviceTokenUrl, tokenUrl);
        Assert.Contains("\"device_auth_id\":\"da-1\"", tokenBody);
        Assert.Contains("\"user_code\":\"ABCD-1234\"", tokenBody);
    }

    /// <summary>非 PKCE 家族换码时也要带上游给的 verifier（OpenAI 的换码用上游配对的 PKCE 对）。</summary>
    [Fact]
    public async Task Exchange_Code_Sends_The_Verifier_Even_Without_Local_Pkce()
    {
        var config = SubscriptionCatalog.Find("openai-subscription")!;
        var handler = new StubHandler(_ => """{"access_token":"at-1","expires_in":3600}""");
        var client = new OAuthClient(new StubFactory(handler));

        await client.ExchangeCodeAsync(config, "the-code", "the-verifier", "https://auth.openai.com/deviceauth/callback");

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code_verifier=the-verifier", body);
        Assert.Contains("redirect_uri=https%3A%2F%2Fauth.openai.com%2Fdeviceauth%2Fcallback", body);
    }

    /// <summary>
    /// Copilot 第二跳：GitHub token（<c>token &lt;gho_…&gt;</c>）换 Copilot 短时令牌，
    /// 请求要带官方 Editor-* 身份头；响应里的 endpoints.api 是渠道下发的 API 主机。
    /// </summary>
    [Fact]
    public async Task Copilot_Token_Exchange_Uses_The_GitHub_Token_And_Reports_Api_Base()
    {
        var config = SubscriptionCatalog.Find("github-copilot-subscription")!;
        var handler = new StubHandler(_ =>
            """{"token":"tid=1;exp=1791948516;","expires_at":1791948516,"refresh_in":1800,"endpoints":{"api":"https://api.githubcopilot.com","proxy":"https://proxy.individual.githubcopilot.com"},"sku":"copilot_individual"}""");
        var client = new OAuthClient(new StubFactory(handler));

        var token = await client.CopilotTokenAsync(config, "gho_abc");

        Assert.Equal("tid=1;exp=1791948516;", token.Token);
        Assert.Equal(1791948516, token.ExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(1800, token.RefreshInSeconds);
        Assert.Equal("https://api.githubcopilot.com", token.ApiBase);
        Assert.Equal("copilot_individual", token.Sku);

        var (url, _, headers) = Assert.Single(handler.Requests);
        Assert.Equal(config.BusinessLoginUrl, url);
        Assert.Equal("token gho_abc", headers["Authorization"]); // GitHub 用 token 前缀，不是 Bearer
        Assert.Equal("vscode-chat", headers["Copilot-Integration-Id"]);
        Assert.StartsWith("vscode/", headers["Editor-Version"]);
        Assert.Equal("2024-12-15", headers["X-GitHub-Api-Version"]);
    }

    [Fact]
    public async Task Copilot_Token_Exchange_Surfaces_A_Missing_Subscription()
    {
        var config = SubscriptionCatalog.Find("github-copilot-subscription")!;
        // 没有 Copilot 订阅的 GitHub 账号会拿到 403。
        var handler = new StubHandler(_ => """{"message":"Not authorized to use Copilot"}""", System.Net.HttpStatusCode.Forbidden);
        var client = new OAuthClient(new StubFactory(handler));

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(() => client.CopilotTokenAsync(config, "gho_none"));

        Assert.Equal("http_403", error.Error);
        Assert.Contains("Copilot", error.Message);
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

    /// <summary>ZAI CLI 链路：init（Bearer poll_token）拿 flow + 授权链接，poll 用同一个 token。</summary>
    [Fact]
    public async Task Zcli_Init_And_Poll_Use_The_Poll_Token_And_Report_Expired_As_3004()
    {
        var config = new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            Style = "zcli",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            CliInitUrl = "https://zcode.z.ai/api/v1/oauth/cli/init",
            CliPollUrl = "https://zcode.z.ai/api/v1/oauth/cli/poll",
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            ClientId = "client_P8X5CMWmlaRO9gyO-KSqtg",
            ExtraHeaders = new Dictionary<string, string> { ["User-Agent"] = "ZCode/3.10.2" },
        };
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/oauth/cli/init" =>
                """{"code":0,"data":{"flow_id":"flow-1","authorize_url":"https://chat.z.ai/api/oauth/authorize?client_id=x","poll_token":"server-token"}}""",
            _ => """{"code":0,"data":{"accessToken":"oauth-1"}}""",
        });
        var client = new OAuthClient(new StubFactory(handler));

        var start = await client.StartZcodeCliAsync(config, "zai", "local-poll-token");
        Assert.Equal("flow-1", start.FlowId);
        Assert.Equal("https://chat.z.ai/api/oauth/authorize?client_id=x", start.AuthorizeUrl);
        Assert.Equal("server-token", start.PollToken); // 上游下发的 poll_token 覆盖本地随机值

        var done = await client.PollZcodeCliAsync(config, start.FlowId, start.PollToken);
        Assert.Equal(ZcodeCliPollKind.Done, done.Kind);
        Assert.Equal("oauth-1", done.AccessToken);

        // init 是 POST + Bearer 本地 poll_token；poll 是 GET + Bearer 下发的 poll_token
        var (initUrl, initBody, initHeaders) = handler.Requests[0];
        Assert.Equal(config.CliInitUrl, initUrl);
        Assert.Contains("\"provider\":\"zai\"", initBody);
        Assert.Equal("Bearer local-poll-token", initHeaders["Authorization"]);
        var (pollUrl, _, pollHeaders) = handler.Requests[1];
        Assert.Equal($"{config.CliPollUrl}/flow-1", pollUrl);
        Assert.Equal("Bearer server-token", pollHeaders["Authorization"]);

        // code=3004 = 会话过期
        var expiredHandler = new StubHandler(_ => """{"code":3004,"msg":"session expired"}""");
        var expired = await new OAuthClient(new StubFactory(expiredHandler))
            .PollZcodeCliAsync(config, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Expired, expired.Kind);

        // 其它业务码是终态失败
        var failedHandler = new StubHandler(_ => """{"code":3005,"msg":"denied"}""");
        var failed = await new OAuthClient(new StubFactory(failedHandler))
            .PollZcodeCliAsync(config, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Error, failed.Kind);
        Assert.Equal("zcode_3005", failed.Error);
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
