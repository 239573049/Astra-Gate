using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Claude subscription through the gateway (plan §5.4): Claude Code relay, the "Claude Code only" client policy,
/// account switching and automatic failover.
/// </summary>
public class ClaudeSubscriptionGatewayTests
{
    private const string Ua = "claude-cli/2.1.99 (external, cli)";
    private const string Message =
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}}""";
    private const string ClaudeCodeBody =
        """{"model":"claude-sonnet-4-5","max_tokens":1024,"metadata":{"user_id":"user_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef_account__session_11111111-2222-3333-4444-555555555555"},"system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.99; cc_entrypoint=cli"}],"messages":[{"role":"user","content":"hi"}]}""";
    private const string PlainBody = """{"model":"claude-sonnet-4-5","max_tokens":1024,"messages":[{"role":"user","content":"hi"}]}""";

    private static readonly (string, string)[] ClaudeCodeHeaders =
    [
        ("User-Agent", Ua), ("x-app", "cli"), ("anthropic-version", "2023-06-01"),
        ("anthropic-beta", "claude-code-20250219,interleaved-thinking-2025-05-14"),
        ("x-stainless-lang", "js"), ("x-claude-code-session-id", "sess-1"), ("Accept", "application/json"),
    ];

    /// <summary>A Claude subscription provider with accounts acc-a (token at-A) and acc-b (at-B), bound to Claude Code.</summary>
    private static async Task<GatewayFixture> StartAsync(Func<FakeUpstream.Seen, HttpResponseMessage> respond, string? subscriptionSettings = null)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, respond, authScheme: AuthSchemes.OAuthSubscription);
        gw.Provider.TemplateId = SubscriptionSupport.ClaudeSubscriptionKey;
        gw.Provider.Settings = JsonNode.Parse(
            $$"""{"subscription_oauth":{"verified":true,"client_id":"test-client"}{{(subscriptionSettings is null ? "" : $",\"subscription\":{subscriptionSettings}")}}}""")!.AsObject();
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        foreach (var (id, token) in new[] { ("acc-a", "at-A"), ("acc-b", "at-B") })
            await gw.Host.Db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = id,
                ProviderId = gw.Provider.Id,
                DisplayName = id,
                AccessTokenEnc = protector.Protect(token),
                RefreshTokenEnc = protector.Protect("rt-" + id),
                ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
                Status = AccountStatus.Active,
            });
        return gw;
    }

    [Fact]
    public async Task Claude_Code_Is_Relayed_With_Its_Own_Headers_And_The_OAuth_Beta()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json(Message, HttpStatusCode.OK,
            ("anthropic-ratelimit-unified-status", "allowed"), ("anthropic-ratelimit-unified-5h-utilization", "0.42"), ("request-id", "req_1")));

        var response = await gw.PostAsync("/v1/messages?beta=true", ClaudeCodeBody, null, [.. ClaudeCodeHeaders, ("x-api-key", gw.Key)]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/messages?beta=true", seen.Url.ToString());
        Assert.Equal("Bearer at-A", seen.Headers["authorization"]);
        Assert.False(seen.Headers.ContainsKey("x-api-key"));
        Assert.Equal(Ua, seen.Headers["user-agent"]);
        Assert.Equal("cli", seen.Headers["x-app"]);
        Assert.Equal("js", seen.Headers["x-stainless-lang"]);
        Assert.Equal("sess-1", seen.Headers["x-claude-code-session-id"]);
        Assert.Equal("application/json", seen.Headers["accept"]);
        Assert.Equal("claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14", seen.Headers["anthropic-beta"]);
        Assert.Equal(ClaudeCodeBody, seen.Body); // the body is forwarded byte for byte

        // Claude Code reads its usage warnings from these.
        Assert.Equal("allowed", response.Headers.GetValues("anthropic-ratelimit-unified-status").Single());
        Assert.Equal("0.42", response.Headers.GetValues("anthropic-ratelimit-unified-5h-utilization").Single());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal("acc-a", record.AccountId);
        Assert.Equal("acc-a", record.AccountName);
    }

    [Fact]
    public async Task Other_Clients_Are_Rejected_Before_Anything_Leaves_The_Machine()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json(Message));

        var response = await gw.PostAsync("/v1/messages", PlainBody, null, ("User-Agent", "opencode/1.0"), ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("仅允许 Claude Code", await response.Content.ReadAsStringAsync());

        var count = await gw.PostAsync("/v1/messages/count_tokens", PlainBody, null, ("User-Agent", "opencode/1.0"));
        Assert.Equal(HttpStatusCode.Forbidden, count.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Lifting_The_Policy_Lets_Other_Clients_Through_With_The_Required_Betas()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json(Message));
        var (status, policy) = await gw.Host.SendAsync(HttpMethod.Put, $"/api/providers/{gw.Provider.Id}/subscription-policy", new { clientPolicy = "any" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("any", policy!["clientPolicy"]!.GetValue<string>());
        Assert.True(policy["claudeSubscription"]!.GetValue<bool>());

        var response = await gw.PostAsync("/v1/messages", PlainBody, null, ("User-Agent", "opencode/1.0"), ("anthropic-beta", "context-1m-2025-08-07"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,context-1m-2025-08-07", seen.Headers["anthropic-beta"]);
        Assert.Equal("Bearer at-A", seen.Headers["authorization"]);
        Assert.Equal("opencode/1.0", seen.Headers["user-agent"]); // nothing is impersonated
    }

    [Fact]
    public async Task Count_Tokens_From_Claude_Code_Is_Relayed_Too()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json("""{"input_tokens":12}"""));

        var response = await gw.PostAsync("/v1/messages/count_tokens?beta=true", PlainBody, null, ClaudeCodeHeaders);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/messages/count_tokens?beta=true", seen.Url.ToString());
        Assert.Contains("oauth-2025-04-20", seen.Headers["anthropic-beta"]);
        Assert.Equal(Ua, seen.Headers["user-agent"]);
        Assert.Equal("Bearer at-A", seen.Headers["authorization"]);
    }

    [Fact]
    public async Task Switching_Accounts_Takes_Effect_On_The_Next_Request()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json(Message));
        var accounts = $"/api/providers/{gw.Provider.Id}/accounts";

        var initial = (await gw.Host.GetJsonAsync(accounts)).AsArray();
        Assert.Equal(["acc-a"], Current(initial));

        var (status, afterSwitch) = await gw.Host.SendAsync(HttpMethod.Post, "/api/provider-accounts/acc-b/activate");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["acc-b"], Current(afterSwitch!.AsArray()));

        await gw.PostAsync("/v1/messages", ClaudeCodeBody, null, ClaudeCodeHeaders);
        Assert.Equal("Bearer at-B", gw.Upstream.Requests.Last().Headers["authorization"]);

        // Disabling the current account hands over to the next usable one by order.
        (status, var afterDisable) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/provider-accounts/acc-b", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["acc-a"], Current(afterDisable!.AsArray()));
        Assert.False(afterDisable.AsArray().Single(a => a!["id"]!.GetValue<string>() == "acc-b")!["enabled"]!.GetValue<bool>());

        (status, var reordered) = await gw.Host.SendAsync(HttpMethod.Put, $"{accounts}/order", new { ids = new[] { "acc-b", "acc-a" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["acc-b", "acc-a"], reordered!.AsArray().Select(a => a!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Failover_Mode_Moves_A_Rate_Limited_Request_To_The_Next_Account()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(3);
        await using var gw = await StartAsync(seen => seen.Headers["authorization"] == "Bearer at-A"
                ? FakeUpstream.Json("""{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""", HttpStatusCode.TooManyRequests,
                    ("anthropic-ratelimit-unified-reset", reset.ToUnixTimeSeconds().ToString()))
                : FakeUpstream.Json(Message),
            subscriptionSettings: """{"switch_mode":"failover"}""");

        var response = await gw.PostAsync("/v1/messages", ClaudeCodeBody, null, ClaudeCodeHeaders);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer at-A", "Bearer at-B"], gw.Upstream.Requests.Select(r => r.Headers["authorization"]));
        Assert.Equal("acc-b", (await gw.RecordOfAsync(response)).AccountId);

        var a = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        Assert.Equal(reset.ToUnixTimeSeconds(), a.CooldownUntilUtc!.Value.ToUnixTimeSeconds());
        Assert.Contains("429", a.LastError);
        Assert.True((await gw.Host.Db.Accounts.GetAsync("acc-b"))!.IsCurrent);

        // The next request goes straight to the new current account.
        await gw.PostAsync("/v1/messages", ClaudeCodeBody, null, ClaudeCodeHeaders);
        Assert.Equal("Bearer at-B", gw.Upstream.Requests.Last().Headers["authorization"]);
        Assert.Equal(3, gw.Upstream.Requests.Count);
    }

    [Fact]
    public async Task Manual_Mode_Hands_The_429_Back_Without_Switching()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json("""{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""",
            HttpStatusCode.TooManyRequests, ("retry-after", "120")));

        var response = await gw.PostAsync("/v1/messages", ClaudeCodeBody, null, ClaudeCodeHeaders);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(gw.Upstream.Requests);
        Assert.Null((await gw.Host.Db.Accounts.GetAsync("acc-a"))!.CooldownUntilUtc);
        Assert.Equal("120", response.Headers.GetValues("retry-after").Single());
    }

    private static string[] Current(JsonArray accounts) =>
        accounts.Where(a => a!["isCurrent"]!.GetValue<bool>()).Select(a => a!["id"]!.GetValue<string>()).ToArray();
}
