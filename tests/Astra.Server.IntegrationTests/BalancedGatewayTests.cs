using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Subscription switch mode "balanced" through the gateway (plan §5.4): new conversations are spread over the accounts,
/// a conversation — one <c>prompt_cache_key</c>, one Claude Code session, one request fingerprint — stays on its account,
/// and a rate-limited account hands over without disturbing the provider's "current" account.
/// </summary>
public class BalancedGatewayTests
{
    private const string Message =
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}}""";

    private static readonly (string, string)[] ClaudeCodeHeaders =
    [
        ("User-Agent", "claude-cli/2.1.99 (external, cli)"), ("x-app", "cli"), ("anthropic-version", "2023-06-01"),
        ("anthropic-beta", "claude-code-20250219,interleaved-thinking-2025-05-14"), ("Accept", "application/json"),
    ];

    private static string ClaudeCodeBody(string text = "hi") =>
        $$"""{"model":"claude-sonnet-4-5","max_tokens":1024,"metadata":{"user_id":"user_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef_account__session_11111111-2222-3333-4444-555555555555"},"system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.99; cc_entrypoint=cli"},{"type":"text","text":"You are Claude Code."}],"messages":[{"role":"user","content":"{{text}}"}]}""";

    private static (string, string)[] WithSession(string session) => [.. ClaudeCodeHeaders, ("x-claude-code-session-id", session)];

    /// <summary>A provider with accounts acc-a (token at-A) and acc-b (at-B) in the given switch mode, bound to <paramref name="clientKind"/>.</summary>
    private static async Task<GatewayFixture> StartAsync(
        string clientKind, ApiProtocol protocol, Func<FakeUpstream.Seen, HttpResponseMessage> respond, string subscription, bool claude)
    {
        var gw = await GatewayFixture.StartAsync(clientKind, protocol, respond, authScheme: AuthSchemes.OAuthSubscription);
        if (claude)
        {
            gw.Provider.TemplateId = SubscriptionSupport.ClaudeSubscriptionKey;
            gw.Provider.Settings = JsonNode.Parse(
                $$"""{"subscription_oauth":{"verified":true,"client_id":"test-client"},"subscription":{{subscription}}}""")!.AsObject();
        }
        else
        {
            gw.Provider.Settings = JsonNode.Parse($$"""{"subscription":{{subscription}}}""")!.AsObject();
        }
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

    private static Task<GatewayFixture> StartClaudeAsync(string subscription, Func<FakeUpstream.Seen, HttpResponseMessage>? respond = null) =>
        StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, respond ?? (_ => FakeUpstream.Json(Message)), subscription, claude: true);

    private static string[] Tokens(GatewayFixture gw) =>
        gw.Upstream.Requests.Select(r => r.Headers["authorization"].Replace("Bearer ", "")).ToArray();

    private const string Balanced = """{"switch_mode":"balanced"}""";

    [Fact]
    public async Task Claude_Code_Sessions_Are_Spread_And_Each_Session_Sticks_To_Its_Account()
    {
        await using var gw = await StartClaudeAsync(Balanced);

        foreach (var session in new[] { "s1", "s2", "s1", "s2", "s1" })
            Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession(session))).StatusCode);

        Assert.Equal(["at-A", "at-B", "at-A", "at-B", "at-A"], Tokens(gw));
    }

    [Fact]
    public async Task The_Session_Id_In_Metadata_User_Id_Also_Sticks()
    {
        await using var gw = await StartClaudeAsync(Balanced);
        string Body(string session) => ClaudeCodeBody().Replace("11111111-2222-3333-4444-555555555555", session);

        // Same device, no session header: only the session inside metadata.user_id tells conversations apart.
        foreach (var session in new[] { "aaaaaaaa-2222-3333-4444-555555555555", "bbbbbbbb-2222-3333-4444-555555555555", "aaaaaaaa-2222-3333-4444-555555555555" })
            Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Body(session), null, ClaudeCodeHeaders)).StatusCode);

        Assert.Equal(["at-A", "at-B", "at-A"], Tokens(gw));
    }

    [Fact]
    public async Task Clients_Without_A_Session_Id_Stick_By_Request_Fingerprint()
    {
        await using var gw = await StartClaudeAsync("""{"switch_mode":"balanced","client_policy":"any"}""");
        (string, string)[] other = [("User-Agent", "opencode/1.0"), ("anthropic-version", "2023-06-01")];
        string Chat(params string[] turns) => new JsonObject
        {
            ["model"] = "claude-sonnet-4-5", ["max_tokens"] = 256, ["system"] = "be brief",
            ["messages"] = new JsonArray(turns.Select((t, i) => (JsonNode)new JsonObject
            {
                ["role"] = i % 2 == 0 ? "user" : "assistant", ["content"] = t,
            }).ToArray()),
        }.ToJsonString();

        await gw.PostAsync("/v1/messages", Chat("first topic"), null, other);                      // new conversation → a
        await gw.PostAsync("/v1/messages", Chat("second topic"), null, other);                     // new conversation → b
        await gw.PostAsync("/v1/messages", Chat("first topic", "ok", "and then?"), null, other);    // same conversation, later turn → a
        await gw.PostAsync("/v1/messages", Chat("second topic", "ok", "and then?"), null, other);   // → b

        Assert.Equal(["at-A", "at-B", "at-A", "at-B"], Tokens(gw));
    }

    [Fact]
    public async Task A_Rate_Limited_Account_Hands_Over_Without_Moving_The_Current_Account()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(3);
        await using var gw = await StartClaudeAsync(Balanced, seen => seen.Headers["authorization"] == "Bearer at-A"
            ? FakeUpstream.Json("""{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""", HttpStatusCode.TooManyRequests,
                ("anthropic-ratelimit-unified-reset", reset.ToUnixTimeSeconds().ToString()))
            : FakeUpstream.Json(Message));

        var response = await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession("s1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acc-b", (await gw.RecordOfAsync(response)).AccountId);

        var a = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        Assert.Equal(reset.ToUnixTimeSeconds(), a.CooldownUntilUtc!.Value.ToUnixTimeSeconds());
        Assert.False((await gw.Host.Db.Accounts.GetAsync("acc-b"))!.IsCurrent); // balanced never touches "current"

        // The conversation moved with it; the cooled-down account is not tried again.
        await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession("s1"));
        await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession("s2"));
        Assert.Equal(["at-A", "at-B", "at-B", "at-B"], Tokens(gw));
    }

    [Fact]
    public async Task When_Every_Account_Is_Rate_Limited_The_429_Is_Handed_Back()
    {
        await using var gw = await StartClaudeAsync(Balanced, _ => FakeUpstream.Json(
            """{"type":"error","error":{"type":"rate_limit_error","message":"limit"}}""", HttpStatusCode.TooManyRequests, ("retry-after", "120")));

        var response = await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession("s1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(["at-A", "at-B"], Tokens(gw)); // each tried once, no loop
        await AssertNoRequestInFlightAsync(gw);
    }

    [Fact]
    public async Task A_Pinned_Account_Is_Never_Rescheduled()
    {
        await using var gw = await StartClaudeAsync(Balanced);
        await gw.Host.Db.Clients.SetBindingAsync(new ClientBinding
        {
            ClientKind = ClientKinds.ClaudeCode, ProviderId = gw.Provider.Id, AccountId = "acc-b",
        });

        foreach (var session in new[] { "s1", "s2", "s3" })
            await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession(session));

        Assert.Equal(["at-B", "at-B", "at-B"], Tokens(gw));
    }

    [Fact]
    public async Task Manual_Mode_Keeps_Using_The_Current_Account_For_Every_Session()
    {
        await using var gw = await StartClaudeAsync("""{"switch_mode":"manual"}""");

        foreach (var session in new[] { "s1", "s2", "s3" })
            await gw.PostAsync("/v1/messages", ClaudeCodeBody(), null, WithSession(session));

        Assert.Equal(["at-A", "at-A", "at-A"], Tokens(gw));
    }

    [Fact]
    public async Task Prompt_Cache_Keys_Are_Bound_To_An_Account()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "response-completed.json"));
        await using var gw = await StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json(body), Balanced, claude: false);
        string Request(string? key) => new JsonObject { ["model"] = "gpt-5", ["input"] = "hi", ["stream"] = false, ["prompt_cache_key"] = key }.ToJsonString();

        foreach (var key in new[] { "k1", "k2", "k1", "k2", "k1" })
            Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/responses", Request(key))).StatusCode);
        // Without a key there is nothing to bind: plain load balancing (a has been used 3 times, b twice → b).
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/responses", Request(null))).StatusCode);

        Assert.Equal(["at-A", "at-B", "at-A", "at-B", "at-A", "at-B"], Tokens(gw));
        await AssertNoRequestInFlightAsync(gw);
    }

    [Fact]
    public async Task Switch_Mode_Balanced_Can_Be_Selected_Through_The_Policy_Api()
    {
        await using var gw = await StartClaudeAsync("""{"switch_mode":"manual"}""");
        var url = $"/api/providers/{gw.Provider.Id}/subscription-policy";

        var (status, policy) = await gw.Host.SendAsync(HttpMethod.Put, url, new { switchMode = "balanced" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("balanced", policy!["switchMode"]!.GetValue<string>());

        (status, _) = await gw.Host.SendAsync(HttpMethod.Put, url, new { switchMode = "round-robin" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>The in-flight slot is released when the request ends, success or not (the handler's finally may trail the response).</summary>
    private static async Task AssertNoRequestInFlightAsync(GatewayFixture gw)
    {
        var scheduler = gw.Host.App.Services.GetRequiredService<AccountScheduler>();
        for (var i = 0; i < 100 && (scheduler.InFlight("acc-a") != 0 || scheduler.InFlight("acc-b") != 0); i++)
            await Task.Delay(20);
        Assert.Equal(0, scheduler.InFlight("acc-a"));
        Assert.Equal(0, scheduler.InFlight("acc-b"));
    }
}
