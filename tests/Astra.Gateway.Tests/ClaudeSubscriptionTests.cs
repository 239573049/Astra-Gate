using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

/// <summary>Claude subscription (plan §5.4): Claude Code detection, OAuth header policy and account selection.</summary>
public class ClaudeSubscriptionTests
{
    private const string Ua = "claude-cli/2.1.99 (external, cli)";
    private const string LegacyUserId =
        "user_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef_account__session_11111111-2222-3333-4444-555555555555";

    private static HeaderDictionary ClaudeCodeHeaders() => new()
    {
        ["User-Agent"] = Ua,
        ["x-app"] = "cli",
        ["anthropic-version"] = "2023-06-01",
        ["anthropic-beta"] = "claude-code-20250219,interleaved-thinking-2025-05-14",
    };

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------- detection ----------

    [Fact]
    public void Claude_Code_Is_Recognized_By_Ua_App_Header_And_User_Id()
    {
        var body = Body($$"""{"model":"claude-sonnet-4-5","max_tokens":32000,"metadata":{"user_id":"{{LegacyUserId}}"},"messages":[]}""");
        Assert.True(ClaudeCodeDetector.IsClaudeCode(ClaudeCodeHeaders(), body));
    }

    [Fact]
    public void Json_User_Id_And_System_Markers_Also_Count()
    {
        var jsonUserId = Body("""{"model":"m","max_tokens":10,"metadata":{"user_id":"{\"device_id\":\"d1\",\"account_uuid\":\"\",\"session_id\":\"s1\"}"}}""");
        Assert.True(ClaudeCodeDetector.IsClaudeCode(ClaudeCodeHeaders(), jsonUserId));

        var billingBlock = Body("""{"model":"m","max_tokens":10,"system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.99; cc_entrypoint=cli"},{"type":"text","text":"..."}]}""");
        Assert.True(ClaudeCodeDetector.IsClaudeCode(ClaudeCodeHeaders(), billingBlock));

        var prompt = Body("""{"model":"m","max_tokens":10,"system":"You are Claude Code, Anthropic's official CLI for Claude. Be brief."}""");
        Assert.True(ClaudeCodeDetector.IsClaudeCode(ClaudeCodeHeaders(), prompt));
    }

    [Fact]
    public void Other_Clients_Are_Not_Claude_Code()
    {
        var body = Body($$$"""{"model":"m","max_tokens":10,"metadata":{"user_id":"{{{LegacyUserId}}}"}}""");

        var otherUa = ClaudeCodeHeaders();
        otherUa["User-Agent"] = "opencode/1.0";
        Assert.False(ClaudeCodeDetector.IsClaudeCode(otherUa, body));

        var noApp = ClaudeCodeHeaders();
        noApp.Remove("x-app");
        Assert.False(ClaudeCodeDetector.IsClaudeCode(noApp, body));

        // Right UA and headers, but neither a Claude Code user id nor a Claude Code system prompt.
        Assert.False(ClaudeCodeDetector.IsClaudeCode(ClaudeCodeHeaders(), Body("""{"model":"m","max_tokens":10,"metadata":{"user_id":"abc"},"system":"You are a helpful bot."}""")));
    }

    [Fact]
    public void Probes_And_Count_Tokens_Pass_On_The_Ua_Alone()
    {
        var bare = new HeaderDictionary { ["User-Agent"] = Ua };
        Assert.True(ClaudeCodeDetector.IsClaudeCode(bare, Body("""{"model":"claude-haiku-4-5","max_tokens":1,"messages":[]}""")));
        Assert.True(ClaudeCodeDetector.IsClaudeCode(bare, null, countTokens: true));
        Assert.False(ClaudeCodeDetector.IsClaudeCode(new HeaderDictionary { ["User-Agent"] = "curl/8" }, null, countTokens: true));
    }

    // ---------- anthropic-beta ----------

