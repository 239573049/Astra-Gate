using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Tracing through the real pipeline (.plan/observability.md §2–§3): the span tree written with each request, the
/// finish reason, and an agent's tool loop linked into one turn by the tool call ids — on the conversion path, where the
/// ids the client sees are the ones the upstream sent.
/// </summary>
public class GatewayTracingTests
{
    // Chat stream: some text, then one tool call (call_A), finish tool_calls.
    private const string ToolCallStream =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Let me look\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_A\",\"type\":\"function\",\"function\":{\"name\":\"shell\",\"arguments\":\"{\\\"cmd\\\":\\\"ls\\\"}\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":8,\"total_tokens\":128}}\n\n" +
        "data: [DONE]\n\n";

    private const string Done =
        """{"id":"c2","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"Fixed."},"finish_reason":"stop"}],"usage":{"prompt_tokens":150,"completion_tokens":3,"total_tokens":153}}""";

    private const string Session = "9f3e0b2c-1111-2222-3333-444455556666";

    private static readonly (string, string)[] Headers =
    [
        ("anthropic-version", "2023-06-01"),
        ("x-claude-code-session-id", Session),
        ("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"),
    ];

    private const string First = """
        {"model":"gpt-5","max_tokens":512,"stream":true,"system":"You are Claude Code.",
         "messages":[{"role":"user","content":"fix the login"}],
         "tools":[{"name":"shell","description":"Run","input_schema":{"type":"object","properties":{"cmd":{"type":"string"}}}}]}
        """;

    private const string Second = """
        {"model":"gpt-5","max_tokens":512,"system":"You are Claude Code.",
         "messages":[{"role":"user","content":"fix the login"},
                     {"role":"assistant","content":[{"type":"tool_use","id":"call_A","name":"shell","input":{"cmd":"ls"}}]},
                     {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_A","content":"login.ts"}]}],
         "tools":[{"name":"shell","description":"Run","input_schema":{"type":"object","properties":{"cmd":{"type":"string"}}}}]}
        """;

    [Fact]
    public async Task A_Tool_Loop_Is_One_Turn_Of_Linked_Requests_With_Span_Trees()
    {
        // The second call carries the tool result (a Chat "tool" message once translated): answer it with a plain stop.
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat,
            seen => seen.Body.Contains("\"tool_call_id\"", StringComparison.Ordinal) ? FakeUpstream.Json(Done) : FakeUpstream.Sse(ToolCallStream));

        var firstResponse = await gw.PostAsync("/v1/messages", First, null, Headers);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        await firstResponse.Content.ReadAsStringAsync();
        var first = await gw.RecordOfAsync(firstResponse);

        // Upstream never sees the agent's trace context.
        Assert.DoesNotContain("traceparent", gw.Upstream.Requests[0].Headers.Keys);

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", first.TraceId); // the agent's trace names the turn
        Assert.Equal("b7ad6b7169203331", first.ParentSpanId);
        Assert.Equal("cc:" + Session, first.SessionKey);
        Assert.StartsWith("fp:", first.ChainKey);
        Assert.Equal(first.Id, first.TurnId);
        Assert.Null(first.ParentRequestId);
        Assert.Equal(1, first.Step);
        Assert.Equal(TraceCallKinds.Main, first.CallKind);
        Assert.Equal(TraceLinkSources.NewTurn, first.LinkSource);
        Assert.Equal("tool_calls", first.FinishReason);
        Assert.Equal(1, first.ToolCalls);

        var spans = await gw.Host.Db.Traces.ListSpansAsync(first.Id);
        Assert.Equal(TraceSpanNames.Request, spans[0].Name);
        Assert.Null(spans[0].ParentSpanId);
        foreach (var name in new[] { TraceSpanNames.Route, TraceSpanNames.Body, TraceSpanNames.Privacy, TraceSpanNames.Resolve,
                     TraceSpanNames.Encode, TraceSpanNames.Attempt, TraceSpanNames.Response })
            Assert.Contains(spans, s => s.Name == name && s.ParentSpanId == spans[0].SpanId);
        Assert.DoesNotContain(spans, s => s.Name == TraceSpanNames.Middleware); // no middleware plugin installed
        Assert.All(spans, s => Assert.Equal(TraceSpanStatus.Ok, s.Status));
        Assert.All(spans.Skip(1), s => Assert.True(s.StartUs + s.DurationUs <= spans[0].DurationUs + 1));

