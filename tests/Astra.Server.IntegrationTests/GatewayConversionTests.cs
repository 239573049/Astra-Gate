using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>End-to-end protocol conversion through the real codecs against a fake upstream.</summary>
public class GatewayConversionTests
{
    private const string ChatStream =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_A\",\"type\":\"function\",\"function\":{\"name\":\"shell\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"cmd\\\":\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"ls\\\"}\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[],\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":80,\"total_tokens\":1280,\"prompt_tokens_details\":{\"cached_tokens\":1000},\"completion_tokens_details\":{\"reasoning_tokens\":30}}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Codex_Responses_Client_Works_Against_A_Chat_Only_Provider()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(ChatStream));
        var response = await gw.PostAsync("/v1/responses", """
            {"model":"gpt-5","stream":true,"instructions":"You are Codex.","store":false,
             "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"list files"}]}],
             "tools":[{"type":"function","name":"shell","description":"Run a command","parameters":{"type":"object","properties":{"cmd":{"type":"string"}}}}],
             "reasoning":{"effort":"medium"}}
            """);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = SseParser.ParseAll(await response.Content.ReadAsStringAsync());
        var types = events.Select(e => JsonNode.Parse(e.Data)!["type"]!.GetValue<string>()).ToList();
        Assert.Equal("response.created", types[0]);
        Assert.Contains("response.output_text.delta", types);
        Assert.Contains("response.function_call_arguments.delta", types);
        Assert.Equal("response.completed", types[^1]);
        var completed = JsonNode.Parse(events[^1].Data)!["response"]!;
        var call = completed["output"]!.AsArray().Single(o => o!["type"]!.GetValue<string>() == "function_call")!;
        Assert.Equal("call_A", call["call_id"]!.GetValue<string>());
        Assert.Equal("{\"cmd\":\"ls\"}", call["arguments"]!.GetValue<string>());
        Assert.Equal(1200, completed["usage"]!["input_tokens"]!.GetValue<long>());
        Assert.Equal(1000, completed["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<long>());

        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/chat/completions", upstream.Url.ToString());
        var body = upstream.Json;
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("shell", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.False(record.Passthrough);
        Assert.Equal("openai-responses", record.InboundProtocol);
        Assert.Equal("openai-chat", record.UpstreamProtocol);
        Assert.Equal("reported", record.UsageSource);
        Assert.Equal(1200, record.TotalInputTokens);
        Assert.Equal(1000, record.CacheReadTokens);
        // Nothing in the priority list is left unusable by a missing address
        Assert.Empty(GatewayRouter.PreferredProtocolsWithoutEndpoint(gw.Provider));
        Assert.Equal(30, record.ReasoningTokens);
        Assert.NotNull(record.TtftMs);
        Assert.True(record.CostNanoUsd > 0, "gpt-5 is a priced seed model");
    }

    [Fact]
    public async Task Responses_Is_Forwarded_Verbatim_When_The_Provider_Also_Speaks_Responses()
    {
        const string completion =
            """{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"deepseek-flash","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":12,"output_tokens":3,"total_tokens":15}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(completion));
        // DeepSeek-shaped provider: chat and responses share one base URL and responses is preferred.
        gw.Provider.Endpoints =
        [
            new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = GatewayFixture.BaseUrlFor(ApiProtocol.OpenAIResponses) },
            .. gw.Provider.Endpoints,
        ];
        gw.Provider.PreferredUpstreamProtocols = [ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat];
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);

        var body = """{"model":"deepseek-flash","input":"hi","stream":false,"store":false}""";
        var response = await gw.PostAsync("/v1/responses", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The responses endpoint wins over translating into chat, and the body goes out untouched.
        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/responses", upstream.Url.ToString());
        Assert.Equal(body, upstream.Body);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.True(record.Passthrough);
        Assert.Equal("openai-responses", record.InboundProtocol);
        Assert.Equal("openai-responses", record.UpstreamProtocol);
        Assert.Equal(12, record.TotalInputTokens);
    }

    [Fact]
    public async Task Responses_Is_Preferred_Over_Chat_For_Every_Client_Even_When_Listed_Last()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json("{}"));
        // Responses is configured but ranked last: the inbound protocol still wins over the priority list.
        gw.Provider.Endpoints =
        [
            new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = GatewayFixture.BaseUrlFor(ApiProtocol.OpenAIResponses) },
            .. gw.Provider.Endpoints,
        ];
        gw.Provider.PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses];
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);

        var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","stream":false}""");

        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/responses", upstream.Url.ToString());
        var record = await gw.RecordOfAsync(response);
        Assert.True(record.Passthrough);
    }

    [Theory]
    [InlineData(ApiProtocol.Anthropic, false, true)]
    [InlineData(ApiProtocol.Anthropic, true, true)]
    [InlineData(ApiProtocol.Anthropic, false, false)]
    [InlineData(ApiProtocol.Anthropic, true, false)]
    [InlineData(ApiProtocol.OpenAIChat, false, true)]
    [InlineData(ApiProtocol.OpenAIChat, true, false)]
    public async Task Codex_Responses_Discovers_Copilot_Model_Protocols_Before_Forwarding(
        ApiProtocol protocol, bool stream, bool storedModel)
    {
        const string chatBody =
            """{"id":"c1","object":"chat.completion","model":"claude-sonnet-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK","tool_calls":[{"id":"call_A","type":"function","function":{"name":"shell","arguments":"{\"cmd\":\"ls\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":3}}""";
        const string anthropicBody =
            """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"OK"},{"type":"tool_use","id":"call_A","name":"shell","input":{"cmd":"ls"}}],"stop_reason":"tool_use","usage":{"input_tokens":12,"cache_read_input_tokens":10,"output_tokens":3}}""";
        const string anthropicStream =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":10,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_A\",\"name\":\"shell\",\"input\":{}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"cmd\\\":\\\"ls\\\"}\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":3}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        var modelList = new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject
            {
                ["id"] = "claude-sonnet-5",
                ["supported_endpoints"] = new JsonArray(protocol == ApiProtocol.Anthropic ? "/v1/messages" : "/chat/completions"),
            }),
        }.ToJsonString();
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses,
            seen => seen.Url.AbsolutePath switch
            {
                "/models" => FakeUpstream.Json(modelList),
                "/v1/messages" => stream ? FakeUpstream.Sse(anthropicStream) : FakeUpstream.Json(anthropicBody),
                "/chat/completions" => stream ? FakeUpstream.Sse(ChatStream) : FakeUpstream.Json(chatBody),
                _ => FakeUpstream.Json("""{"error":{"message":"The requested model is not supported.","code":"model_not_supported","param":"model","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest),
            }, authScheme: AuthSchemes.OAuthSubscription);
        gw.Provider.TemplateId = "github-copilot-subscription";
        gw.Provider.Endpoints =
        [
            new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://upstream.test" },
            new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://upstream.test" },
            new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://upstream.test" },
        ];
        gw.Provider.PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, ApiProtocol.Anthropic];
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        if (storedModel)
            await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "claude-sonnet-5" });
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        foreach (var current in new[] { true, false })
            await gw.Host.Db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = current ? "acc-current" : "acc-pinned",
                ProviderId = gw.Provider.Id,
                DisplayName = current ? "Current" : "Pinned",
                IsCurrent = current,
                AccessTokenEnc = protector.Protect(current ? "current-copilot-token" : "pinned-copilot-token"),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                Status = AccountStatus.Active,
            });
        await gw.Host.Db.Clients.SetBindingAsync(new ClientBinding
        {
            ClientKind = ClientKinds.Codex, ProviderId = gw.Provider.Id, AccountId = "acc-pinned",
        });

        // A legacy row and an unlisted model both learn their capabilities without re-login; the second call is cached.
        for (var i = 0; i < 2; i++)
        {
            var response = await gw.PostAsync("/v1/responses", new JsonObject
            {
                ["model"] = "claude-sonnet-5", ["stream"] = stream, ["store"] = false,
                ["instructions"] = "You are Codex.", ["input"] = "list files", ["max_output_tokens"] = 1024,
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function", ["name"] = "shell", ["parameters"] = new JsonObject { ["type"] = "object" },
                }),
            }.ToJsonString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode completed;
            if (stream)
            {
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
                var frames = SseParser.ParseAll(text);
                Assert.Equal("response.created", frames[0].Event);
                Assert.Contains(frames, f => f.Event == "response.function_call_arguments.delta");
                Assert.Equal("response.completed", frames[^1].Event);
                completed = JsonNode.Parse(frames[^1].Data)!["response"]!;
            }
            else
            {
                Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
                completed = JsonNode.Parse(text)!;
            }
            Assert.Equal("completed", completed["status"]!.GetValue<string>());
            Assert.True(completed["usage"]!["input_tokens"]!.GetValue<long>() > 0);
            var call = completed["output"]!.AsArray().Single(o => o!["type"]!.GetValue<string>() == "function_call")!;
            Assert.Equal("call_A", call["call_id"]!.GetValue<string>());
            Assert.Equal("shell", call["name"]!.GetValue<string>());
            Assert.Equal("{\"cmd\":\"ls\"}", call["arguments"]!.GetValue<string>());
            var record = await gw.RecordOfAsync(response);
            Assert.Equal(RequestStatus.Success, record.Status);
            Assert.False(record.Passthrough);
            Assert.Equal("openai-responses", record.InboundProtocol);
            Assert.Equal(protocol.ToId(), record.UpstreamProtocol);
            Assert.Equal("acc-pinned", record.AccountId);
        }

        var discovery = Assert.Single(gw.Upstream.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal("/models", discovery.Url.AbsolutePath);
        Assert.All(gw.Upstream.Requests, r => Assert.Equal("Bearer pinned-copilot-token", r.Headers["authorization"]));
        var generations = gw.Upstream.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, generations.Count);
        Assert.All(generations, r =>
        {
            Assert.Equal(protocol == ApiProtocol.Anthropic ? "/v1/messages" : "/chat/completions", r.Url.AbsolutePath);
            Assert.Equal("claude-sonnet-5", r.Json["model"]!.GetValue<string>());
            Assert.Equal(stream, r.Json["stream"]?.GetValue<bool>() ?? false);
            Assert.NotNull(r.Json["messages"]);
            Assert.Null(r.Json["input"]);
            Assert.NotNull(r.Json["tools"]);
        });
        if (storedModel)
            Assert.Equal([protocol], (await gw.Host.Db.Providers.GetModelAsync(gw.Provider.Id, "claude-sonnet-5"))!.UpstreamProtocols);
    }

    [Fact]
    public async Task Copilot_Models_That_Advertise_Responses_Keep_Responses_Passthrough()
    {
        const string completion = """{"id":"resp_1","object":"response","status":"completed","model":"gpt-5","output":[],"usage":{"input_tokens":12,"output_tokens":3}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses,
            seen => seen.Method == HttpMethod.Get
                ? FakeUpstream.Json("""{"data":[{"id":"gpt-5","supported_endpoints":["/chat/completions","/responses","ws:/responses"]}]}""")
                : FakeUpstream.Json(completion));
        gw.Provider.TemplateId = "github-copilot-subscription";
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        const string body = """{"model":"gpt-5","input":"hi","stream":false,"store":false}""";
        var response = await gw.PostAsync("/v1/responses", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(completion, await response.Content.ReadAsStringAsync());
        var generation = Assert.Single(gw.Upstream.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/v1/responses", generation.Url.AbsolutePath);
        Assert.Equal(body, generation.Body);
        Assert.True((await gw.RecordOfAsync(response)).Passthrough);
    }

    [Fact]
    public async Task Client_Policy_Is_Checked_Before_Copilot_Model_Discovery()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses,
            _ => FakeUpstream.Json("{}"));
        gw.Provider.TemplateId = "github-copilot-subscription";
        gw.Provider.Settings["subscription"] = new JsonObject { ["client_policy"] = "claude-code-only" };
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var response = await gw.PostAsync("/v1/responses", """{"model":"claude-sonnet-5","input":"hi"}""");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Claude_Code_Works_Against_A_Chat_Provider_And_Gets_Anthropic_Events()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(ChatStream));
        var response = await gw.PostAsync("/v1/messages", """
            {"model":"gpt-5","max_tokens":1024,"stream":true,"system":[{"type":"text","text":"You are Claude Code.","cache_control":{"type":"ephemeral"}}],
             "messages":[{"role":"user","content":[{"type":"text","text":"list files"}]}],
             "tools":[{"name":"shell","description":"Run","input_schema":{"type":"object","properties":{"cmd":{"type":"string"}}}}]}
            """, null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = SseParser.ParseAll(await response.Content.ReadAsStringAsync());
        Assert.Equal("message_start", events[0].Event);
        Assert.Equal("message_stop", events[^1].Event);
        var delta = events.Single(e => e.Event == "message_delta");
        Assert.Equal("tool_use", JsonNode.Parse(delta.Data)!["delta"]!["stop_reason"]!.GetValue<string>());
        var toolStart = events.Single(e => e.Event == "content_block_start" && e.Data.Contains("tool_use"));
        Assert.Equal("call_A", JsonNode.Parse(toolStart.Data)!["content_block"]!["id"]!.GetValue<string>());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal(1000, record.CacheReadTokens);
    }

    [Fact]
    public async Task Chat_Client_Works_Against_An_Anthropic_Provider()
    {
        const string anthropicStream =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":20,\"cache_read_input_tokens\":100,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi there\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.Anthropic, _ => FakeUpstream.Sse(anthropicStream),
            authScheme: Astra.Core.Models.AuthSchemes.XApiKey);
        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"claude-sonnet-4-5","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hi there", text);
        Assert.EndsWith("data: [DONE]\n\n", text);

        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/messages", upstream.Url.ToString());
        Assert.Equal("2023-06-01", upstream.Headers["anthropic-version"]);
        var body = upstream.Json;
        Assert.True(body["max_tokens"]!.GetValue<int>() > 0);
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(20 + 100, record.TotalInputTokens);
        Assert.Equal(7, record.TotalOutputTokens);
    }

    [Fact]
    public async Task Gemini_Cli_Works_Against_A_Chat_Provider()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.GeminiCli, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(ChatStream));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1beta/models/gpt-5:streamGenerateContent?alt=sse")
        {
            Content = new StringContent("""{"contents":[{"role":"user","parts":[{"text":"list files"}]}],"tools":[{"functionDeclarations":[{"name":"shell","parameters":{"type":"object"}}]}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", gw.Key);
        var response = await gw.Host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var chunks = SseParser.ParseAll(await response.Content.ReadAsStringAsync()).Select(e => JsonNode.Parse(e.Data)!).ToList();
        var parts = chunks.SelectMany(c => c["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? []).ToList();
        Assert.Contains(parts, p => p!["functionCall"]?["name"]?.GetValue<string>() == "shell");
        Assert.NotNull(chunks[^1]["usageMetadata"]);

        var body = Assert.Single(gw.Upstream.Requests).Json;
        Assert.Equal("gpt-5", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Upstream_Errors_Are_Translated_Into_The_Client_Protocol()
    {
        const string overloaded = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.Anthropic,
            _ => FakeUpstream.Json(overloaded, (HttpStatusCode)529), authScheme: Astra.Core.Models.AuthSchemes.XApiKey);
        var response = await gw.PostAsync("/v1/responses", """{"model":"claude-sonnet-4-5","input":"hi","stream":true}""");
        Assert.Equal(529, (int)response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal("Overloaded", error["message"]!.GetValue<string>());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.UpstreamError, record.Status);
    }

    [Fact]
    public async Task Responses_Passthrough_Records_Usage_From_The_Stream()
    {
        const string stream =
            "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"gpt-5\",\"output\":[]}}\n\n" +
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
            "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\",\"sequence_number\":2,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"}}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":3,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":4,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-5\",\"service_tier\":\"default\",\"output\":[{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}],\"usage\":{\"input_tokens\":500,\"input_tokens_details\":{\"cached_tokens\":400},\"output_tokens\":20,\"output_tokens_details\":{\"reasoning_tokens\":12},\"total_tokens\":520}}}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Sse(stream));
        var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","stream":true,"store":false}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: response.completed", text);

        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/responses", upstream.Url.ToString());
        var record = await gw.RecordOfAsync(response);
        Assert.True(record.Passthrough);
        Assert.Equal(500, record.TotalInputTokens);
        Assert.Equal(400, record.CacheReadTokens);
        Assert.Equal(12, record.ReasoningTokens);
        Assert.Equal("default", record.ServiceTier);
        Assert.NotNull(record.TtftMs);
    }

    // ---------- Gemini :streamGenerateContent without ?alt=sse (JSON array wire format) ----------

    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private static async Task<HttpResponseMessage> PostGeminiAsync(GatewayFixture gw, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", gw.Key);
        return await gw.Host.Client.SendAsync(request);
    }

    private const string GeminiChunkTail =
        """],"usageMetadata":{"promptTokenCount":1200,"candidatesTokenCount":80,"totalTokenCount":1280}""";

    [Fact]
    public async Task Gemini_Stream_Without_Alt_Sse_Answers_With_A_Json_Array()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.GeminiCli, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(ChatStream));
        var response = await PostGeminiAsync(gw, "/v1beta/models/gpt-5:streamGenerateContent",
            """{"contents":[{"role":"user","parts":[{"text":"list files"}]}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        // One JSON array of GenerateContentResponse chunks — not SSE.
        var chunks = Assert.IsType<JsonArray>(JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        Assert.True(chunks.Count > 1);
        var parts = chunks.SelectMany(c => c!["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? []).ToList();
        Assert.Contains(parts, p => p!["functionCall"]?["name"]?.GetValue<string>() == "shell");
        Assert.NotNull(chunks[^1]!["usageMetadata"]);
        Assert.Equal("STOP", chunks[^1]!["candidates"]![0]!["finishReason"]!.GetValue<string>());

        // The upstream was still asked to stream (alt=sse).
        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/chat/completions", upstream.Url.ToString());
        Assert.True(upstream.Json["stream"]!.GetValue<bool>());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(1200, record.TotalInputTokens);
    }

    [Fact]
    public async Task Gemini_Passthrough_Stream_Without_Alt_Sse_Is_Also_An_Array()
    {
        const string sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"OK\"}]},\"index\":0}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[]},\"finishReason\":\"STOP\",\"index\":0}" + GeminiChunkTail + "}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.GeminiCli, ApiProtocol.Gemini, _ => FakeUpstream.Sse(sse));

        var response = await PostGeminiAsync(gw, "/v1beta/models/gemini-2.5:streamGenerateContent",
            """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new Exception($"PARSE FAILED ({e.Message}) BODY WAS: {text}");
        }
        Assert.True(parsed is JsonArray, "BODY WAS: " + text);
        var chunks = (JsonArray)parsed!;
        Assert.Equal(2, chunks.Count); // every upstream chunk becomes one array element
        Assert.Equal("OK", chunks[0]!["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>());

        // The gateway still streams from the upstream with alt=sse.
        var upstream = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1beta/models/gemini-2.5:streamGenerateContent?alt=sse", upstream.Url.ToString());
    }

    [Fact]
    public async Task An_Upstream_Ignoring_The_Stream_Flag_Still_Answers_Conversion_And_Passthrough_Clients()
    {
        // Conversion: Codex → Chat upstream that answers with one JSON body despite stream:true.
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","stream":true,"store":false}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var types = SseParser.ParseAll(await response.Content.ReadAsStringAsync())
            .Select(e => JsonNode.Parse(e.Data)!["type"]!.GetValue<string>()).ToList();
        Assert.Equal("response.created", types[0]);
        Assert.Contains("response.output_text.delta", types);
        Assert.Equal("response.completed", types[^1]);
        Assert.True(gw.Upstream.Requests[^1].Json["stream"]!.GetValue<bool>());

        // Pass-through: the non-SSE body is forwarded instead of producing an empty stream.
        await using var gw2 = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var forwarded = await gw2.PostAsync("/v1/chat/completions", """{"model":"gpt-5","stream":true,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Equal(ChatCompletion, await forwarded.Content.ReadAsStringAsync());
    }
}