    /// <summary>
    /// 真实抓包（claude-cli/2.1.281，原始请求）：同一个 CC 在 auth-token 模式（Astra 收到的）与订阅模式
    /// （能用的直连）下的 anthropic-beta。订阅集 = auth-token 集 + 恰好两个 token——oauth 紧跟 claude-code、
    /// extended-cache-ttl 放最后，其余逐个相同；集合大小随模型变（8 / 10 / 12），所以不能是固定列表。
    /// </summary>
    [Theory]
    [InlineData(
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,per-turn-control-2026-07-01,mid-conversation-tool-changes-2026-07-01,advisor-tool-2026-03-01,effort-2025-11-24",
        "claude-opus-5-5",
        "claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,per-turn-control-2026-07-01,mid-conversation-tool-changes-2026-07-01,advisor-tool-2026-03-01,effort-2025-11-24,extended-cache-ttl-2025-04-11")]
    [InlineData(
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,advisor-tool-2026-03-01,effort-2025-11-24",
        "claude-sonnet-5",
        "claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,advisor-tool-2026-03-01,effort-2025-11-24,extended-cache-ttl-2025-04-11")]
    // haiku 的 -p 订阅请求（真实抓包）：claude-code 在 prompt-caching-scope 之后，oauth 紧跟它。
    [InlineData(
        "interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,claude-code-20250219,advisor-tool-2026-03-01",
        "claude-haiku-4-5",
        "interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,claude-code-20250219,oauth-2025-04-20,advisor-tool-2026-03-01,extended-cache-ttl-2025-04-11")]
    // 已经带 oauth / ttl 的客户端：不重复追加。
    [InlineData("claude-code-20250219,oauth-2025-04-20,extended-cache-ttl-2025-04-11", "claude-sonnet-5",
        "claude-code-20250219,oauth-2025-04-20,extended-cache-ttl-2025-04-11")]
    // 客户端没发 beta：退回 CLI 默认值，再补两个订阅专属 token。
    [InlineData(null, "claude-haiku-4-5", "oauth-2025-04-20,interleaved-thinking-2025-05-14,extended-cache-ttl-2025-04-11")]
    public void Relay_Beta_Is_The_Clients_Own_Set_Plus_The_Two_Subscription_Only_Tokens(string? client, string model, string expected) =>
        Assert.Equal(expected, ClaudeOAuthHeaders.MergeRelayBeta(client, model));

    /// <summary>
    /// 真实抓包：auth-token 模式的 CC 请求体里 system[1]/system[2]/最后一个消息块的 cache_control 是
    /// {"type":"ephemeral"}，订阅模式是 {"type":"ephemeral","ttl":"1h"}——这是两种模式体的唯一结构差异，
    /// 且与头里的 extended-cache-ttl beta 配套。已有 ttl 的不动，非 JSON 原样返回。
    /// </summary>
    [Fact]
    public void Relay_Body_Gets_The_One_Hour_Cache_Ttl_The_Subscription_Mode_Sends()
    {
        const string authTokenBody = """
            {"model":"claude-opus-5-5","system":[{"type":"text","text":"billing"},{"type":"text","text":"id","cache_control":{"type":"ephemeral"}},{"type":"text","text":"rules","cache_control":{"type":"ephemeral"}}],
             "messages":[{"role":"user","content":[{"type":"text","text":"hi","cache_control":{"type":"ephemeral"}}]}],"tools":[{"name":"t","cache_control":{"type":"ephemeral","ttl":"5m"}}]}
            """;

        var result = System.Text.Json.Nodes.JsonNode.Parse(ClaudeOAuthHeaders.PromoteCacheControlTtl(authTokenBody))!;

        Assert.Equal("1h", result["system"]![1]!["cache_control"]!["ttl"]!.GetValue<string>());
        Assert.Equal("1h", result["system"]![2]!["cache_control"]!["ttl"]!.GetValue<string>());
        Assert.Equal("1h", result["messages"]![0]!["content"]![0]!["cache_control"]!["ttl"]!.GetValue<string>());
        // 客户端自己给过 ttl 的块不被改写；没有 cache_control 的块不被加。
        Assert.Equal("5m", result["tools"]![0]!["cache_control"]!["ttl"]!.GetValue<string>());
        Assert.Null(result["system"]![0]!["cache_control"]);
        Assert.Equal("claude-opus-5-5", result["model"]!.GetValue<string>());
        // 幂等：订阅形态的体再过一遍不变；非 JSON 原样返回。
        var once = ClaudeOAuthHeaders.PromoteCacheControlTtl(authTokenBody);
        Assert.Equal(once, ClaudeOAuthHeaders.PromoteCacheControlTtl(once));
        Assert.Equal("not json", ClaudeOAuthHeaders.PromoteCacheControlTtl("not json"));
    }

