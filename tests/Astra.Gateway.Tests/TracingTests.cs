using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Requests;
using Astra.Gateway.Tracing;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

/// <summary>Agent identity read from the client's body (.plan/observability.md §3.1, §3.2).</summary>
public class TraceKeysTests
{
    private static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    [Fact]
    public void The_Cause_Names_Each_Tool_Result_After_The_Call_It_Answers()
    {
        var anthropic = TraceKeys.CauseOf(ApiProtocol.Anthropic, Json("""
            {"messages":[{"role":"user","content":"fix it"},
                         {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{}},
                                                        {"type":"tool_use","id":"toolu_2","name":"Bash","input":{}}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"hello"},
                                                   {"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"boom"}]},
                                                   {"type":"text","text":"<system-reminder>x</system-reminder>"}]}]}
            """), withPrompt: true)!;
        Assert.Equal(RequestCauseKinds.ToolResult, anthropic.Kind);
        Assert.Equal([new ToolResultInfo("Read", false, 5), new ToolResultInfo("Bash", true, 4)], anthropic.ToolResults);
        Assert.Null(anthropic.Prompt);

        var chat = TraceKeys.CauseOf(ApiProtocol.OpenAIChat, Json("""
            {"messages":[{"role":"user","content":"go"},
                         {"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"grep","arguments":"{}"}}]},
                         {"role":"tool","tool_call_id":"c1","content":"3 hits"}]}
            """), withPrompt: false)!;
        Assert.Equal([new ToolResultInfo("grep", null, 6)], chat.ToolResults);

        var responses = TraceKeys.CauseOf(ApiProtocol.OpenAIResponses, Json("""
            {"input":[{"type":"message","role":"user","content":"go"},
                      {"type":"function_call","call_id":"r1","name":"shell","arguments":"{}"},
                      {"type":"function_call_output","call_id":"r1","output":"done"}]}
            """), withPrompt: false)!;
        Assert.Equal([new ToolResultInfo("shell", null, 4)], responses.ToolResults);

        var gemini = TraceKeys.CauseOf(ApiProtocol.Gemini, Json("""
            {"contents":[{"role":"user","parts":[{"functionResponse":{"name":"lookup","response":{"error":"nope"}}}]}]}
            """), withPrompt: false)!;
        Assert.Equal([new ToolResultInfo("lookup", true, 4)], gemini.ToolResults);
    }

    [Fact]
    public void The_Prompt_Is_Kept_Only_When_Asked_And_Skips_System_Reminders()
    {
        var body = Json("""
            {"messages":[{"role":"user","content":[{"type":"text","text":"<system-reminder>context</system-reminder>"},{"type":"text","text":"  rename the button  "}]}]}
            """);
        var withoutPrompt = TraceKeys.CauseOf(ApiProtocol.Anthropic, body, withPrompt: false)!;
        Assert.Equal(RequestCauseKinds.User, withoutPrompt.Kind);
        Assert.Null(withoutPrompt.Prompt);
        Assert.Equal("rename the button", TraceKeys.CauseOf(ApiProtocol.Anthropic, body, withPrompt: true)!.Prompt);

        var long_ = TraceKeys.CauseOf(ApiProtocol.OpenAIResponses, Json($$"""{"input":"{{new string('a', 500)}}"}"""), withPrompt: true)!;
        Assert.Equal(TraceKeys.MaxPromptChars + 1, long_.Prompt!.Length); // cut, with an ellipsis

        Assert.Null(TraceKeys.CauseOf(ApiProtocol.Anthropic, Json("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"Sure"}]}"""), true));
    }

    [Fact]
    public void Attempts_Report_Each_Send_With_Its_Account_Status_And_Retry()
    {
        var trace = new RequestTrace();
        var first = trace.Start(TraceSpanNames.Attempt, TraceSpanKinds.Client).Set("astra.provider.id", "p1").Set("astra.provider.name", "One")
            .Set("astra.account.id", "acc1");
        first.Set("http.response.status_code", 429).Set("astra.attempt.retry", "rate_limited_failover").Error("429").End();
        trace.Start(TraceSpanNames.Attempt, TraceSpanKinds.Client).Set("astra.provider.id", "p1").Set("astra.account.id", "acc2");

        var attempts = trace.Attempts();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(("p1", "One", "acc1", 429, "rate_limited_failover"), (attempts[0].ProviderId, attempts[0].ProviderName, attempts[0].AccountId, attempts[0].Status, attempts[0].Retry));
        Assert.NotNull(attempts[0].EndMs);
        Assert.Null(attempts[0].Error); // it has a status
        Assert.Equal("acc2", attempts[1].AccountId);
        Assert.Null(attempts[1].Status); // still in flight
        Assert.Null(attempts[1].EndMs);
    }

