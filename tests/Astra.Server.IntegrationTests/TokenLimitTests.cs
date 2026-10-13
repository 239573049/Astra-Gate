using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Token caps, client restrictions and the key's self-service views (plan: alerts-webhooks-token-limits §1–2).</summary>
public class TokenLimitTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string Body = """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""";

    private static Task<GatewayFixture> StartAsync() =>
        GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));

    private static async Task PatchAsync(GatewayFixture gw, object body)
    {
        var (status, response) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{TokenIds.Default}", body);
        Assert.True(status == HttpStatusCode.OK, response?.ToJsonString());
    }

    [Fact]
    public async Task A_Used_Up_Periodic_Budget_Answers_429_With_Retry_After_And_Is_Not_Counted()
    {
        await using var gw = await StartAsync();
        await PatchAsync(gw, new { limits = new { budgets = new[] { new { metric = "requests", period = "day", limit = 2 } } } });

        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        var refused = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("今日请求数额度已用完", text);
        Assert.DoesNotContain(gw.Provider.Name, text);
        Assert.Equal(2, gw.Upstream.Requests.Count);

        // Logged, but counted nowhere: the budget still reads 2 of 2.
        var record = await gw.RecordOfAsync(refused);
        Assert.Equal(TokenLimits.RefusedErrorType, record.ErrorType);
        Assert.Equal(RequestStatus.GatewayError, record.Status);
        var token = Assert.Single((await gw.Host.GetJsonAsync("/api/tokens")).AsArray())!;
        var usage = Assert.Single(token["limitUsage"]!.AsArray())!;
        Assert.Equal(2m, usage["used"]!.GetValue<decimal>());
        Assert.True(usage["exhausted"]!.GetValue<bool>());
        Assert.NotNull(usage["resetsAt"]);
        Assert.Equal(2, token["total"]!["requests"]!.GetValue<long>());
        var daily = await gw.Host.Db.Tokens.DailyAsync(TokenIds.Default, "2000-01-01");
        Assert.Equal(2, Assert.Single(daily).Requests);

        // Raising the limit lets requests through again.
        await PatchAsync(gw, new { limits = new { budgets = new[] { new { metric = "requests", period = "day", limit = 5 } } } });
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
    }

    [Fact]
    public async Task A_Used_Up_Lifetime_Budget_Answers_403()
    {
        await using var gw = await StartAsync();
        await PatchAsync(gw, new { limits = new { budgets = new[] { new { metric = "tokens", period = "total", limit = 10 } } } });
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode); // 15 tokens
        var refused = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(refused.Headers.RetryAfter);
        Assert.Contains("总 token 额度已用完", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_Rpm_Window_Refuses_Bursts()
    {
        await using var gw = await StartAsync();
        await PatchAsync(gw, new { limits = new { budgets = Array.Empty<object>(), rpm = 1 } });
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        var refused = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Allowed_Clients_Restrict_The_Key_Suffix()
    {
        await using var gw = await StartAsync();
        await PatchAsync(gw, new { allowedClients = new[] { ClientKinds.ClaudeCode } });
        var refused = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("不允许客户端", await refused.Content.ReadAsStringAsync());
        // Restrictions apply to the auxiliary endpoints too (they route through the same check).
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
            Assert.Equal(HttpStatusCode.Forbidden, (await gw.Host.Client.SendAsync(request)).StatusCode);
        }

        await PatchAsync(gw, new { allowedClients = new[] { ClientKinds.OpenCode } });
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        await PatchAsync(gw, new Dictionary<string, object?> { ["allowedClients"] = null });
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Null(Assert.Single((await gw.Host.GetJsonAsync("/api/tokens")).AsArray())!["allowedClients"]);
    }

    [Fact]
    public async Task Invalid_Limits_And_Clients_Are_Rejected()
    {
        await using var host = await TestHost.StartAsync();
        var url = $"/api/tokens/{TokenIds.Default}";
        foreach (var body in new object[]
                 {
                     new { limits = new { budgets = new[] { new { metric = "money", period = "day", limit = 1 } } } },
                     new { limits = new { budgets = new[] { new { metric = "cost", period = "year", limit = 1 } } } },
                     new { limits = new { budgets = new[] { new { metric = "cost", period = "day", limit = 0 } } } },
                     new { limits = new { budgets = new[] { new { metric = "cost", period = "day", limit = 1 }, new { metric = "cost", period = "day", limit = 2 } } } },
                     new { limits = new { budgets = Array.Empty<object>(), rpm = 0 } },
                     new { allowedClients = new[] { "not-a-client" } },
                     new { allowedClients = "codex" },
                 })
        {
            var (status, _) = await host.SendAsync(HttpMethod.Patch, url, body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }

        // A cost limit is taken in USD and stored in nanodollars; an empty value means unlimited.
        var (ok, token) = await host.SendAsync(HttpMethod.Patch, url, new { limits = new { budgets = new[] { new { metric = "cost", period = "month", limit = 12.5m } } } });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(12.5m, token!["limits"]!["budgets"]![0]!["limit"]!.GetValue<decimal>());
        Assert.Equal(12_500_000_000, (await host.Db.Tokens.GetAsync(TokenIds.Default))!.Limits()!.Budgets[0].Limit);
        (ok, token) = await host.SendAsync(HttpMethod.Patch, url, new { limits = new { budgets = Array.Empty<object>() } });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Null(token!["limits"]);
        Assert.Null((await host.Db.Tokens.GetAsync(TokenIds.Default))!.LimitsJson);
    }

    [Fact]
    public async Task The_Key_View_Shows_Its_Own_Usage_And_Nothing_Upstream()
    {
        await using var gw = await StartAsync();
        // Written straight to the database (past the usage writer), so before anything loads the token's ledger.
        await gw.Host.Db.Requests.InsertBatchAsync([new RequestRecord
        {
            Id = Ulid.NewUlid(), StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5), Status = RequestStatus.Success,
            TokenId = TokenIds.Default, ProviderId = gw.Provider.Id, ProviderName = gw.Provider.Name, CostNanoUsd = 2_500_000_000,
            TotalInputTokens = 100, TotalOutputTokens = 20,
        }]);
        await PatchAsync(gw, new
        {
            allowedClients = new[] { ClientKinds.OpenCode },
            limits = new { budgets = new[] { new { metric = "cost", period = "month", limit = 10m } }, rpm = 30 },
        });

        var info = await GetAsync(gw, "/v1/astra/key", gw.Key);
        Assert.Equal(HttpStatusCode.OK, info.Status);
        var body = info.Body!;
        Assert.Equal(gw.Key[..15], body["keyPrefix"]!.GetValue<string>()[..15]);
        Assert.Equal(ClientKinds.OpenCode, body["allowedClients"]![0]!.GetValue<string>());
        var budget = Assert.Single(body["limits"]!.AsArray())!;
        Assert.Equal("usd", budget["unit"]!.GetValue<string>());
        Assert.Equal(10m, budget["limit"]!.GetValue<decimal>());
        Assert.Equal(2.5m, budget["used"]!.GetValue<decimal>());
        Assert.Equal(7.5m, budget["remaining"]!.GetValue<decimal>());
        Assert.Equal(30, body["rpm"]!["limit"]!.GetValue<int>());
        Assert.Equal(2.5m, body["usage"]!["today"]!["costUsd"]!.GetValue<decimal>());
        Assert.Equal(KeyInfoDays, body["daily"]!.AsArray().Count);
        var raw = body.ToJsonString();
        Assert.DoesNotContain(gw.Provider.Id, raw);
        Assert.DoesNotContain(gw.Provider.Name, raw);
        Assert.DoesNotContain("provider", raw, StringComparison.OrdinalIgnoreCase);

        // The bare token works the same; so does the path without /v1.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(gw, "/astra/key", gw.Token)).Status);

        // Legacy OpenAI balance API: limit and usage of the binding cost budget.
        var subscription = (await GetAsync(gw, "/v1/dashboard/billing/subscription", gw.Key)).Body!;
        Assert.Equal(10m, subscription["hard_limit_usd"]!.GetValue<decimal>());
        var usage = (await GetAsync(gw, "/dashboard/billing/usage?start_date=2020-01-01&end_date=2020-01-02", gw.Key)).Body!;
        Assert.Equal(250m, usage["total_usage"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task The_Key_View_Needs_A_Valid_Key_Even_Without_The_Key_Requirement()
    {
        await using var gw = await StartAsync();
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s => s.RequireGatewayKey = false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/key", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/key", "sk-not-an-astra-key")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/dashboard/billing/subscription", gw.Token[..^4] + "zzzz")).Status);

        // Unlimited: one-api's "no limit" figure and this month's cost.
        var subscription = (await GetAsync(gw, "/v1/dashboard/billing/subscription", gw.Token)).Body!;
        Assert.Equal(100_000_000m, subscription["hard_limit_usd"]!.GetValue<decimal>());
    }

    private const int KeyInfoDays = Astra.Gateway.Http.KeyInfoEndpoints.DailyDays;

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(GatewayFixture gw, string path, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        var response = await gw.Host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }
}
