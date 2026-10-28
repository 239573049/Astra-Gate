using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Direct Copilot tokens and refreshable GitHub grants follow different auth-recovery paths.</summary>
public class CopilotGatewayTests
{
    private const string TokenA = "tid=account-a;exp=4102444800;sku=copilot_pro";
    private const string TokenB = "tid=account-b;exp=4102444800;sku=copilot_pro";
    private const string RenewedA = "tid=account-a-new;exp=4102444800;sku=copilot_pro";
    private const string RenewedB = "tid=account-b-new;exp=4102444800;sku=copilot_pro";
    private const string Request = """{"model":"gpt-5","input":"hi","stream":false,"prompt_cache_key":"same-conversation"}""";
    private const string Denied = """{"error":{"message":"Channel unauthorized, please contact administrator","type":"authentication_error"}}""";
    private const string Completed = """{"id":"resp_1","object":"response","status":"completed","model":"gpt-5","output":[],"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}}""";

    private static async Task<GatewayFixture> StartAsync(
        string mode, bool refreshable, Func<FakeUpstream.Seen, HttpResponseMessage> respond)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, respond,
            authScheme: AuthSchemes.OAuthSubscription, routeOAuthThroughUpstream: true);
        gw.Provider.TemplateId = "github-copilot-subscription";
        gw.Provider.Settings = new JsonObject { ["subscription"] = new JsonObject { ["switch_mode"] = mode } };
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        // Known model capabilities keep discovery out of these credential/retry assertions.
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
        {
            ProviderId = gw.Provider.Id, ModelId = "gpt-5", UpstreamProtocols = [ApiProtocol.OpenAIResponses],
        });
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        foreach (var (id, token, order) in new[] { ("acc-a", TokenA, 0), ("acc-b", TokenB, 1) })
            await gw.Host.Db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = id, ProviderId = gw.Provider.Id, DisplayName = id, SortOrder = order,
                AccessTokenEnc = protector.Protect(token),
                RefreshTokenEnc = refreshable ? protector.Protect("gho-" + id) : null,
                ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(4102444800),
            });
        return gw;
    }

    private static string[] GenerationTokens(GatewayFixture gw) => gw.Upstream.Requests
        .Where(r => r.Url.AbsolutePath == "/v1/responses")
        .Select(r => r.Headers["authorization"]).ToArray();

    private static async Task<List<JsonObject>> AttemptsAsync(GatewayFixture gw, HttpResponseMessage response)
    {
        var record = await gw.RecordOfAsync(response);
        return (await gw.Host.Db.Traces.ListSpansAsync(record.Id))
            .Where(s => s.Name == TraceSpanNames.Attempt)
            .Select(s => JsonNode.Parse(s.AttributesJson!)!.AsObject()).ToList();
    }

    [Fact]
    public async Task Chat_Request_Translated_To_Responses_Carries_One_Cache_Key_Per_Conversation()
    {
        await using var gw = await StartAsync("failover", false, _ => FakeUpstream.Json(Completed));
        string Turn(string reminder) =>
            $$"""{"model":"gpt-5","stream":false,"messages":[{"role":"system","content":"You are an agent."},{"role":"user","content":"Fix it."},{"role":"system","content":"{{reminder}}"}]}""";

        using var first = await gw.PostAsync("/v1/chat/completions", Turn("10:00"));
        using var second = await gw.PostAsync("/v1/chat/completions", Turn("10:05"));
        using var own = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-5","stream":false,"prompt_cache_key":"mine","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var bodies = gw.Upstream.Requests.Where(r => r.Url.AbsolutePath == "/v1/responses").Select(r => r.Json).ToList();
        Assert.Equal(3, bodies.Count);
        var key = bodies[0]["prompt_cache_key"]!.GetValue<string>();
        Assert.StartsWith("astra-", key);
        Assert.Equal(key, bodies[1]["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal("You are an agent.", bodies[1]["instructions"]!.GetValue<string>());
        Assert.Equal("mine", bodies[2]["prompt_cache_key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("failover", 401)]
    [InlineData("failover", 403)]
    [InlineData("balanced", 401)]
    [InlineData("balanced", 403)]
    public async Task Direct_Token_Auth_Rejection_Switches_Accounts_Without_Refreshing(string mode, int status)
    {
        await using var gw = await StartAsync(mode, false, seen => seen.Headers["authorization"] == "Bearer " + TokenA
            ? FakeUpstream.Json(Denied, (HttpStatusCode)status) : FakeUpstream.Json(Completed));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer " + TokenA, "Bearer " + TokenB], GenerationTokens(gw));
        Assert.Equal(2, gw.Upstream.Requests.Count); // no GitHub exchange and no same-token retry
        var attempts = await AttemptsAsync(gw, response);
        Assert.Equal(2, attempts.Count);
        Assert.Equal("auth_failover", attempts[0]["astra.attempt.retry"]!.GetValue<string>());
        var rejected = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        Assert.Equal(AccountStatus.Active, rejected.Status); // a channel refusal need not revoke the grant
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal(TokenA, protector.Unprotect(rejected.AccessTokenEnc!));
        Assert.Null(rejected.RefreshTokenEnc);
        Assert.True(rejected.CooldownUntilUtc > DateTimeOffset.UtcNow);
        Assert.Contains(status.ToString(), rejected.LastError);
        Assert.Equal(mode == "failover", (await gw.Host.Db.Accounts.GetAsync("acc-b"))!.IsCurrent);

        // The same conversation moves to the next account, including on later client retries.
        using var next = await gw.PostAsync("/v1/responses", Request);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(["Bearer " + TokenA, "Bearer " + TokenB, "Bearer " + TokenB], GenerationTokens(gw));
    }

    [Fact]
    public async Task A_Successful_Refresh_Keeps_The_Conversation_On_The_Same_Account()
    {
        await using var gw = await StartAsync("balanced", true, seen => seen.Url.AbsolutePath == "/copilot_internal/v2/token"
            ? FakeUpstream.Json($$"""{"token":"{{RenewedA}}","expires_at":4102444800}""")
            : seen.Headers["authorization"] == "Bearer " + TokenA
                ? FakeUpstream.Json(Denied, HttpStatusCode.Unauthorized) : FakeUpstream.Json(Completed));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer " + TokenA, "Bearer " + RenewedA], GenerationTokens(gw));
        Assert.Equal(3, gw.Upstream.Requests.Count);
        Assert.Equal("acc-a", (await gw.RecordOfAsync(response)).AccountId);
        Assert.Null((await gw.Host.Db.Accounts.GetAsync("acc-a"))!.CooldownUntilUtc);
    }

    [Fact]
    public async Task A_Rejected_GitHub_Grant_Still_Hands_Over_To_The_Next_Account()
    {
        await using var gw = await StartAsync("balanced", true, seen => seen.Url.AbsolutePath == "/copilot_internal/v2/token"
            ? FakeUpstream.Json("""{"message":"Copilot not enabled"}""", HttpStatusCode.Forbidden)
            : seen.Headers["authorization"] == "Bearer " + TokenA
                ? FakeUpstream.Json(Denied, HttpStatusCode.Unauthorized) : FakeUpstream.Json(Completed));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer " + TokenA, "Bearer " + TokenB], GenerationTokens(gw));
        Assert.Equal(3, gw.Upstream.Requests.Count);
        Assert.Equal(AccountStatus.Revoked, (await gw.Host.Db.Accounts.GetAsync("acc-a"))!.Status);
        Assert.Equal("acc-b", (await gw.RecordOfAsync(response)).AccountId);
    }

    [Theory]
    [InlineData("failover", 401)]
    [InlineData("failover", 403)]
    [InlineData("balanced", 401)]
    [InlineData("balanced", 403)]
    public async Task Auth_Rejection_After_A_Successful_Refresh_Still_Switches_Accounts(string mode, int status)
    {
        await using var gw = await StartAsync(mode, true, seen => seen.Url.AbsolutePath == "/copilot_internal/v2/token"
            ? FakeUpstream.Json($$"""{"token":"{{RenewedA}}","expires_at":4102444800}""")
            : seen.Headers["authorization"] == "Bearer " + TokenB
                ? FakeUpstream.Json(Completed) : FakeUpstream.Json(Denied, (HttpStatusCode)status));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer " + TokenA, "Bearer " + RenewedA, "Bearer " + TokenB], GenerationTokens(gw));
        var refresh = Assert.Single(gw.Upstream.Requests, r => r.Url.AbsolutePath == "/copilot_internal/v2/token");
        Assert.Equal("token gho-acc-a", refresh.Headers["authorization"]);
        var attempts = await AttemptsAsync(gw, response);
        Assert.Equal(3, attempts.Count);
        Assert.Equal("auth_refresh", attempts[0]["astra.attempt.retry"]!.GetValue<string>());
        Assert.Equal("auth_failover", attempts[1]["astra.attempt.retry"]!.GetValue<string>());
        Assert.Equal("acc-a", attempts[1]["astra.account.id"]!.GetValue<string>());
        Assert.Equal("acc-b", attempts[2]["astra.account.id"]!.GetValue<string>());
        var rejected = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        Assert.True(rejected.CooldownUntilUtc > DateTimeOffset.UtcNow);

        // Quota headroom says nothing about authorization for the model/channel that just refused this account.
        rejected.Extra["quota"] = new JsonObject
        {
            ["fetchedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-6).ToString("o"),
            ["session"] = new JsonObject { ["usedPercent"] = 10 },
        };
        await gw.Host.Db.Accounts.UpdateAsync(rejected);
        var sync = gw.Host.App.Services.GetRequiredService<SubscriptionQuotaSync>();
        Assert.False(sync.IsCooledDue(gw.Provider, rejected, DateTimeOffset.UtcNow));
        Assert.False(await sync.ReleaseIfRecoveredAsync(rejected.Id));
        Assert.Equal(rejected.CooldownUntilUtc, (await gw.Host.Db.Accounts.GetAsync("acc-a"))!.CooldownUntilUtc);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    public async Task Manual_Mode_Returns_The_Original_Auth_Error_Without_Switching(bool refreshable, int status)
    {
        await using var gw = await StartAsync("manual", refreshable, seen => seen.Url.AbsolutePath == "/copilot_internal/v2/token"
            ? FakeUpstream.Json($$"""{"token":"{{RenewedA}}","expires_at":4102444800}""")
            : FakeUpstream.Json(Denied, (HttpStatusCode)status));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Contains("Channel unauthorized", await response.Content.ReadAsStringAsync());
        Assert.Equal(refreshable ? ["Bearer " + TokenA, "Bearer " + RenewedA] : ["Bearer " + TokenA], GenerationTokens(gw));
        Assert.Equal(refreshable ? 3 : 1, gw.Upstream.Requests.Count);
        var account = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Null(account.CooldownUntilUtc);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    public async Task When_All_Accounts_Are_Rejected_Each_Renews_At_Most_Once(bool refreshable, int status)
    {
        await using var gw = await StartAsync("balanced", refreshable, seen => seen.Url.AbsolutePath == "/copilot_internal/v2/token"
            ? FakeUpstream.Json($$"""{"token":"{{(seen.Headers["authorization"] == "token gho-acc-a" ? RenewedA : RenewedB)}}","expires_at":4102444800}""")
            : FakeUpstream.Json(Denied, (HttpStatusCode)status));

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(refreshable
            ? ["Bearer " + TokenA, "Bearer " + RenewedA, "Bearer " + TokenB, "Bearer " + RenewedB]
            : ["Bearer " + TokenA, "Bearer " + TokenB], GenerationTokens(gw));
        Assert.Equal(refreshable ? 6 : 2, gw.Upstream.Requests.Count);
        Assert.NotNull(await gw.RecordOfAsync(response));
        var scheduler = gw.Host.App.Services.GetRequiredService<AccountScheduler>();
        Assert.Equal(0, scheduler.InFlight("acc-a"));
        Assert.Equal(0, scheduler.InFlight("acc-b"));
    }

    [Theory]
    [InlineData("failover")]
    [InlineData("balanced")]
    public async Task An_Expired_Direct_Token_Does_Not_Block_The_Next_Usable_Account(string mode)
    {
        await using var gw = await StartAsync(mode, false, _ => FakeUpstream.Json(Completed));
        var expired = (await gw.Host.Db.Accounts.GetAsync("acc-a"))!;
        expired.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await gw.Host.Db.Accounts.UpdateAsync(expired);

        using var response = await gw.PostAsync("/v1/responses", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer " + TokenB], GenerationTokens(gw));
        Assert.Single(gw.Upstream.Requests);
        Assert.Equal(AccountStatus.Expired, (await gw.Host.Db.Accounts.GetAsync("acc-a"))!.Status);
    }
}