    [Fact]
    public void Anthropic_Tool_Results_Continue_And_Text_Starts_A_Turn()
    {
        var loop = TraceKeys.LastMessageOf(ApiProtocol.Anthropic, Json("""
            {"messages":[{"role":"user","content":"fix it"},
                         {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"},{"type":"text","text":"<reminder>"}]}]}
            """));
        Assert.Equal(["toolu_1"], loop.ToolResultIds);
        Assert.False(loop.UserTurn);

        var turn = TraceKeys.LastMessageOf(ApiProtocol.Anthropic, Json("""{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}"""));
        Assert.Empty(turn.ToolResultIds);
        Assert.True(turn.UserTurn);

        var prefill = TraceKeys.LastMessageOf(ApiProtocol.Anthropic, Json("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"Sure"}]}"""));
        Assert.False(prefill.UserTurn);
    }

    [Fact]
    public void The_Shape_Of_A_Request_Counts_Without_Reading_Content()
    {
        var anthropic = TraceKeys.ShapeOf(ApiProtocol.Anthropic, Json("""
            {"max_tokens":32000,"system":[{"type":"text","text":"abcd"},{"type":"text","text":"ef"}],
             "tools":[{"name":"Read"},{"name":"Bash"}],"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"yo"},{"role":"user","content":"go"}]}
            """));
        Assert.Equal(new RequestShape(3, 2, 6, 32000), anthropic);

        var chat = TraceKeys.ShapeOf(ApiProtocol.OpenAIChat, Json("""
            {"max_completion_tokens":800,"messages":[{"role":"system","content":"sys"},{"role":"developer","content":[{"type":"text","text":"dev"}]},{"role":"user","content":"q"}]}
            """));
        Assert.Equal(new RequestShape(3, null, 6, 800), chat);

        var gemini = TraceKeys.ShapeOf(ApiProtocol.Gemini, Json("""
            {"systemInstruction":{"parts":[{"text":"be brief"}]},"contents":[{"role":"user","parts":[{"text":"q"}]}],
             "tools":[{"functionDeclarations":[{"name":"a"},{"name":"b"}]}],"generationConfig":{"maxOutputTokens":1024}}
            """));
        Assert.Equal(new RequestShape(1, 2, 8, 1024), gemini);

        Assert.Equal(new RequestShape(1, null, null, null), TraceKeys.ShapeOf(ApiProtocol.OpenAIResponses, Json("""{"input":"hello"}""")));
    }

    [Fact]
    public void Chat_Collects_Every_Trailing_Tool_Message()
    {
        var last = TraceKeys.LastMessageOf(ApiProtocol.OpenAIChat, Json("""
            {"messages":[{"role":"system","content":"s"},{"role":"user","content":"q"},
                         {"role":"assistant","tool_calls":[{"id":"call_a"},{"id":"call_b"}]},
                         {"role":"tool","tool_call_id":"call_a","content":"1"},{"role":"tool","tool_call_id":"call_b","content":"2"}]}
            """));
        Assert.Equal(["call_a", "call_b"], last.ToolResultIds);
        Assert.False(last.UserTurn);
        Assert.True(TraceKeys.LastMessageOf(ApiProtocol.OpenAIChat, Json("""{"messages":[{"role":"user","content":"q"}]}""")).UserTurn);
    }

    [Fact]
    public void Responses_Reads_Outputs_After_The_Last_Message()
    {
        var last = TraceKeys.LastMessageOf(ApiProtocol.OpenAIResponses, Json("""
            {"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"q"}]},
                      {"type":"function_call","call_id":"c1"},{"type":"function_call","call_id":"c2"},
                      {"type":"function_call_output","call_id":"c1","output":"x"},{"type":"custom_tool_call_output","call_id":"c2","output":"y"}]}
            """));
        Assert.Equal(["c1", "c2"], last.ToolResultIds);
        Assert.True(TraceKeys.LastMessageOf(ApiProtocol.OpenAIResponses, Json("""{"input":"hello"}""")).UserTurn);
    }

    [Fact]
    public void Gemini_Function_Responses_Continue_Even_Without_Ids()
    {
        var withId = TraceKeys.LastMessageOf(ApiProtocol.Gemini, Json("""{"contents":[{"role":"user","parts":[{"functionResponse":{"id":"g1","name":"f","response":{}}}]}]}"""));
        Assert.Equal(["g1"], withId.ToolResultIds);
        var noId = TraceKeys.LastMessageOf(ApiProtocol.Gemini, Json("""{"contents":[{"parts":[{"functionResponse":{"name":"f","response":{}}}]}]}"""));
        Assert.Empty(noId.ToolResultIds);
        Assert.False(noId.UserTurn);
    }

