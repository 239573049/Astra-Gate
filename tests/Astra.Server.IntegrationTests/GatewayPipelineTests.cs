using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>An in-process upstream: records every request and answers with a scripted response.</summary>
public sealed class FakeUpstream(Func<FakeUpstream.Seen, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Url, Dictionary<string, string> Headers, string Body)
    {
        public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
    }

    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var seen = new Seen(request.Method, request.RequestUri!, headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        lock (Requests) Requests.Add(seen);
        return respond(seen);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}

/// <summary>
/// A gateway test environment: enabled client using the default token ("&lt;token&gt;.&lt;kind&gt;"), bound to a
/// fake-upstream provider.
/// </summary>
public sealed class GatewayFixture : IAsyncDisposable
{
    private GatewayFixture(TestHost host, FakeUpstream upstream, Provider provider, string token, string key)
    {
        Host = host;
        Upstream = upstream;
        Provider = provider;
        Token = token;
        Key = key;
    }

    public TestHost Host { get; }
    public FakeUpstream Upstream { get; }
    public Provider Provider { get; }

    /// <summary>The bare default token (direct calls).</summary>
    public string Token { get; }

    /// <summary>The client key: <see cref="Token"/> plus the client suffix.</summary>
    public string Key { get; }

    public static async Task<GatewayFixture> StartAsync(
        string clientKind, ApiProtocol upstreamProtocol, Func<FakeUpstream.Seen, HttpResponseMessage> respond,
        string authScheme = AuthSchemes.Bearer, bool routeOAuthThroughUpstream = false)
    {
        var upstream = new FakeUpstream(respond);
        // 模型能力查询和可选的订阅令牌刷新都指向假上游，测试不访问真实提供商。
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder> configure = b =>
        {
            b.Services.AddHttpClient(ProviderProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
            if (routeOAuthThroughUpstream)
                b.Services.AddHttpClient(Astra.Providers.Subscription.OAuthClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => upstream);
        };
        var host = await TestHost.StartAsync(configure);
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => upstream;
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Fake upstream",
            AuthScheme = authScheme,
            ApiKeyEnc = protector.Protect("sk-upstream-secret"),
            Endpoints = [new ProviderEndpoint { Protocol = upstreamProtocol, BaseUrl = BaseUrlFor(upstreamProtocol) }],
            PreferredUpstreamProtocols = [upstreamProtocol],
        };
        await host.Db.Providers.InsertAsync(provider);
        var client = new ClientRecord { Kind = clientKind, Enabled = true, TokenId = TokenIds.Default };
        var token = protector.Unprotect((await host.Db.Tokens.GetAsync(TokenIds.Default))!.KeyEnc!);
        await host.Db.Clients.UpsertAsync(client);
        await host.Db.Clients.SetBindingAsync(new ClientBinding { ClientKind = clientKind, ProviderId = provider.Id });
        return new GatewayFixture(host, upstream, provider, token, GatewayTokens.ForClient(token, clientKind));
    }

    public static string BaseUrlFor(ApiProtocol p) => p switch
    {
        ApiProtocol.Anthropic => "https://upstream.test",
        ApiProtocol.Gemini => "https://upstream.test/v1beta",
        _ => "https://upstream.test/v1",
    };

