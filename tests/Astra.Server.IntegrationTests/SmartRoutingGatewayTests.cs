using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Core.Tokens;
using Astra.Gateway.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Smart routing through the real pipeline (plan §5): a request for the virtual model reaches the model of the grade the
/// judge chose, on every protocol, while the request log keeps what the client asked for. The judge is a fake service
/// at judge.test and the upstream a fake at upstream.test; both are served by the same stand-in transport.
/// </summary>
public class SmartRoutingGatewayTests
{
    private const string JudgeHost = "judge.test";

    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string GeminiAnswer =
        """{"responseId":"g1","modelVersion":"upstream","candidates":[{"content":{"role":"model","parts":[{"text":"OK"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":1,"totalTokenCount":3}}""";

    /// <summary>What the fake judge answers; change it between requests.</summary>
    private sealed class JudgeScript
    {
        private int _calls;
        public string Choice { get; set; } = "heavy";
        public double Confidence { get; set; } = 0.9;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls => Volatile.Read(ref _calls);

        public HttpResponseMessage Answer()
        {
            Interlocked.Increment(ref _calls);
            if (Status != HttpStatusCode.OK) return FakeUpstream.Json("""{"error":"the judge is down"}""", Status);
            return FakeUpstream.Json(new JsonObject
            {
                ["model"] = "jev-1.13.0",
                ["answers"] = new JsonObject
                {
                    ["grade"] = new JsonObject
                    {
                        ["type"] = "choice", ["choice"] = Choice, ["confidence"] = Confidence,
                        ["probabilities"] = new JsonObject { [Choice] = Confidence },
                    },
                },
                ["usage"] = new JsonObject { ["input_tokens"] = 200, ["output_tokens"] = 10 },
            }.ToJsonString());
        }
    }