    [Fact]
    public void Chain_Is_Stable_Across_A_Loop_And_Differs_For_A_Subagent()
    {
        var first = TraceKeys.ChainOf(ApiProtocol.Anthropic, Json("""{"system":"You are Claude Code","messages":[{"role":"user","content":"fix the login"}]}"""));
        var later = TraceKeys.ChainOf(ApiProtocol.Anthropic, Json("""
            {"system":[{"type":"text","text":"You are Claude Code"}],
             "messages":[{"role":"user","content":[{"type":"text","text":"fix the login"}]},{"role":"assistant","content":"…"},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"file"}]}]}
            """));
        var sub = TraceKeys.ChainOf(ApiProtocol.Anthropic, Json("""{"system":"You are a search agent","messages":[{"role":"user","content":"find redirects"}]}"""));
        Assert.StartsWith("fp:", first);
        Assert.Equal(first, later);
        Assert.NotEqual(first, sub);
        Assert.Null(TraceKeys.ChainOf(ApiProtocol.Anthropic, Json("""{"messages":[]}""")));
    }

    [Fact]
    public void Session_Prefers_Explicit_Ids_And_Hashes_The_Rest()
    {
        var headers = new HeaderDictionary { ["x-claude-code-session-id"] = "9f3e0b2c-1111-2222-3333-444455556666" };
        Assert.Equal("cc:9f3e0b2c-1111-2222-3333-444455556666", TraceKeys.SessionOf(ApiProtocol.Anthropic, Json("{}"), headers, "fp:x"));

        var codex = new HeaderDictionary { ["session-id"] = "0199-abc" };
        Assert.Equal("codex:0199-abc", TraceKeys.SessionOf(ApiProtocol.OpenAIResponses, Json("{}"), codex, "fp:x"));

        var cacheKey = TraceKeys.SessionOf(ApiProtocol.OpenAIChat, Json("""{"prompt_cache_key":"my secret project"}"""), new HeaderDictionary(), "fp:x");
        Assert.StartsWith("h:", cacheKey);
        Assert.DoesNotContain("secret", cacheKey);

        Assert.Equal("fp:x", TraceKeys.SessionOf(ApiProtocol.Gemini, Json("{}"), new HeaderDictionary(), "fp:x"));
    }

    [Fact]
    public void Traceparent_Is_Parsed_Strictly()
    {
        Assert.Equal(("0af7651916cd43dd8448eb211c80319c", "b7ad6b7169203331"),
            TraceKeys.ParseTraceparent("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"));
        Assert.Equal((null, null), TraceKeys.ParseTraceparent("00-00000000000000000000000000000000-b7ad6b7169203331-01"));
        Assert.Equal((null, null), TraceKeys.ParseTraceparent("garbage"));
        Assert.Equal((null, null), TraceKeys.ParseTraceparent(null));
    }
}

/// <summary>Turn / sub-agent / side-call linking (.plan/observability.md §3.2).</summary>
public class TraceLinkerTests
{
    private static readonly LastMessage User = new([], true);
    private static LastMessage Results(params string[] ids) => new(ids, false);

    [Fact]
    public void Tool_Results_Continue_The_Call_That_Made_Them()
    {
        var linker = new TraceLinker();
        var first = linker.Link("r1", "cc:s", "fp:main", User, null);
        Assert.Equal(TraceLinkSources.NewTurn, first.LinkSource);
        Assert.Equal("r1", first.TurnId);
        Assert.Equal(1, first.Step);
        linker.Complete(first, ["toolu_a", "toolu_b"]);

        var second = linker.Link("r2", "cc:s", "fp:main", Results("toolu_a", "toolu_b"), null);
        Assert.Equal(TraceLinkSources.ToolCall, second.LinkSource);
        Assert.Equal("r1", second.ParentRequestId);
        Assert.Equal("r1", second.TurnId);
        Assert.Equal(first.TraceId, second.TraceId);
        Assert.Equal(2, second.Step);
        Assert.Equal(TraceCallKinds.Main, second.CallKind);

        // The user's next message opens a new turn with a new trace.
        var next = linker.Link("r3", "cc:s", "fp:main", User, null);
        Assert.Equal("r3", next.TurnId);
        Assert.NotEqual(first.TraceId, next.TraceId);
        Assert.Null(next.ParentRequestId);
    }