        var encode = JsonNode.Parse(spans.Single(s => s.Name == TraceSpanNames.Encode).AttributesJson!)!;
        Assert.Equal("anthropic", encode["astra.protocol.inbound"]!.GetValue<string>());
        Assert.Equal("openai-chat", encode["astra.protocol.upstream"]!.GetValue<string>());
        Assert.False(encode["astra.passthrough"]!.GetValue<bool>());
        var attempt = JsonNode.Parse(spans.Single(s => s.Name == TraceSpanNames.Attempt).AttributesJson!)!;
        Assert.Equal(200, attempt["http.response.status_code"]!.GetValue<long>());
        var response = spans.Single(s => s.Name == TraceSpanNames.Response);
        var responseAttributes = JsonNode.Parse(response.AttributesJson!)!;
        Assert.Equal("tool_calls", responseAttributes["gen_ai.response.finish_reasons"]!.GetValue<string>());
        Assert.Equal(1, responseAttributes["astra.response.tool_calls"]!.GetValue<long>());
        Assert.Equal("shell", responseAttributes["astra.response.tool_names"]!.GetValue<string>());
        Assert.Contains("first_token", response.EventsJson);
        var root = JsonNode.Parse(spans[0].AttributesJson!)!;
        Assert.Equal(120, root["gen_ai.usage.input_tokens"]!.GetValue<long>());

        // The agent runs the tool and answers call_A: same turn, same trace, next step.
        var secondResponse = await gw.PostAsync("/v1/messages", Second, null, ("anthropic-version", "2023-06-01"), ("x-claude-code-session-id", Session));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await gw.RecordOfAsync(secondResponse);
        Assert.Equal(first.Id, second.ParentRequestId);
        Assert.Equal(first.Id, second.TurnId);
        Assert.Equal(first.TraceId, second.TraceId);
        Assert.Equal(2, second.Step);
        Assert.Equal(TraceLinkSources.ToolCall, second.LinkSource);
        Assert.Equal(first.ChainKey, second.ChainKey);
        Assert.Equal("stop", second.FinishReason);
        Assert.Equal(0, second.ToolCalls);

        Assert.Equal([first.Id, second.Id], (await gw.Host.Db.Requests.ListByTurnAsync(first.Id)).Select(r => r.Id));
        var session = Assert.Single(await gw.Host.Db.Traces.ListSessionsAsync(DateTimeOffset.UtcNow.AddHours(-1), null, 10));
        Assert.Equal(2, session.Requests);
        Assert.Equal(1, session.Turns);

        // The admin views over it (.plan/observability.md §4).
        var detail = (await gw.Host.GetJsonAsync($"/api/traces/requests/{second.Id}"))!;
        Assert.Equal(first.Id, detail["parent"]!["id"]!.GetValue<string>());
        Assert.False(detail["inFlight"]!.GetValue<bool>());
        Assert.Contains(detail["spans"]!.AsArray(), s => s!["name"]!.GetValue<string>() == TraceSpanNames.Attempt);
        Assert.Equal(second.Id, (await gw.Host.GetJsonAsync($"/api/traces/requests/{first.Id}"))!["children"]![0]!["id"]!.GetValue<string>());

        var sessions = (await gw.Host.GetJsonAsync("/api/traces/sessions?range=1h"))!.AsArray();
        Assert.Equal("cc:" + Session, Assert.Single(sessions)!["sessionKey"]!.GetValue<string>());
        Assert.True(sessions[0]!["active"]!.GetValue<bool>());
        var sessionDetail = (await gw.Host.GetJsonAsync($"/api/traces/sessions/{Uri.EscapeDataString("cc:" + Session)}"))!;
        Assert.Equal(first.Id, sessionDetail["turnId"]!.GetValue<string>());
        Assert.Equal(2, sessionDetail["calls"]!.AsArray().Count);
        Assert.Equal(2, sessionDetail["spans"]!.AsArray().Count(s => s!["name"]!.GetValue<string>() == TraceSpanNames.Request));