    private static async Task<(GatewayFixture Gw, JudgeScript Judge)> StartAsync(
        string clientKind, ApiProtocol upstreamProtocol, Func<FakeUpstream.Seen, HttpResponseMessage> upstream,
        Action<SmartRoutingSettings>? configure = null, bool enabled = true)
    {
        var judge = new JudgeScript();
        var gw = await GatewayFixture.StartAsync(clientKind, upstreamProtocol,
            seen => seen.Url.Host == JudgeHost ? judge.Answer() : upstream(seen));
        foreach (var model in new[] { "small-model", "mid-model", "big-model" })
            await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = model });
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        await gw.Host.App.Services.GetRequiredService<SmartRoutingService>().UpdateAsync(s =>
        {
            s.Enabled = enabled;
            s.Judge.BaseUrl = "https://" + JudgeHost;
            s.Judge.ApiKeyProtected = protector.Protect("sk-judge");
            s.Grades[0].Model = "small-model";
            s.Grades[1].Model = "mid-model";
            s.Grades[2].Model = "big-model";
            configure?.Invoke(s);
        });
        return (gw, judge);
    }

    private static List<FakeUpstream.Seen> Upstream(GatewayFixture gw) => gw.Upstream.Requests.ToList().Where(r => r.Url.Host != JudgeHost).ToList();

    private static List<FakeUpstream.Seen> Judged(GatewayFixture gw) => gw.Upstream.Requests.ToList().Where(r => r.Url.Host == JudgeHost).ToList();

    private static async Task<JsonObject> RouteAttributesAsync(GatewayFixture gw, HttpResponseMessage response)
    {
        var record = await gw.RecordOfAsync(response);
        var span = (await gw.Host.Db.Traces.ListSpansAsync(record.Id)).First(s => s.Name == TraceSpanNames.Resolve);
        return JsonNode.Parse(span.AttributesJson!)!.AsObject();
    }

    private static string Chat(string model, string text, string? session = "conv-1") =>
        new JsonObject
        {
            ["model"] = model,
            ["prompt_cache_key"] = session,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
        }.ToJsonString();

    // ---------------------------------------------------------------- the basic path

    [Fact]
    public async Task The_Virtual_Model_Reaches_The_Chosen_Grades_Model_And_The_Log_Keeps_Both_Names()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Upstream(gw));
        Assert.Equal("big-model", sent.Json["model"]!.GetValue<string>());
        Assert.Equal(0.2, sent.Json["temperature"]!.GetValue<double>()); // nothing but the model was touched
        Assert.Equal("Bearer sk-upstream-secret", sent.Headers["authorization"]);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.True(record.Passthrough);
        Assert.Equal("astra-auto", record.RequestedModel); // what the client asked for
        Assert.Equal("big-model", record.UpstreamModel); // what it got

        // The judge got its own key, the grades and the user's words — and nothing of the upstream's.
        var asked = Assert.Single(Judged(gw));
        Assert.Equal("https://judge.test/v1/systemone", asked.Url.ToString());
        Assert.Equal("Bearer sk-judge", asked.Headers["authorization"]);
        var question = asked.Json["questions"]!["grade"]!;
        Assert.Equal("choice", question["type"]!.GetValue<string>());
        Assert.Equal(["light", "standard", "heavy"], question["criteria"]!.AsObject().Select(p => p.Key));
        Assert.Equal("Redesign the billing module", asked.Json["state"]!["request"]!.GetValue<string>());
        Assert.Equal("jev-latest", asked.Json["model"]!.GetValue<string>());
        Assert.Equal(1, judge.Calls);

        // The decision is on the span of the resolution.
        var span = await RouteAttributesAsync(gw, response);
        Assert.Equal("judge", span["astra.route.source"]!.GetValue<string>());
        Assert.Equal("session", span["astra.route.mode"]!.GetValue<string>());
        Assert.Equal("heavy", span["astra.route.grade"]!.GetValue<string>());
        Assert.Equal("big-model", span["astra.route.model"]!.GetValue<string>());
        Assert.Equal(0.9, span["astra.route.confidence"]!.GetValue<double>());
        Assert.Equal("typesafe", span["astra.route.judge.protocol"]!.GetValue<string>());
        Assert.Equal("jev-1.13.0", span["astra.route.judge.model"]!.GetValue<string>());
        Assert.Equal(200, span["astra.route.judge.input_tokens"]!.GetValue<int>());
        Assert.NotNull(span["astra.route.judge.ms"]);

        // And the request log marks it without opening the trace: judged, which grade and model.
        var routing = (await RoutingInLogAsync(gw, record.Id))!;
        Assert.Equal(("heavy", "big-model", "judge", "session"),
            (routing["grade"]!.GetValue<string>(), routing["model"]!.GetValue<string>(), routing["source"]!.GetValue<string>(), routing["mode"]!.GetValue<string>()));
        Assert.Equal(0.9, routing["confidence"]!.GetValue<double>());
        Assert.NotNull(routing["judgeMs"]);
        Assert.Null(routing["fallbackReason"]);
    }

    /// <summary>The smart-routing decision the request log shows for request <paramref name="id"/> (list and detail agree).</summary>
    private static async Task<JsonNode?> RoutingInLogAsync(GatewayFixture gw, string id)
    {
        var listed = (await gw.Host.GetJsonAsync("/api/requests"))["items"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!["routing"];
        var detail = (await gw.Host.GetJsonAsync($"/api/requests/{id}"))["routing"];
        Assert.Equal(listed?.ToJsonString(), detail?.ToJsonString());
        return listed;
    }

    [Fact]
    public async Task A_Session_Is_Judged_Once_And_Another_Session_On_Its_Own()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var first = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));
        var second = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "ok thanks"));
        judge.Choice = "light";
        var other = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "fix a typo", session: "conv-2"));

        Assert.All(new[] { first, second, other }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(["big-model", "big-model", "small-model"], Upstream(gw).Select(r => r.Json["model"]!.GetValue<string>()));
        Assert.Equal(2, judge.Calls); // the second request of conv-1 did not ask again
        Assert.Equal("pin", (await RouteAttributesAsync(gw, second))["astra.route.source"]!.GetValue<string>());
        Assert.Null((await RouteAttributesAsync(gw, second))["astra.route.judge.protocol"]); // reused: no call of its own
    }

    [Fact]
    public async Task A_Real_Model_Is_Never_Touched_And_The_Judge_Is_Not_Asked()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/chat/completions", Chat("mid-model", "hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mid-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        Assert.Equal(0, judge.Calls);
        Assert.Null((await RouteAttributesAsync(gw, response))["astra.route.source"]);
        Assert.Null(await RoutingInLogAsync(gw, (await gw.RecordOfAsync(response)).Id));
    }

    [Fact]
    public async Task Turn_Mode_Asks_For_Every_New_User_Message()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion),
            s => s.Rules.Mode = RoutingModes.Turn);
        await using var _ = gw;

        await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));
        judge.Choice = "light";
        var second = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "fix a typo"));

        Assert.Equal(["big-model", "small-model"], Upstream(gw).Select(r => r.Json["model"]!.GetValue<string>()));
        Assert.Equal(2, judge.Calls);
        Assert.Equal("turn", (await RouteAttributesAsync(gw, second))["astra.route.mode"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- the judge cannot be trusted

    [Fact]
    public async Task A_Judge_Outage_Never_Fails_The_Request_It_Goes_To_The_Fallback_Grade()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        judge.Status = HttpStatusCode.ServiceUnavailable;

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mid-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        var span = await RouteAttributesAsync(gw, response);
        Assert.Equal("fallback", span["astra.route.source"]!.GetValue<string>());
        Assert.Equal("overloaded", span["astra.route.fallback_reason"]!.GetValue<string>());
        Assert.Equal("standard", span["astra.route.grade"]!.GetValue<string>());
        var routing = (await RoutingInLogAsync(gw, (await gw.RecordOfAsync(response)).Id))!;
        Assert.Equal(("standard", "fallback", "overloaded"),
            (routing["grade"]!.GetValue<string>(), routing["source"]!.GetValue<string>(), routing["fallbackReason"]!.GetValue<string>()));
        // The next request of the session gets another chance: nothing was pinned.
        judge.Status = HttpStatusCode.OK;
        await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));
        Assert.Equal("big-model", Upstream(gw)[^1].Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Judge_That_Is_Not_Confident_Enough_Goes_To_The_Fallback_Grade()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        judge.Confidence = 0.05;

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "hmm"));

        Assert.Equal("mid-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        var span = await RouteAttributesAsync(gw, response);
        Assert.Equal("low_confidence", span["astra.route.fallback_reason"]!.GetValue<string>());
        Assert.Equal(0.05, span["astra.route.confidence"]!.GetValue<double>());
    }

    // ---------------------------------------------------------------- off

    [Fact]
    public async Task While_It_Is_Off_The_Virtual_Model_Is_Refused_Before_Anything_Leaves_The_Machine()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion), enabled: false);
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "hello"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Contains("智能路由未启用", error["message"]!.GetValue<string>());
        Assert.Empty(gw.Upstream.Requests); // no judge, no upstream
        Assert.Equal(0, judge.Calls);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.GatewayError, record.Status);
    }

    // ---------------------------------------------------------------- every protocol

    [Fact]
    public async Task An_Anthropic_Request_Is_Translated_To_The_Chosen_Model_And_Answered_Under_The_Virtual_Name()
    {
        var (gw, judge) = await StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/messages",
            """{"model":"astra-auto","max_tokens":16,"messages":[{"role":"user","content":"Redesign the billing module"}]}""", null,
            ("anthropic-version", "2023-06-01"), ("x-claude-code-session-id", "9f3e0b2c-1111-2222-3333-444455556666"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("big-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        Assert.Equal("astra-auto", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["model"]!.GetValue<string>());
        var record = await gw.RecordOfAsync(response);
        Assert.False(record.Passthrough);
        Assert.Equal(("astra-auto", "big-model"), (record.RequestedModel, record.UpstreamModel));
        Assert.Equal(1, judge.Calls);
    }

    [Fact]
    public async Task A_Gemini_Request_Whose_Model_Is_In_The_Url_Goes_To_The_Chosen_Model()
    {
        var (gw, judge) = await StartAsync(ClientKinds.GeminiCli, ApiProtocol.Gemini, _ => FakeUpstream.Json(GeminiAnswer));
        await using var _ = gw;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1beta/models/astra-auto:generateContent")
        {
            Content = new StringContent("""{"contents":[{"role":"user","parts":[{"text":"Redesign the billing module"}]}]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", gw.Key);
        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://upstream.test/v1beta/models/big-model:generateContent", Assert.Single(Upstream(gw)).Url.ToString());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(("astra-auto", "big-model"), (record.RequestedModel, record.UpstreamModel));
        Assert.Equal(1, judge.Calls);
    }

    [Fact]
    public async Task A_Responses_Request_Is_Routed_Too()
    {
        var responsesAnswer =
            """{"id":"resp_1","object":"response","status":"completed","model":"upstream","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":2,"output_tokens":1}}""";
        var (gw, judge) = await StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json(responsesAnswer));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/responses", """{"model":"astra-auto","input":"Redesign the billing module"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("big-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        Assert.Equal("Redesign the billing module", Assert.Single(Judged(gw)).Json["state"]!["request"]!.GetValue<string>());
        Assert.Equal(1, judge.Calls);
    }

    // ---------------------------------------------------------------- an agent's tool loop

    // Chat stream: a tool call (call_A), finish tool_calls.
    private const string ToolCallStream =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_A\",\"type\":\"function\",\"function\":{\"name\":\"shell\",\"arguments\":\"{\\\"cmd\\\":\\\"ls\\\"}\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":8,\"total_tokens\":128}}\n\n" +
        "data: [DONE]\n\n";

    private const string ToolSchema = """[{"name":"shell","description":"Run","input_schema":{"type":"object","properties":{"cmd":{"type":"string"}}}}]""";

    [Fact]
    public async Task The_Steps_Of_A_Tool_Loop_Stay_On_The_Turns_Model_And_The_Next_User_Message_Is_Judged_Again()
    {
        var (gw, judge) = await StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat,
            seen => seen.Body.Contains("\"tool_call_id\"", StringComparison.Ordinal) ? FakeUpstream.Json(ChatCompletion) : FakeUpstream.Sse(ToolCallStream),
            s => s.Rules.Mode = RoutingModes.Turn);
        await using var _ = gw;
        (string, string)[] headers = [("anthropic-version", "2023-06-01"), ("x-claude-code-session-id", "9f3e0b2c-1111-2222-3333-444455556666")];

        // Turn 1, first step: the model asks for a tool.
        var first = await gw.PostAsync("/v1/messages",
            $$$"""{"model":"astra-auto","max_tokens":512,"stream":true,"system":"You are an agent.","messages":[{"role":"user","content":"fix the login"}],"tools":{{{ToolSchema}}} }""", null, headers);
        await first.Content.ReadAsStringAsync();
        // Turn 1, second step: the tool's result comes back. The judge must not be asked again, and the model must not change.
        judge.Choice = "light";
        var step = await gw.PostAsync("/v1/messages",
            $$$"""
            {"model":"astra-auto","max_tokens":512,"system":"You are an agent.",
             "messages":[{"role":"user","content":"fix the login"},
                         {"role":"assistant","content":[{"type":"tool_use","id":"call_A","name":"shell","input":{"cmd":"ls"}}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_A","content":"login.ts"}]}],
             "tools":{{{ToolSchema}}} }
            """, null, headers);
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        Assert.Equal(1, judge.Calls);
        Assert.Equal("turn", (await RouteAttributesAsync(gw, step))["astra.route.source"]!.GetValue<string>());

        // Turn 2: the user writes again. Now the judge is asked, and says something else.
        var next = await gw.PostAsync("/v1/messages",
            $$$"""
            {"model":"astra-auto","max_tokens":512,"system":"You are an agent.",
             "messages":[{"role":"user","content":"fix the login"},
                         {"role":"assistant","content":[{"type":"tool_use","id":"call_A","name":"shell","input":{"cmd":"ls"}}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_A","content":"login.ts"}]},
                         {"role":"assistant","content":"Fixed."},
                         {"role":"user","content":"now fix a typo in the readme"}],
             "tools":{{{ToolSchema}}} }
            """, null, headers);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);

        Assert.Equal(2, judge.Calls);
        Assert.Equal(["big-model", "big-model", "small-model"], Upstream(gw).Select(r => r.Json["model"]!.GetValue<string>()));
        Assert.Equal("now fix a typo in the readme", Judged(gw)[1].Json["state"]!["request"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- providers

    [Fact]
    public async Task The_Grades_Model_Decides_Which_Of_The_Clients_Providers_Gets_The_Request()
    {
        var (gw, _) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        var second = new Provider
        {
            Id = Ulid.NewUlid(), Name = "Second upstream", AuthScheme = AuthSchemes.Bearer, ApiKeyEnc = protector.Protect("sk-second-secret"),
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://second.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await gw.Host.Db.Providers.InsertAsync(second);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "huge-model" });
        await gw.Host.Db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode,
            [new ClientBinding { ProviderId = gw.Provider.Id }, new ClientBinding { ProviderId = second.Id }]);
        // Given no provider, the grade gets the first one serving its model.
        await gw.Host.App.Services.GetRequiredService<SmartRoutingService>().UpdateAsync(s =>
        {
            s.Grades[2].Model = "huge-model";
            s.Grades[2].ProviderId = null;
        });

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "Redesign the billing module"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Upstream(gw));
        Assert.Equal("second.test", sent.Url.Host); // only the second provider serves the heavy grade's model
        Assert.Equal("huge-model", sent.Json["model"]!.GetValue<string>());
        Assert.Equal("Bearer sk-second-secret", sent.Headers["authorization"]);
        Assert.Equal(second.Id, (await gw.RecordOfAsync(response)).ProviderId);
    }

    /// <summary>A second provider serving <paramref name="model"/>, which the client is not bound to, and the light grade on it.</summary>
    private static async Task<Provider> LightGradeElsewhereAsync(GatewayFixture gw, string model = "tiny-model")
    {
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        var other = new Provider
        {
            Id = Ulid.NewUlid(), Name = "Elsewhere", AuthScheme = AuthSchemes.Bearer, ApiKeyEnc = protector.Protect("sk-elsewhere-secret"),
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://elsewhere.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await gw.Host.Db.Providers.InsertAsync(other);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = other.Id, ModelId = model });
        await gw.Host.App.Services.GetRequiredService<SmartRoutingService>().UpdateAsync(s =>
        {
            s.Grades[0].Model = model;
            s.Grades[0].ProviderId = other.Id;
        });
        return other;
    }

    [Fact]
    public async Task A_Grade_Goes_To_The_Provider_It_Names_Even_One_The_Client_Is_Not_Bound_To()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        var other = await LightGradeElsewhereAsync(gw);
        judge.Choice = "light";

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "1+1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Upstream(gw));
        Assert.Equal(("elsewhere.test", "tiny-model"), (sent.Url.Host, sent.Json["model"]!.GetValue<string>()));
        Assert.Equal("Bearer sk-elsewhere-secret", sent.Headers["authorization"]);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal((other.Id, "astra-auto", "tiny-model"), (record.ProviderId, record.RequestedModel, record.UpstreamModel));
        var span = await RouteAttributesAsync(gw, response);
        Assert.Equal("light", span["astra.route.grade"]!.GetValue<string>());
        Assert.Null(span["astra.route.adjusted_from"]);
    }

    [Fact]
    public async Task A_Token_Limited_To_Some_Clients_Keeps_To_Its_Own_Providers_And_The_Console_Is_Told()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        await LightGradeElsewhereAsync(gw);
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.AllowedClientsJson = $"[\"{ClientKinds.OpenCode}\"]";
        await gw.Host.Db.Tokens.UpdateAsync(token);
        judge.Choice = "light";

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "1+1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Upstream(gw));
        Assert.Equal(("upstream.test", "mid-model"), (sent.Url.Host, sent.Json["model"]!.GetValue<string>())); // its own provider, the next grade
        var span = await RouteAttributesAsync(gw, response);
        Assert.Equal(("standard", "light"), (span["astra.route.grade"]!.GetValue<string>(), span["astra.route.adjusted_from"]!.GetValue<string>()));
        Assert.Equal("token_limited", span["astra.route.adjusted_reason"]!.GetValue<string>());
        var routing = (await RoutingInLogAsync(gw, (await gw.RecordOfAsync(response)).Id))!;
        Assert.Equal(("standard", "light", "token_limited"),
            (routing["grade"]!.GetValue<string>(), routing["adjustedFrom"]!.GetValue<string>(), routing["adjustedReason"]!.GetValue<string>()));

        var status = (await gw.Host.GetJsonAsync("/api/smart-routing"))["status"]!;
        var problem = Assert.Single(status["unreachable"]!.AsArray())!;
        Assert.Equal((ClientKinds.OpenCode, "light", "tiny-model", "standard", 1),
            (problem["caller"]!.GetValue<string>(), problem["grade"]!.GetValue<string>(), problem["model"]!.GetValue<string>(),
                problem["usedGrade"]!.GetValue<string>(), problem["count"]!.GetValue<int>()));
        Assert.Equal(("token_limited", "Elsewhere"), (problem["reason"]!.GetValue<string>(), problem["providerName"]!.GetValue<string>()));
        Assert.Equal(1, status["recent"]!["total"]!.GetValue<int>());
        Assert.Equal(1, status["recent"]!["outcomes"]!["judge"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_Grade_Whose_Provider_Was_Switched_Off_Gives_Way_And_Says_So()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        var other = await LightGradeElsewhereAsync(gw);
        other.Enabled = false;
        await gw.Host.Db.Providers.UpdateAsync(other);
        judge.Choice = "light";

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "1+1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mid-model", Assert.Single(Upstream(gw)).Json["model"]!.GetValue<string>());
        var problem = Assert.Single((await gw.Host.GetJsonAsync("/api/smart-routing"))["status"]!["unreachable"]!.AsArray())!;
        Assert.Equal(("provider_off", "Elsewhere"), (problem["reason"]!.GetValue<string>(), problem["providerName"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_Console_Is_Told_When_The_Judge_Keeps_Falling_Back()
    {
        var (gw, judge) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        judge.Status = HttpStatusCode.ServiceUnavailable;

        await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "1+1", session: "conv-1"));
        await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "1+1", session: "conv-2"));

        var recent = (await gw.Host.GetJsonAsync("/api/smart-routing"))["status"]!["recent"]!;
        Assert.Equal(2, recent["total"]!.GetValue<int>());
        Assert.Equal(2, recent["outcomes"]!["overloaded"]!.GetValue<int>());
        Assert.Equal("overloaded", recent["lastFallback"]!.GetValue<string>());
        Assert.NotNull(recent["lastFallbackAt"]);
        Assert.Null(recent["medianJudgeMs"]); // no answer to time
    }

    // ---------------------------------------------------------------- what the judge may read

    [Fact]
    public async Task The_Judge_Reads_What_The_Privacy_Guard_Let_Through_Not_The_Original()
    {
        var (gw, _) = await StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        await gw.Host.App.Services.GetRequiredService<Astra.Gateway.Pipeline.PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact });

        var response = await gw.PostAsync("/v1/chat/completions", Chat("astra-auto", "mail a@b.com about the billing redesign"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var asked = Assert.Single(Judged(gw));
        Assert.DoesNotContain("a@b.com", asked.Body);
        Assert.Contains("REDACTED", asked.Json["state"]!["request"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- model lists and token counting

    [Fact]
    public async Task The_Model_Lists_Offer_The_Virtual_Model_Only_While_It_Is_On()
    {
        var (gw, _) = await StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json("{}"));
        await using var _ = gw;

        async Task<JsonNode> List(string path, params (string, string)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!;
        }

        var openai = await List("/v1/models");
        var entry = openai["data"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == "astra-auto")!;
        Assert.Equal("astra", entry["owned_by"]!.GetValue<string>());
        Assert.Equal(["small-model", "mid-model", "big-model", "astra-auto"], openai["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));

        var claude = await List("/v1/models", ("anthropic-version", "2023-06-01"));
        var claudeEntry = claude["data"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == "astra-auto")!;
        Assert.Equal("Astra Auto", claudeEntry["display_name"]!.GetValue<string>());

        var gemini = await List("/v1beta/models");
        Assert.Contains(gemini["models"]!.AsArray(), m => m!["name"]!.GetValue<string>() == "models/astra-auto");

        await gw.Host.App.Services.GetRequiredService<SmartRoutingService>().UpdateAsync(s => s.Enabled = false);
        var off = await List("/v1/models");
        Assert.DoesNotContain(off["data"]!.AsArray(), m => m!["id"]!.GetValue<string>() == "astra-auto");
    }

    [Fact]
    public async Task Counting_Tokens_For_The_Virtual_Model_Uses_The_Fallback_Grades_Model_Without_Asking_The_Judge()
    {
        var (gw, judge) = await StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json("""{"input_tokens":10}"""));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/messages/count_tokens",
            """{"model":"astra-auto","messages":[{"role":"user","content":"Redesign the billing module"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Upstream(gw));
        Assert.Equal("https://upstream.test/v1/messages/count_tokens", sent.Url.ToString());
        Assert.Equal("mid-model", sent.Json["model"]!.GetValue<string>());
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task Counting_Tokens_For_The_Virtual_Model_Is_Refused_While_It_Is_Off()
    {
        var (gw, _) = await StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json("""{"input_tokens":10}"""), enabled: false);
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/messages/count_tokens", """{"model":"astra-auto","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("智能路由未启用", await response.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);
    }
}
