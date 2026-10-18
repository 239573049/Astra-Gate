using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;
using Astra.Gateway.Tracing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Gateway.Tests;

/// <summary>
/// When the router asks its judge and when it does not (plan §5.4): once per session, once per user turn, never in the
/// middle of a turn, never again for a chained Responses request, and never at all while the judge cannot be trusted.
/// </summary>
public class SmartRouterTests
{
    private sealed class ScriptedJudge : IRoutingJudge
    {
        public string Protocol => JudgeProtocols.TypeSafe;
        public Func<JudgeQuestion, JudgeVerdict> Answer { get; set; } = _ => Verdict("heavy");
        public Func<Task>? Gate { get; set; }
        public List<JudgeQuestion> Asked { get; } = [];
        public List<JudgeEndpoint> Endpoints { get; } = [];

        /// <summary>Each call opens a connection first (the transport would call the endpoint's hook).</summary>
        public bool Connects { get; set; }

        public async Task<JudgeVerdict> JudgeAsync(JudgeEndpoint endpoint, JudgeQuestion question, CancellationToken ct)
        {
            lock (Asked)
            {
                Asked.Add(question);
                Endpoints.Add(endpoint);
            }
            if (Connects) endpoint.OnNewConnection?.Invoke();
            if (Gate is not null) await Gate();
            return Answer(question);
        }
    }

    /// <param name="Store">The rig's database; its one provider serves every grade's model.</param>
    private sealed record Rig(SmartRouter Router, SmartRoutingService Service, ScriptedJudge Judge, RoutingClock Clock, FakeProtector Protector, RoutingStore Store)
    {
        public string ProviderId => Store.Provider.Id;
    }

    private static JudgeVerdict Verdict(string choice, double confidence = 0.9) =>
        new(choice, confidence, new Dictionary<string, double> { [choice] = confidence }, "jev-1.13.0", 100, 10);

