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
    public void Catalog_Has_The_Five_Subscription_Families()
    {
        Assert.NotNull(SubscriptionCatalog.Find("claude-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("openai-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("grok-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("kimi-subscription"));
        Assert.NotNull(SubscriptionCatalog.Find("zcode-subscription"));
        Assert.Null(SubscriptionCatalog.Find("nope"));
        // Nothing ships as verified: plan §15 requires field verification before login is offered.
        Assert.All(SubscriptionCatalog.All, c => Assert.False(c.Verified));
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
        Assert.False(SubscriptionCatalog.IsReady(claude)); // shipped unverified

        // All three families ship complete endpoint sets + client ids from public reverse
        // engineering, so flipping verified=true is enough to open the login.
        var openai = SubscriptionCatalog.Effective(SubscriptionCatalog.Find("openai-subscription")!, JsonNode.Parse(
            """{"subscription_oauth":{"verified":true}}""")!.AsObject());
        Assert.True(SubscriptionCatalog.IsReady(openai));

        var grok = SubscriptionCatalog.Effective(SubscriptionCatalog.Find("grok-subscription")!, JsonNode.Parse(
            """{"subscription_oauth":{"verified":true}}""")!.AsObject());
        Assert.True(SubscriptionCatalog.IsReady(grok)); // device flow: device url + client id suffice

        var kimi = SubscriptionCatalog.Effective(SubscriptionCatalog.Find("kimi-subscription")!, JsonNode.Parse(
            """{"subscription_oauth":{"verified":true}}""")!.AsObject());
        Assert.True(SubscriptionCatalog.IsReady(kimi));

        var zcode = SubscriptionCatalog.Effective(SubscriptionCatalog.Find("zcode-subscription")!, JsonNode.Parse(
            """{"subscription_oauth":{"verified":true}}""")!.AsObject());
        Assert.True(SubscriptionCatalog.IsReady(zcode)); // zcode style: authorize url + client id suffice
    }

    /// <summary>The shipped OAuth constants must match the public reverse-engineering sources they came from.</summary>
    [Fact]
    public void Catalog_Pins_The_Reverse_Engineered_OAuth_Constants()
    {
        var openai = SubscriptionCatalog.Find("openai-subscription")!;
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", openai.ClientId); // openai/codex manager.rs CLIENT_ID
        Assert.Equal("https://auth.openai.com/oauth/authorize", openai.AuthorizeUrl);
        Assert.Equal("https://auth.openai.com/oauth/token", openai.TokenUrl);
        Assert.True(openai.UsePkce);

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

        // ZCode（Z.AI 渠道）：Vibe Coding Labs《ZCode RE》 + ZCode.app v3.11.2 逆向，
        // 经 NextCoWork issuers/zcode-zai.ts 实测走通的那条链路。
        var zcode = SubscriptionCatalog.Find("zcode-subscription")!;
        Assert.Equal("zcode", zcode.Style);
        Assert.Equal("https://chat.z.ai/api/oauth/authorize", zcode.AuthorizeUrl);
        Assert.Equal("https://zcode.z.ai/api/v1/oauth/token", zcode.TokenUrl);
        Assert.Equal("client_P8X5CMWmlaRO9gyO-KSqtg", zcode.ClientId);
        Assert.Equal("https://api.z.ai/api/auth/z/login", zcode.BusinessLoginUrl);
        Assert.Equal("ZCode/3.10.2", zcode.ExtraHeaders["User-Agent"]);
        Assert.False(zcode.UsePkce);
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

    /// <summary>Subscription templates pin the fixed identity headers the upstream CLI clients send.</summary>
    [Fact]
    public void Subscription_Templates_Pin_Client_Identity_Headers()
    {
        var codex = _catalog.Get("openai-subscription")!;
        Assert.Equal("codex_cli_rs", codex.DefaultHeaders["originator"]);
        Assert.Equal("responses=experimental", codex.DefaultHeaders["OpenAI-Beta"]);

        var grok = _catalog.Get("grok-subscription")!;
        Assert.Equal("xai-grok-cli", grok.DefaultHeaders["x-grok-client-identifier"]);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", Assert.Single(grok.Endpoints).BaseUrl);
        Assert.False(grok.Endpoints[0].FullUrl);
        Assert.Equal("/models", grok.ModelListEndpoint?.Path);
    }
}