    public async Task<HttpResponseMessage> PostAsync(string path, string json, string? key = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key ?? Key}");
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await Host.Client.SendAsync(request);
    }

    /// <summary>The persisted request log row of a gateway response (waits for the background writer).</summary>
    public async Task<RequestRecord> RecordOfAsync(HttpResponseMessage response)
    {
        var id = response.Headers.GetValues("x-astra-request-id").Single();
        await Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        return (await Host.Db.Requests.GetAsync(id))!;
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

public class GatewayPipelineTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    [Fact]
    public async Task Missing_Or_Unknown_Keys_Are_Rejected_Before_Anything_Leaves_The_Machine()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
        {
            var response = await gw.Host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var wrong = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""", key: "sk-not-an-astra-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var rotated = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""", key: GatewayTokens.ForClient(gw.Token[..^4] + "zzzz", ClientKinds.OpenCode));
        Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Legacy_Keys_Disabled_Tokens_And_Unknown_Clients_Are_Rejected()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var legacy = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""", key: "astra-opencode-0123456789abcdefghijABCDEFGHIJKL");
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        Assert.Contains("旧版客户端密钥已失效", await legacy.Content.ReadAsStringAsync());

        // A known client kind that is not enabled in Astra.
        var other = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""", key: GatewayTokens.ForClient(gw.Token, ClientKinds.Pi));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.Enabled = false;
        await gw.Host.Db.Tokens.UpdateAsync(token);
        var disabled = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""");
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Contains("已停用", await disabled.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Bare_Tokens_Are_Direct_Calls_To_The_Tokens_Default_Provider()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var body = """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""";
        var unset = await gw.PostAsync("/v1/chat/completions", body, key: gw.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unset.StatusCode);
        Assert.Contains("没有设置默认提供商", await unset.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);

        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.ProviderId = gw.Provider.Id;
        await gw.Host.Db.Tokens.UpdateAsync(token);
        var response = await gw.PostAsync("/v1/chat/completions", body, key: gw.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(gw.Upstream.Requests);
        var record = await gw.RecordOfAsync(response);
        Assert.Null(record.ClientKind);
        Assert.Equal(TokenIds.Default, record.TokenId);
        Assert.Equal(token.Name, record.TokenName);
    }

    [Fact]
    public async Task Disabled_Clients_And_Providers_Get_Clear_Errors()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        gw.Provider.Enabled = false;
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("已停用", await response.Content.ReadAsStringAsync());

        var client = (await gw.Host.Db.Clients.GetAsync(ClientKinds.OpenCode))!;
        client.Enabled = false;
        await gw.Host.Db.Clients.UpsertAsync(client);
        response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[]}""");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Passthrough_Forwards_The_Body_With_The_Provider_Key_And_Records_The_Request()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json(ChatCompletion, HttpStatusCode.OK, ("x-request-id", "req_upstream_1")));
        var body = """{"model":"gpt-5","messages":[{"role":"user","content":"Say OK"}],"seed":7,"x_unknown":{"kept":true}}""";
        var response = await gw.PostAsync("/v1/chat/completions", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ChatCompletion, await response.Content.ReadAsStringAsync());

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/chat/completions", seen.Url.ToString());
        Assert.Equal("Bearer sk-upstream-secret", seen.Headers["authorization"]);
        Assert.DoesNotContain(gw.Key, string.Join(" ", seen.Headers.Values));
        Assert.Equal(body, seen.Body);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.True(record.Passthrough);
        Assert.Equal("openai-chat", record.InboundProtocol);
        Assert.Equal("openai-chat", record.UpstreamProtocol);
        Assert.Equal(ClientKinds.OpenCode, record.ClientKind);
        Assert.Equal(TokenIds.Default, record.TokenId);
        Assert.Equal("gpt-5", record.RequestedModel);
        Assert.Equal("gpt-5", record.ResponseModel);
        Assert.Equal("req_upstream_1", record.UpstreamRequestId);
        Assert.Equal(200, record.HttpStatus);
        Assert.NotNull(record.TtfbMs);
    }

    [Fact]
    public async Task Passthrough_Upstream_Errors_Keep_Status_And_Body()
    {
        const string error = """{"error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit"}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json(error, HttpStatusCode.TooManyRequests));
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.UpstreamError, record.Status);
        Assert.Equal(429, record.HttpStatus);
        Assert.Null(record.ResponseModel);
    }

    [Fact]
    public async Task Chat_Streaming_Injects_Include_Usage_And_Hides_The_Extra_Usage_Chunk()
    {
        const string sse =
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(sse));
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","stream":true,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("\"usage\"", text);
        Assert.EndsWith("data: [DONE]\n\n", text);

        var upstreamBody = Assert.Single(gw.Upstream.Requests).Json;
        Assert.True(upstreamBody["stream_options"]!["include_usage"]!.GetValue<bool>());

        // A client that asked for usage itself gets the chunk.
        var asked = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-5","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Contains("\"usage\"", await asked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anthropic_Passthrough_Uses_The_Messages_Path_And_Forwards_Version_Headers()
    {
        const string message = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":1}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic,
            _ => FakeUpstream.Json(message), authScheme: AuthSchemes.XApiKey);
        var response = await gw.PostAsync("/v1/messages",
            """{"model":"claude-sonnet-4-5","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""",
            null, ("anthropic-version", "2023-06-01"), ("anthropic-beta", "interleaved-thinking-2025-05-14"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/messages", seen.Url.ToString());
        Assert.Equal("sk-upstream-secret", seen.Headers["x-api-key"]);
        Assert.False(seen.Headers.ContainsKey("authorization"));
        Assert.Equal("2023-06-01", seen.Headers["anthropic-version"]);
        Assert.Equal("interleaved-thinking-2025-05-14", seen.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Model_List_Uses_The_Bound_Provider_And_The_Asking_Protocol()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json("{}"));
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "claude-sonnet-4-5" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "disabled-model", Enabled = false });

        using var openai = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        openai.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var list = JsonNode.Parse(await (await gw.Host.Client.SendAsync(openai)).Content.ReadAsStringAsync())!;
        Assert.Equal("list", list["object"]!.GetValue<string>());
        Assert.Equal(["claude-sonnet-4-5"], list["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));

        using var anthropic = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        anthropic.Headers.TryAddWithoutValidation("x-api-key", gw.Key);
        anthropic.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var claude = JsonNode.Parse(await (await gw.Host.Client.SendAsync(anthropic)).Content.ReadAsStringAsync())!;
        Assert.Equal("model", claude["data"]![0]!["type"]!.GetValue<string>());
        Assert.False(claude["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Previous_Response_Id_Cannot_Cross_To_A_Non_Responses_Upstream()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","previous_response_id":"resp_123"}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("previous_response_id", await response.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);
    }

    // ---------- Claude Desktop role mapping (plan §7.5) ----------

    private static async Task SetRoleMapAsync(GatewayFixture gw, string roleMapJson)
    {
        var client = (await gw.Host.Db.Clients.GetAsync(ClientKinds.ClaudeDesktop))!;
        client.ExtraJson = $$"""{"roleMap":{{roleMapJson}}}""";
        await gw.Host.Db.Clients.UpsertAsync(client);
    }

    [Fact]
    public async Task Claude_Desktop_Role_Ids_Are_Mapped_To_Provider_Models()
    {
        const string message = """{"id":"msg_1","type":"message","role":"assistant","model":"glm-4.6","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":1}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeDesktop, ApiProtocol.Anthropic,
            _ => FakeUpstream.Json(message), authScheme: AuthSchemes.XApiKey);
        await SetRoleMapAsync(gw, """{"sonnet":"glm-4.6","haiku":"glm-4.5-air","opus":""}""");

        var response = await gw.PostAsync("/v1/messages",
            """{"model":"claude-sonnet-4-5-20250929","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"metadata":{"user_id":"u"}}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = gw.Upstream.Requests[^1].Json;
        Assert.Equal("glm-4.6", sent["model"]!.GetValue<string>());
        Assert.Equal("u", sent["metadata"]!["user_id"]!.GetValue<string>()); // the rest of the body is untouched
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("claude-sonnet-4-5-20250929", record.RequestedModel);
        Assert.Equal("glm-4.6", record.UpstreamModel);
        Assert.Equal("glm-4.6", record.ResponseModel);

        // Haiku has its own model; an unmapped role (opus) falls back to the first mapped one.
        await gw.PostAsync("/v1/messages", """{"model":"claude-haiku-4-5","max_tokens":8,"messages":[{"role":"user","content":"hi"}]}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal("glm-4.5-air", gw.Upstream.Requests[^1].Json["model"]!.GetValue<string>());
        await gw.PostAsync("/v1/messages", """{"model":"claude-opus-5-5","max_tokens":8,"messages":[{"role":"user","content":"hi"}]}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal("glm-4.6", gw.Upstream.Requests[^1].Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_Desktop_Mapping_Also_Applies_Through_Conversion_And_Answers_With_The_Role_Id()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeDesktop, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await SetRoleMapAsync(gw, """{"sonnet":"gpt-5"}""");
        // Both APIs exist, but the mapped model supports only Chat: select using its id, not the role id.
        gw.Provider.Endpoints.Add(new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = GatewayFixture.BaseUrlFor(ApiProtocol.Anthropic) });
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
        {
            ProviderId = gw.Provider.Id, ModelId = "gpt-5", SystemModelId = "gpt-5", UpstreamProtocols = [ApiProtocol.OpenAIChat],
        });
        var response = await gw.PostAsync("/v1/messages",
            """{"model":"claude-sonnet-5-5","max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""", null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gpt-5", Assert.Single(gw.Upstream.Requests).Json["model"]!.GetValue<string>());
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("claude-sonnet-5-5", answer["model"]!.GetValue<string>());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("gpt-5", record.UpstreamModel);
        Assert.Equal("gpt-5", record.ResponseModel);
        Assert.Equal("openai-chat", record.UpstreamProtocol);
        Assert.False(record.Passthrough);
        Assert.True(record.CostNanoUsd > 0, "billing uses the mapped (priced) model");
    }

    [Fact]
    public async Task Claude_Desktop_Model_List_Shows_Only_Mapped_Role_Ids()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeDesktop, ApiProtocol.Anthropic, _ => FakeUpstream.Json("{}"));
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "glm-4.6" });
        await SetRoleMapAsync(gw, """{"sonnet":"glm-4.6","haiku":"glm-4.5-air"}""");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var list = JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!;
        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-4-5"], list["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Other_Clients_Keep_Model_Names_Untouched()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic,
            _ => FakeUpstream.Json("""{"id":"m","type":"message","role":"assistant","model":"x","content":[],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""),
            authScheme: AuthSchemes.XApiKey);
        var client = (await gw.Host.Db.Clients.GetAsync(ClientKinds.ClaudeCode))!;
        client.ExtraJson = """{"roleMap":{"sonnet":"glm-4.6"}}""";
        await gw.Host.Db.Clients.UpsertAsync(client);
        await gw.PostAsync("/v1/messages", """{"model":"claude-sonnet-4-5","max_tokens":8,"messages":[{"role":"user","content":"hi"}]}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal("claude-sonnet-4-5", Assert.Single(gw.Upstream.Requests).Json["model"]!.GetValue<string>());
    }

    // ---------- raw upstream model identity ----------

    public static IEnumerable<object[]> ReturnedModelCases()
    {
        foreach (var protocol in new[] { ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, ApiProtocol.Anthropic, ApiProtocol.Gemini })
        foreach (var stream in new[] { false, true })
        foreach (var passthrough in new[] { false, true })
            yield return [protocol, stream, passthrough];
    }

    [Theory]
    [MemberData(nameof(ReturnedModelCases))]
    public async Task Returned_Model_Comes_From_The_Raw_Upstream_Without_Request_Fallback(ApiProtocol upstream, bool stream, bool passthrough)
    {
        var inbound = passthrough ? upstream : upstream == ApiProtocol.OpenAIChat ? ApiProtocol.Anthropic : ApiProtocol.OpenAIChat;
        foreach (var reportedModel in new string?[] { "provider-actual-model", null })
        {
            await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, upstream,
                _ => ModelResponse(upstream, stream, reportedModel));
            var (path, body) = ModelRequest(inbound, stream);
            var response = await gw.PostAsync(path, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var record = await gw.RecordOfAsync(response);
            Assert.Equal(RequestStatus.Success, record.Status);
            Assert.Equal(passthrough, record.Passthrough);
            Assert.Equal("requested-alias", record.RequestedModel);
            Assert.Equal("requested-alias", record.UpstreamModel);
            Assert.Equal(reportedModel, record.ResponseModel);

            // A translated response echoes the client alias, but the log must retain the upstream's raw id.
            if (!passthrough && !stream)
                Assert.Equal("requested-alias", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["model"]!.GetValue<string>());
        }
    }

    private static (string Path, string Body) ModelRequest(ApiProtocol protocol, bool stream) => protocol switch
    {
        ApiProtocol.OpenAIChat => ("/v1/chat/completions", $$"""{"model":"requested-alias","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"hi"}]}"""),
        ApiProtocol.OpenAIResponses => ("/v1/responses", $$"""{"model":"requested-alias","stream":{{(stream ? "true" : "false")}},"input":"hi"}"""),
        ApiProtocol.Anthropic => ("/v1/messages", $$"""{"model":"requested-alias","stream":{{(stream ? "true" : "false")}},"max_tokens":8,"messages":[{"role":"user","content":"hi"}]}"""),
        _ => ($"/v1beta/models/requested-alias:{(stream ? "streamGenerateContent" : "generateContent")}", """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}"""),
    };

    private static HttpResponseMessage ModelResponse(ApiProtocol protocol, bool stream, string? reportedModel)
    {
        var body = JsonNode.Parse(protocol switch
        {
            ApiProtocol.OpenAIChat => ChatCompletion,
            ApiProtocol.OpenAIResponses => """{"id":"resp_1","object":"response","status":"completed","model":"upstream","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":2,"output_tokens":1}}""",
            ApiProtocol.Anthropic => """{"id":"msg_1","type":"message","role":"assistant","model":"upstream","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""",
            _ => """{"responseId":"g1","modelVersion":"upstream","candidates":[{"content":{"role":"model","parts":[{"text":"OK"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":1,"totalTokenCount":3}}""",
        })!.AsObject();
        var modelKey = protocol == ApiProtocol.Gemini ? "modelVersion" : "model";
        if (reportedModel is null) body.Remove(modelKey);
        else body[modelKey] = reportedModel;
        if (!stream) return FakeUpstream.Json(body.ToJsonString());

        if (protocol == ApiProtocol.OpenAIChat)
        {
            var choice = body["choices"]![0]!.AsObject();
            choice.Remove("message");
            choice["delta"] = new JsonObject { ["content"] = "OK" };
            return FakeUpstream.Sse($"data: {body.ToJsonString()}\n\ndata: [DONE]\n\n");
        }
        if (protocol == ApiProtocol.OpenAIResponses)
            return FakeUpstream.Sse($"event: response.created\ndata: {{\"type\":\"response.created\",\"response\":{body.ToJsonString()}}}\n\n"
                + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}\n\n"
                + $"event: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{body.ToJsonString()}}}\n\n");
        if (protocol == ApiProtocol.Anthropic)
            return FakeUpstream.Sse($"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{body.ToJsonString()}}}\n\n"
                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n"
                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return FakeUpstream.Sse($"data: {body.ToJsonString()}\n\n");
    }

    [Fact]
    public async Task Chat_Streaming_Records_A_Late_Model_And_Keeps_It_When_Later_Chunks_Omit_It()
    {
        const string sse =
            "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
            "data: {\"id\":\"c1\",\"model\":\"actual-late-model\",\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n" +
            "data: {\"id\":\"c1\",\"model\":\"\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(sse));
        var (path, body) = ModelRequest(ApiProtocol.OpenAIChat, stream: true);
        var response = await gw.PostAsync(path, body);
        Assert.Equal("actual-late-model", (await gw.RecordOfAsync(response)).ResponseModel);
    }

    [Fact]
    public async Task Responses_Streaming_Records_The_Final_Reported_Model()
    {
        const string sse =
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r1\",\"model\":\"early-model\"}}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"model\":\"actual-final-model\",\"status\":\"completed\",\"output\":[]}}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Sse(sse));
        var (path, body) = ModelRequest(ApiProtocol.OpenAIChat, stream: true);
        var response = await gw.PostAsync(path, body);
        var record = await gw.RecordOfAsync(response);
        Assert.False(record.Passthrough);
        Assert.Equal("actual-final-model", record.ResponseModel);
    }

    // ---------- reasoning configuration in the request log ----------

    /// <summary>Pass-through captures exactly what the client sent, on every protocol, verbatim.</summary>
    public static IEnumerable<object?[]> ReasoningPassthroughCases()
    {
        yield return
        [
            ApiProtocol.OpenAIChat,
            """{"model":"requested-alias","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"high"}""",
            "high", null, null,
        ];
        yield return
        [
            ApiProtocol.OpenAIChat,
            """{"model":"requested-alias","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"none"}""",
            "none", null, null,
        ];
        yield return
        [
            ApiProtocol.OpenAIResponses,
            """{"model":"requested-alias","input":"hi","reasoning":{"effort":"xhigh"}}""",
            "xhigh", null, null,
        ];
        yield return
        [
            ApiProtocol.Anthropic,
            """{"model":"requested-alias","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"thinking":{"type":"adaptive"}}""",
            null, "adaptive", null,
        ];
        yield return
        [
            ApiProtocol.Anthropic,
            """{"model":"requested-alias","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"thinking":{"type":"enabled","budget_tokens":4096},"output_config":{"effort":"low"}}""",
            "low", "enabled", 4096L,
        ];
        yield return
        [
            ApiProtocol.Gemini,
            """{"contents":[{"role":"user","parts":[{"text":"hi"}]}],"generationConfig":{"thinkingConfig":{"thinkingBudget":6144}}}""",
            null, null, 6144L,
        ];
        yield return
        [
            ApiProtocol.Gemini,
            """{"contents":[{"role":"user","parts":[{"text":"hi"}]}],"generation_config":{"thinking_config":{"thinking_budget":-1}}}""",
            null, "auto", null,
        ];
        yield return
        [
            ApiProtocol.Gemini,
            """{"contents":[{"role":"user","parts":[{"text":"hi"}]}],"generationConfig":{"thinkingConfig":{"thinkingBudget":0,"thinkingLevel":"high"}}}""",
            "high", "disabled", null,
        ];
    }

    [Theory]
    [MemberData(nameof(ReasoningPassthroughCases))]
    public async Task Reasoning_Config_Is_Captured_Verbatim_On_Passthrough(
        ApiProtocol protocol, string body, string? effort, string? mode, long? budget)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, protocol,
            _ => ModelResponse(protocol, stream: false, "provider-actual-model"));
        var (path, _) = ModelRequest(protocol, stream: false);
        var response = await gw.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await gw.RecordOfAsync(response);
        Assert.True(record.Passthrough);
        Assert.Equal(effort, record.ReasoningEffort);
        Assert.Equal(mode, record.ReasoningMode);
        Assert.Equal(budget, record.ReasoningBudgetTokens);
        // Logging is metadata only: the wire body stays untouched.
        Assert.Equal(body, Assert.Single(gw.Upstream.Requests).Body);
    }

    [Fact]
    public async Task Missing_Or_Mistyped_Reasoning_Config_Stays_Null_And_Never_Throws()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var unset = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, unset.StatusCode);
        var record = await gw.RecordOfAsync(unset);
        Assert.Null(record.ReasoningEffort);
        Assert.Null(record.ReasoningMode);
        Assert.Null(record.ReasoningBudgetTokens);

        // A wrong-typed value is ignored, not guessed and not an error.
        var mistyped = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"reasoning_effort":3}""");
        Assert.Equal(HttpStatusCode.OK, mistyped.StatusCode);
        var mistypedRecord = await gw.RecordOfAsync(mistyped);
        Assert.Null(mistypedRecord.ReasoningEffort);
    }

    [Fact]
    public async Task Conversion_Logs_The_Client_Effort_Never_The_Derived_Upstream_Budget()
    {
        const string message = """{"id":"msg_1","type":"message","role":"assistant","model":"up","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json(message));
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"requested-alias","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"high"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var record = await gw.RecordOfAsync(response);
        Assert.False(record.Passthrough);
        Assert.Equal("high", record.ReasoningEffort);
        Assert.Null(record.ReasoningMode);
        Assert.Null(record.ReasoningBudgetTokens); // the client named no budget; the derived one is never logged as such

        // The wire still carries the derived budget (EffortBudgets default: high → 16384) — capture precedes it.
        var upstream = Assert.Single(gw.Upstream.Requests).Json;
        Assert.Equal(16384, upstream["thinking"]!["budget_tokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task Anthropic_Modes_And_Output_Config_Survive_Conversion_Into_The_Log()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));

        var adaptive = await gw.PostAsync("/v1/messages",
            """{"model":"requested-alias","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, adaptive.StatusCode);
        var adaptiveRecord = await gw.RecordOfAsync(adaptive);
        Assert.Equal("high", adaptiveRecord.ReasoningEffort);
        Assert.Equal("adaptive", adaptiveRecord.ReasoningMode);
        Assert.Null(adaptiveRecord.ReasoningBudgetTokens);

        var disabled = await gw.PostAsync("/v1/messages",
            """{"model":"requested-alias","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"thinking":{"type":"disabled"}}""",
            null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var disabledRecord = await gw.RecordOfAsync(disabled);
        Assert.Equal("disabled", disabledRecord.ReasoningMode);
        Assert.Null(disabledRecord.ReasoningEffort);
        Assert.Null(disabledRecord.ReasoningBudgetTokens);
    }

    [Fact]
    public async Task Gemini_Thinking_Config_Is_Captured_On_The_Conversion_Path_Too()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var response = await gw.PostAsync("/v1beta/models/requested-alias:generateContent",
            """{"contents":[{"role":"user","parts":[{"text":"hi"}]}],"generation_config":{"thinking_config":{"thinking_level":"high","thinking_budget":-1}}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var record = await gw.RecordOfAsync(response);
        Assert.False(record.Passthrough);
        Assert.Equal("high", record.ReasoningEffort);
        Assert.Equal("auto", record.ReasoningMode); // -1 = auto, kept as the mode, not as a negative budget
        Assert.Null(record.ReasoningBudgetTokens);
    }

    [Fact]
    public async Task Upstream_Api_Errors_Still_Keep_The_Captured_Reasoning_Config()
    {
        const string error = """{"error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit"}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json(error, HttpStatusCode.TooManyRequests));
        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"medium"}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.UpstreamError, record.Status);
        Assert.Equal("medium", record.ReasoningEffort);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.5")]
    [InlineData("1e100")]
    [InlineData("9223372036854775808")]
    [InlineData("\"4096\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Invalid_Reasoning_Budgets_Are_Not_Truncated_Or_Guessed(string budgetJson)
    {
        var anthropic = new RequestRecord();
        RequestReasoning.Capture(anthropic, ApiProtocol.Anthropic,
            JsonNode.Parse("{\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":" + budgetJson + "}}")!.AsObject());
        Assert.Null(anthropic.ReasoningBudgetTokens);

        var gemini = new RequestRecord();
        RequestReasoning.Capture(gemini, ApiProtocol.Gemini,
            JsonNode.Parse("{\"generationConfig\":{\"thinkingConfig\":{\"thinkingBudget\":" + budgetJson + "}}}")!.AsObject());
        Assert.Null(gemini.ReasoningBudgetTokens);
        Assert.Null(gemini.ReasoningMode);
    }

    // ---------- privacy guard (plan §6.7) ----------

    private static async Task EnablePrivacyAsync(GatewayFixture gw, string defaultAction)
    {
        var guard = gw.Host.App.Services.GetRequiredService<PrivacyGuardService>();
        await guard.UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = defaultAction });
    }

    [Fact]
    public async Task Privacy_Block_Rejects_The_Request_Before_It_Reaches_The_Upstream()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await EnablePrivacyAsync(gw, PrivacyActions.Block);

        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-5","messages":[{"role":"user","content":"my key is AKIAIOSFODNN7EXAMPLE"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("隐私护栏", await response.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Blocked, record.Status);
        Assert.NotNull(record.PrivacyJson);
        Assert.Contains("aws_key", record.PrivacyJson);
    }

    [Fact]
    public async Task Privacy_Redact_Hides_Secrets_From_The_Upstream_And_Restores_The_Answer()
    {
        Func<FakeUpstream.Seen, HttpResponseMessage> respond = seen =>
        {
            var content = seen.Json["messages"]![0]!["content"]!.GetValue<string>();
            var body = new JsonObject
            {
                ["id"] = "c1", ["object"] = "chat.completion", ["created"] = 1, ["model"] = "gpt-5",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "echo: " + content },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 5, ["completion_tokens"] = 2 },
            };
            return FakeUpstream.Json(body.ToJsonString());
        };
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond);
        await EnablePrivacyAsync(gw, PrivacyActions.Redact);

        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"gpt-5","messages":[{"role":"user","content":"mail a@b.com please"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The upstream saw the placeholder, never the address.
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Contains("[REDACTED:email#1]", seen.Body);
        Assert.DoesNotContain("a@b.com", seen.Body);

        // The client got the original value back — the agent keeps working.
        var answer = await response.Content.ReadAsStringAsync();
        Assert.Contains("echo: mail a@b.com please", answer);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.NotNull(record.PrivacyJson);
    }

    [Fact]
    public async Task Count_Tokens_Also_Goes_Through_The_Privacy_Guard()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json("""{"input_tokens":10}"""));
        await EnablePrivacyAsync(gw, PrivacyActions.Block);

        var response = await gw.PostAsync("/v1/messages/count_tokens",
            """{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":"AKIAIOSFODNN7EXAMPLE"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    // ---------- subscription accounts through the gateway (plan §5.4) ----------

    [Fact]
    public async Task Subscription_Providers_Authenticate_With_The_Account_Token_And_Refresh_On_401()
    {
        const string tokenResponse = """{"access_token":"at-NEW","refresh_token":null,"expires_in":3600}""";
        Func<FakeUpstream.Seen, HttpResponseMessage> respond = seen =>
        {
            if (seen.Url.AbsolutePath.Contains("oauth/token"))
                return FakeUpstream.Json(tokenResponse);
            if (seen.Headers.TryGetValue("authorization", out var auth) && auth == "Bearer at-STALE")
                return FakeUpstream.Json("""{"error":{"message":"expired"}}""", HttpStatusCode.Unauthorized);
            return FakeUpstream.Json(ChatCompletion);
        };
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond,
            authScheme: AuthSchemes.OAuthSubscription, routeOAuthThroughUpstream: true);
        gw.Provider.TemplateId = "claude-subscription"; // catalog defaults: verified PKCE endpoints + client id
        // OpenCode is not Claude Code: lift the Claude subscription's default "Claude Code only" client policy.
        gw.Provider.Settings = JsonNode.Parse("""{"subscription_oauth":{"verified":true,"client_id":"test-client"},"subscription":{"client_policy":"any"}}""")!.AsObject();
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);

        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        await gw.Host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-1",
            ProviderId = gw.Provider.Id,
            DisplayName = "me@example.com",
            AccessTokenEnc = protector.Protect("at-STALE"),
            RefreshTokenEnc = protector.Protect("rt-1"),
            ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            Status = AccountStatus.Active,
        });
        await gw.Host.Db.Clients.SetBindingAsync(new ClientBinding
        { ClientKind = ClientKinds.OpenCode, ProviderId = gw.Provider.Id, AccountId = "acc-1" });

        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {responseText} | upstream: {string.Join(" || ", gw.Upstream.Requests.Select(r => $"{r.Method} {r.Url} auth={r.Headers.GetValueOrDefault("authorization")}"))}");

        // 401 → refresh (token endpoint) → retry with the rotated token.
        Assert.True(gw.Upstream.Requests.Count >= 3);
        var refresh = gw.Upstream.Requests.Single(r => r.Url.AbsolutePath.Contains("oauth/token"));
        // Claude 的刷新是 JSON body（Anthropic 那套端点不是表单）。
        Assert.Contains("\"grant_type\"", refresh.Body);
        Assert.Contains("refresh_token", refresh.Body);
        Assert.Contains("rt-1", refresh.Body);

        var stored = await gw.Host.Db.Accounts.GetAsync("acc-1");
        Assert.Equal("at-NEW", protector.Unprotect(stored!.AccessTokenEnc!));
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Contains("订阅额度", record.BillingDescription); // equivalent-cost labelling (plan §5.4)
    }
}