    [Fact]
    public void A_New_Chain_While_Tool_Calls_Are_Pending_Is_A_Subagent()
    {
        var linker = new TraceLinker();
        var main = linker.Link("m1", "cc:s", "fp:main", User, null);
        linker.Complete(main, ["toolu_task"]);

        var sub1 = linker.Link("s1", "cc:s", "fp:sub", User, null);
        Assert.Equal(TraceCallKinds.Sidechain, sub1.CallKind);
        Assert.Equal(TraceLinkSources.Inferred, sub1.LinkSource);
        Assert.Equal("m1", sub1.ParentRequestId);
        Assert.Equal("m1", sub1.TurnId);
        linker.Complete(sub1, ["toolu_grep"]);

        var sub2 = linker.Link("s2", "cc:s", "fp:sub", Results("toolu_grep"), null);
        Assert.Equal(TraceCallKinds.Sidechain, sub2.CallKind);
        Assert.Equal("s1", sub2.ParentRequestId);
        Assert.Equal(2, sub2.Step);

        // The main agent gets the Task result: back on the main chain.
        var back = linker.Link("m2", "cc:s", "fp:main", Results("toolu_task"), null);
        Assert.Equal(TraceCallKinds.Main, back.CallKind);
        Assert.Equal("m1", back.ParentRequestId);
        Assert.Equal(2, back.Step);

        // Nothing pending any more: another chain is a side call of the turn.
        var aux = linker.Link("x1", "cc:s", "fp:title", User, null);
        Assert.Equal(TraceCallKinds.Aux, aux.CallKind);
        Assert.Equal("m2", aux.ParentRequestId);
        Assert.Equal("m1", aux.TurnId);
    }

    [Fact]
    public void Unknown_Tool_Results_Fall_Back_To_The_Chain()
    {
        var linker = new TraceLinker();
        var first = linker.Link("r1", "cc:s", "fp:main", User, null);
        var orphan = linker.Link("r2", "cc:s", "fp:main", Results("toolu_from_before_a_restart"), null);
        Assert.Equal(TraceLinkSources.Session, orphan.LinkSource);
        Assert.Equal("r1", orphan.ParentRequestId);
        Assert.Equal(first.TraceId, orphan.TraceId);

        var stranger = new TraceLinker().Link("z", null, null, Results("toolu_x"), null);
        Assert.Equal(TraceLinkSources.NewTurn, stranger.LinkSource);
    }

    [Fact]
    public void The_Agents_Trace_Id_Names_A_New_Turn()
    {
        var link = new TraceLinker().Link("r1", "cc:s", "fp:main", User, "0af7651916cd43dd8448eb211c80319c");
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", link.TraceId);
    }
}

/// <summary>Span recording (.plan/observability.md §2.1).</summary>
public class RequestTraceTests
{
    [Fact]
    public void Builds_A_Tree_With_Attributes_Events_And_Closed_Leftovers()
    {
        var trace = new RequestTrace();
        trace.Start(TraceSpanNames.Route).Set("astra.client.kind", "claude-code").Set("astra.body.bytes", 42L).Ok().End();
        var attempt = trace.Start(TraceSpanNames.Attempt, TraceSpanKinds.Client).Set("http.response.status_code", 429L).Set("astra.passthrough", true);
        attempt.Event("first_token");
        var cut = trace.Start(TraceSpanNames.Response); // never ended: the request failed
        trace.Root.Ok();

        var spans = trace.Build("r1");

        Assert.Equal([TraceSpanNames.Request, TraceSpanNames.Route, TraceSpanNames.Attempt, TraceSpanNames.Response], spans.Select(s => s.Name));
        Assert.All(spans, s => Assert.Equal("r1", s.RequestId));
        Assert.Null(spans[0].ParentSpanId);
        Assert.All(spans.Skip(1), s => Assert.Equal(spans[0].SpanId, s.ParentSpanId));
        Assert.Equal(16, spans[0].SpanId.Length);
        Assert.Equal(TraceSpanKinds.Server, spans[0].Kind);
        Assert.Equal(TraceSpanKinds.Client, spans[2].Kind);
        Assert.True(spans.All(s => s.StartUs >= 0 && s.DurationUs >= 0));
        Assert.True(spans[1].StartUs + spans[1].DurationUs <= spans[0].DurationUs);

        using var route = JsonDocument.Parse(spans[1].AttributesJson!);
        Assert.Equal("claude-code", route.RootElement.GetProperty("astra.client.kind").GetString());
        Assert.Equal(42, route.RootElement.GetProperty("astra.body.bytes").GetInt64());
        using var attemptAttributes = JsonDocument.Parse(spans[2].AttributesJson!);
        Assert.True(attemptAttributes.RootElement.GetProperty("astra.passthrough").GetBoolean());
        using var events = JsonDocument.Parse(spans[2].EventsJson!);
        Assert.Equal("first_token", events.RootElement[0].GetProperty("name").GetString());

        Assert.Equal(TraceSpanStatus.Error, spans[3].Status);
        Assert.Equal("incomplete", spans[3].StatusMessage);
        Assert.Equal(TraceSpanStatus.Ok, spans[1].Status);
        _ = cut;
    }
}
