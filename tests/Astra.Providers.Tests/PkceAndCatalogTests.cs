using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;

namespace Astra.Providers.Tests;

public class PkceTests
{
    [Fact]
    public void Challenge_Matches_The_Rfc7636_Appendix_B_Vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge(verifier));
    }

    [Fact]
    public void Create_Produces_Valid_Verifier_And_Matching_Challenge()
    {
        var (verifier, challenge) = Pkce.Create();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9\\-._~]+$", verifier);
        Assert.NotEqual(verifier, challenge);
        Assert.DoesNotContain("=", challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
        Assert.Equal(Pkce.Challenge(verifier), challenge);
    }
}

public class SubscriptionCatalogTests
{
    [Fact]
    public void Catalog_Has_The_Six_Subscription_Families()
    {
        Assert.NotNull(SubscriptionCatalog.Find("claude-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("openai-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("grok-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("kimi-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("zcode-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("github-copilot-subscription"));
        Assert.Null(SubscriptionCatalog.Find("nope"));
        // 登录默认开放：端点与 client id 都已按公开来源写死，verified 只是"允许登录"的开关。
        Assert.All(SubscriptionCatalog.All, c => Assert.True(c.Verified));

        // GitHub Copilot：官方 device flow 的 client id + repo/workflow scope，第二跳换 Copilot 令牌。
        var copilot = SubscriptionCatalog.Find("github-copilot-subscription")!;
        Assert.Equal("Iv1.b507a08c87ecfe98", copilot.ClientId); // copilot-language-server GitHubAppInfo
        Assert.Equal("https://github.com/login/device/code", copilot.DeviceCodeUrl);
        Assert.Equal("https://github.com/login/oauth/access_token", copilot.TokenUrl);
        Assert.Equal(["repo", "workflow"], copilot.Scopes);
        Assert.False(copilot.UsePkce);
        Assert.Equal("github-copilot", copilot.Style);
        Assert.Equal("https://api.github.com/copilot_internal/v2/token", copilot.BusinessLoginUrl);
        Assert.Equal("vscode-chat", copilot.ExtraHeaders["Copilot-Integration-Id"]);
        Assert.True(SubscriptionCatalog.IsReady(copilot));
    }

    [Fact]
    public void Effective_Merges_Instance_Overrides_Over_The_Catalog_Base()
    {
        var baseConfig = SubscriptionCatalog.Find("claude-subscription")!;
        var settings = JsonNode.Parse("""
            {"subscription_oauth":{"verified":true,"client_id":"test-client","scopes":["s1","s2"]}}
            """)!.AsObject();

        var effective = SubscriptionCatalog.Effective(baseConfig, settings);

        Assert.True(effective.Verified);
        Assert.Equal("test-client", effective.ClientId);
        Assert.Equal(["s1", "s2"], effective.Scopes);
        Assert.Equal(baseConfig.AuthorizeUrl, effective.AuthorizeUrl); // inherited
        Assert.Equal(baseConfig.TokenUrl, effective.TokenUrl);
    }

    [Fact]
    public void Ready_Requires_Verified_ClientId_And_Flow_Endpoints()
    {
        var claude = SubscriptionCatalog.Find("claude-subscription")!;
        Assert.True(SubscriptionCatalog.IsReady(claude)); // PKCE: authorize url + token url + client id

        // All five families ship complete endpoint sets + client ids from public sources, so they
        // all start a login out of the box; a provider instance can still close it (verified=false).
        Assert.All(SubscriptionCatalog.All, c => Assert.True(SubscriptionCatalog.IsReady(c), c.ProviderKey));

        var openai = SubscriptionCatalog.Find("openai-subscription")!;
        Assert.Equal("", openai.Style); // codex-cli 默认的浏览器 PKCE 登录，不是 deviceauth
        Assert.True(openai.UsePkce);
        // 走 device code 备选时才需要额外的轮询端点：把它清掉不影响 PKCE 就绪。
        var noPoll = SubscriptionCatalog.Effective(openai, JsonNode.Parse(
            """{"subscription_oauth":{"device_token_url":"","verified":true}}""")!.AsObject());
        Assert.True(SubscriptionCatalog.IsReady(noPoll));

        // verified=false on the instance closes a catalog-open family again.
        var closed = SubscriptionCatalog.Effective(claude, JsonNode.Parse(
            """{"subscription_oauth":{"verified":false}}""")!.AsObject());
        Assert.False(SubscriptionCatalog.IsReady(closed));
    }

    /// <summary>The shipped OAuth constants must match the public reverse-engineering sources they came from.</summary>
    [Fact]
    public void Catalog_Pins_The_Reverse_Engineered_OAuth_Constants()
    {
        var openai = SubscriptionCatalog.Find("openai-subscription")!;
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", openai.ClientId); // openai/codex manager.rs CLIENT_ID
        Assert.Equal("https://auth.openai.com/oauth/authorize", openai.AuthorizeUrl);
        Assert.Equal("https://auth.openai.com/oauth/token", openai.TokenUrl);
        // codex-cli 的默认登录：PKCE 授权码 + 它固定带的三个额外参数与完整 scope 集合。
        Assert.Equal("", openai.Style);
        Assert.True(openai.UsePkce);
        Assert.Equal(["openid", "profile", "email", "offline_access", "api.connectors.read", "api.connectors.invoke"], openai.Scopes);
        Assert.Equal("true", openai.ExtraAuthorizeParams["id_token_add_organizations"]);
        Assert.Equal("true", openai.ExtraAuthorizeParams["codex_cli_simplified_flow"]);
        Assert.Equal("codex_cli_rs", openai.ExtraAuthorizeParams["originator"]);
        // 上游只认 codex 自己的回环回调（1455/1457 + /auth/callback），别的 redirect_uri 会被拒。
        Assert.True(SubscriptionCatalog.IsCodexLogin(openai));
        Assert.Equal([1455, 1457], SubscriptionCatalog.CodexLoopbackPorts);
        Assert.Equal("/auth/callback", SubscriptionCatalog.CodexLoopbackPath);
        // device code 端点是备选路径（Style="deviceauth"）用的，保留但不默认启用。
        Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/usercode", openai.DeviceCodeUrl);
        Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/token", openai.DeviceTokenUrl);

        // Claude：端点、client_id 与 scope 逐字取自 Claude Code 自己的二进制
        // （claude-code-darwin-arm64/claude 内嵌的 OAuth 配置与授权 URL 构造函数）。
        var claude = SubscriptionCatalog.Find("claude-subscription")!;
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", claude.ClientId);
        Assert.Equal("https://claude.com/cai/oauth/authorize", claude.AuthorizeUrl);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", claude.TokenUrl);
        Assert.Equal("claude-loopback", claude.Style);
        Assert.True(SubscriptionCatalog.IsClaudeLoopback(claude));
        Assert.Equal("true", claude.ExtraAuthorizeParams["code"]); // 授权链接必须带 code=true
        // 默认走回环（临时端口），不是无头兜底：后者只在实例显式覆盖 redirect_uri 时才用。
        Assert.Equal("", claude.RedirectUriOverride);
        Assert.False(SubscriptionCatalog.IsPasteLogin(claude));
        // Claude Code 的 yDr()。
        Assert.Equal(
            ["org:create_api_key", "user:profile", "user:inference", "user:sessions:claude_code",
             "user:mcp_servers", "user:file_upload", "user:plugins"],
            claude.Scopes);

        // 授权 URL 必须逐字复刻 Claude Code 的**参数顺序**：顺序不对上游直接回 "Invalid request format"。
        var authorize = OAuthClient.BuildAuthorizeUrl(claude, "http://localhost:55522/callback", "st-1", "ch-1");
        Assert.Equal(
            "https://claude.com/cai/oauth/authorize?"
            + "code=true&client_id=" + claude.ClientId
            + "&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A55522%2Fcallback"
            + "&scope=" + string.Join('+', claude.Scopes.Select(s => s.Replace(":", "%3A")))
            + "&code_challenge=ch-1&code_challenge_method=S256&state=st-1",
            authorize);
        // 只有 Claude 是回环 + 参数顺序敏感的那条链路。
        Assert.All(SubscriptionCatalog.All.Where(c => c.ProviderKey != "claude-subscription"),
            c => Assert.False(SubscriptionCatalog.IsClaudeLoopback(c), c.ProviderKey));

        var grok = SubscriptionCatalog.Find("grok-subscription")!;
        Assert.Equal("https://auth.x.ai/oauth2/device/code", grok.DeviceCodeUrl); // xai-org/grok-build device_code.rs
        Assert.Equal("https://auth.x.ai/oauth2/token", grok.TokenUrl);
        Assert.Equal("b1a00492-073a-47ea-816f-4c329264a828", grok.ClientId); // xai-grok-login config.rs default
        // FROZEN client contract: exactly the 10 scopes grok-build pins.
        Assert.Equal(
            ["openid", "profile", "email", "offline_access", "grok-cli:access", "api:access",
             "conversations:read", "conversations:write", "workspaces:read", "workspaces:write"],
            grok.Scopes);
        Assert.Equal("grok-build", grok.ExtraDeviceParams["referrer"]);

        // Kimi：官方一方源码 @moonshot-ai/kimi-code v0.42.0 的 packages/oauth（非逆向）。
        var kimi = SubscriptionCatalog.Find("kimi-subscription")!;
        Assert.Equal("https://auth.kimi.com/api/oauth/device_authorization", kimi.DeviceCodeUrl);
        Assert.Equal("https://auth.kimi.com/api/oauth/token", kimi.TokenUrl);
        Assert.Equal("17e5f671-d194-4dfb-9706-5516cb48c098", kimi.ClientId);
        Assert.False(kimi.UsePkce);
        Assert.Equal("kimi_code_cli", kimi.ExtraHeaders["X-Msh-Platform"]);
        Assert.Equal("https://api.kimi.com/coding/v1/me", kimi.IdentityUrl);

        // ZCode（Z.AI 渠道）改走官方 CLI 链路：授权码 + 回环回调会被上游的 client 白名单拒掉
        // （ZCode.app 只登记了 zcode://oauth/callback），CLI 链路无回调、服务端轮询取码。
        var zcode = SubscriptionCatalog.Find("zcode-subscription")!;
        Assert.Equal("zcli", zcode.Style);
        Assert.Equal("https://zcode.z.ai/api/v1/oauth/cli/init", zcode.CliInitUrl);
        Assert.Equal("https://zcode.z.ai/api/v1/oauth/cli/poll", zcode.CliPollUrl);
        Assert.Equal("https://chat.z.ai/api/oauth/authorize", zcode.AuthorizeUrl);
        Assert.Equal("https://zcode.z.ai/api/v1/oauth/token", zcode.TokenUrl);
        Assert.Equal("client_P8X5CMWmlaRO9gyO-KSqtg", zcode.ClientId);
        Assert.Equal("https://api.z.ai/api/auth/z/login", zcode.BusinessLoginUrl);
        Assert.Equal("ZCode/3.10.2", zcode.ExtraHeaders["User-Agent"]);
        Assert.False(zcode.UsePkce);

        // 智谱 BigModel：同一条 CLI 链路（provider 取 bigmodel，服务端收回调），最后一步供应真 API Key
        // 而不是换业务 JWT（coding 端点不认 OAuth token，直接用回 1234）。
        var bigmodel = SubscriptionCatalog.Find("bigmodel-subscription")!;
        Assert.Equal("zcli", bigmodel.Style);
        Assert.Equal(zcode.CliInitUrl, bigmodel.CliInitUrl);
        Assert.Equal(zcode.CliPollUrl, bigmodel.CliPollUrl);
        Assert.Equal("zcode", bigmodel.ClientId); // 桌面端 appId
        Assert.Equal("https://bigmodel.cn", bigmodel.BizHost);
        Assert.Equal("zcode-api-key", bigmodel.ApiKeyName);
        Assert.Equal("", bigmodel.BusinessLoginUrl); // 不走 z/login（真 token 也被拒：z.ai用户信息异常）
        Assert.True(SubscriptionCatalog.IsReady(bigmodel));

        // 实例级覆盖也认 biz_host / api_key_name
        var overridden = SubscriptionCatalog.Effective(bigmodel,
            new System.Text.Json.Nodes.JsonObject
            {
                ["subscription_oauth"] = new System.Text.Json.Nodes.JsonObject { ["biz_host"] = "https://biz.example", ["api_key_name"] = "mine" },
            });
        Assert.Equal("https://biz.example", overridden.BizHost);
        Assert.Equal("mine", overridden.ApiKeyName);
    }

    /// <summary>
    /// Claude 的收尾是"用户把授权页上的 code 粘回来"，所以要能把三种粘贴形态都还原成换码要的东西：
    /// 完整回调地址、页面回显的"授权码#state"、以及只有授权码。收整条地址而不是光秃秃一个 code，
    /// 是为了让 state 一起回来（它是 CSRF 防线）。
    /// </summary>
    [Fact]
    public void Pasted_Code_Parses_A_Callback_Url_A_Hash_Value_And_A_Bare_Code()
    {
        var url = SubscriptionCatalog.ParsePastedCode(
            "https://platform.claude.com/oauth/code/callback?code=ac%2F1&state=st-9");
        Assert.Equal("ac/1#st-9", url.Raw);
        Assert.Equal("st-9", url.State);

        var hash = SubscriptionCatalog.ParsePastedCode("ac-1#st-9");
        Assert.Equal("ac-1#st-9", hash.Raw);
        Assert.Equal("st-9", hash.State);

        var bare = SubscriptionCatalog.ParsePastedCode("  ac-1  ");
        Assert.Equal("ac-1", bare.Raw);
        Assert.Null(bare.State);

        // 换码时只发 # 前面的授权码，state 单独发。
        Assert.Equal("ac-1", SubscriptionCatalog.CodeWithoutState("ac-1#st-9"));
        Assert.Equal("ac-1", SubscriptionCatalog.CodeWithoutState("ac-1"));

        Assert.Equal("", SubscriptionCatalog.ParsePastedCode(null).Raw);
        Assert.Equal("", SubscriptionCatalog.ParsePastedCode("   ").Raw);
    }
}

public class ProviderTemplateCatalogTests
{
    private readonly ProviderTemplateCatalog _catalog = new();

    /// <summary>A preferred protocol without an address can never be selected, so it must not be listed first.</summary>
    [Fact]
    public void Every_Template_Has_An_Endpoint_For_Its_First_Preferred_Protocol()
    {
        Assert.All(_catalog.All, t =>
            Assert.True(t.Endpoints.Any(e => e.Protocol == t.PreferredUpstreamProtocols[0]),
                $"{t.Id} prefers {t.PreferredUpstreamProtocols[0].ToId()} but has no such endpoint"));
    }

    /// <summary>Same rule for every entry: a dead entry in the order silently falls through to a lower preference.</summary>
    [Fact]
    public void No_Template_Prefers_A_Protocol_It_Has_No_Endpoint_For()
    {
        Assert.All(_catalog.All, t => Assert.All(t.PreferredUpstreamProtocols, p =>
            Assert.True(t.Endpoints.Any(e => e.Protocol == p), $"{t.Id} prefers {p.ToId()} but has no such endpoint")));
        Assert.All(_catalog.All.Where(t => t.Variants is not null), t => Assert.All(t.Variants!, v =>
            Assert.All(v.EndpointsOverride ?? t.Endpoints, e =>
                Assert.True(t.PreferredUpstreamProtocols.Contains(e.Protocol),
                    $"{t.Id} variant {v.Id} has an unused {e.Protocol.ToId()} endpoint"))));
    }

    public static TheoryData<string, string> ResponsesTemplates => new()
    {
        { "openai", "https://api.openai.com/v1" },
        { "xai", "https://api.x.ai/v1" },
        { "deepseek", "https://api.deepseek.com/v1" },
        { "moonshot", "https://api.moonshot.cn/v1" },
        { "zhipuai", "https://open.bigmodel.cn/api/v1" },
        { "alibaba", "https://dashscope.aliyuncs.com/compatible-mode/v1" },
        { "openrouter", "https://openrouter.ai/api/v1" },
        { "ollama", "http://127.0.0.1:11434/v1" },
        { "lm-studio", "http://127.0.0.1:1234/v1" },
        { "routin", "https://api.routin.ai/plan/v1" },
        { "nextcowork", "https://nextco.work/v1" },
        { "openai-subscription", "https://chatgpt.com/backend-api/codex" },
        { "custom-responses", "http://127.0.0.1:8000/v1" },
    };

    /// <summary>Every provider that documents a Responses API ships it, ranked first, so Codex passes through.</summary>
    [Theory]
    [MemberData(nameof(ResponsesTemplates))]
    public void Responses_Providers_Ship_A_Responses_Endpoint_First(string id, string baseUrl)
    {
        var template = _catalog.Get(id)!;
        Assert.Equal(ApiProtocol.OpenAIResponses, template.PreferredUpstreamProtocols[0]);
        var endpoint = Assert.Single(template.Endpoints, e => e.Protocol == ApiProtocol.OpenAIResponses);
        Assert.Equal(baseUrl, endpoint.BaseUrl);
        Assert.False(endpoint.FullUrl); // /responses is appended
    }

    [Fact]
    public void DeepSeek_Ships_All_Three_Protocols_With_Responses_First()
    {
        var deepseek = _catalog.Get("deepseek")!;
        Assert.Equal([ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, ApiProtocol.Anthropic], deepseek.PreferredUpstreamProtocols);
        Assert.Equal(
            [ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, ApiProtocol.Anthropic],
            deepseek.Endpoints.Select(e => e.Protocol));
        Assert.All(deepseek.Endpoints, e => Assert.StartsWith("https://api.deepseek.com/", e.BaseUrl));
    }

    /// <summary>
    /// NextCoWork 认可的 API Key 通道只有 <c>x-api-key</c>：它的 <c>Authorization: Bearer</c> 会被解析成控制台
    /// 登录态 JWT（实测 /v1/messages、/v1/chat/completions、/v1/models 都报 invalid_credentials），
    /// 所以模板必须固定 x-api-key，三个协议共用。
    /// </summary>
    [Fact]
    public void NextCoWork_Ships_All_Three_Protocols_With_XApiKey_Auth()
    {
        var template = _catalog.Get("nextcowork")!;
        Assert.Equal(AuthSchemes.XApiKey, template.Auth.Scheme);
        Assert.Equal([ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, ApiProtocol.Anthropic],
            template.PreferredUpstreamProtocols);
        Assert.All(template.Endpoints, e => Assert.Equal("https://nextco.work/v1", e.BaseUrl));
        Assert.Equal("/models", template.ModelListEndpoint?.Path);
    }

    /// <summary>Subscription templates pin the fixed identity headers the upstream CLI clients send.</summary>
    [Fact]
    public void Subscription_Templates_Pin_Client_Identity_Headers()
    {
        var codex = _catalog.Get("openai-subscription")!;
        Assert.Equal("codex_cli_rs", codex.DefaultHeaders["originator"]);
        Assert.Equal("responses=experimental", codex.DefaultHeaders["OpenAI-Beta"]);

        var grok = _catalog.Get("grok-subscription")!;
        Assert.Equal("xai-grok-cli", grok.DefaultHeaders["x-grok-client-identifier"]);
        Assert.Equal("1.0.13", grok.DefaultHeaders["x-grok-client-version"]);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", Assert.Single(grok.Endpoints).BaseUrl);
        Assert.False(grok.Endpoints[0].FullUrl);
        Assert.Equal("/models", grok.ModelListEndpoint?.Path);
    }
}
