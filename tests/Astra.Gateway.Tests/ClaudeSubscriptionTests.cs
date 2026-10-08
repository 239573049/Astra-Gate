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
        var body = Body($$"""{"model":"m","max_tokens":10,"metadata":{"user_id":"{{LegacyUserId}}"}}""");

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

    [Theory]
    [InlineData("claude-code-20250219,interleaved-thinking-2025-05-14", "claude-sonnet-4-5",
        "claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14")]
    [InlineData("interleaved-thinking-2025-05-14", "claude-sonnet-4-5", "oauth-2025-04-20,interleaved-thinking-2025-05-14")]
    [InlineData("claude-code-20250219, oauth-2025-04-20", "claude-sonnet-4-5", "claude-code-20250219,oauth-2025-04-20")]
    [InlineData("", "claude-sonnet-4-5", ClaudeOAuthHeaders.DefaultBeta)]
    [InlineData(null, "claude-haiku-4-5", ClaudeOAuthHeaders.HaikuBeta)]
    public void Relay_Beta_Gets_The_OAuth_Beta_Next_To_Claude_Code(string? client, string model, string expected) =>
        Assert.Equal(expected, ClaudeOAuthHeaders.MergeRelayBeta(client, model));

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

        string H(string name) => string.Join(",", request.Headers.GetValues(name));
        Assert.Equal(Ua, H("User-Agent"));
        Assert.Equal("cli", H("x-app"));
        Assert.Equal("js", H("x-stainless-lang"));
        Assert.Equal("0", H("x-stainless-retry-count"));
        Assert.Equal("sess-1", H("x-claude-code-session-id"));
        Assert.Equal("application/json", H("Accept"));
        Assert.Equal("claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14", H("anthropic-beta"));
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
