using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Tests.Traces;

/// <summary>
/// Ported assertions of <c>web/src/test/traces.test.tsx</c> (the "trace layouts" block) plus the cases the web test does not
/// pin: errors, streaming, clock skew, empty traces, inspectors, routes and the page logic. Complements the golden cases.
/// </summary>
[Collection("Loc")]
public sealed class TraceModelTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public TraceModelTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    private static readonly TraceText T = TraceTexts.FromLoc;
    private static string Label(string? kind) => kind == "claude-code" ? "Claude Code" : kind ?? "";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-14T09:00:00.000Z");
    private static readonly double T0Ms = T0.ToUnixTimeMilliseconds();
    private static readonly Dictionary<string, ProviderBrand> NoBrands = [];
    private static readonly double Now = T0.AddSeconds(10).ToUnixTimeMilliseconds();

    private static TraceCallDto Call(string id, long startMs, long? totalMs, string status = "success", int toolCalls = 0, string callKind = "main",
        string? parent = null, int step = 1, string linkSource = "new_turn", string? chainKey = "fp:main", string? providerId = "copilot", int? httpStatus = 200) =>
        new(id, T0.AddMilliseconds(startMs), "claude-code", providerId, "GitHub Copilot", null, null, "anthropic", "openai-chat", false, true,
            "claude-sonnet-5-5", "claude-sonnet-5.5", null, status, httpStatus, null, null, 300, null, totalMs, null, null, 1000, 50, 0, 0, 1_000_000, null,
            null, null, "cc:abc", chainKey, "m1", parent, step, callKind, linkSource, null, toolCalls, null, null);

    private static SpanDto Span(string req, string name, long startUs, long durUs, string status = "ok", JsonNode? attrs = null, JsonNode? events = null,
        string? message = null) =>
        new(req, $"{req}:{name}:{startUs}", name == "gateway.request" ? null : $"{req}:root", name,
            name == "gateway.request" ? "server" : name == "upstream.attempt" ? "client" : "internal", startUs, durUs, status, message, attrs, events);

    private static JsonObject Attrs(params (string, object)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv)
            o[k] = v switch { string s => s, bool b => b, int i => i, double d => d, _ => throw new ArgumentException(k) };
        return o;
    }

    private static JsonArray Events(params (string Name, double Offset)[] ev) =>
        new(ev.Select(e => (JsonNode?)new JsonObject { ["name"] = e.Name, ["offset_us"] = e.Offset }).ToArray());

    private static List<SpanDto> FailoverSpans(string id) =>
    [
        Span(id, "gateway.request", 0, 8_420_000),
        Span(id, "gateway.route", 0, 600),
        Span(id, "gateway.privacy", 700, 2_000, attrs: Attrs(("astra.privacy.hits", 2), ("astra.privacy.restorable", 2))),
        Span(id, "gateway.encode", 3_000, 900, attrs: Attrs(("astra.passthrough", false), ("astra.protocol.upstream", "openai-chat"))),
        Span(id, "upstream.attempt", 5_000, 620_000, "error", Attrs(("http.response.status_code", 429), ("astra.account.id", "gh-alice"), ("astra.attempt.retry", "rate_limited_failover"))),
        Span(id, "upstream.attempt", 625_000, 940_000, attrs: Attrs(("http.response.status_code", 200), ("astra.account.id", "gh-bob"))),
        Span(id, "gateway.response", 1_565_000, 6_850_000,
            attrs: Attrs(("astra.restore.placeholders", 2), ("astra.response.tool_names", "Read,Read,Grep"), ("astra.response.events", 96)),
            events: Events(("first_token", 1_835_000))),
    ];

    private static TraceRequestDto Trace(List<SpanDto> spans, TraceCallDto? request = null, bool inFlight = false) =>
        new(request ?? Call("r1", 0, 8420), inFlight, spans, null, []);

    private static TraceTopologyDto Topology() => new("live", 5,
        new TraceGatewayDto(10, 1, 2, 4_900, 38_000, 1, [new TraceStageDto("gateway.route", 400, 900, 10)]),
        [new TraceClientNodeDto("claude-code", 10, 1, 2, 1)],
        [new TraceProviderNodeDto("copilot", "GitHub Copilot", null, null, 10, 1, 11, 1, 1, 2, 1400,
            [new TraceAccountDto("gh-alice", "gh-alice", "cooldown", true, T0.AddMinutes(2), null, "0", null, null)])]);

    private static RequestSummaryDto Live(string id, string status = "pending", string? providerId = "copilot", string? clientKind = "claude-code",
        string? stage = null, int? httpStatus = null, string? turnId = null, string? phases = null, string? parent = null, long startMs = 9_000, long? totalMs = null) =>
        new(id, T0.AddMilliseconds(startMs), clientKind, providerId, null, "anthropic", null, false, "claude-sonnet-5-5", null, null, null, true, status, httpStatus,
            null, totalMs, null, 0, 0, 0, 0, 0, 0, "missing", null, null, null, null, null, null, null, false, null, "cc:abc", stage, turnId, null, parent, phases);

    private static TraceNode Node(TraceGraph g, string id) => g.Nodes.Single(n => n.Id == id);
    private static TraceEdge Edge(TraceGraph g, string id) => g.Edges.Single(e => e.Id == id);

    // ------------------------------------------------------------------ web "trace layouts"

    [Fact]
    public void Topology_puts_clients_and_providers_around_the_hub_with_warn_colouring()
    {
        var graph = TopologyBuilder.Build(Topology(), T, Label, NoBrands, null, new TopologyOptions(Now));
        Assert.Equal([TopologyBuilder.HubId, "client:claude-code", "provider:copilot"], graph.Nodes.Select(n => n.Id));
        Assert.Equal(NodeKind.Hub, graph.Nodes[0].Kind);
        Assert.Equal(("Claude Code", Tone.Ok, "claude-code"), (Node(graph, "client:claude-code").Card!.Title, Node(graph, "client:claude-code").Card!.Tone!.Value, Node(graph, "client:claude-code").Card!.Client));
        Assert.Equal(("GitHub Copilot", Tone.Warn), (Node(graph, "provider:copilot").Card!.Title, Node(graph, "provider:copilot").Card!.Tone!.Value));
        // one row per account, each with its own line from the hub
        Assert.Equal(NodeKind.Provider, Node(graph, "provider:copilot").Kind);
        Assert.Equal([("a:gh-alice", "cooldown")], Node(graph, "provider:copilot").Rows!.Select(r => (r.Id, r.Status)));
        Assert.Equal([("client:claude-code", TopologyBuilder.HubId, null), (TopologyBuilder.HubId, "provider:copilot", "a:gh-alice")],
            graph.Edges.Select(e => (e.Source, e.Target, e.TargetRow)));
        Assert.Equal(["client", "account"], graph.Edges.Select(e => e.Flow!.Role));
        Assert.All(graph.Edges, e => Assert.Empty(e.Flow!.Requests));
    }

    [Fact]
    public void Topology_puts_requests_in_flight_on_their_lines_and_adds_nodes_the_window_has_not_seen()
    {
        RequestSummaryDto[] live = [Live("a"), Live("b", providerId: "deepseek", clientKind: "codex"), Live("c", status: "success")];
        var graph = TopologyBuilder.Build(Topology(), T, Label, new Dictionary<string, ProviderBrand> { ["deepseek"] = new("DeepSeek") }, live, new TopologyOptions(Now));
        Assert.Contains(graph.Nodes, n => n.Id == "client:codex");
        var ds = Node(graph, "provider:deepseek").Card!;
        Assert.Equal(("DeepSeek", 1), (ds.Title, ds.Live));
        // a finished request still rides its lines (its burst), but only pending ones count as in flight
        Assert.Equal(1, Node(graph, "provider:copilot").Card!.Live);
        Assert.Equal(["a", "c"], Edge(graph, "e:provider:copilot:a:gh-alice").Flow!.Requests);
        // a provider without accounts is one API-key row
        Assert.Equal("a:key", graph.Edges.Single(e => e.Target == "provider:deepseek").TargetRow);
        Assert.True(Node(graph, TopologyBuilder.HubId).Busy);
    }

    [Fact]
    public void Topology_rides_each_request_on_its_account_and_bounces_a_refused_one_back()
    {
        var two = Topology() with
        {
            Providers = [Topology().Providers[0] with
            {
                Accounts = [new TraceAccountDto("main", "Main", "ok", true, null, null, null, null, null, 7), new TraceAccountDto("spare", "Spare", "ok", true, null, null, null, null, null, 3)],
            }],
        };
        var retried = Live("x") with
        {
            Attempts = [new Astra.Core.Requests.RequestAttempt("copilot", null, "main", 429, "rate_limited_failover", 100, 500, null), new Astra.Core.Requests.RequestAttempt("copilot", null, "spare", null, null, 600, null, null)],
        };
        var graph = TopologyBuilder.Build(two, T, Label, NoBrands, [retried], new TopologyOptions(Now));
        var main = graph.Edges.Single(e => e.TargetRow == "a:main").Flow!;
        var spare = graph.Edges.Single(e => e.TargetRow == "a:spare").Flow!;
        Assert.Empty(main.Requests);
        Assert.Equal(["x"], main.Failed);
        Assert.False(main.Retry);
        Assert.Equal(["x"], spare.Requests);
        Assert.Empty(spare.Failed);
        Assert.True(spare.Retry);
        Assert.Equal([("a:main", "fail", 0), ("a:spare", "on", 1)], Node(graph, "provider:copilot").Rows!.Select(r => (r.Id, r.Hit, r.Live)));
        Assert.Equal(new HubNote("Rate limited, next account → Spare", "warn"), Node(graph, TopologyBuilder.HubId).Note);
        // long after, the failure no longer shows
        var later = TopologyBuilder.Build(two, T, Label, NoBrands, [retried], new TopologyOptions(Now + 5_000));
        Assert.Empty(later.Edges.Single(e => e.TargetRow == "a:main").Flow!.Failed);
        Assert.Null(Node(later, TopologyBuilder.HubId).Note);
        // hovering a request in the feed dims what it did not touch (never the hub)
        var lit = TopologyBuilder.Build(two, T, Label, NoBrands, [], new TopologyOptions(Now, new HighlightPath("claude-code", [new Astra.Core.Requests.RequestAttempt("copilot", null, "spare", null, null, 0, null, null)])));
        Assert.Equal([("a:main", true), ("a:spare", false)], Node(lit, "provider:copilot").Rows!.Select(r => (r.Id, r.Dim)));
        Assert.Equal(0.25, lit.Edges.Single(e => e.TargetRow == "a:main").Opacity);
        Assert.Null(lit.Edges.Single(e => e.TargetRow == "a:spare").Opacity);
    }

    [Fact]
    public void Live_phase_is_read_from_the_request_fields()
    {
        Assert.Equal(LivePhase.Arriving, TraceAnalysis.LivePhaseOf("pending", null, null));
        Assert.Equal(LivePhase.Upstream, TraceAnalysis.LivePhaseOf("pending", "p", null));
        Assert.Equal(LivePhase.Streaming, TraceAnalysis.LivePhaseOf("pending", "p", 200));
        Assert.Equal(LivePhase.Done, TraceAnalysis.LivePhaseOf("success", "p", 200));
        Assert.Equal(LivePhase.Failed, TraceAnalysis.LivePhaseOf("upstream_error", "p", 429));
    }

    [Fact]
    public void A_failover_is_a_fork_of_attempts_and_the_response_leg_mirrors_it()
    {
        var graph = RequestGraphBuilder.Build(Trace(FailoverSpans("r1")), T, Label, new ProviderBrand("GitHub Copilot"));
        var ids = graph.Nodes.Select(n => n.Id).ToList();
        foreach (var id in new[] { "agent", "route", "privacy", "encode", "attempt:0", "attempt:1", "provider", "decode", "reencode", "restore" }) Assert.Contains(id, ids);
        Assert.DoesNotContain("middleware", ids);
        Assert.DoesNotContain("routing", ids);
        var failed = Edge(graph, "e:attempt:0");
        Assert.True(failed.Dashed);
        Assert.Equal("429", failed.Label);
        Assert.Equal(Tone.Error, Node(graph, "attempt:0").Card!.Tone);
        Assert.Contains(graph.Edges, e => e.Target == "agent" && e.TargetHandle == Handle.Bottom);
    }

    [Fact]
    public void Each_provider_of_a_pool_failover_gets_its_own_node_the_answering_one_last()
    {
        var spans = FailoverSpans("r1").Select(s => s.Name != "upstream.attempt" ? s
            : s.StartUs == 5_000
                ? s with { Attributes = Attrs(("http.response.status_code", 429), ("astra.provider.id", "relay"), ("astra.provider.name", "Relay"), ("astra.attempt.retry", "provider_failover")) }
                : s with { Attributes = Attrs(("http.response.status_code", 200), ("astra.provider.id", "copilot"), ("astra.provider.name", "GitHub Copilot")) }).ToList();
        var graph = RequestGraphBuilder.Build(Trace(spans), T, Label, new ProviderBrand("GitHub Copilot"));
        var relay = Node(graph, "provider:relay").Card!;
        Assert.Equal(("Relay", Tone.Error), (relay.Title, relay.Tone!.Value));
        Assert.Equal("provider:relay", Edge(graph, "e:attempt:0").Target);
        Assert.Equal("provider", Edge(graph, "e:attempt:1").Target);
        Assert.Equal("Relay", Node(graph, "attempt:0").Card!.Lines[0]);
    }

    [Fact]
    public void A_request_splits_into_astra_retries_first_byte_first_token_and_generation()
    {
        var split = TraceAnalysis.SplitRequestTime(FailoverSpans("r1"))!;
        Assert.Equal(5_000, split.AstraUs);
        Assert.Equal(620_000, split.RetryUs);
        Assert.Equal(940_000, split.TtfbUs);
        Assert.Equal(270_000, split.FirstTokenUs);
        Assert.Equal(6_580_000, split.GenerationUs);
        Assert.Equal(8_420_000, split.TotalUs);
        Assert.Equal(0, split.RoutingUs);
    }

    [Fact]
    public void A_judged_request_gets_a_routing_node_and_the_judge_is_taken_out_of_resolve()
    {
        var spans = new List<SpanDto>
        {
            Span("r1", "gateway.request", 0, 5_000_000), Span("r1", "gateway.privacy", 700, 2_000),
            Span("r1", "gateway.resolve", 3_000, 2_030_000, attrs: Attrs(("astra.provider.name", "Grok"), ("astra.route.mode", "session"), ("astra.route.source", "judge"),
                ("astra.route.grade", "standard"), ("astra.route.judge.ms", 2010), ("astra.route.judge.new_connection", true))),
            Span("r1", "upstream.attempt", 2_050_000, 2_000_000, attrs: Attrs(("http.response.status_code", 200))),
        };
        var graph = RequestGraphBuilder.Build(Trace(spans, Call("r1", 0, 5000)), T, Label, new ProviderBrand("Grok"));
        var order = graph.Nodes.Select(n => n.Id).ToList();
        Assert.True(order.IndexOf("routing") > order.IndexOf("privacy"));
        Assert.True(order.IndexOf("routing") < order.IndexOf("resolve"));
        var routing = Node(graph, "routing").Card!;
        Assert.Equal(("Smart routing", Tone.Ok), (routing.Title, routing.Tone!.Value));
        Assert.Equal(["standard", "Judged · 2.01 s"], routing.Lines);
        Assert.Equal(["Grok", "20 ms"], Node(graph, "resolve").Card!.Lines);
        var split = TraceAnalysis.SplitRequestTime(spans)!;
        Assert.Equal(2_010_000, split.RoutingUs);
        Assert.Equal(2_050_000, split.AstraUs);
    }

    [Fact]
    public void The_grade_is_mentioned_on_a_session_call_that_was_smart_routed()
    {
        var spans = new List<SpanDto> { Span("m1", "gateway.resolve", 0, 10, attrs: Attrs(("astra.route.source", "pin"), ("astra.route.grade", "light"))) };
        var graph = SessionBuilder.Build([Call("m1", 0, 1000)], spans, T, NoBrands);
        Assert.Contains("Smart routing · light", Node(graph, "m1").Card!.Lines);
    }

    [Fact]
    public void Tools_between_two_calls_label_the_line_joining_them_and_sub_agents_hang_underneath()
    {
        TraceCallDto[] calls =
        [
            Call("m1", 0, 3_000, toolCalls: 1),
            Call("s1", 3_200, 2_000, callKind: "sidechain", chainKey: "fp:sub", parent: "m1", linkSource: "inferred", providerId: "anthropic"),
            Call("m2", 6_000, 2_000, parent: "m1", step: 2, linkSource: "tool_call"),
        ];
        var spans = FailoverSpans("m2").Append(Span("m1", "gateway.response", 0, 10, attrs: Attrs(("astra.response.tool_names", "Task")))).ToList();
        var graph = SessionBuilder.Build(calls, spans, T, NoBrands);
        Assert.Equal(["m1", "m2", "s1"], graph.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal));
        var step = graph.Edges.Single(e => e.Source == "m1" && e.Target == "m2");
        Assert.Equal(EdgeType.Step, step.Type);
        Assert.Equal("Task · 3000 ms", step.Step!.Label);
        Assert.Equal("tool:m2", step.Step.SelectId);
        Assert.Contains(graph.Edges, e => e.Source == "m1" && e.Target == "s1");
        Assert.True(Node(graph, "s1").Y > Node(graph, "m1").Y);
        // two attempts: orange, flagged, and the turn's only anomaly
        var m2 = Node(graph, "m2").Card!;
        Assert.Equal(Tone.Warn, m2.Tone);
        Assert.Equal(new CardFlag(T("traces.flag.retry"), Tone.Warn), m2.Flag);
        Assert.Equal(["m2"], graph.Anomalies);

        var tl = TimelineBuilder.Build(calls, spans, T, null, T0Ms);
        Assert.Equal(8_000, tl.TotalMs);
        Assert.Equal([TimelineRowKind.Turn, TimelineRowKind.Llm, TimelineRowKind.Sub, TimelineRowKind.Tool, TimelineRowKind.Llm, TimelineRowKind.AttemptFail, TimelineRowKind.AttemptOk],
            tl.Rows.Select(r => r.Kind));
        Assert.Equal(T("traces.session.toolRow", ("names", "Task")), tl.Rows.Single(r => r.Kind == TimelineRowKind.Tool).Label);
        var row = tl.Rows.Single(r => r.Id == "m2");
        Assert.Equal(Anomaly.Retry, row.Flag);
        // waiting until the first token (1.835 s into the call), on the turn's axis
        Assert.Equal([new PhaseSegment(PhaseKind.Wait, 6_000, 7_835)], row.Segments);

        var time = TraceAnalysis.SplitTurnTime(calls, spans);
        Assert.Equal(8_000, time.TotalMs);
        Assert.Equal(1_000, time.ToolMs); // 5.2 to 6.0 s uncovered
        Assert.Equal(620, time.RetryMs);
    }

    private static List<TraceCallDto> Mains(int n, Func<int, string>? status = null) =>
        Enumerable.Range(0, n).Select(i => Call($"m{i}", i * 4_000, 3_000, status?.Invoke(i) ?? "success", toolCalls: 1, step: i + 1)).ToList();

    [Fact]
    public void A_long_main_chain_wraps_into_rows_and_the_wrapping_step_runs_back_along_the_gap()
    {
        var graph = SessionBuilder.Build(Mains(6), [], T, NoBrands, new SessionOptions(4, true, new HashSet<string>()));
        Assert.Equal(Node(graph, "m0").Y, Node(graph, "m3").Y);
        Assert.Equal(Node(graph, "m0").X, Node(graph, "m4").X);
        Assert.True(Node(graph, "m4").Y > Node(graph, "m3").Y);
        Assert.Null(graph.Edges.Single(e => e.Source == "m0" && e.Target == "m1").Step!.Wrap);
        var wrap = graph.Edges.Single(e => e.Source == "m3" && e.Target == "m4").Step!.Wrap!;
        Assert.InRange(wrap.GapY, Node(graph, "m3").Y + 0.001, Node(graph, "m4").Y - 0.001);
        Assert.True(wrap.RightX > Node(graph, "m3").X);
        Assert.True(wrap.LeftX < Node(graph, "m4").X);
    }

    [Fact]
    public void Unremarkable_runs_of_a_long_turn_fold_and_the_ends_and_anomalies_stay()
    {
        var quiet = SessionBuilder.Build(Mains(12), [], T, NoBrands);
        Assert.Equal(["m0", "group:m1", "m10", "m11"], quiet.Nodes.Select(n => n.Id));
        Assert.True(quiet.Nodes[1].Card!.Group);
        Assert.Equal("LLM #2–#10", quiet.Nodes[1].Card!.Title);
        Assert.Equal([("m0", "group:m1"), ("group:m1", "m10"), ("m10", "m11")], quiet.Edges.Select(e => (e.Source, e.Target)));

        var failing = SessionBuilder.Build(Mains(12, i => i == 5 ? "upstream_error" : "success"), [], T, NoBrands);
        Assert.Equal(["m0", "group:m1", "m5", "group:m6", "m10", "m11"], failing.Nodes.Select(n => n.Id));
        Assert.Equal(["m5"], failing.Anomalies);
        Assert.Equal((Tone.Error, Tone.Error), (Node(failing, "m5").Card!.Tone!.Value, Node(failing, "m5").Card!.Flag!.Tone));

        var opened = SessionBuilder.Build(Mains(12), [], T, NoBrands, new SessionOptions(4, true, new HashSet<string> { "group:m1" }));
        Assert.Equal(12, opened.Nodes.Count);
        Assert.Equal(8, SessionBuilder.Build(Mains(8), [], T, NoBrands).Nodes.Count); // a short turn never folds
    }

    [Fact]
    public void Calls_in_flight_are_drawn_after_the_last_step_with_a_moving_line_and_a_growing_bar()
    {
        var calls = Mains(4);
        var spans = new List<SpanDto> { Span("m3", "gateway.response", 0, 10, attrs: Attrs(("astra.response.tool_names", "Bash"))) };
        RequestSummaryDto[] pending = [Live("p1"), Live("p2"), Live("m0")];
        var graph = SessionBuilder.Build(calls, spans, T, NoBrands, new SessionOptions(4, true, new HashSet<string>(), pending));
        // m0 is stored already: only the two others are drawn, stacked in the next slot (a new row here)
        var p1 = Node(graph, "p1");
        var p2 = Node(graph, "p2");
        Assert.Single(graph.Nodes, n => n.Id == "m0");
        Assert.Equal(("p1", "LLM · sonnet-5-5"), (p1.Card!.Pending, p1.Card.Title));
        Assert.Equal(Node(graph, "m0").X, p1.X);
        Assert.True(p2.Y > p1.Y);
        var edge = graph.Edges.Single(e => e.Target == "p1");
        Assert.Equal(("m3", true, "Bash"), (edge.Source, edge.Animated, edge.Step!.Tools));
        Assert.NotNull(edge.Step.Wrap);

        var tl = TimelineBuilder.Build(calls, spans, T, [Live("p1")], T0Ms + 20_000);
        Assert.Equal(20_000, tl.TotalMs);
        Assert.Equal(("p1", TimelineRowKind.Live, 9_000, 20_000), (tl.Rows[^1].Id, tl.Rows[^1].Kind, tl.Rows[^1].StartMs, tl.Rows[^1].EndMs));
    }

    [Fact]
    public void A_call_splits_into_waiting_thinking_writing_and_tool_calls_from_the_response_events()
    {
        var ev = new JsonArray(
            Ev("reasoning_start", 100_000), Ev("reasoning_end", 2_100_000), Ev("text_start", 2_200_000), Ev("text_end", 3_000_000),
            Ev("tool_call", 3_100_000, "Read"), Ev("tool_call", 3_200_000, "Grep"));
        var spans = new List<SpanDto> { Span("c", "gateway.response", 50_000, 3_300_000, events: ev) };
        var p = TraceAnalysis.CallPhasesOf(spans)!;
        Assert.Equal((100, 2_000, 800, 250), (p.WaitMs, p.ThinkMs, p.WriteMs, p.ToolMs));
        Assert.Equal(["Read", "Grep"], p.Tools);
        Assert.Equal([PhaseKind.Wait, PhaseKind.Think, PhaseKind.Write, PhaseKind.Tool], p.Segments.Select(s => s.Kind));
        Assert.Equal("Thinking 2000 ms · Writing 800 ms · Writing tool calls 250 ms", TraceAnalysis.PhaseLine(p, T));
        Assert.Null(TraceAnalysis.CallPhasesOf([]));
        var sums = TraceAnalysis.TurnPhases([Call("c", 0, 3_400)], spans);
        Assert.Equal(new PhaseSums(100, 2_000, 800, 250), sums);
    }

    private static JsonNode? Ev(string name, double offset, string? tool = null)
    {
        var o = new JsonObject { ["name"] = name, ["offset_us"] = offset };
        if (tool is not null) o["attributes"] = new JsonObject { ["name"] = tool };
        return o;
    }

    [Fact]
    public void A_call_in_flight_is_coloured_by_the_stages_the_live_feed_reports()
    {
        var r = Live("x", startMs: 0, phases: "thinking@1500,writing@6000,tool:Bash@9000");
        Assert.Equal([new PhaseSegment(PhaseKind.Wait, 0, 1_500), new PhaseSegment(PhaseKind.Think, 1_500, 6_000), new PhaseSegment(PhaseKind.Write, 6_000, 9_000),
            new PhaseSegment(PhaseKind.Tool, 9_000, 10_000)], TraceAnalysis.LivePhases(r, T0Ms + 10_000));
        Assert.Equal([new PhaseSegment(PhaseKind.Wait, 0, 2_000)], TraceAnalysis.LivePhases(Live("y", startMs: 0), T0Ms + 2_000));
    }

    [Fact]
    public void The_agent_status_is_the_live_stage_else_what_the_last_call_left_it_doing()
    {
        Assert.Equal(AgentStatusKind.Thinking, TraceAnalysis.AgentStatusOf(Live("x", stage: "thinking", httpStatus: 200, startMs: 10_000), [], [])!.Kind);
        var tool = TraceAnalysis.AgentStatusOf(Live("x", stage: "tool:Bash", httpStatus: 200), [], [])!;
        Assert.Equal((AgentStatusKind.Tool, "Bash"), (tool.Kind, tool.Tool));
        Assert.Equal(AgentStatusKind.Upstream, TraceAnalysis.AgentStatusOf(Live("x"), [], [])!.Kind);
        Assert.Equal(AgentStatusKind.Waiting, TraceAnalysis.AgentStatusOf(Live("x", httpStatus: 200), [], [])!.Kind);

        var toolSpans = new List<SpanDto> { Span("m1", "gateway.response", 0, 10, attrs: Attrs(("astra.response.tool_names", "Read,Read"))) };
        Assert.Equal(new AgentStatus(AgentStatusKind.Tools, T0Ms + 3_000, Tools: "Read×2"), TraceAnalysis.AgentStatusOf(null, [Call("m1", 0, 3_000, toolCalls: 2)], toolSpans));
        Assert.Equal(new AgentStatus(AgentStatusKind.Idle, T0Ms + 3_000), TraceAnalysis.AgentStatusOf(null, [Call("m1", 0, 3_000)], []));
        Assert.Equal(AgentStatusKind.Failed, TraceAnalysis.AgentStatusOf(null, [Call("m1", 0, 3_000, "upstream_error")], [])!.Kind);
        Assert.Null(TraceAnalysis.AgentStatusOf(null, [], []));
    }

    // ------------------------------------------------------------------ beyond the web test

    [Fact]
    public void An_empty_trace_has_no_split_graph_beyond_the_agent_and_provider_and_no_time_bar()
    {
        var graph = RequestGraphBuilder.Build(Trace([]), T, Label, null);
        Assert.Equal(["agent", "provider"], graph.Nodes.Select(n => n.Id));
        Assert.Empty(graph.Edges);
        Assert.Null(TraceAnalysis.SplitRequestTime([]));
        Assert.Null(TimeBarBuilder.ForRequest([], T));
        Assert.Equal(Timeline.Empty, TimelineBuilder.Build([], [], T, null, T0Ms));
        Assert.Empty(SessionBuilder.Build([], [], T, NoBrands).Nodes);
        Assert.Equal(new TurnTime(0, 0, 0, 0, 0), TraceAnalysis.SplitTurnTime([], []));
    }

    [Fact]
    public void A_request_with_stages_but_no_attempt_links_the_last_stage_to_the_provider()
    {
        var spans = new List<SpanDto> { Span("r1", "gateway.request", 0, 50_000), Span("r1", "gateway.route", 0, 600), Span("r1", "gateway.privacy", 700, 2_000) };
        var graph = RequestGraphBuilder.Build(Trace(spans, Call("r1", 0, null, "pending"), true), T, Label, null);
        Assert.Contains(graph.Edges, e => e.Id == "e:req:provider" && e.Source == "privacy");
        Assert.Equal(Tone.Error, Node(graph, "provider").Card!.Tone); // anything but success
    }

    [Fact]
    public void An_errored_span_turns_its_node_red_and_the_entry_inspector_shows_the_message()
    {
        var spans = new List<SpanDto>
        {
            Span("r1", "gateway.request", 0, 400_000, "error"),
            Span("r1", "gateway.route", 0, 600, "error", message: "bad token"),
            Span("r1", "upstream.attempt", 5_000, 300_000, "error", Attrs(("http.response.status_code", 500)), message: "boom"),
        };
        var trace = Trace(spans, Call("r1", 0, 400, "upstream_error", httpStatus: 500) with { ErrorMessage = "upstream 500" });
        var graph = RequestGraphBuilder.Build(trace, T, Label, null);
        Assert.Equal(Tone.Error, Node(graph, "route").Card!.Tone);
        var entry = Inspectors.ForRequestNode("route", trace, T, d => d.ToString("u"), Label, null)!;
        Assert.Equal(["upstream 500"], entry.Code!.Lines);
        var attempt = Inspectors.ForRequestNode("attempt:0", trace, T, d => "", Label, null)!;
        Assert.Equal(["boom"], attempt.Code!.Lines);
        Assert.Equal("500 · 300 ms", attempt.Sub);
    }

    [Fact]
    public void Streaming_response_splits_generation_by_phase_and_reencode_is_skipped_for_passthrough()
    {
        var spans = new List<SpanDto>
        {
            Span("r1", "gateway.request", 0, 9_000_000),
            Span("r1", "gateway.encode", 5_500, 700, attrs: Attrs(("astra.passthrough", true))),
            Span("r1", "upstream.attempt", 7_000, 1_200_000, attrs: Attrs(("http.response.status_code", 200))),
            Span("r1", "gateway.response", 1_207_000, 7_700_000, events: Events(("first_token", 2_000_000), ("reasoning_start", 2_000_000), ("reasoning_end", 4_000_000))),
        };
        var graph = RequestGraphBuilder.Build(Trace(spans), T, Label, null);
        Assert.Contains(graph.Nodes, n => n.Id == "decode");
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "reencode");
        var bar = TimeBarBuilder.ForRequest(spans, T)!;
        Assert.Contains(bar.Parts, p => p.Label == "Thinking" && p.Swatch == TraceSwatch.PhaseThink && p.Value == 2_000_000);
        Assert.DoesNotContain(bar.Parts, p => p.Label == T("traces.request.time.generation"));
    }

    [Fact]
    public void Clock_skew_never_produces_negative_phases_or_a_negative_wait()
    {
        var spans = new List<SpanDto>
        {
            Span("r1", "gateway.request", 0, 1_000_000),
            Span("r1", "upstream.attempt", 900_000, 300_000, attrs: Attrs(("http.response.status_code", 200))),
            Span("r1", "upstream.attempt", 100_000, -50_000, "error", Attrs(("http.response.status_code", 502))),
            Span("r1", "gateway.response", 400_000, 200_000, events: Events(("first_token", 50_000))),
        };
        var split = TraceAnalysis.SplitRequestTime(spans)!;
        Assert.All(new[] { split.RetryUs, split.FirstTokenUs, split.GenerationUs, split.TailUs }, v => Assert.True(v >= 0));
        Assert.Equal((100_000, 800_000, 0, 0), (split.AstraUs, split.RetryUs, split.FirstTokenUs, split.GenerationUs));
        var bar = TimeBarBuilder.ForRequest(spans, T)!;
        Assert.All(bar.Parts, p => Assert.True(p.Value >= 0));
        // a call that ends before it starts must not give a negative turn
        var skew = TraceAnalysis.SplitTurnTime([Call("a", 5_000, 2_000), Call("b", 1_000, -500)], []);
        Assert.True(skew.ModelMs >= 0 && skew.ToolMs >= 0);
    }

    [Fact]
    public void Anomalies_flag_errors_retries_and_slow_calls_only_in_turns_of_three_or_more()
    {
        var calls = new[] { Call("a", 0, 1_000), Call("b", 1_500, 1_000), Call("c", 3_000, 1_000), Call("d", 5_000, 9_000) };
        var found = TraceAnalysis.FindAnomalies(calls, []);
        Assert.Equal(Anomaly.Slow, found["d"]);
        Assert.Single(found);
        // under three timed calls nothing is "slow", however long
        Assert.DoesNotContain("z", TraceAnalysis.FindAnomalies(calls.Take(1).Append(Call("z", 6_000, 90_000)).ToList(), []).Keys);
    }

    [Fact]
    public void Formatting_matches_the_web_helpers()
    {
        Assert.Equal(["—", "0.4 ms", "10 ms", "1000 ms", "1.00 s", "1.83 s"],
            new double?[] { null, 400, 10_000, 999_999, 1_000_000, 1_834_567 }.Select(TraceFormat.Us));
        Assert.Equal(["—", "1 ms", "3 ms", "10.0 s"], new double?[] { null, 0.5, 2.5, 10_000 }.Select(TraceFormat.Ms));
        Assert.Equal("12.3K", TraceFormat.Tokens(12_345, true));
        Assert.Equal("1M", TraceFormat.Tokens(999_950, true));
        Assert.Equal("12,345", TraceFormat.Tokens(12_345));
        Assert.Equal("$0.0213", TraceFormat.Usd(0.0213));
        Assert.Equal("$1,234.50", TraceFormat.Usd(1234.5));
        Assert.Equal("Read×2 · Grep", TraceFormat.ToolSummary(["Read", "Read", "Grep"]));
        Assert.Equal("sonnet-5-5", TraceFormat.ShortModel("claude-sonnet-5-5"));
        Assert.Equal(["12 s", "4 min", "2 h"], new double[] { 12_000, 240_000, 7_300_000 }.Select(TraceFormat.Since));
    }

    [Fact]
    public void Span_attribute_reads_follow_the_json_kind()
    {
        var s = Span("r", "x", 0, 1, attrs: Attrs(("n", 5), ("s", "7"), ("b", true), ("empty", ""), ("zero", 0)));
        Assert.Equal(5, SpanAttrs.Num(s, "n"));
        Assert.Null(SpanAttrs.Num(s, "s")); // a numeric string is not a number (typeof v === 'number')
        Assert.Equal("7", SpanAttrs.Str(s, "s"));
        Assert.True(SpanAttrs.IsTrue(s, "b"));
        Assert.False(SpanAttrs.Truthy(s, "empty"));
        Assert.False(SpanAttrs.Truthy(s, "zero"));
        Assert.True(SpanAttrs.Truthy(s, "n"));
        // a parsed tree (JsonElement-backed) reads the same as a built one
        var parsed = Span("r", "x", 0, 1, attrs: JsonNode.Parse("""{"n":5,"f":0.5,"b":false}"""));
        Assert.Equal(0.5, SpanAttrs.Num(parsed, "f"));
        Assert.Equal(5, SpanAttrs.Num(parsed, "n"));
        Assert.False(SpanAttrs.IsTrue(parsed, "b"));
    }

    // ------------------------------------------------------------------ inspectors, routes, page logic

    [Fact]
    public void The_request_inspectors_list_what_each_step_recorded()
    {
        var spans = FailoverSpans("r1").Select(s => s.Name == "gateway.request"
            ? s with { Attributes = Attrs(("astra.request.messages", 42), ("astra.request.tools", 18), ("astra.request.system_chars", 12_000), ("gen_ai.request.max_tokens", 32_000)) } : s).ToList();
        var trace = Trace(spans);
        var agent = Inspectors.ForRequestNode("agent", trace, T, d => "when", Label, null)!;
        Assert.Equal("18", agent.Rows.Single(r => r.Label == T("traces.kv.toolDefs")).Value);
        Assert.Equal(T("traces.unit.chars", ("count", "12,000")), agent.Rows.Single(r => r.Label == T("traces.kv.systemChars")).Value);
        Assert.Equal("Claude Code", agent.Title);
        Assert.Contains("astra.request.messages = 42", agent.Raw!.Lines);

        var decode = Inspectors.ForRequestNode("decode", trace, T, d => "", Label, null)!;
        Assert.Equal("Read×2 · Grep", decode.Rows.Single(r => r.Label == T("traces.kv.toolCalls")).Value);
        Assert.Equal("2", decode.Rows.Single(r => r.Label == T("traces.kv.restored")).Value);

        var attempt = Inspectors.ForRequestNode("attempt:0", trace, T, d => "", Label, null)!;
        Assert.Equal(T("traces.retry.rate_limited_failover"), attempt.Rows.Single(r => r.Label == T("traces.kv.retry")).Value);
        Assert.Null(Inspectors.ForRequestNode("attempt:9", trace, T, d => "", Label, null));
        Assert.Null(Inspectors.ForRequestNode("attempt:x", trace, T, d => "", Label, null));
        Assert.Null(Inspectors.ForRequestNode("nope", trace, T, d => "", Label, null));
    }

    [Fact]
    public void The_topology_inspectors_cover_the_hub_clients_and_providers_with_their_accounts()
    {
        var data = Topology();
        string Fmt(DateTimeOffset d) => "T";
        var hub = Inspectors.ForTopologyNode(TopologyBuilder.HubId, data, T, Fmt, Label, NoBrands)!;
        Assert.Contains(hub.Rows, r => r.Label == T("traces.kv.overheadP95") && r.Value == "38 ms");
        Assert.Contains(hub.Rows, r => r.Label == T("traces.stage.gateway.route") && r.Value == "0.4 ms / 0.9 ms");
        var client = Inspectors.ForTopologyNode("client:claude-code", data, T, Fmt, Label, NoBrands)!;
        Assert.Equal("/traces/sessions?client=claude-code", client.Actions[0].Route.Path);
        var provider = Inspectors.ForTopologyNode("provider:copilot", data, T, Fmt, Label, NoBrands)!;
        var account = Assert.Single(provider.Accounts);
        Assert.Equal(("gh-alice", T("traces.account.cooldown", ("time", "T")) + " · " + T("traces.account.remaining", ("count", "0")), false), (account.Name, account.Text, account.Muted));
        Assert.Null(Inspectors.ForTopologyNode("provider:none", data, T, Fmt, Label, NoBrands));
    }

    [Fact]
    public void The_session_inspector_shows_the_call_the_tool_gap_and_the_turn()
    {
        TraceCallDto[] calls = [Call("m1", 0, 3_000, toolCalls: 1), Call("m2", 4_000, 2_000, parent: "m1", step: 2, linkSource: "tool_call")];
        var spans = new List<SpanDto> { Span("m1", "gateway.response", 0, 10, attrs: Attrs(("astra.response.tool_names", "Read"))) };
        var gap = Inspectors.ForSessionNode("tool:m2", calls, spans, T, NoBrands)!;
        Assert.Equal("Read", gap.Title);
        Assert.Equal("1000 ms", gap.Rows.Single(r => r.Label == T("traces.kv.gap")).Value);
        var call = Inspectors.ForSessionNode("m2", calls, spans, T, NoBrands)!;
        Assert.Equal("LLM #2 · sonnet-5.5", call.Title);
        Assert.Equal("/traces/requests/m2", call.Actions[0].Route.Path);
        Assert.Equal("GitHub Copilot", call.Sub);
        Assert.Equal(T("traces.session.turnRow"), Inspectors.ForSessionNode("turn", calls, spans, T, NoBrands)!.Title);
        Assert.Null(Inspectors.ForSessionNode("zzz", calls, spans, T, NoBrands));
    }

    [Fact]
    public void Routes_and_tabs_follow_the_web_paths()
    {
        Assert.Equal("/traces/sessions/cc%3Aabc?turn=m%201", TraceRoutes.Session("cc:abc", "m 1").Path);
        Assert.Equal("/traces/sessions", TraceRoutes.Sessions().Path);
        Assert.Equal(TraceRoutes.Tab.Sessions, TraceRoutes.TabOf("/traces/sessions/x"));
        Assert.Equal(TraceRoutes.Tab.Requests, TraceRoutes.TabOf("/traces/requests"));
        Assert.Equal(TraceRoutes.Tab.Topology, TraceRoutes.TabOf("/traces"));
        Assert.Equal(["/traces", "/traces/sessions", "/traces/requests"], new[] { TraceRoutes.Tab.Topology, TraceRoutes.Tab.Sessions, TraceRoutes.Tab.Requests }.Select(TraceRoutes.PathOf));
    }

    [Fact]
    public void Session_page_logic_picks_the_live_turn_the_view_and_the_column_count()
    {
        var detail = new TraceSessionDetailDto(
            new TraceSessionDto("cc:abc", "claude-code", T0, T0, 1, 1, 0, 0, 0, 0, [], true),
            [new TraceTurnDto("m1", T0, 1, 0, 0, 1000)], "m1", [Call("m1", 0, 3_000)], []);
        // a call in flight that opened a turn the server does not know yet is shown as that turn
        var state = SessionPageLogic.Resolve(detail, null, [Live("p1", turnId: "p1", stage: "writing", httpStatus: 200)], null);
        Assert.Equal(("p1", "p1", 1), (state.TurnId, state.LiveTurn, state.TurnIndex));
        Assert.Empty(state.Calls);
        Assert.Equal(["p1"], state.Pending.Select(r => r.Id));
        // a picked turn is never replaced by a live one
        Assert.Null(SessionPageLogic.Resolve(detail, "m1", [Live("p1", turnId: "p1")], null).LiveTurn);
        // a long turn opens on the timeline unless a view is asked for
        var longTurn = detail with { Calls = Mains(16) };
        Assert.Equal(SessionView.Timeline, SessionPageLogic.Resolve(longTurn, null, [], null).View);
        Assert.Equal(SessionView.Canvas, SessionPageLogic.Resolve(longTurn, null, [], "canvas").View);
        Assert.Equal(4, SessionPageLogic.Columns(null));
        Assert.Equal(2, SessionPageLogic.Columns(300));
        Assert.Equal(6, SessionPageLogic.Columns(5000));
        Assert.Equal("m1", SessionPageLogic.NextAnomaly(["m1", "m5"], null));
        Assert.Equal("m1", SessionPageLogic.NextAnomaly(["m1", "m5"], "m5"));
        Assert.Null(SessionPageLogic.NextAnomaly([], "x"));
    }

    [Fact]
    public void Live_dots_go_out_come_back_and_burst_where_the_request_ended()
    {
        var now = T0Ms + 20_000;
        Assert.Equal((TopologyPageLogic.DotMode.Hidden, false, 0d), TopologyPageLogic.Dot(Live("a", providerId: null), "provider", now));
        Assert.Equal((TopologyPageLogic.DotMode.Out, true, 1.4), TopologyPageLogic.Dot(Live("a", startMs: 0), "client", now)); // 20 s upstream: slow
        Assert.Equal((TopologyPageLogic.DotMode.Back, false, 0.9), TopologyPageLogic.Dot(Live("a", httpStatus: 200), "client", now));
        Assert.Equal(TopologyPageLogic.DotMode.Burst, TopologyPageLogic.Dot(Live("a", status: "success"), "client", now).Mode);
        Assert.Equal(TopologyPageLogic.DotMode.Hidden, TopologyPageLogic.Dot(Live("a", status: "success"), "provider", now).Mode);
        Assert.Equal(TopologyPageLogic.DotMode.Burst, TopologyPageLogic.Dot(Live("a", status: "upstream_error", httpStatus: 429), "provider", now).Mode);
        Assert.InRange(TopologyPageLogic.DotOffset("request-1"), 0, 0.999);
        Assert.Equal(TopologyPageLogic.DotOffset("request-1"), TopologyPageLogic.DotOffset("request-1"));
    }
}