    [Fact]
    public void Compat_Beta_Puts_The_Required_Betas_First_Without_Duplicates() =>
        Assert.Equal("claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,context-1m-2025-08-07",
            ClaudeOAuthHeaders.MergeCompatBeta("context-1m-2025-08-07,oauth-2025-04-20"));

    [Fact]
    public void Relay_Copies_Claude_Code_Headers_But_Never_Its_Credentials()
    {
        var client = ClaudeCodeHeaders();
        client["Accept"] = "application/json";
        client["x-stainless-lang"] = "js";
        client["x-stainless-retry-count"] = "0";
        client["x-claude-code-session-id"] = "sess-1";
        client["Authorization"] = "Bearer sk-astra-local";
        client["x-api-key"] = "sk-astra-local";
        client["Cookie"] = "a=b";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        ClaudeOAuthHeaders.ApplyRelay(request, client, "claude-sonnet-4-5",
            new Dictionary<string, string> { ["anthropic-version"] = "2099-01-01", ["x-extra"] = "1" }, "2023-06-01");

        // The raw wire values (GetValues would re-split a User-Agent into product tokens).
        string H(string name) => request.Headers.NonValidated[name].ToString();
        // UA 重建为单个 product token（.NET 无法表示 product+注释 组合，见 SetUserAgent）；
        // 版本取客户端 UA 里的 2.1.99。
        Assert.Equal("claude-cli/2.1.99", H("User-Agent"));
        Assert.Equal("cli", H("x-app"));
        Assert.Equal("js", H("x-stainless-lang"));
        Assert.Equal("0", H("x-stainless-retry-count"));
        Assert.Equal("sess-1", H("x-claude-code-session-id"));
        Assert.Equal("application/json", H("Accept"));
        Assert.Contains("oauth-2025-04-20", H("anthropic-beta"));
        Assert.EndsWith("extended-cache-ttl-2025-04-11", H("anthropic-beta"));
        // The client's value wins over provider extras; extras only fill gaps.
        Assert.Equal("2023-06-01", H("anthropic-version"));
        Assert.Equal("1", H("x-extra"));
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("x-api-key"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString());
    }

    // ---------- account selection ----------