    private static async Task<Rig> StartAsync(Action<SmartRoutingSettings>? configure = null, bool enabled = true)
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();
        var protector = new FakeProtector();
        await service.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, protector);
            s.Enabled = enabled;
            configure?.Invoke(s);
        });
        var judge = new ScriptedJudge();
        var clock = new RoutingClock();
        var router = new SmartRouter(service, new RoutingJudgeRegistry([judge]), protector, NullLogger<SmartRouter>.Instance, clock);
        return new Rig(router, service, judge, clock, protector, store);
    }

    private static TraceLink NewTurn(string turn, string? session = "s1") =>
        new("trace-" + turn, turn, null, 1, TraceCallKinds.Main, TraceLinkSources.NewTurn, turn, session, "chain");

    private static TraceLink ToolStep(string turn, string? session = "s1") =>
        new("trace-" + turn, turn, turn, 2, TraceCallKinds.Main, TraceLinkSources.ToolCall, Ulid.NewUlid(), session, "chain");

    private static RoutingInput Input(TraceLink link, string text = "Fix the login bug", string token = "t1") =>
        new(token, ApiProtocol.Anthropic,
            new JsonObject { ["model"] = "astra-auto", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }) }, link);

    private static RoutingInput Chained(TraceLink link, string? previousResponseId, string text = "And now the docs") =>
        new("t1", ApiProtocol.OpenAIResponses,
            new JsonObject { ["model"] = "astra-auto", ["input"] = text, ["previous_response_id"] = previousResponseId }, link);

    // ---------------------------------------------------------------- off

    [Fact]
    public async Task While_It_Is_Off_The_Virtual_Model_Is_Refused_With_A_Plain_Message_And_The_Judge_Is_Never_Asked()
    {
        var rig = await StartAsync(enabled: false);

        var e = await Assert.ThrowsAsync<GatewayException>(() => rig.Router.DecideAsync(Input(NewTurn("a")), default));

        Assert.Equal(400, e.Status);
        Assert.Contains("智能路由未启用", e.Message);
        Assert.Contains("astra-auto", e.Message);
        Assert.Empty(rig.Judge.Asked);
        Assert.Null(rig.Router.AdvertisedModel);
        Assert.True(rig.Router.IsVirtual("astra-auto")); // still the virtual model's name: it is refused, not forwarded
        Assert.Throws<GatewayException>(() => rig.Router.FallbackModel("astra-auto"));
    }

    [Fact]
    public async Task The_Virtual_Model_Is_Advertised_Only_While_It_Is_On()
    {
        var rig = await StartAsync();
        Assert.Equal("astra-auto", rig.Router.AdvertisedModel);

        await rig.Service.UpdateAsync(s => s.Enabled = false);
        Assert.Null(rig.Router.AdvertisedModel);
    }

    // ---------------------------------------------------------------- session mode (the default)

    [Fact]
    public async Task A_Session_Is_Judged_Once_And_Keeps_Its_Grade_For_Every_Later_Turn()
    {
        var rig = await StartAsync();

        var first = await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        var later = await rig.Router.DecideAsync(Input(NewTurn("b"), "write the docs"), default);
        var third = await rig.Router.DecideAsync(Input(NewTurn("c"), "ok thanks"), default);

        Assert.Equal(("heavy", "big-model", RoutingSources.Judge), (first.GradeId, first.Model, first.Source));
        Assert.Equal(RoutingSources.Pin, later.Source);
        Assert.Equal(RoutingSources.Pin, third.Source);
        Assert.Equal("big-model", later.Model);
        Assert.Single(rig.Judge.Asked);
        // A decision served again made no judge call of its own.
        Assert.Null(later.JudgeProtocol);
        Assert.Equal(0, later.JudgeMs);
        Assert.Equal(first.Confidence, later.Confidence);
    }

    [Fact]
    public async Task Two_Tokens_With_The_Same_Session_Id_Do_Not_Share_A_Pin()
    {
        var rig = await StartAsync();

        await rig.Router.DecideAsync(Input(NewTurn("a"), token: "t1"), default);
        var other = await rig.Router.DecideAsync(Input(NewTurn("b"), token: "t2"), default);

        Assert.Equal(RoutingSources.Judge, other.Source);
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task Another_Session_Is_Judged_On_Its_Own_And_Can_Land_On_Another_Grade()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = q => Verdict(q.State.Request.Contains("typo") ? "light" : "heavy");

        var hard = await rig.Router.DecideAsync(Input(NewTurn("a"), "redesign the billing module", "t1"), default);
        var easy = await rig.Router.DecideAsync(Input(new TraceLink("tr", "b", null, 1, TraceCallKinds.Main, TraceLinkSources.NewTurn, "b", "s2", "chain"), "fix a typo"), default);

        Assert.Equal("big-model", hard.Model);
        Assert.Equal("small-model", easy.Model);
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task Without_A_Session_Every_New_Turn_Is_Judged_But_The_Tool_Steps_Of_A_Turn_Are_Not()
    {
        var rig = await StartAsync();

        var a = await rig.Router.DecideAsync(Input(NewTurn("a", session: null)), default);
        var aStep = await rig.Router.DecideAsync(Input(ToolStep("a", session: null)), default);
        var b = await rig.Router.DecideAsync(Input(NewTurn("b", session: null)), default);

        Assert.Equal(RoutingSources.Judge, a.Source);
        Assert.Equal(RoutingSources.Turn, aStep.Source);
        Assert.Equal(RoutingSources.Judge, b.Source);
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Pin_Lives_As_Long_As_The_Session_Is_Used_And_Is_Forgotten_After_Its_Idle_Time()
    {
        var rig = await StartAsync(s => s.Rules.SessionTtlMinutes = 10);
        await rig.Router.DecideAsync(Input(NewTurn("a")), default);

        rig.Clock.Now += TimeSpan.FromMinutes(9);
        Assert.Equal(RoutingSources.Pin, (await rig.Router.DecideAsync(Input(NewTurn("b")), default)).Source);
        rig.Clock.Now += TimeSpan.FromMinutes(9); // 18 minutes after the start, 9 after the last use: still alive
        Assert.Equal(RoutingSources.Pin, (await rig.Router.DecideAsync(Input(NewTurn("c")), default)).Source);
        Assert.Single(rig.Judge.Asked);

        rig.Clock.Now += TimeSpan.FromMinutes(11); // idle longer than the lifetime
        Assert.Equal(RoutingSources.Judge, (await rig.Router.DecideAsync(Input(NewTurn("d")), default)).Source);
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Session_That_Has_Only_Asked_For_Small_Things_Is_Judged_Again_Until_A_Turn_Needs_More()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = q => Verdict(q.State.Request is "hi" or "1+1" ? "light" : "heavy");

        var greeting = await rig.Router.DecideAsync(Input(NewTurn("a"), "hi"), default);
        var greetingStep = await rig.Router.DecideAsync(Input(ToolStep("a"), "hi"), default);
        var sum = await rig.Router.DecideAsync(Input(NewTurn("b"), "1+1"), default);
        var task = await rig.Router.DecideAsync(Input(NewTurn("c"), "analyse the architecture of this project"), default);
        var thanks = await rig.Router.DecideAsync(Input(NewTurn("d"), "hi"), default);

        Assert.Equal(("small-model", RoutingSources.Judge), (greeting.Model, greeting.Source));
        Assert.Equal(("small-model", RoutingSources.Turn), (greetingStep.Model, greetingStep.Source)); // no switch inside the turn
        Assert.Equal(("small-model", RoutingSources.Judge), (sum.Model, sum.Source)); // the lowest grade held no new turn
        Assert.Equal(("big-model", RoutingSources.Judge), (task.Model, task.Source));
        Assert.Equal(("big-model", RoutingSources.Pin), (thanks.Model, thanks.Source)); // a higher grade holds the session as before
        Assert.Equal(3, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Turn_That_Fell_Back_Keeps_Its_Grade_Although_The_Session_Holds_The_Lowest_One()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => Verdict("light");
        await rig.Router.DecideAsync(Input(NewTurn("a"), "hi"), default);

        rig.Judge.Answer = _ => throw new RoutingJudgeException(JudgeFailure.Timeout, "slow");
        var next = await rig.Router.DecideAsync(Input(NewTurn("b")), default);
        var step = await rig.Router.DecideAsync(Input(ToolStep("b")), default);

        Assert.Equal(("mid-model", RoutingSources.Fallback), (next.Model, next.Source));
        Assert.Equal(("mid-model", RoutingSources.Turn), (step.Model, step.Source)); // the turn's own grade, not the session's
    }

    [Fact]
    public async Task Changing_The_Settings_Drops_Every_Pin()
    {
        var rig = await StartAsync();
        await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        Assert.Equal(1, rig.Router.PinCount);

        await rig.Service.UpdateAsync(s => s.Grades[2].Model = "mid-model");
        var after = await rig.Router.DecideAsync(Input(NewTurn("b")), default);

        Assert.Equal(RoutingSources.Judge, after.Source);
        Assert.Equal("mid-model", after.Model); // the new mapping, not the pinned old one
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    // ---------------------------------------------------------------- turn mode

    [Fact]
    public async Task In_Turn_Mode_Every_New_Turn_Is_Judged_And_The_Tool_Steps_Of_A_Turn_Follow_Their_Turn()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);
        rig.Judge.Answer = q => Verdict(q.State.Request.Contains("typo") ? "light" : "heavy");

        var hard = await rig.Router.DecideAsync(Input(NewTurn("a"), "redesign billing"), default);
        var hardStep = await rig.Router.DecideAsync(Input(ToolStep("a"), "redesign billing"), default);
        var easy = await rig.Router.DecideAsync(Input(NewTurn("b"), "fix a typo"), default);
        var easyStep = await rig.Router.DecideAsync(Input(ToolStep("b"), "fix a typo"), default);

        Assert.Equal("big-model", hard.Model);
        Assert.Equal((RoutingSources.Turn, "big-model"), (hardStep.Source, hardStep.Model));
        Assert.Equal("small-model", easy.Model); // the session's first grade does not hold in this mode
        Assert.Equal((RoutingSources.Turn, "small-model"), (easyStep.Source, easyStep.Model));
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Turn_Keeps_Its_Fallback_So_Its_Later_Steps_Do_Not_Switch_Model_Halfway()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);
        rig.Judge.Answer = _ => throw new RoutingJudgeException(JudgeFailure.Timeout, "slow");

        var first = await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        rig.Judge.Answer = _ => Verdict("heavy"); // the judge is back, but the turn is already decided
        var step = await rig.Router.DecideAsync(Input(ToolStep("a")), default);

        Assert.Equal(RoutingSources.Fallback, first.Source);
        Assert.Equal(RoutingSources.Turn, step.Source);
        Assert.Equal("mid-model", step.Model);
        Assert.Single(rig.Judge.Asked);
    }

    // ---------------------------------------------------------------- side calls and sub-agents

    private static TraceLink SideCall(string mainTurn, string chain = "other-chain", string? session = "s1") =>
        new("trace-" + mainTurn, mainTurn, mainTurn, 1, TraceCallKinds.Aux, TraceLinkSources.Inferred, Ulid.NewUlid(), session, chain);

    private static TraceLink SubAgent(string mainTurn, string chain = "sub-chain", string? session = "s1") =>
        new("trace-" + mainTurn, mainTurn, mainTurn, 1, TraceCallKinds.Sidechain, TraceLinkSources.Inferred, Ulid.NewUlid(), session, chain);

    [Fact]
    public async Task In_Turn_Mode_A_Side_Call_Is_A_Task_Of_Its_Own_And_Leaves_The_Main_Turn_Alone()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);
        rig.Judge.Answer = q => Verdict(q.State.Request.Contains("typo") ? "light" : "heavy");

        var main = await rig.Router.DecideAsync(Input(NewTurn("a"), "redesign billing"), default);
        var side = await rig.Router.DecideAsync(Input(SideCall("a"), "fix a typo"), default);
        var mainStep = await rig.Router.DecideAsync(Input(ToolStep("a"), "redesign billing"), default);
        var sideAgain = await rig.Router.DecideAsync(Input(SideCall("a"), "fix a typo"), default);

        Assert.Equal(("big-model", RoutingSources.Judge), (main.Model, main.Source));
        Assert.Equal(("small-model", RoutingSources.Judge), (side.Model, side.Source)); // judged on its own words
        Assert.Equal(("big-model", RoutingSources.Turn), (mainStep.Model, mainStep.Source)); // not overwritten by the side call
        Assert.Equal(("small-model", RoutingSources.Turn), (sideAgain.Model, sideAgain.Source)); // the same chain keeps its grade
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Sub_Agent_Follows_The_Main_Turns_Model()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);
        rig.Judge.Answer = q => Verdict(q.State.Request.Contains("search") ? "light" : "heavy");

        await rig.Router.DecideAsync(Input(NewTurn("a"), "redesign billing"), default);
        var sub = await rig.Router.DecideAsync(Input(SubAgent("a"), "search the repo for usages"), default);

        Assert.Equal(("big-model", RoutingSources.Turn), (sub.Model, sub.Source));
        Assert.Single(rig.Judge.Asked);
    }

    [Fact]
    public async Task A_Side_Call_Never_Pins_The_Session()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = q => Verdict(q.State.Request.Contains("title") ? "light" : "heavy");

        var side = await rig.Router.DecideAsync(Input(SideCall("a"), "write a short title"), default);
        Assert.Equal(("small-model", RoutingSources.Judge), (side.Model, side.Source));
        Assert.Equal(0, rig.Router.PinCount);

        // The session's own first real request decides the session, not the side call before it.
        var main = await rig.Router.DecideAsync(Input(NewTurn("b"), "redesign billing"), default);
        Assert.Equal(("big-model", RoutingSources.Judge), (main.Model, main.Source));
        Assert.Equal(1, rig.Router.PinCount);
    }

    // ---------------------------------------------------------------- chained Responses requests

    [Fact]
    public async Task A_Responses_Request_Chained_By_A_Previous_Response_Never_Leaves_The_Last_Grade_Even_In_Turn_Mode()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);

        var first = await rig.Router.DecideAsync(Chained(NewTurn("a"), previousResponseId: null), default);
        var chained = await rig.Router.DecideAsync(Chained(NewTurn("b"), "resp_123"), default);
        var unchained = await rig.Router.DecideAsync(Chained(NewTurn("c"), previousResponseId: null), default);

        Assert.Equal(RoutingSources.Judge, first.Source);
        Assert.Equal(RoutingSources.Pin, chained.Source);
        Assert.Equal(first.Model, chained.Model);
        Assert.Equal(RoutingSources.Judge, unchained.Source); // a fresh turn without a chain is judged as usual
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Fact]
    public async Task A_Chained_Request_With_Nothing_Pinned_Is_Judged_Once_And_Pinned()
    {
        var rig = await StartAsync(s => s.Rules.Mode = RoutingModes.Turn);

        var chained = await rig.Router.DecideAsync(Chained(NewTurn("a"), "resp_123"), default);
        var next = await rig.Router.DecideAsync(Chained(NewTurn("b"), "resp_124"), default);

        Assert.Equal(RoutingSources.Judge, chained.Source);
        Assert.Equal(RoutingSources.Pin, next.Source);
        Assert.Single(rig.Judge.Asked);
    }

    // ---------------------------------------------------------------- not confident, failing, nothing to read

    [Fact]
    public async Task A_Judge_That_Is_Not_Confident_Enough_Gets_The_Fallback_Grade_And_Is_Asked_Again_Next_Turn()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => Verdict("light", confidence: 0.1); // below the 0.3 default

        var first = await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        var step = await rig.Router.DecideAsync(Input(ToolStep("a")), default);
        rig.Judge.Answer = _ => Verdict("heavy", confidence: 0.9);
        var next = await rig.Router.DecideAsync(Input(NewTurn("b")), default);

        Assert.Equal((RoutingSources.Fallback, "standard", "mid-model", "low_confidence"), (first.Source, first.GradeId, first.Model, first.FallbackReason));
        Assert.Equal(0.1, first.Confidence);
        Assert.Equal("jev-1.13.0", first.JudgeModel); // the judge did answer; its words were not enough
        Assert.Equal(RoutingSources.Turn, step.Source); // the turn stays on its fallback
        Assert.Equal((RoutingSources.Judge, "big-model"), (next.Source, next.Model)); // not pinned: the session got another chance
        Assert.Equal(2, rig.Judge.Asked.Count);
    }

    [Theory]
    [InlineData(JudgeFailure.Timeout, "timeout")]
    [InlineData(JudgeFailure.Network, "network")]
    [InlineData(JudgeFailure.Auth, "auth")]
    [InlineData(JudgeFailure.RateLimited, "rate_limited")]
    [InlineData(JudgeFailure.Overloaded, "overloaded")]
    [InlineData(JudgeFailure.BadRequest, "bad_request")]
    [InlineData(JudgeFailure.BadResponse, "bad_response")]
    public async Task A_Failing_Judge_Never_Fails_The_Request_It_Falls_Back_And_Says_Why(JudgeFailure failure, string reason)
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => throw new RoutingJudgeException(failure, "details that must not leak", 503);

        var decision = await rig.Router.DecideAsync(Input(NewTurn("a")), default);

        Assert.Equal((RoutingSources.Fallback, "mid-model", reason), (decision.Source, decision.Model, decision.FallbackReason));
        Assert.Equal(JudgeProtocols.TypeSafe, decision.JudgeProtocol); // the call was made
        Assert.Null(decision.Confidence);
        Assert.Equal(0, rig.Router.PinCount);
    }

    [Fact]
    public async Task An_Unexpected_Exception_In_The_Judge_Is_Also_Only_A_Fallback()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => throw new InvalidOperationException("a bug");

        var decision = await rig.Router.DecideAsync(Input(NewTurn("a")), default);

        Assert.Equal((RoutingSources.Fallback, "error"), (decision.Source, decision.FallbackReason));
    }

    [Fact]
    public async Task A_Judge_That_Keeps_Failing_Is_Left_Alone_For_A_While_Then_Tried_Again()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => throw new RoutingJudgeException(JudgeFailure.Timeout, "slow");

        for (var i = 0; i < 3; i++)
            Assert.Equal("timeout", (await rig.Router.DecideAsync(Input(NewTurn($"t{i}"), token: $"tok{i}"), default)).FallbackReason);
        Assert.True(rig.Router.BreakerOpen);

        var skipped = await rig.Router.DecideAsync(Input(NewTurn("t3"), token: "tok3"), default);
        Assert.Equal((RoutingSources.Breaker, "breaker"), (skipped.Source, skipped.FallbackReason));
        Assert.Equal(3, rig.Judge.Asked.Count); // no fourth call

        rig.Clock.Now += TimeSpan.FromSeconds(31);
        Assert.False(rig.Router.BreakerOpen);
        // The first call after the cooldown is a probe: one more failure closes the door again at once.
        Assert.Equal("timeout", (await rig.Router.DecideAsync(Input(NewTurn("t4"), token: "tok4"), default)).FallbackReason);
        Assert.True(rig.Router.BreakerOpen);
        Assert.Equal(4, rig.Judge.Asked.Count);

        rig.Clock.Now += TimeSpan.FromSeconds(31);
        rig.Judge.Answer = _ => Verdict("light");
        Assert.Equal(RoutingSources.Judge, (await rig.Router.DecideAsync(Input(NewTurn("t5"), token: "tok5"), default)).Source);
        Assert.False(rig.Router.BreakerOpen);
        // Healthy again: failures start counting from zero.
        rig.Judge.Answer = _ => throw new RoutingJudgeException(JudgeFailure.Timeout, "slow");
        await rig.Router.DecideAsync(Input(NewTurn("t6"), token: "tok6"), default);
        Assert.False(rig.Router.BreakerOpen);
    }

    [Fact]
    public async Task A_Request_With_No_User_Text_Falls_Back_Without_Asking_The_Judge()
    {
        var rig = await StartAsync();
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = "hello" }) };

        var decision = await rig.Router.DecideAsync(new RoutingInput("t1", ApiProtocol.Anthropic, body, NewTurn("a")), default);

        Assert.Equal((RoutingSources.Fallback, "no_text", "mid-model"), (decision.Source, decision.FallbackReason, decision.Model));
        Assert.Empty(rig.Judge.Asked);
    }

    [Fact]
    public async Task A_Key_That_Cannot_Be_Read_Back_Falls_Back_Without_Calling_The_Judge()
    {
        var rig = await StartAsync();
        var broken = new SmartRouter(rig.Service, new RoutingJudgeRegistry([rig.Judge]), new ThrowingProtector(), NullLogger<SmartRouter>.Instance, rig.Clock);

        var decision = await broken.DecideAsync(Input(NewTurn("a")), default);

        Assert.Equal((RoutingSources.Fallback, "not_configured"), (decision.Source, decision.FallbackReason));
        Assert.Empty(rig.Judge.Asked);
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => throw new CryptographicException("the key ring changed");
    }

    // ---------------------------------------------------------------- what the judge is asked

    [Fact]
    public async Task The_Judge_Is_Asked_With_The_Settings_The_Grades_And_The_Users_Words()
    {
        var rig = await StartAsync();
        var body = new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject(), new JsonObject()),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Add a login test" },
                new JsonObject { ["role"] = "assistant", ["content"] = "Done" },
                new JsonObject { ["role"] = "user", ["content"] = "Fix the flaky login test" }),
        };

        await rig.Router.DecideAsync(new RoutingInput("t1", ApiProtocol.Anthropic, body, NewTurn("a")), default);

        var endpoint = Assert.Single(rig.Judge.Endpoints);
        Assert.Equal(("https://api.typesafe.ai", "sk-judge", "jev-latest", TimeSpan.FromSeconds(2)), (endpoint.BaseUrl, endpoint.ApiKey, endpoint.Model, endpoint.Timeout));
        var question = Assert.Single(rig.Judge.Asked);
        Assert.Equal(SmartRoutingSettings.DefaultInstructions, question.Instructions);
        Assert.Equal(["light", "standard", "heavy"], question.Choices.Select(c => c.Id)); // cheapest first
        Assert.Equal(rig.Service.Current.Grades.Select(g => g.Description), question.Choices.Select(c => c.Description));
        Assert.Equal("Fix the flaky login test", question.State.Request);
        Assert.Equal(["Add a login test"], question.State.Earlier);
        Assert.Equal(new JudgeShape(3, 2, false), question.State.Shape);
    }

    [Fact]
    public async Task Custom_Settings_Reach_The_Judge_As_They_Are()
    {
        var rig = await StartAsync(s =>
        {
            s.Judge.BaseUrl = "https://openrouter.ai/api";
            s.Judge.Model = "~typesafe/jev-latest";
            s.Judge.TimeoutMs = 1500;
            s.Judge.Instructions = "Pick the grade.";
            s.Rules.ContextTurns = 0;
        });

        await rig.Router.DecideAsync(Input(NewTurn("a")), default);

        var endpoint = Assert.Single(rig.Judge.Endpoints);
        Assert.Equal(("https://openrouter.ai/api", "~typesafe/jev-latest", TimeSpan.FromMilliseconds(1500)), (endpoint.BaseUrl, endpoint.Model, endpoint.Timeout));
        var question = Assert.Single(rig.Judge.Asked);
        Assert.Equal("Pick the grade.", question.Instructions);
        Assert.Empty(question.State.Earlier);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Concurrent_First_Requests_Of_One_Session_Share_One_Judgement()
    {
        var rig = await StartAsync();
        var release = new TaskCompletionSource();
        rig.Judge.Gate = () => release.Task;

        var first = rig.Router.DecideAsync(Input(NewTurn("a")), default);
        var second = rig.Router.DecideAsync(Input(NewTurn("b"), "another request"), default); // same session, another turn
        Assert.Single(rig.Judge.Asked);
        release.SetResult();
        var both = await Task.WhenAll(first, second);

        Assert.Single(rig.Judge.Asked);
        Assert.All(both, d => Assert.Equal("big-model", d.Model));
    }

    [Fact]
    public async Task A_Client_That_Goes_Away_Does_Not_Take_The_Judgement_Down_With_It()
    {
        var rig = await StartAsync();
        var release = new TaskCompletionSource();
        rig.Judge.Gate = () => release.Task;
        using var gone = new CancellationTokenSource();

        var leaving = rig.Router.DecideAsync(Input(NewTurn("a")), gone.Token);
        var staying = rig.Router.DecideAsync(Input(NewTurn("b")), default);
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        release.SetResult();

        var decision = await staying;
        Assert.Equal("big-model", decision.Model);
        Assert.Single(rig.Judge.Asked);
    }

    // ---------------------------------------------------------------- grades the caller cannot reach

    /// <summary>A request whose caller can use only the grades whose models are <paramref name="served"/>; for the others the grade's provider "serves no such model".</summary>
    private static RoutingInput Reaching(TraceLink link, string text, params string[] served) =>
        Input(link, text) with
        {
            Caller = "tkcode",
            ProblemOf = (grade, _) => Task.FromResult(served.Contains(grade.Model) ? null : new GradeProblem(GradeProblems.ModelNotServed, "Models")),
        };

    [Fact]
    public async Task A_Grade_Whose_Model_The_Caller_Cannot_Reach_Gives_Way_To_The_Nearest_Stronger_One()
    {
        var rig = await StartAsync();
        rig.Judge.Answer = _ => Verdict("light");

        var moved = await rig.Router.DecideAsync(Reaching(NewTurn("a"), "1+1", "mid-model", "big-model"), default);

        Assert.Equal(("standard", "mid-model", "light", GradeProblems.ModelNotServed), (moved.GradeId, moved.Model, moved.AdjustedFrom, moved.AdjustedReason));
        Assert.Equal(RoutingSources.Judge, moved.Source);
        var problem = Assert.Single(rig.Router.Activity.Unreachable);
        Assert.Equal(("tkcode", "light", "small-model", "standard", 1), (problem.Caller, problem.Grade, problem.Model, problem.UsedGrade, problem.Count));
        Assert.Equal((GradeProblems.ModelNotServed, "Models"), (problem.Reason, problem.ProviderName)); // what the console explains
    }

    [Fact]
    public async Task A_Decision_Names_The_Provider_Its_Grade_Was_Given_And_A_Moved_One_The_New_Grades()
    {
        var rig = await StartAsync();
        var elsewhere = await rig.Store.AddProviderAsync("Elsewhere", "mid-model");
        await rig.Service.UpdateAsync(s => s.Grades[1].ProviderId = elsewhere.Id);

        var judged = await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        rig.Judge.Answer = _ => Verdict("light");
        var moved = await rig.Router.DecideAsync(Reaching(NewTurn("b", session: "s2"), "1+1", "mid-model"), default);

        Assert.Equal(rig.ProviderId, judged.ProviderId); // filled in for a grade saved without one
        Assert.Equal(("standard", elsewhere.Id), (moved.GradeId, moved.ProviderId));
    }

    [Fact]
    public async Task Without_A_Stronger_Grade_In_Reach_The_Nearest_Weaker_One_Is_Used()
    {
        var rig = await StartAsync();

        var moved = await rig.Router.DecideAsync(Reaching(NewTurn("a"), "redesign billing", "small-model", "mid-model"), default);

        Assert.Equal(("standard", "heavy"), (moved.GradeId, moved.AdjustedFrom));
    }

    [Fact]
    public async Task A_Caller_That_Reaches_None_Of_The_Grades_Models_Keeps_The_Decision()
    {
        var rig = await StartAsync();

        var kept = await rig.Router.DecideAsync(Reaching(NewTurn("a"), "redesign billing"), default);

        Assert.Equal(("heavy", "big-model"), (kept.GradeId, kept.Model));
        Assert.Null(kept.AdjustedFrom);
        // Nowhere to go, but not silent: the console still says this caller cannot use the grade, and why.
        var problem = Assert.Single(rig.Router.Activity.Unreachable);
        Assert.Equal(("heavy", null, GradeProblems.ModelNotServed), (problem.Grade, problem.UsedGrade, problem.Reason));
    }

    [Fact]
    public async Task The_Session_Keeps_The_Judged_Grade_So_A_Provider_Bound_Later_Counts_At_Once()
    {
        var rig = await StartAsync();

        var before = await rig.Router.DecideAsync(Reaching(NewTurn("a"), "redesign billing", "mid-model"), default);
        var after = await rig.Router.DecideAsync(Reaching(NewTurn("b"), "and the docs", "mid-model", "big-model"), default);

        Assert.Equal(("mid-model", "heavy"), (before.Model, before.AdjustedFrom));
        Assert.Equal(("big-model", RoutingSources.Pin), (after.Model, after.Source));
        Assert.Null(after.AdjustedFrom);
        Assert.Empty(rig.Router.Activity.Unreachable); // reaching the grade ends the problem reported for it
        Assert.Single(rig.Judge.Asked);
    }

    // ---------------------------------------------------------------- what the console is shown

    [Fact]
    public async Task The_Activity_Holds_Each_Fresh_Decision_Once_And_Starts_Afresh_With_New_Settings()
    {
        var rig = await StartAsync();
        rig.Judge.Connects = true;
        await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        await rig.Router.DecideAsync(Input(NewTurn("b")), default); // the pin: no decision of its own
        rig.Judge.Answer = _ => throw new RoutingJudgeException(JudgeFailure.Timeout, "slow");
        await rig.Router.DecideAsync(Input(NewTurn("c", session: "s2")), default);

        var decisions = rig.Router.Activity.Decisions;
        Assert.Equal([RoutingSources.Judge, "timeout"], decisions.Select(d => d.Outcome));
        Assert.All(decisions, d => Assert.True(d.NewConnection));

        await rig.Service.UpdateAsync(s => s.Rules.MinConfidence = 0.5);
        Assert.Empty(rig.Router.Activity.Decisions);
    }

    [Fact]
    public async Task A_Judgement_That_Had_To_Open_A_Connection_Says_So_On_Its_Decision_Only()
    {
        var rig = await StartAsync();
        rig.Judge.Connects = true;

        var first = await rig.Router.DecideAsync(Input(NewTurn("a")), default);
        var pinned = await rig.Router.DecideAsync(Input(NewTurn("b")), default);

        Assert.True(first.JudgeNewConnection);
        Assert.False(pinned.JudgeNewConnection);
    }

    // ---------------------------------------------------------------- the fallback model alone

    [Fact]
    public async Task The_Fallback_Model_Needs_No_Judgement_And_Follows_The_Settings()
    {
        var rig = await StartAsync();

        var routed = rig.Router.FallbackModel("astra-auto");

        Assert.Equal(new RoutedModel("astra-auto", "mid-model", "standard", rig.ProviderId), routed);
        Assert.Empty(rig.Judge.Asked);

        await rig.Service.UpdateAsync(s => s.Rules.FallbackGrade = "light");
        Assert.Equal("small-model", rig.Router.FallbackModel("astra-auto").ModelId);
    }
}
