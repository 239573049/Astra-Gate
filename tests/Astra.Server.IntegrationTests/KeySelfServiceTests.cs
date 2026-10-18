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

/// <summary>
/// A token holder's own request log and statistics (<c>/v1/astra/key/requests</c>, <c>/v1/astra/key/stats</c>): metadata
/// and the requested model only, never anything that names the upstream (.agents/memory/key-self-service-requested-model.md).
/// </summary>
public class KeySelfServiceTests
{
    private static Task<GatewayFixture> StartAsync() =>
        GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json("{}"));

    private static async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> GetAsync(GatewayFixture gw, string path, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        var response = await gw.Host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null, text);
    }

    /// <summary>Rows of the default token (and one of another token) with every upstream detail filled in.</summary>
    private static async Task SeedAsync(GatewayFixture gw)
    {
        var now = DateTimeOffset.UtcNow;
        RequestRecord Row(int i, string status, int http, string? errorType = null, string token = TokenIds.Default, string model = "gpt-x") => new()
        {
            Id = $"01KREQ{i:D20}",
            StartedAtUtc = now.AddSeconds(-60 + i),
            TokenId = token,
            ClientKind = i % 2 == 0 ? ClientKinds.OpenCode : null,
            ProviderId = gw.Provider.Id,
            ProviderName = gw.Provider.Name,
            AccountId = "acct-secret",
            RequestedModel = model,
            UpstreamModel = "upstream-only-model",
            SystemModelId = "catalog-only-model",
            Status = status,
            HttpStatus = http,
            ErrorType = errorType,
            ErrorMessage = errorType is null ? null : $"提供商 {gw.Provider.Name} 返回了错误",
            ClientIp = "203.0.113.7",
            UserAgent = "secret-agent/1.0",
            TotalInputTokens = 100,
            TotalOutputTokens = 10,
            CostNanoUsd = 1_000_000_000,
            TtftMs = 300,
            TotalMs = 900,
            SessionKey = "cc:session-secret",
            TraceId = "0af7651916cd43dd8448eb211c80319c",
        };
        await gw.Host.Db.Requests.InsertBatchAsync([
            Row(1, RequestStatus.Success, 200),
            Row(2, RequestStatus.UpstreamError, 429, "rate_limit_error"),
            Row(3, RequestStatus.GatewayError, 429, TokenLimits.RefusedErrorType),
            Row(4, RequestStatus.UpstreamError, 502, "upstream_unreachable", model: "claude-x"),
            Row(5, RequestStatus.Success, 200),
            Row(6, RequestStatus.Success, 200, token: "other-token"),
        ]);
    }

    private static void AssertNothingUpstream(GatewayFixture gw, string raw)
    {
        foreach (var secret in new[]
                 {
                     gw.Provider.Id, gw.Provider.Name, "acct-secret", "upstream-only-model", "catalog-only-model", "返回了错误",
                     "203.0.113.7", "secret-agent", "session-secret", "0af7651916cd43dd8448eb211c80319c", "rate_limit_error",
                 })
            Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("provider", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_Context_Lists_The_Models_Of_The_Key_And_Names_No_Upstream()
    {
        await using var gw = await StartAsync();
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.ProviderId = gw.Provider.Id;
        await gw.Host.Db.Tokens.UpdateAsync(token);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "gpt-x" });

        // The bare token (a company-issued key) and its client-suffixed form both get the list.
        foreach (var key in new[] { gw.Token, gw.Key })
        {
            var (status, body, raw) = await GetAsync(gw, "/v1/astra/connect-context", key);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(token.Name, body!["name"]!.GetValue<string>());
            var model = Assert.Single(body["models"]!.AsArray());
            Assert.Equal("gpt-x", model!["id"]!.GetValue<string>());
            Assert.Contains("openai-chat", raw);
            foreach (var secret in new[] { gw.Provider.Id, gw.Provider.Name, "sk-upstream-secret", "upstream.test" })
                Assert.DoesNotContain(secret, raw);
            Assert.DoesNotContain("provider", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Connect_Context_Needs_A_Valid_Key_Even_Without_The_Key_Requirement()
    {
        await using var gw = await StartAsync();
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s => s.RequireGatewayKey = false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/connect-context", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/connect-context", "sk-not-an-astra-key")).Status);
    }

    [Fact]
    public async Task Connect_Context_Of_A_Key_Without_A_Provider_Explains_Why()
    {
        await using var gw = await StartAsync();
        var (status, body, _) = await GetAsync(gw, "/v1/astra/connect-context", gw.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("api_error", body!["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_Request_Log_Shows_Metadata_Only_And_Pages_Newest_First()
    {
        await using var gw = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        await gw.Host.Db.Tokens.InsertAsync(new TokenRecord { Id = "other-token", Name = "Other", CreatedAt = now, UpdatedAt = now });
        await SeedAsync(gw);

        var (status, body, raw) = await GetAsync(gw, "/v1/astra/key/requests?limit=2", gw.Key);
        Assert.Equal(HttpStatusCode.OK, status);
        AssertNothingUpstream(gw, raw);
        var items = body!["items"]!.AsArray();
        Assert.Equal(["01KREQ00000000000000000005", "01KREQ00000000000000000004"], items.Select(i => i!["id"]!.GetValue<string>()));
        var failed = items[1]!;
        Assert.Equal("claude-x", failed["model"]!.GetValue<string>());
        Assert.Equal(RequestErrorCategories.Unavailable, failed["errorCategory"]!.GetValue<string>());
        Assert.Equal(1m, failed["costUsd"]!.GetValue<decimal>());
        Assert.Null(items[0]!["errorCategory"]);

        // The next page continues after the last id; the other token's row never shows.
        var next = body["nextBefore"]!.GetValue<string>();
        (_, body, raw) = await GetAsync(gw, $"/astra/key/requests?limit=10&before={next}", gw.Token);
        AssertNothingUpstream(gw, raw);
        Assert.Equal(["01KREQ00000000000000000003", "01KREQ00000000000000000002", "01KREQ00000000000000000001"],
            body!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
        Assert.Null(body["nextBefore"]);
        Assert.Equal(["limit", "rate_limited", null], body["items"]!.AsArray().Select(i => i!["errorCategory"]?.GetValue<string>()));

        // Filters: status list and exact model.
        (_, body, _) = await GetAsync(gw, "/v1/astra/key/requests?status=upstream_error,gateway_error&model=gpt-x", gw.Key);
        Assert.Equal(["01KREQ00000000000000000003", "01KREQ00000000000000000002"], body!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Statistics_Group_By_The_Requested_Model_And_Error_Category()
    {
        await using var gw = await StartAsync();
        var now = DateTimeOffset.UtcNow;
        await gw.Host.Db.Tokens.InsertAsync(new TokenRecord { Id = "other-token", Name = "Other", CreatedAt = now, UpdatedAt = now });
        await SeedAsync(gw);

        var (status, body, raw) = await GetAsync(gw, "/v1/astra/key/stats?range=7d", gw.Key);
        Assert.Equal(HttpStatusCode.OK, status);
        AssertNothingUpstream(gw, raw);
        Assert.Equal("7d", body!["range"]!.GetValue<string>());
        Assert.Equal(5, body["summary"]!["requests"]!.GetValue<long>());
        Assert.Equal(2, body["summary"]!["successRequests"]!.GetValue<long>());
        Assert.Equal(5m, body["summary"]!["costUsd"]!.GetValue<decimal>());
        Assert.Equal(5, body["series"]!.AsArray().Sum(p => p!["requests"]!.GetValue<long>()));

        var models = body["models"]!.AsArray();
        Assert.Equal(["gpt-x", "claude-x"], models.Select(m => m!["model"]!.GetValue<string>()));
        Assert.Equal(4, models[0]!["requests"]!.GetValue<long>());
        var errors = body["errors"]!.AsArray().ToDictionary(e => e!["category"]!.GetValue<string>(), e => e!["count"]!.GetValue<long>());
        Assert.Equal(new Dictionary<string, long> { ["rate_limited"] = 1, ["limit"] = 1, ["unavailable"] = 1 }, errors);
        Assert.Contains(body["clients"]!.AsArray(), c => c!["client"]?.GetValue<string>() == ClientKinds.OpenCode);
    }

    [Fact]
    public async Task Unknown_Keys_Are_Refused_And_A_Disabled_Token_Still_Sees_Its_Own()
    {
        await using var gw = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/key/requests", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(gw, "/v1/astra/key/stats", "sk-astra-nope")).Status);

        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.Enabled = false;
        await gw.Host.Db.Tokens.UpdateAsync(token);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(gw, "/v1/astra/key/requests", gw.Token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(gw, "/v1/astra/key/stats?range=90d", gw.Token)).Status);
    }
}