    private static ProviderAccount Acc(string id, bool current = false, bool enabled = true, string status = AccountStatus.Active,
        DateTimeOffset? cooldown = null) => new()
    {
        Id = id, ProviderId = "p", IsCurrent = current, Enabled = enabled, Status = status, CooldownUntilUtc = cooldown,
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Selection_Prefers_Pin_Then_Current_Then_Order()
    {
        List<ProviderAccount> all = [Acc("a"), Acc("b", current: true), Acc("c")];
        Assert.Equal("c", SubscriptionSupport.SelectAccount(all, "c", Now)!.Id);
        Assert.Equal("b", SubscriptionSupport.SelectAccount(all, null, Now)!.Id);
        Assert.Equal("a", SubscriptionSupport.SelectAccount([Acc("a"), Acc("b")], null, Now)!.Id);
    }

    [Fact]
    public void Selection_Skips_Disabled_Dead_And_Cooling_Accounts()
    {
        List<ProviderAccount> all =
        [
            Acc("disabled", enabled: false),
            Acc("revoked", status: AccountStatus.Revoked),
            Acc("cooling", current: true, cooldown: Now.AddMinutes(10)),
            Acc("ok"),
        ];
        Assert.Equal("ok", SubscriptionSupport.SelectAccount(all, "revoked", Now)!.Id);
        // An expired cooldown no longer counts.
        Assert.Equal("cooling", SubscriptionSupport.SelectAccount(all, null, Now.AddMinutes(11))!.Id);
    }

    [Fact]
    public void Selection_Falls_Back_To_An_Enabled_Account_And_Never_To_A_Disabled_One()
    {
        // Everyone is cooling down: still try the first enabled active account (the upstream decides).
        Assert.Equal("a", SubscriptionSupport.SelectAccount([Acc("a", cooldown: Now.AddHours(1))], null, Now)!.Id);
        Assert.Null(SubscriptionSupport.SelectAccount([Acc("a", enabled: false)], null, Now));
    }

    [Fact]
    public void Client_Policy_Defaults_To_Claude_Code_Only_For_The_Claude_Subscription_Only()
    {
        var claude = new Provider { Id = "p1", TemplateId = "claude-subscription", AuthScheme = AuthSchemes.OAuthSubscription };
        var codex = new Provider { Id = "p2", TemplateId = "openai-subscription", AuthScheme = AuthSchemes.OAuthSubscription };
        Assert.Equal(ClientPolicies.ClaudeCodeOnly, SubscriptionSupport.ClientPolicyOf(claude));
        Assert.Equal(ClientPolicies.Any, SubscriptionSupport.ClientPolicyOf(codex));
        Assert.Equal(SwitchModes.Manual, SubscriptionSupport.SwitchModeOf(claude));

        claude.Settings = Body("""{"subscription":{"client_policy":"any","switch_mode":"failover"}}""");
        Assert.Equal(ClientPolicies.Any, SubscriptionSupport.ClientPolicyOf(claude));
        Assert.Equal(SwitchModes.Failover, SubscriptionSupport.SwitchModeOf(claude));
        SubscriptionSupport.EnforceClientPolicy(claude, isClaudeCode: false); // lifted: no throw

        var error = Assert.Throws<GatewayException>(() => SubscriptionSupport.EnforceClientPolicy(
            new Provider { Id = "p3", Name = "Claude", TemplateId = "claude-subscription", AuthScheme = AuthSchemes.OAuthSubscription }, false));
        Assert.Equal(403, error.Status);
    }

    // ---------- rate-limit reset ----------

    [Fact]
    public void Rate_Limit_Reset_Reads_Anthropic_Then_Retry_After_Then_Defaults()
    {
        using var anthropic = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        anthropic.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-reset", Now.AddHours(2).ToUnixTimeSeconds().ToString());
        Assert.Equal(Now.AddHours(2), GatewayPipeline.RateLimitResetOf(anthropic, Now));

        using var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retry.Headers.TryAddWithoutValidation("Retry-After", "600");
        Assert.Equal(Now.AddMinutes(10), GatewayPipeline.RateLimitResetOf(retry, Now));

        using var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(Now.AddMinutes(5), GatewayPipeline.RateLimitResetOf(none, Now));

        using var tiny = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        tiny.Headers.TryAddWithoutValidation("Retry-After", "1");
        Assert.Equal(Now.AddMinutes(1), GatewayPipeline.RateLimitResetOf(tiny, Now));
    }

    [Fact]
    public void Client_Query_Is_Appended()
    {
        Assert.Equal("https://u/v1/messages?beta=true", GatewayPipeline.WithClientQuery("https://u/v1/messages", new QueryString("?beta=true")));
        Assert.Equal("https://u/x?a=1&beta=true", GatewayPipeline.WithClientQuery("https://u/x?a=1", new QueryString("?beta=true")));
        Assert.Equal("https://u/x", GatewayPipeline.WithClientQuery("https://u/x", QueryString.Empty));
    }
}
