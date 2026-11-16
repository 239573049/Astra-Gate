using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Named routing groups (<c>group/&lt;slug&gt;</c>, magpie parity) through the real gateway with fake upstreams: failover between members of
/// different protocols, quota-aware choice, conversation affinity, limited tokens, model lists and the admin API.
/// </summary>
public class RoutingGroupGatewayTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-x","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string AnthropicMessage =
        """{"id":"m1","type":"message","role":"assistant","model":"model-b","content":[{"type":"text","text":"from b"}],"stop_reason":"end_turn","usage":{"input_tokens":5,"output_tokens":1}}""";

    private static string Chat(string model, string? cacheKey = null) =>
        new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["prompt_cache_key"] = cacheKey,
        }.ToJsonString();

    private static HttpResponseMessage Error(HttpStatusCode status) =>
        FakeUpstream.Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "down", ["type"] = "server_error" } }.ToJsonString(), status);

    /// <summary>
    /// Three providers: A (the fixture's, upstream.test, OpenAI Chat, "model-a"), B ("Second", second.test, Anthropic, "model-b") — both bound to the
    /// OpenCode client — and C ("Elsewhere", elsewhere.test, OpenAI Chat, "model-c"), bound to nothing.
    /// </summary>
    private static async Task<(GatewayFixture Gw, Provider A, Provider B, Provider C)> StartAsync(Func<FakeUpstream.Seen, HttpResponseMessage> respond)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond);
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        Provider Make(string name, string host, ApiProtocol protocol) => new()
        {
            Id = Ulid.NewUlid(), Name = name, AuthScheme = AuthSchemes.Bearer, ApiKeyEnc = protector.Protect("sk-" + name),
            Endpoints = [new ProviderEndpoint { Protocol = protocol, BaseUrl = protocol == ApiProtocol.Anthropic ? $"https://{host}" : $"https://{host}/v1" }],
            PreferredUpstreamProtocols = [protocol],
        };
        var b = Make("Second", "second.test", ApiProtocol.Anthropic);
        var c = Make("Elsewhere", "elsewhere.test", ApiProtocol.OpenAIChat);
        await gw.Host.Db.Providers.InsertAsync(b);
        await gw.Host.Db.Providers.InsertAsync(c);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "model-a" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = b.Id, ModelId = "model-b" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = c.Id, ModelId = "model-c" });
        await gw.Host.Db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode,
            [new ClientBinding { ProviderId = gw.Provider.Id }, new ClientBinding { ProviderId = b.Id }]);
        return (gw, gw.Provider, b, c);
    }

    private static HttpResponseMessage ByHost(FakeUpstream.Seen seen) => seen.Url.Host switch
    {
        "second.test" => FakeUpstream.Json(AnthropicMessage),
        _ => FakeUpstream.Json(ChatCompletion),
    };

    private static async Task<JsonNode> MakeGroupAsync(GatewayFixture gw, string name, string mode, string[] members, string? stays = null, string? slug = null)
    {
        var body = new JsonObject
        {
            ["name"] = name, ["mode"] = mode, ["stays"] = stays, ["slug"] = slug,
            ["members"] = new JsonArray(members.Select(m =>
            {
                var at = m.IndexOf('/');
                return m.StartsWith("group/", StringComparison.Ordinal)
                    ? (JsonNode)new JsonObject { ["group"] = m["group/".Length..] }
                    : new JsonObject { ["provider"] = m[..at], ["model"] = m[(at + 1)..] };
            }).ToArray()),
        };
        var (status, json) = await gw.Host.SendAsync(HttpMethod.Post, "/api/routing/groups", body);
        Assert.Equal(HttpStatusCode.Created, status);
        return json!;
    }

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture gw, string body, string? key = null) => gw.PostAsync("/v1/chat/completions", body, key);

    private static string[] Hosts(GatewayFixture gw) => gw.Upstream.Requests.Select(r => r.Url.Host).ToArray();

    private static async Task<List<string>> ModelIdsAsync(GatewayFixture gw, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key ?? gw.Key}");
        var list = JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!["data"]!.AsArray();
        return list.Select(m => m!["id"]!.GetValue<string>()).ToList();
    }

    [Fact]
    public async Task An_Order_Group_Fails_Over_To_A_Member_Speaking_Another_Protocol_And_The_Log_Says_So()
    {
        var (gw, a, b, _) = await StartAsync(seen => seen.Url.Host == "upstream.test" ? Error(HttpStatusCode.ServiceUnavailable) : ByHost(seen));
        await using var _gw = gw;
        // Providers are named by name or alias on the way in and stored as ids.
        var group = await MakeGroupAsync(gw, "Opus anywhere", "order", [$"{a.Name}/model-a", "Second/model-b"]);
        Assert.Equal("group/opus-anywhere", group["modelId"]!.GetValue<string>());
        Assert.Equal(a.Id, group["members"]![0]!["provider"]!.GetValue<string>());
        Assert.Equal(b.Id, group["members"]![1]!["provider"]!.GetValue<string>());

        var response = await PostAsync(gw, Chat("group/opus-anywhere"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
        // Each member gets its own model and protocol; the request body's model is never rewritten for the group.
        Assert.Equal("model-a", gw.Upstream.Requests[0].Json["model"]!.GetValue<string>());
        Assert.Equal("/v1/messages", gw.Upstream.Requests[1].Url.AbsolutePath);
        Assert.Equal("model-b", gw.Upstream.Requests[1].Json["model"]!.GetValue<string>());
        Assert.Equal("from b", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!.GetValue<string>());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal((b.Id, "group/opus-anywhere", "model-b", RequestStatus.Success), (record.ProviderId, record.RequestedModel, record.UpstreamModel, record.Status));
        var routing = JsonNode.Parse(record.RoutingJson!)!;
        Assert.Equal(("group/opus-anywhere", "model-b", "group", "order", "failover"),
            (routing["grade"]!.GetValue<string>(), routing["model"]!.GetValue<string>(), routing["source"]!.GetValue<string>(),
                routing["mode"]!.GetValue<string>(), routing["reason"]!.GetValue<string>()));

        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        var resolve = JsonNode.Parse(spans.First(s => s.Name == TraceSpanNames.Resolve).AttributesJson!)!;
        Assert.Equal(("opus-anywhere", "order", 2), (resolve["astra.group.id"]!.GetValue<string>(), resolve["astra.group.mode"]!.GetValue<string>(), resolve["astra.group.candidates"]!.GetValue<int>()));
        var attempts = spans.Where(s => s.Name == TraceSpanNames.Attempt).Select(s => JsonNode.Parse(s.AttributesJson!)!).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("provider_failover", attempts[0]["astra.attempt.retry"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Request_That_Is_Not_Retryable_Is_Not_Failed_Over()
    {
        var (gw, a, _, _) = await StartAsync(seen => Error(HttpStatusCode.BadRequest));
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Pair", "order", [$"{a.Id}/model-a", "Second/model-b"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(gw, Chat("group/pair"))).StatusCode);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_Smart_Group_Sends_The_Request_To_The_Member_Whose_Allowance_Renews_Soonest()
    {
        var (gw, a, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        var now = DateTimeOffset.UtcNow;
        JsonObject Plan(double used, double hours) => new()
        {
            ["fetchedAtUtc"] = now.AddMinutes(-2).ToString("O"), ["isValid"] = true,
            ["plans"] = new JsonArray(new JsonObject { ["usedPercent"] = used, ["resetsAtUtc"] = now.AddHours(hours).ToString("O") }),
        };
        await gw.Host.Db.Providers.UpdateQuotaAsync(a.Id, Plan(20, 100), now);
        await gw.Host.Db.Providers.UpdateQuotaAsync(b.Id, Plan(60, 5), now);
        await MakeGroupAsync(gw, "Smart pair", "smart", [$"{a.Id}/model-a", $"{b.Id}/model-b"]);

        var response = await PostAsync(gw, Chat("group/smart-pair"));

        Assert.Equal(["second.test"], Hosts(gw));
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(b.Id, record.ProviderId);
        Assert.Equal("soonest_reset", JsonNode.Parse(record.RoutingJson!)!["reason"]!.GetValue<string>());

        // Past 90% the same member goes behind the other: quota decides, not the order.
        await gw.Host.Db.Providers.UpdateQuotaAsync(b.Id, Plan(95, 5), now);
        gw.Upstream.Requests.Clear();
        await PostAsync(gw, Chat("group/smart-pair"));
        Assert.Equal(["upstream.test"], Hosts(gw));
    }

    [Fact]
    public async Task Missing_Quota_Falls_Back_To_The_Given_Order_Not_To_An_Error()
    {
        var (gw, a, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        await MakeGroupAsync(gw, "No data", "pace", [$"{a.Id}/model-a", $"{b.Id}/model-b"]);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat("group/no-data"))).StatusCode);
        Assert.Equal(["upstream.test"], Hosts(gw));
    }

    [Fact]
    public async Task A_Rotating_Group_Keeps_A_Session_On_Its_Member_And_Gives_The_Next_Conversation_The_Next_One()
    {
        var (gw, a, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Spin", "rotate", [$"{a.Id}/model-a", $"{b.Id}/model-b"], stays: "session");

        await PostAsync(gw, Chat("group/spin", "conv-1"));
        await PostAsync(gw, Chat("group/spin", "conv-1"));
        await PostAsync(gw, Chat("group/spin", "conv-2"));
        await PostAsync(gw, Chat("group/spin", "conv-2"));

        Assert.Equal(["upstream.test", "upstream.test", "second.test", "second.test"], Hosts(gw));
    }

    [Fact]
    public async Task A_Weighted_Group_Shares_The_Requests_By_Weight()
    {
        var (gw, a, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        var body = new JsonObject
        {
            ["name"] = "Shared", ["mode"] = "weight", ["stays"] = "off",
            ["members"] = new JsonArray(new JsonObject { ["provider"] = a.Id, ["model"] = "model-a", ["weight"] = 3 }, new JsonObject { ["provider"] = b.Id, ["model"] = "model-b", ["weight"] = 1 }),
        };
        Assert.Equal(HttpStatusCode.Created, (await gw.Host.SendAsync(HttpMethod.Post, "/api/routing/groups", body)).Status);
        for (var i = 0; i < 8; i++) await PostAsync(gw, Chat("group/shared"));
        Assert.Equal(6, Hosts(gw).Count(h => h == "upstream.test"));
        Assert.Equal(2, Hosts(gw).Count(h => h == "second.test"));
    }

    [Fact]
    public async Task A_Group_Can_Hold_Another_Group_And_Fails_Over_Through_It()
    {
        var (gw, a, b, c) = await StartAsync(seen => seen.Url.Host is "upstream.test" or "second.test" ? Error(HttpStatusCode.BadGateway) : ByHost(seen));
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Inner", "order", [$"{a.Id}/model-a", $"{b.Id}/model-b"]);
        await MakeGroupAsync(gw, "Outer", "order", ["group/inner", $"{c.Id}/model-c"]);

        var response = await PostAsync(gw, Chat("group/outer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test", "elsewhere.test"], Hosts(gw));
        Assert.Equal(c.Id, (await gw.RecordOfAsync(response)).ProviderId);
    }

    [Fact]
    public async Task A_Token_Limited_To_Some_Clients_Skips_Members_It_Cannot_Reach()
    {
        var (gw, a, _, c) = await StartAsync(ByHost);
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Mixed", "order", [$"{c.Id}/model-c", $"{a.Id}/model-a"]);
        await MakeGroupAsync(gw, "Only elsewhere", "order", [$"{c.Id}/model-c"]);

        // A token open to any client reaches every provider: the first member answers.
        await PostAsync(gw, Chat("group/mixed"));
        Assert.Equal(["elsewhere.test"], Hosts(gw));

        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.AllowedClientsJson = $"[\"{ClientKinds.OpenCode}\"]";
        await gw.Host.Db.Tokens.UpdateAsync(token);
        gw.Upstream.Requests.Clear();

        // Limited, it keeps to the client's own providers: the unbound member is skipped, the next one serves.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(gw, Chat("group/mixed"))).StatusCode);
        Assert.Equal(["upstream.test"], Hosts(gw));
        // Nothing left for it: the usual refusal, and nothing leaves the machine.
        gw.Upstream.Requests.Clear();
        var refused = await PostAsync(gw, Chat("group/only-elsewhere"));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
        // The refusal names neither a provider nor a member.
        Assert.DoesNotContain("Elsewhere", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Offered_In_Every_Model_List_The_Caller_Reaches_By_Name_Only()
    {
        var (gw, a, _, c) = await StartAsync(ByHost);
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Visible", "smart", [$"{a.Id}/model-a", $"{c.Id}/model-c"]);
        await MakeGroupAsync(gw, "Hidden", "smart", [$"{a.Id}/model-a"]);
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/routing/groups/{(await gw.Host.GetJsonAsync("/api/routing/groups")).AsArray().First(g => g!["slug"]!.GetValue<string>() == "hidden")!["id"]!.GetValue<string>()}", new { listed = false });
        Assert.Equal(HttpStatusCode.OK, status);

        var ids = await ModelIdsAsync(gw);
        Assert.Contains("group/visible", ids);
        Assert.DoesNotContain("group/hidden", ids);

        // The self-service connect context: the group's id and name, never a member's provider.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/astra/connect-context");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var raw = await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync();
        var entry = JsonNode.Parse(raw)!["models"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == "group/visible")!;
        Assert.Equal("Visible", entry["name"]!.GetValue<string>());
        Assert.Null(entry["provider"]);
        Assert.DoesNotContain("Elsewhere", raw);
        Assert.DoesNotContain("model-c", raw.Replace("\"id\":\"model-c\"", ""));

        // A token limited to some clients is not offered a group none of whose providers it reaches.
        await MakeGroupAsync(gw, "Far", "order", [$"{c.Id}/model-c"]);
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.AllowedClientsJson = $"[\"{ClientKinds.OpenCode}\"]";
        await gw.Host.Db.Tokens.UpdateAsync(token);
        ids = await ModelIdsAsync(gw);
        Assert.Contains("group/visible", ids);
        Assert.DoesNotContain("group/far", ids);
    }

    [Fact]
    public async Task A_Disabled_Group_Is_Refused_And_Not_Listed()
    {
        var (gw, a, _, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        var group = await MakeGroupAsync(gw, "Off", "order", [$"{a.Id}/model-a"]);
        Assert.Equal(HttpStatusCode.OK, (await gw.Host.SendAsync(HttpMethod.Patch, $"/api/routing/groups/{group["id"]!.GetValue<string>()}", new { enabled = false })).Status);
        Assert.DoesNotContain("group/off", await ModelIdsAsync(gw));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(gw, Chat("group/off"))).StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Count_Tokens_Takes_The_Groups_First_Choice()
    {
        var (gw, _, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        await MakeGroupAsync(gw, "Count", "order", [$"{b.Id}/model-b"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens")
        {
            Content = new StringContent(new JsonObject { ["model"] = "group/count", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        request.Headers.TryAddWithoutValidation("x-api-key", gw.Key);
        var response = await gw.Host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The first choice is an Anthropic provider: the request was forwarded there with its own model.
        var seen = gw.Upstream.Requests.Single();
        Assert.Equal("second.test", seen.Url.Host);
        Assert.Equal("model-b", seen.Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_Admin_Api_Validates_Nesting_And_Names_And_Reports_Live_Status()
    {
        var (gw, a, b, _) = await StartAsync(ByHost);
        await using var _gw = gw;
        var inner = await MakeGroupAsync(gw, "Inner", "order", [$"{a.Id}/model-a"]);
        await MakeGroupAsync(gw, "Outer", "order", ["group/inner", $"{b.Id}/model-b"]);

        async Task<(HttpStatusCode, string[])> Codes(HttpMethod method, string url, object body)
        {
            var (status, json) = await gw.Host.SendAsync(method, url, body);
            return (status, json?["details"]?["problems"]?.AsArray().Select(p => p!["code"]!.GetValue<string>()).ToArray() ?? []);
        }

        // A cycle, a duplicate slug, an unknown provider and a slug change are refused with codes.
        var (cycleStatus, cycle) = await Codes(HttpMethod.Patch, $"/api/routing/groups/{inner["id"]!.GetValue<string>()}", new { members = new[] { new { group = "outer" } } });
        Assert.Equal(HttpStatusCode.BadRequest, cycleStatus);
        Assert.Contains("member_cycle", cycle);
        var (dupStatus, dup) = await Codes(HttpMethod.Post, "/api/routing/groups", new { name = "x", slug = "inner", members = new[] { new { provider = a.Id, model = "m" } } });
        Assert.Equal((HttpStatusCode.Conflict, true), (dupStatus, dup.Contains("slug_duplicate")));
        Assert.Contains("member_provider_missing", (await Codes(HttpMethod.Post, "/api/routing/groups", new { name = "y", members = new[] { new { provider = "nobody", model = "m" } } })).Item2);
        Assert.Contains("slug_invalid", (await Codes(HttpMethod.Patch, $"/api/routing/groups/{inner["id"]!.GetValue<string>()}", new { slug = "renamed" })).Item2);
        Assert.Contains("members_empty", (await Codes(HttpMethod.Post, "/api/routing/groups", new { name = "z", members = Array.Empty<object>() })).Item2);

        // A group another group holds cannot be deleted.
        var (inUse, _) = await gw.Host.SendAsync(HttpMethod.Delete, $"/api/routing/groups/{inner["id"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.Conflict, inUse);

        // Live status: every member with its health, and the last decision once a request went through.
        await PostAsync(gw, Chat("group/outer"));
        var outer = (await gw.Host.GetJsonAsync("/api/routing/groups")).AsArray().First(g => g!["slug"]!.GetValue<string>() == "outer")!;
        var status = await gw.Host.GetJsonAsync($"/api/routing/groups/{outer["id"]!.GetValue<string>()}/status");
        var members = status["members"]!.AsArray();
        Assert.Equal(2, members.Count);
        Assert.Equal("group", members[0]!["health"]!.GetValue<string>());
        Assert.Equal(("healthy", "Second"), (members[1]!["health"]!.GetValue<string>(), members[1]!["providerName"]!.GetValue<string>()));
        Assert.Equal("order", status["lastReason"]!.GetValue<string>());
        Assert.Equal(1, (await gw.Host.GetJsonAsync("/api/routing/groups/status")).AsArray().Count(g => g!["slug"]!.GetValue<string>() == "outer"));

        // Deleting the outer group frees the inner one.
        Assert.Equal(HttpStatusCode.NoContent, (await gw.Host.SendAsync(HttpMethod.Delete, $"/api/routing/groups/{outer["id"]!.GetValue<string>()}")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await gw.Host.SendAsync(HttpMethod.Delete, $"/api/routing/groups/{inner["id"]!.GetValue<string>()}")).Status);
    }
}