        var topology = (await gw.Host.GetJsonAsync("/api/traces/topology?range=1h"))!;
        Assert.Equal(2, topology["gateway"]!["requests"]!.GetValue<long>());
        Assert.NotNull(topology["gateway"]!["overheadP50Us"]);
        Assert.Equal("claude-code", topology["clients"]![0]!["clientKind"]!.GetValue<string>());
        Assert.Equal(1, topology["clients"]![0]!["activeSessions"]!.GetValue<int>());
        Assert.Equal(gw.Provider.Id, topology["providers"]![0]!["providerId"]!.GetValue<string>());
    }

    // Anthropic stream: a thinking block, then the answer text.
    private const string ThinkingStream =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-x\",\"content\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Let me think\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Done.\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public async Task The_Response_Span_Records_When_The_Model_Thought_And_Wrote()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Sse(ThinkingStream));

        var response = await gw.PostAsync("/v1/messages", First, null, ("anthropic-version", "2023-06-01"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var record = await gw.RecordOfAsync(response);

        var span = (await gw.Host.Db.Traces.ListSpansAsync(record.Id)).Single(s => s.Name == TraceSpanNames.Response);
        var attributes = JsonNode.Parse(span.AttributesJson!)!;
        Assert.Equal("Let me think".Length, attributes["astra.response.reasoning_chars"]!.GetValue<long>());
        Assert.Equal("Done.".Length, attributes["astra.response.text_chars"]!.GetValue<long>());
        var events = JsonNode.Parse(span.EventsJson!)!.AsArray().Select(e => (Name: e!["name"]!.GetValue<string>(), At: e["offset_us"]!.GetValue<long>())).ToList();
        string[] phases = ["reasoning_start", "reasoning_end", "text_start", "text_end"];
        Assert.All(phases, name => Assert.Contains(events, e => e.Name == name));
        var at = phases.Select(name => events.First(e => e.Name == name).At).ToList();
        Assert.Equal(at.Order(), at); // thought first, then wrote
        Assert.Null(record.LiveStage); // live only, never stored
        Assert.Null(record.LivePhases);

        // the live feed says where the call sits, so a call in flight is drawn in its own turn
        var summary = RequestEndpoints.ToSummary(record);
        Assert.Equal(record.TurnId, summary.TurnId);
        Assert.NotNull(summary.TurnId);
        Assert.Equal("main", summary.CallKind);
        Assert.Equal(record.SessionKey, summary.SessionKey);
        Assert.Equal(record.ParentRequestId, summary.ParentRequestId);
    }

    [Fact]
    public async Task Conversation_Content_Is_Kept_Only_While_Trace_Content_Is_On()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat,
            seen => seen.Body.Contains("\"tool_call_id\"", StringComparison.Ordinal) ? FakeUpstream.Json(Done) : FakeUpstream.Sse(ToolCallStream));

        // Off (the default): why the call was sent and which tools it called, never what they said.
        var plain = await gw.PostAsync("/v1/messages", First, null, ("anthropic-version", "2023-06-01"));
        await plain.Content.ReadAsStringAsync();
        var plainSpans = await gw.Host.Db.Traces.ListSpansAsync((await gw.RecordOfAsync(plain)).Id);
        var plainRoot = JsonNode.Parse(plainSpans[0].AttributesJson!)!;
        Assert.Equal("user", plainRoot["astra.request.cause"]!.GetValue<string>());
        Assert.Null(plainRoot["astra.request.prompt"]);
        var plainResponse = plainSpans.Single(x => x.Name == TraceSpanNames.Response);
        var plainCall = JsonNode.Parse(plainResponse.EventsJson!)!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "tool_call")!["attributes"]!;
        Assert.Equal("shell", plainCall["name"]!.GetValue<string>());
        Assert.Equal("call_A", plainCall["id"]!.GetValue<string>());
        Assert.Null(plainCall["arguments"]);
        Assert.Null(JsonNode.Parse(plainResponse.AttributesJson!)!["astra.response.text"]);

        // On: the prompt, each tool call's arguments and the start of the reply.
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(x => x.TraceContent = true);
        var first = await gw.PostAsync("/v1/messages", First, null, Headers);
        await first.Content.ReadAsStringAsync();
        var firstRecord = await gw.RecordOfAsync(first);
        var spans = await gw.Host.Db.Traces.ListSpansAsync(firstRecord.Id);
        Assert.Equal("fix the login", JsonNode.Parse(spans[0].AttributesJson!)!["astra.request.prompt"]!.GetValue<string>());
        var response = spans.Single(x => x.Name == TraceSpanNames.Response);
        var call = JsonNode.Parse(response.EventsJson!)!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "tool_call")!["attributes"]!;
        Assert.Equal("""{"cmd":"ls"}""", call["arguments"]!.GetValue<string>());
        Assert.Equal("Let me look", JsonNode.Parse(response.AttributesJson!)!["astra.response.text"]!.GetValue<string>());

        // The tool result: named after the call it answers (from the history), with its size.
        var second = await gw.PostAsync("/v1/messages", Second, null, ("anthropic-version", "2023-06-01"), ("x-claude-code-session-id", Session));
        var secondRecord = await gw.RecordOfAsync(second);
        var secondRoot = (await gw.Host.Db.Traces.ListSpansAsync(secondRecord.Id))[0];
        Assert.Equal("tool_result", JsonNode.Parse(secondRoot.AttributesJson!)!["astra.request.cause"]!.GetValue<string>());
        var result = JsonNode.Parse(secondRoot.EventsJson!)!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "tool_result")!["attributes"]!;
        Assert.Equal("shell", result["name"]!.GetValue<string>());
        Assert.False(result["error"]!.GetValue<bool>());
        Assert.Equal("login.ts".Length, result["chars"]!.GetValue<long>());

        // The activity feed reads it back, newest first.
        var activity = (await gw.Host.GetJsonAsync("/api/traces/activity?limit=10"))!.AsArray();
        Assert.Equal(3, activity.Count);
        Assert.Equal(secondRecord.Id, activity[0]!["request"]!["id"]!.GetValue<string>());
        Assert.Equal("tool_result", activity[0]!["cause"]!["kind"]!.GetValue<string>());
        Assert.Equal("shell", activity[0]!["cause"]!["toolResults"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(2, activity[0]!["step"]!.GetValue<int>());
        Assert.Equal("stop", activity[0]!["finishReason"]!.GetValue<string>());
        var withContent = activity[1]!;
        Assert.Equal(firstRecord.Id, withContent["request"]!["id"]!.GetValue<string>());
        Assert.Equal("fix the login", withContent["cause"]!["prompt"]!.GetValue<string>());
        Assert.Equal("Let me look", withContent["reply"]!.GetValue<string>());
        Assert.Equal("""{"cmd":"ls"}""", withContent["toolCalls"]![0]!["arguments"]!.GetValue<string>());
        var attempt = Assert.Single(withContent["attempts"]!.AsArray())!;
        Assert.Equal(gw.Provider.Id, attempt["providerId"]!.GetValue<string>());
        Assert.Equal(200, attempt["status"]!.GetValue<int>());
        Assert.Null(attempt["retry"]);
        Assert.Null(activity[2]!["toolCalls"]![0]!["arguments"]); // recorded before the setting was turned on

        Assert.True((await gw.Host.GetJsonAsync("/api/settings"))!["traceContent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_Refused_Request_Still_Gets_A_Trace()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(Done));

        var response = await gw.PostAsync("/v1/messages", First, "not-an-astra-token", ("anthropic-version", "2023-06-01"),
            ("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var record = await gw.RecordOfAsync(response);

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", record.TraceId); // the agent's trace, even refused
        Assert.Null(record.TurnId); // never got as far as reading the body
        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        Assert.Equal(TraceSpanStatus.Error, spans[0].Status);
        var route = spans.Single(s => s.Name == TraceSpanNames.Route);
        Assert.Equal(TraceSpanStatus.Error, route.Status);
    }
}
