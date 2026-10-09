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

        /// <summary>Media type of each request body, in order (JSON vs form matters for the Claude endpoints).</summary>
        public List<string?> ContentTypes { get; } = [];

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
            ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
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

    /// <summary>
    /// Claude（Anthropic）的换码形状与常规 OAuth 不同：JSON body + 把"授权码#state"拆开成两个字段 +
    /// redirect_uri 必须逐字等于授权时用的那个（回环地址）。少一样上游就报「参数错误」。
    /// </summary>
    [Fact]
    public async Task Claude_Exchange_Splits_The_State_And_Posts_Json_With_The_Callback_Redirect()
    {
        var config = SubscriptionCatalog.Find("claude-subscription")!;
        var handler = new StubHandler(_ => """{"access_token":"at-c","refresh_token":"rt-c","expires_in":3600}""");
        var client = new OAuthClient(new StubFactory(handler));

        var token = await client.ExchangeClaudeCodeAsync(config, "ac-1#st-9", "the-verifier", "http://localhost:55522/callback");

        Assert.Equal("at-c", token.AccessToken);
        Assert.Equal("rt-c", token.RefreshToken);
        var (url, body, _) = Assert.Single(handler.Requests);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", url);
        Assert.Equal("application/json", Assert.Single(handler.ContentTypes));
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("ac-1", json["code"]!.GetValue<string>());        // state 不能留在 code 里
        Assert.Equal("st-9", json["state"]!.GetValue<string>());       // 但要单独发
        Assert.Equal(config.ClientId, json["client_id"]!.GetValue<string>());
        Assert.Equal("http://localhost:55522/callback", json["redirect_uri"]!.GetValue<string>());
        Assert.Equal("the-verifier", json["code_verifier"]!.GetValue<string>());
        Assert.Equal("authorization_code", json["grant_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_Exchange_Sends_No_State_Field_When_The_Code_Has_None()
    {
        var config = SubscriptionCatalog.Find("claude-subscription")!;
        var handler = new StubHandler(_ => """{"access_token":"at-c"}""");
        var client = new OAuthClient(new StubFactory(handler));

        await client.ExchangeClaudeCodeAsync(config, "ac-only", "v", "http://localhost:1/callback");

        var json = JsonNode.Parse(Assert.Single(handler.Requests).Body)!.AsObject();
        Assert.Equal("ac-only", json["code"]!.GetValue<string>());
        Assert.False(json.ContainsKey("state"));
    }

    [Fact]
    public async Task Claude_Refresh_Posts_Json_Not_A_Form()
    {
        var config = SubscriptionCatalog.Find("claude-subscription")!;
        var handler = new StubHandler(_ => """{"access_token":"at-c2","expires_in":7200}""");
        var client = new OAuthClient(new StubFactory(handler));

        var token = await client.RefreshAsync(config, "rt-old");

        Assert.Equal("at-c2", token.AccessToken);
        var (_, body, _) = Assert.Single(handler.Requests);
        Assert.Equal("application/json", Assert.Single(handler.ContentTypes));
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("refresh_token", json["grant_type"]!.GetValue<string>());
        Assert.Equal("rt-old", json["refresh_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_Exchange_Surfaces_The_Upstream_Error_Body()
    {
        var config = SubscriptionCatalog.Find("claude-subscription")!;
        var handler = new StubHandler(_ => """{"error":"invalid_request","error_description":"参数错误"}""",
            System.Net.HttpStatusCode.BadRequest);
        var client = new OAuthClient(new StubFactory(handler));

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(
            () => client.ExchangeClaudeCodeAsync(config, "ac", "v", "http://localhost:1/callback"));

        Assert.Equal("invalid_request", error.Error);
        Assert.Contains("参数错误", error.Message);
    }

    [Fact]
    public void Authorize_Url_Carries_Pkce_State_And_Scopes()    {
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

    /// <summary>
    /// 上游的错误体不一定是 JSON：api.github.com 对不认识的客户端直接回 "403 Forbidden. …" 纯文本。
    /// 这类响应必须还是报成 http_403（“这个账号没有 Copilot”），而不是先解析 JSON 失败后
    /// 变成看不懂的 invalid_token_response。真实事故：本机读到的 VS Code 令牌撞上的就是这个。
    /// </summary>
    [Fact]
    public async Task Copilot_Token_Exchange_Tolerates_A_Non_Json_Error_Body()
    {
        var config = SubscriptionCatalog.Find("github-copilot-subscription")!;
        var handler = new StubHandler(_ => "403 Forbidden. For more on scraping GitHub and how it may affect your rights, please review our Terms of Service.",
            System.Net.HttpStatusCode.Forbidden);
        var client = new OAuthClient(new StubFactory(handler));

        var error = await Assert.ThrowsAsync<OAuthProtocolException>(() => client.CopilotTokenAsync(config, "gho_x"));

        Assert.Equal("http_403", error.Error);
        Assert.Contains("403 Forbidden", error.Message);
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

        // 真实上游形态：data.status = pending / ready（凭证在渠道键下）/ failed
        var pending = await new OAuthClient(new StubFactory(new StubHandler(_ => """{"code":0,"data":{"status":"pending"}}""")))
            .PollZcodeCliAsync(config, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Pending, pending.Kind);

        var ready = await new OAuthClient(new StubFactory(new StubHandler(_ =>
                """{"code":0,"data":{"status":"ready","token":"x","user":{"user_id":"u1"},"zai":{"access_token":"oauth-2"}}}""")))
            .PollZcodeCliAsync(config, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Done, ready.Kind);
        Assert.Equal("oauth-2", ready.AccessToken);

        var denied = await new OAuthClient(new StubFactory(new StubHandler(_ => """{"code":0,"data":{"status":"failed"}}""")))
            .PollZcodeCliAsync(config, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Error, denied.Kind);
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

    private static SubscriptionOAuthConfig BigModelConfig => SubscriptionCatalog.Find("bigmodel-subscription")!;

    private const string CustomerInfo = """
        {"code":200,"data":{"organizations":[
          {"organizationName":"其它","organizationId":"org-0","projects":[{"projectName":"p","projectId":"proj-0"}]},
          {"organizationName":"我的默认机构","organizationId":"org-1","projects":[
            {"projectName":"别的","projectId":"proj-a"},{"projectName":"默认项目","projectId":"proj-1"}]}]}}
        """;

    [Fact]
    public async Task BigModel_Provisions_An_Api_Key_Creating_It_When_Missing()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/biz/customer/getCustomerInfo" => CustomerInfo,
            "/api/biz/v1/organization/org-1/projects/proj-1/api_keys" when request.Method == HttpMethod.Get =>
                """{"code":200,"data":[{"name":"other","apiKey":"nope"}]}""",
            "/api/biz/v1/organization/org-1/projects/proj-1/api_keys" =>
                """{"code":200,"data":{"name":"zcode-api-key","apiKey":"key-1"}}""",
            "/api/biz/v1/organization/org-1/projects/proj-1/api_keys/copy/key-1" =>
                """{"code":200,"data":{"secretKey":"sec-1"}}""",
            _ => """{"code":404,"msg":"unexpected"}""",
        });
        var client = new OAuthClient(new StubFactory(handler));

        var credential = await client.ZcodeApiCredentialAsync(BigModelConfig, "oauth-token");

        Assert.Equal("key-1.sec-1", credential); // 最终凭据 = apiKey.secretKey
        Assert.Equal(4, handler.Requests.Count);
        // 挑的是名字含「默认机构」/「默认项目」的那条，而不是第 0 条
        Assert.Contains("/organization/org-1/projects/proj-1/api_keys", handler.Requests[1].Url);
        // 创建 key 的 body 只有 name；鉴权头是裸 OAuth token（无 Bearer）
        Assert.Equal("""{"name":"zcode-api-key"}""", handler.Requests[2].Body);
        foreach (var request in handler.Requests)
            Assert.Equal("oauth-token", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task BigModel_Reuses_The_Existing_Key_And_Tolerates_A_Failed_Copy()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/biz/customer/getCustomerInfo" => CustomerInfo,
            "/api/biz/v1/organization/org-1/projects/proj-1/api_keys" =>
                """{"code":200,"data":[{"name":"zcode-api-key","apiKey":"key-9"}]}""",
            _ => """{"code":500,"msg":"copy failed"}""",
        });
        var client = new OAuthClient(new StubFactory(handler));

        var credential = await client.ZcodeApiCredentialAsync(BigModelConfig, "oauth-token");

        Assert.Equal("key-9", credential); // 已存在就不再创建；copy 失败退回只有 id 的 key
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("zcode-api-key"));
    }

    [Fact]
    public async Task BigModel_Provisioning_Reports_A_Dead_Oauth_Token_And_A_Missing_Project()
    {
        var unauthorized = new StubHandler(_ => """{"code":1001,"msg":"token invalid"}""", HttpStatusCode.Unauthorized);
        var dead = await Assert.ThrowsAsync<OAuthProtocolException>(() =>
            new OAuthClient(new StubFactory(unauthorized)).ZcodeApiCredentialAsync(BigModelConfig, "t"));
        Assert.Equal("http_401", dead.Error);

        var empty = new StubHandler(_ => """{"code":200,"data":{"organizations":[]}}""");
        var none = await Assert.ThrowsAsync<OAuthProtocolException>(() =>
            new OAuthClient(new StubFactory(empty)).ZcodeApiCredentialAsync(BigModelConfig, "t"));
        Assert.Equal("biz_no_project", none.Error);

        var envelope = new StubHandler(_ => """{"code":1234,"msg":"nope"}""");
        var rejected = await Assert.ThrowsAsync<OAuthProtocolException>(() =>
            new OAuthClient(new StubFactory(envelope)).ZcodeApiCredentialAsync(BigModelConfig, "t"));
        Assert.Equal("biz_1234", rejected.Error);
    }

    [Fact]
    public async Task Zcli_Poll_Reads_The_BigModel_Credentials_And_Email_And_Treats_Network_Errors_As_Pending()
    {
        var ready = new StubHandler(_ =>
            """{"code":0,"data":{"status":"ready","user":{"user_id":"u1","email":"a@b.c"},"bigmodel":{"access_token":"oauth-bm","refresh_token":"r"}}}""");
        var done = await new OAuthClient(new StubFactory(ready)).PollZcodeCliAsync(BigModelConfig, "flow-1", "t");
        Assert.Equal(ZcodeCliPollKind.Done, done.Kind);
        Assert.Equal("oauth-bm", done.AccessToken);
        Assert.Equal("a@b.c", done.Email);

        var broken = new OAuthClient(new ThrowingFactory());
        Assert.Equal(ZcodeCliPollKind.Pending, (await broken.PollZcodeCliAsync(BigModelConfig, "flow-1", "t")).Kind);
    }

    private sealed class ThrowingFactory : IHttpClientFactory
    {
        private sealed class Throwing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("connection reset");
        }

        public HttpClient CreateClient(string name) => new(new Throwing(), disposeHandler: true);
    }
}
