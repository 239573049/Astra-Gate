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

/// <summary>Provider pools of a token's direct calls (GitHub issue #2): routing per strategy and failover between members.</summary>
public class TokenPoolGatewayTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-x","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string AnthropicMessage =
        """{"id":"m1","type":"message","role":"assistant","model":"gpt-x","content":[{"type":"text","text":"from b"}],"stop_reason":"end_turn","usage":{"input_tokens":5,"output_tokens":1}}""";

    private static string Chat(string model = "gpt-x", string? cacheKey = null) =>
        new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["prompt_cache_key"] = cacheKey,
        }.ToJsonString();

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        FakeUpstream.Json(new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "server_error" } }.ToJsonString(), status);

    /// <summary>
    /// Three providers — upstream.test (the fixture's), second.test, third.test — each serving gpt-x, pooled on the
    /// default token in that order. <paramref name="respond"/> sees every upstream request (tell them apart by host).
    /// </summary>
    private static async Task<(GatewayFixture Gw, Provider[] P)> StartAsync(
        Func<FakeUpstream.Seen, HttpResponseMessage> respond, Action<TokenPool>? configure = null,
        ApiProtocol secondProtocol = ApiProtocol.OpenAIChat)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond);
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        Provider Make(string name, string host, ApiProtocol protocol) => new()
        {
            Id = Ulid.NewUlid(),
            Name = name,
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = protector.Protect("sk-" + name),
            Endpoints = [new ProviderEndpoint { Protocol = protocol, BaseUrl = protocol == ApiProtocol.Anthropic ? $"https://{host}" : $"https://{host}/v1" }],
            PreferredUpstreamProtocols = [protocol],
        };
        var second = Make("Second", "second.test", secondProtocol);
        var third = Make("Third", "third.test", ApiProtocol.OpenAIChat);
        await gw.Host.Db.Providers.InsertAsync(second);
        await gw.Host.Db.Providers.InsertAsync(third);
        Provider[] providers = [gw.Provider, second, third];
        foreach (var p in providers)
            await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = p.Id, ModelId = "gpt-x" });
        var pool = new TokenPool { Sticky = TokenPoolSticky.Off, Members = providers.Select(p => new TokenPoolMember { ProviderId = p.Id }).ToList() };
        configure?.Invoke(pool);
        await SetPoolAsync(gw, pool);
        return (gw, providers);
    }

    private static async Task SetPoolAsync(GatewayFixture gw, TokenPool? pool)
    {
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.PoolJson = pool is null ? null : Json.Serialize(pool);
        await gw.Host.Db.Tokens.UpdateAsync(token);
    }

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture gw, string body) => gw.PostAsync("/v1/chat/completions", body, gw.Token);

    private static string[] Hosts(GatewayFixture gw) => gw.Upstream.Requests.Select(r => r.Url.Host).ToArray();

    private static async Task<List<JsonNode>> AttemptsAsync(GatewayFixture gw, RequestRecord record) =>
        (await gw.Host.Db.Traces.ListSpansAsync(record.Id)).Where(s => s.Name == TraceSpanNames.Attempt)
        .Select(s => JsonNode.Parse(s.AttributesJson!)!).ToList();

    [Fact]
    public async Task A_Server_Error_Hands_The_Request_To_The_Next_Member()
    {
        var (gw, p) = await StartAsync(seen => seen.Url.Host == "upstream.test"
            ? Error(HttpStatusCode.ServiceUnavailable, "first is down")
            : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var response = await PostAsync(gw, Chat());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
        Assert.Equal("Bearer sk-Second", gw.Upstream.Requests[1].Headers["authorization"]);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(p[1].Id, record.ProviderId);
        Assert.Equal(200, record.HttpStatus);
        Assert.Equal(RequestStatus.Success, record.Status);

        var attempts = await AttemptsAsync(gw, record);
        Assert.Equal([p[0].Id, p[1].Id], attempts.Select(a => a["astra.provider.id"]!.GetValue<string>()));
        Assert.Equal(GatewayPipeline.ProviderFailoverRetry, attempts[0]["astra.attempt.retry"]!.GetValue<string>());
        Assert.Equal(503, attempts[0]["http.response.status_code"]!.GetValue<long>());
        Assert.Null(attempts[1]["astra.attempt.retry"]);

        // Nothing is left in flight once the answer is out.
        var scheduler = gw.Host.App.Services.GetRequiredService<ProviderPoolScheduler>();
        Assert.All(p, provider => Assert.Equal(0, scheduler.InFlight(provider.Id)));
    }

    [Fact]
    public async Task A_Bad_Request_Is_Not_Failed_Over()
    {
        var (gw, _) = await StartAsync(_ => Error(HttpStatusCode.BadRequest, "bad"));
        await using var _gw = gw;
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_Rate_Limited_Member_Is_Skipped_Until_Its_Reset()
    {
        var (gw, p) = await StartAsync(seen => seen.Url.Host == "upstream.test"
            ? FakeUpstream.Json("""{"error":{"message":"slow down"}}""", HttpStatusCode.TooManyRequests, ("retry-after", "600"))
            : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat())).StatusCode);
        // The second request no longer tries the cooling member.
        Assert.Equal(["upstream.test", "second.test", "second.test"], Hosts(gw));
        var health = gw.Host.App.Services.GetRequiredService<ProviderHealth>();
        Assert.Equal(ProviderHealthStates.Cooling, health.Status(p[0].Id).State);
    }

    [Fact]
    public async Task When_Every_Member_Fails_The_Last_Error_Is_The_Answer()
    {
        var (gw, p) = await StartAsync(seen => Error(HttpStatusCode.BadGateway, seen.Url.Host + " failed"));
        await using var _ = gw;

        var response = await PostAsync(gw, Chat());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("third.test failed", text);
        Assert.DoesNotContain("upstream.test", text);
        Assert.DoesNotContain("second.test", text);
        Assert.Equal(["upstream.test", "second.test", "third.test"], Hosts(gw));
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(p[2].Id, record.ProviderId);
        Assert.Equal(502, record.HttpStatus);
    }

    [Fact]
    public async Task Max_Attempts_Limits_The_Members_Tried()
    {
        var (gw, _) = await StartAsync(seen => Error(HttpStatusCode.InternalServerError, "down"), pool => pool.MaxAttempts = 2);
        await using var _gw = gw;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
    }

    [Fact]
    public async Task Failover_Off_Sends_Once()
    {
        var (gw, _) = await StartAsync(seen => Error(HttpStatusCode.InternalServerError, "down"), pool => pool.Failover = false);
        await using var _gw = gw;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_Connection_Failure_Fails_Over_And_The_Last_One_Is_A_502()
    {
        var (gw, _) = await StartAsync(seen => seen.Url.Host == "upstream.test"
            ? throw new HttpRequestException("connection refused")
            : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        var response = await PostAsync(gw, Chat());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await AttemptsAsync(gw, await gw.RecordOfAsync(response));
        Assert.Equal(GatewayPipeline.ProviderFailoverRetry, attempts[0]["astra.attempt.retry"]!.GetValue<string>());

        await using var down = (await StartAsync(_ => throw new HttpRequestException("connection refused"))).Gw;
        var failed = await PostAsync(down, Chat());
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(3, down.Upstream.Requests.Count);
        Assert.Equal("upstream_unreachable", (await down.RecordOfAsync(failed)).ErrorType);
    }

    [Fact]
    public async Task Round_Robin_And_Weighted_Spread_New_Requests()
    {
        var (gw, _) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion), pool => pool.Strategy = TokenPoolStrategies.RoundRobin);
        await using (gw)
        {
            for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat())).StatusCode);
            Assert.Equal(["upstream.test", "second.test", "third.test", "upstream.test"], Hosts(gw));
        }

        var (weighted, _) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion), pool =>
        {
            pool.Strategy = TokenPoolStrategies.Weighted;
            pool.Members[0].Weight = 3;
            pool.Members.RemoveAt(2);
        });
        await using (weighted)
        {
            for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await PostAsync(weighted, Chat())).StatusCode);
            Assert.Equal(["upstream.test", "upstream.test", "second.test", "upstream.test"], Hosts(weighted));
        }
    }

    [Fact]
    public async Task Sticky_Sessions_Keep_A_Conversation_On_One_Member()
    {
        var (gw, _) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion), pool =>
        {
            pool.Strategy = TokenPoolStrategies.RoundRobin;
            pool.Sticky = TokenPoolSticky.Session;
        });
        await using var _ = gw;
        foreach (var key in new[] { "s1", "s2", "s1", "s2", "s3" })
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat(cacheKey: key))).StatusCode);
        Assert.Equal(["upstream.test", "second.test", "upstream.test", "second.test", "third.test"], Hosts(gw));
    }

    [Fact]
    public async Task Only_Members_Serving_The_Model_Are_Candidates()
    {
        var (gw, p) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion), pool => pool.Strategy = TokenPoolStrategies.RoundRobin);
        await using var _ = gw;
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = p[2].Id, ModelId = "only-third" });
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat("only-third"))).StatusCode);
        Assert.Equal(["third.test", "third.test"], Hosts(gw));

        // Nobody serves it: the first member answers for it, once.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat("unknown"))).StatusCode);
        Assert.Equal("upstream.test", Hosts(gw)[^1]);

        // The model list is the union of the members.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Token}");
        var list = JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!["data"]!.AsArray();
        Assert.Equal(["gpt-x", "only-third"], list.Select(m => m!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_Disabled_Member_Is_Skipped_And_An_Empty_Pool_Is_Refused_Without_Names()
    {
        var (gw, p) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        p[0].Enabled = false;
        await gw.Host.Db.Providers.UpdateAsync(p[0]);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Equal("second.test", Hosts(gw)[^1]);

        foreach (var provider in p.Skip(1))
        {
            provider.Enabled = false;
            await gw.Host.Db.Providers.UpdateAsync(provider);
        }
        var refused = await PostAsync(gw, Chat());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Second", text);
        Assert.DoesNotContain("Fake upstream", text);
    }

    [Fact]
    public async Task A_Rewrite_For_One_Member_Never_Reaches_The_Next()
    {
        var (gw, p) = await StartAsync(seen => seen.Url.Host == "upstream.test"
            ? Error(HttpStatusCode.ServiceUnavailable, "down")
            : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        // The first member maps gpt-x onto its own model name.
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = p[0].Id, ModelId = "a-model" });
        p[0].Settings[Provider.ModelMapKey] = new JsonObject { ["gpt-x"] = "a-model" };
        await gw.Host.Db.Providers.UpdateAsync(p[0]);

        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-x","stream":true,"messages":[{"role":"user","content":"hi"}]}""", gw.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a-model", gw.Upstream.Requests[0].Json["model"]!.GetValue<string>());
        Assert.Equal("gpt-x", gw.Upstream.Requests[1].Json["model"]!.GetValue<string>());
        Assert.True(gw.Upstream.Requests[1].Json["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_Failover_Can_Translate_For_A_Member_Speaking_Another_Protocol()
    {
        var (gw, p) = await StartAsync(seen => seen.Url.Host switch
        {
            "upstream.test" => Error(HttpStatusCode.ServiceUnavailable, "down"),
            "second.test" => FakeUpstream.Json(AnthropicMessage),
            _ => FakeUpstream.Json(ChatCompletion),
        }, secondProtocol: ApiProtocol.Anthropic);
        await using var _ = gw;

        var response = await PostAsync(gw, Chat());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = gw.Upstream.Requests[1];
        Assert.Equal("/v1/messages", second.Url.AbsolutePath);
        Assert.NotNull(second.Json["max_tokens"]);
        // The client still gets OpenAI Chat.
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("from b", answer["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(p[1].Id, record.ProviderId);
        Assert.False(record.Passthrough);
        Assert.Equal("anthropic", record.UpstreamProtocol);
    }

    [Fact]
    public async Task A_Token_Without_A_Pool_Is_Never_Retried()
    {
        var (gw, p) = await StartAsync(_ => Error(HttpStatusCode.ServiceUnavailable, "down"));
        await using var _ = gw;
        await SetPoolAsync(gw, null);
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.ProviderId = p[0].Id;
        await gw.Host.Db.Tokens.UpdateAsync(token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(gw, Chat())).StatusCode);
        Assert.Single(gw.Upstream.Requests);
        // The failure still counts for the provider's health (pools read it).
        var health = gw.Host.App.Services.GetRequiredService<ProviderHealth>();
        Assert.Equal(1, health.Status(p[0].Id).ConsecutiveFailures);
    }

    [Fact]
    public async Task Client_Calls_Keep_Their_Own_Bindings()
    {
        var (gw, _) = await StartAsync(_ => Error(HttpStatusCode.ServiceUnavailable, "down"));
        await using var _gw = gw;
        // "<token>.<kind>" goes through the client's binding (the fixture's provider), never the token's pool.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await gw.PostAsync("/v1/chat/completions", Chat())).StatusCode);
        Assert.Equal(["upstream.test"], Hosts(gw));
    }
}
