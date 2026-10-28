using Astra.Api.Contracts;
using Astra.Core.Requests;
using static Astra.Desktop.Core.Traces.SpanAttrs;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// Pure analysis of a trace's spans and calls: routing decision, time splits, the model's phases, anomalies, in-flight
/// phases and the agent's status. Ported from <c>web/src/components/traces/model.ts</c> (function names in each summary).
/// Span names are <see cref="TraceSpanNames"/> (the web's <c>SPAN</c>); microsecond fields are <see cref="double"/> because
/// event offsets are fractional and the web does all of this in JS numbers.
/// </summary>
public static class TraceAnalysis
{
    /// <summary>Values the web accepts for <c>astra.route.source</c>; others show raw.</summary>
    public static readonly string[] RouteSources = ["judge", "pin", "turn", "fallback", "breaker"];
    /// <summary>Values the web accepts for <c>astra.route.mode</c>.</summary>
    public static readonly string[] RouteModes = ["session", "turn"];
    /// <summary>Fallback reasons the smart-routing page has words for.</summary>
    public static readonly string[] RouteReasons =
    [
        "timeout", "network", "auth", "rate_limited", "overloaded", "bad_request", "bad_response",
        "low_confidence", "breaker", "no_text", "not_configured", "error",
    ];

    /// <summary>Calls of a turn longer than this fold unremarkable runs and (in the page) open on the timeline.</summary>
    public const int CollapseAfter = 8;

    /// <summary>A call's start in unix ms (<c>startMs</c>; <c>Date.parse</c> keeps whole milliseconds).</summary>
    public static double StartMs(TraceCallDto c) => c.StartedAtUtc.ToUnixTimeMilliseconds();

    /// <summary>A call's end in unix ms (<c>endMs</c>).</summary>
    public static double EndMs(TraceCallDto c) => StartMs(c) + (c.TotalMs ?? 0);

    /// <summary>A live request's start in unix ms.</summary>
    public static double StartMs(RequestSummaryDto r) => r.StartedAtUtc.ToUnixTimeMilliseconds();

    /// <summary>The spans of one call, keyed by request id (<c>spansByRequest</c>).</summary>
    public static Dictionary<string, List<SpanDto>> SpansByRequest(IEnumerable<SpanDto> spans)
    {
        var map = new Dictionary<string, List<SpanDto>>(StringComparer.Ordinal);
        foreach (var s in spans)
        {
            if (map.TryGetValue(s.RequestId, out var list)) list.Add(s);
            else map[s.RequestId] = [s];
        }
        return map;
    }

    /// <summary>The attempt spans of a request, earliest first.</summary>
    public static List<SpanDto> Attempts(IEnumerable<SpanDto> spans) =>
        spans.Where(s => s.Name == TraceSpanNames.Attempt).OrderBy(s => s.StartUs).ToList();

    /// <summary>The last span of a name by start time (a provider-pool failover resolves and encodes once per member tried).</summary>
    public static SpanDto? LastByName(IEnumerable<SpanDto> spans, string name) =>
        spans.Where(s => s.Name == name).OrderBy(s => s.StartUs).LastOrDefault();

    // ---------------------------------------------------------------- live

    /// <summary>Where an in-flight request is, from the live feed's fields (<c>livePhase</c>).</summary>
    public static LivePhase LivePhaseOf(string status, string? providerId, int? httpStatus)
    {
        if (status != "pending") return status == "success" ? LivePhase.Done : LivePhase.Failed;
        if (string.IsNullOrEmpty(providerId)) return LivePhase.Arriving; // still being authenticated / routed
        return httpStatus is null ? LivePhase.Upstream : LivePhase.Streaming; // sent, waiting for the first byte
    }

    /// <summary>As <see cref="LivePhaseOf(string,string?,int?)"/> for a feed row.</summary>
    public static LivePhase LivePhaseOf(RequestSummaryDto r) => LivePhaseOf(r.Status, r.ProviderId, r.HttpStatus);

    /// <summary>A call in flight as phase bars (ms from its start), from the feed's stage start times (<c>livePhases</c>).</summary>
    public static List<PhaseSegment> LivePhases(RequestSummaryDto r, double nowMs)
    {
        var end = r.TotalMs ?? Math.Max(0, nowMs - StartMs(r));
        var marks = new List<(string Stage, double At)>();
        foreach (var p in (r.Phases ?? "").Split(','))
        {
            var idx = p.LastIndexOf('@');
            // JS slice(0, -1) / Number(...) semantics for a malformed mark without '@' are kept on purpose.
            var stage = idx >= 0 ? p[..idx] : (p.Length > 0 ? p[..^1] : "");
            var at = JsNumber(idx >= 0 ? p[(idx + 1)..] : p);
            if (stage.Length > 0 && double.IsFinite(at)) marks.Add((stage, at));
        }
        static PhaseKind KindOf(string stage) =>
            stage == "thinking" ? PhaseKind.Think : stage == "writing" ? PhaseKind.Write
            : stage.StartsWith("tool:", StringComparison.Ordinal) ? PhaseKind.Tool : PhaseKind.Wait;
        var segments = new List<PhaseSegment> { new(PhaseKind.Wait, 0, Math.Min(marks.Count > 0 ? marks[0].At : end, end)) };
        for (var i = 0; i < marks.Count; i++)
            segments.Add(new PhaseSegment(KindOf(marks[i].Stage), marks[i].At, Math.Max(marks[i].At, i + 1 < marks.Count ? marks[i + 1].At : end)));
        return segments;
    }

    /// <summary>What the agent of an active session is doing: the live call's stage, else what the last call left it doing (<c>agentStatus</c>).</summary>
    public static AgentStatus? AgentStatusOf(RequestSummaryDto? live, IReadOnlyList<TraceCallDto> calls, IEnumerable<SpanDto> spans)
    {
        if (live is not null)
        {
            var since = StartMs(live);
            var stage = live.Stage ?? "";
            if (stage.StartsWith("tool:", StringComparison.Ordinal))
                return new AgentStatus(AgentStatusKind.Tool, since, live.RequestedModel, Tool: stage["tool:".Length..]);
            if (stage == "thinking") return new AgentStatus(AgentStatusKind.Thinking, since, live.RequestedModel);
            if (stage == "writing") return new AgentStatus(AgentStatusKind.Writing, since, live.RequestedModel);
            if (live.HttpStatus is { } h && h != 0) return new AgentStatus(AgentStatusKind.Waiting, since, live.RequestedModel);
            return new AgentStatus(AgentStatusKind.Upstream, since, live.RequestedModel);
        }
        var last = calls.Where(c => c.CallKind != TraceCallKinds.Aux).OrderByDescending(EndMs).FirstOrDefault();
        if (last is null) return null;
        var end = EndMs(last);
        if (last.Status != "success") return new AgentStatus(AgentStatusKind.Failed, end);
        if (last.ToolCalls > 0 || last.FinishReason == "tool_calls")
        {
            var by = SpansByRequest(spans);
            var names = CallPhasesOf(by.GetValueOrDefault(last.Id) ?? [])?.Tools ?? [];
            return new AgentStatus(AgentStatusKind.Tools, end, Tools: names.Count > 0 ? TraceFormat.ToolSummary(names) : last.ToolCalls.ToString());
        }
        return new AgentStatus(AgentStatusKind.Idle, end);
    }

    /// <summary>Where a live request is, in words (<c>stageText</c> in live.tsx).</summary>
    public static string StageText(RequestSummaryDto r, TraceText t)
    {
        var phase = LivePhaseOf(r);
        if (phase == LivePhase.Streaming && !string.IsNullOrEmpty(r.Stage))
            return r.Stage.StartsWith("tool:", StringComparison.Ordinal)
                ? t("traces.agent.tool", ("name", r.Stage["tool:".Length..]))
                : t("traces.agent." + r.Stage);
        return t("traces.live." + phase.ToString().ToLowerInvariant());
    }

    /// <summary>Elapsed ms of a live request: its final total, else the clock since it started (<c>PendingLine</c> / <c>LiveDot</c>).</summary>
    public static double Elapsed(RequestSummaryDto r, double nowMs) => Math.Max(0, r.TotalMs ?? nowMs - StartMs(r));

    // ---------------------------------------------------------------- routing

    /// <summary>The smart-routing decision on this request, if it asked for the virtual model (<c>routeDecision</c>).</summary>
    public static RouteDecision? RouteDecisionOf(IEnumerable<SpanDto> spans)
    {
        var span = spans.FirstOrDefault(s => s.Name == TraceSpanNames.Resolve
            && (!string.IsNullOrEmpty(Str(s, "astra.route.grade")) || !string.IsNullOrEmpty(Str(s, "astra.route.source"))));
        if (span is null) return null;
        return new RouteDecision(span,
            Str(span, "astra.route.mode"), Str(span, "astra.route.source"), Str(span, "astra.route.grade"), Str(span, "astra.route.model"),
            Num(span, "astra.route.confidence"), Str(span, "astra.route.fallback_reason"), Str(span, "astra.route.adjusted_from"),
            Str(span, "astra.route.judge.protocol"), Str(span, "astra.route.judge.model"), Num(span, "astra.route.judge.ms") ?? 0,
            Num(span, "astra.route.judge.input_tokens"), Num(span, "astra.route.judge.output_tokens"),
            IsTrue(span, "astra.route.judge.new_connection"));
    }

    /// <summary>The source in the page's words; the raw value when it is a new one (<c>routeSourceLabel</c>).</summary>
    public static string RouteSourceLabel(string? source, TraceText t) =>
        source is not null && RouteSources.Contains(source) ? t("traces.route.source." + source) : source ?? TraceFormat.Dash;

    /// <summary>The routing mode in the page's words (<c>routeModeLabel</c>).</summary>
    public static string RouteModeLabel(string? mode, TraceText t) =>
        mode is not null && RouteModes.Contains(mode) ? t("traces.route.mode." + mode) : mode ?? TraceFormat.Dash;

    /// <summary>A fallback reason in the smart-routing page's words (<c>routeReasonLabel</c>).</summary>
    public static string? RouteReasonLabel(string? reason, TraceText t) =>
        string.IsNullOrEmpty(reason) ? null : RouteReasons.Contains(reason) ? t("smartRouting.failure." + reason) : reason;

    /// <summary>Judge time inside a resolve span in microseconds; 0 when this span did not record the decision (<c>judgeUsOn</c>).</summary>
    public static double JudgeUsOn(SpanDto? span, RouteDecision? decision)
    {
        if (span is null || decision is null || span.SpanId != decision.Span.SpanId || decision.JudgeMs <= 0) return 0;
        return Math.Min(span.DurationUs, TraceFormat.JsRound(decision.JudgeMs * 1000));
    }

    /// <summary>"standard · Judged · 2.01 s": the decision in one line (<c>routeSummary</c>).</summary>
    public static string RouteSummary(RouteDecision d, TraceText t)
    {
        var parts = new List<string> { d.Grade ?? TraceFormat.Dash, RouteSourceLabel(d.Source, t) };
        if (d.JudgeMs > 0) parts.Add(TraceFormat.Us(TraceFormat.JsRound(d.JudgeMs * 1000)));
        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- one request's time

    /// <summary>Astra, the routing judge, wasted attempts, upstream first byte, first token, generation (<c>splitRequestTime</c>).</summary>
    public static TimeSplit? SplitRequestTime(IReadOnlyList<SpanDto> spans)
    {
        var root = spans.FirstOrDefault(s => s.Name == TraceSpanNames.Request);
        var attempts = Attempts(spans);
        if (root is null || attempts.Count == 0) return null;
        var first = attempts[0];
        var last = attempts[^1];
        double headersAt = last.StartUs + last.DurationUs;
        var response = spans.FirstOrDefault(s => s.Name == TraceSpanNames.Response);
        double? firstToken = response is null ? null : EventOffsetUs(response, "first_token");
        var responseEnd = response is not null ? (double)(response.StartUs + response.DurationUs) : headersAt;
        var tokenAt = firstToken is { } ft && ft >= headersAt ? ft : headersAt;
        var routing = RouteDecisionOf(spans);
        return new TimeSplit(
            AstraUs: first.StartUs,
            RoutingUs: Math.Min(first.StartUs, JudgeUsOn(routing?.Span, routing)),
            RetryUs: Math.Max(0, last.StartUs - first.StartUs),
            TtfbUs: last.DurationUs,
            FirstTokenUs: Math.Max(0, tokenAt - headersAt),
            GenerationUs: Math.Max(0, responseEnd - tokenAt),
            TailUs: Math.Max(0, root.DurationUs - responseEnd),
            TotalUs: root.DurationUs);
    }

    /// <summary>The generation time split by what the model produced; the rest is pauses between phases (the <c>generation</c> memo in request.tsx).</summary>
    public static (double Think, double Write, double Tool, double Other)? GenerationSplit(TimeSplit? split, CallPhases? phases)
    {
        if (split is null || phases is null || phases.ThinkMs + phases.WriteMs + phases.ToolMs <= 0) return null;
        var left = split.GenerationUs;
        double Take(double ms)
        {
            var us = Math.Min(left, Math.Max(0, ms * 1000));
            left -= us;
            return us;
        }
        var think = Take(phases.ThinkMs);
        var write = Take(phases.WriteMs);
        var tool = Take(phases.ToolMs);
        return (think, write, tool, left);
    }

    // ---------------------------------------------------------------- the model's phases

    /// <summary>When the model waited, thought, wrote and called tools in one call, from the response span's events (<c>callPhases</c>).</summary>
    public static CallPhases? CallPhasesOf(IEnumerable<SpanDto> spans)
    {
        var response = spans.FirstOrDefault(s => s.Name == TraceSpanNames.Response);
        if (response is null) return null;
        var events = Events(response);
        double? Ev(string name)
        {
            foreach (var e in events) if (e.Name == name) return e.OffsetUs / 1000;
            return null;
        }
        var toolEvents = events.Where(e => e.Name == "tool_call").ToList();
        double end = (response.StartUs + response.DurationUs) / 1000.0;
        var rs = Ev("reasoning_start");
        var re = Ev("reasoning_end");
        var ts = Ev("text_start");
        var te = Ev("text_end");
        double? firstTool = toolEvents.Count > 0 ? toolEvents[0].OffsetUs / 1000 : null;
        var outputs = new[] { rs, ts, firstTool, Ev("first_token") }.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var firstOutput = outputs.Count > 0 ? outputs.Min() : response.StartUs / 1000.0;
        var segments = new List<PhaseSegment> { new(PhaseKind.Wait, 0, firstOutput) };
        if (rs is { } a && re is { } b) segments.Add(new PhaseSegment(PhaseKind.Think, a, Math.Max(b, a)));
        if (ts is { } c && te is { } d) segments.Add(new PhaseSegment(PhaseKind.Write, c, Math.Max(d, c)));
        if (firstTool is { } f) segments.Add(new PhaseSegment(PhaseKind.Tool, f, Math.Max(end, f)));
        var names = toolEvents
            .Select(e => e.Attributes is not null && e.Attributes.TryGetPropertyValue("name", out var n) && n is not null ? JsString(n) : "")
            .Where(n => n.Length > 0).ToList();
        var fromAttr = Str(response, "astra.response.tool_names");
        return new CallPhases(
            WaitMs: firstOutput,
            ThinkMs: rs.HasValue && re.HasValue ? Math.Max(0, re.Value - rs.Value) : 0,
            WriteMs: ts.HasValue && te.HasValue ? Math.Max(0, te.Value - ts.Value) : 0,
            ToolMs: firstTool.HasValue ? Math.Max(0, end - firstTool.Value) : 0,
            Segments: segments,
            Tools: names.Count > 0 ? names : !string.IsNullOrEmpty(fromAttr) ? fromAttr.Split(',') : []);
    }

    /// <summary>"Thinking 2.1 s · Writing 1.3 s": only the phases that took 50 ms or more (<c>phaseLine</c>).</summary>
    public static string? PhaseLine(CallPhases? p, TraceText t)
    {
        if (p is null) return null;
        var parts = new List<string>();
        if (p.ThinkMs >= 50) parts.Add($"{t("traces.phase.think")} {TraceFormat.Ms(p.ThinkMs)}");
        if (p.WriteMs >= 50) parts.Add($"{t("traces.phase.write")} {TraceFormat.Ms(p.WriteMs)}");
        if (p.ToolMs >= 50) parts.Add($"{t("traces.phase.tool")} {TraceFormat.Ms(p.ToolMs)}");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>The phase list of a call inspector: waiting always, the others when they took time (the <c>phaseText</c> in session.tsx / the response inspector).</summary>
    public static string PhaseDetail(CallPhases p, TraceText t) =>
        string.Join(" · ", new[] { PhaseKind.Wait, PhaseKind.Think, PhaseKind.Write, PhaseKind.Tool }
            .Select(k => (k, ms: k switch { PhaseKind.Wait => p.WaitMs, PhaseKind.Think => p.ThinkMs, PhaseKind.Write => p.WriteMs, _ => p.ToolMs }))
            .Where(x => x.k == PhaseKind.Wait || x.ms > 0)
            .Select(x => $"{t("traces.phase." + x.k.ToString().ToLowerInvariant())} {TraceFormat.Ms(x.ms)}"));

    /// <summary>The model's phases summed over a turn's calls, retries taken out of the wait (<c>turnPhases</c>).</summary>
    public static PhaseSums TurnPhases(IEnumerable<TraceCallDto> calls, IEnumerable<SpanDto> spans)
    {
        var by = SpansByRequest(spans);
        double wait = 0, think = 0, write = 0, tool = 0;
        foreach (var c in calls)
        {
            var own = by.GetValueOrDefault(c.Id) ?? [];
            var p = CallPhasesOf(own);
            if (p is null) continue;
            wait += Math.Max(0, p.WaitMs - (SplitRequestTime(own)?.RetryUs ?? 0) / 1000);
            think += p.ThinkMs;
            write += p.WriteMs;
            tool += p.ToolMs;
        }
        return new PhaseSums(wait, think, write, tool);
    }

    // ---------------------------------------------------------------- one turn

    /// <summary>Where a turn's time went: model calls, the agent's own time between them, wasted retries, Astra (<c>splitTurnTime</c>).</summary>
    public static TurnTime SplitTurnTime(IReadOnlyList<TraceCallDto> calls, IEnumerable<SpanDto> spans)
    {
        var by = SpansByRequest(spans);
        var ordered = calls.OrderBy(StartMs).ToList();
        if (ordered.Count == 0) return new TurnTime(0, 0, 0, 0, 0);
        var begin = StartMs(ordered[0]);
        var end = ordered.Max(EndMs);
        double retryUs = 0, overheadUs = 0;
        foreach (var c in ordered)
        {
            var split = SplitRequestTime(by.GetValueOrDefault(c.Id) ?? []);
            if (split is null) continue;
            retryUs += split.RetryUs;
            overheadUs += split.AstraUs;
        }
        // Time covered by at least one call (main and sub-agent calls overlap); the rest is the agent's own.
        double busy = 0;
        var cursor = begin;
        foreach (var c in ordered)
        {
            var s = Math.Max(StartMs(c), cursor);
            var e = EndMs(c);
            if (e > s) busy += e - s;
            cursor = Math.Max(cursor, e);
        }
        var retryMs = retryUs / 1000;
        return new TurnTime(Math.Max(0, busy - retryMs), Math.Max(0, end - begin - busy), retryMs, overheadUs / 1000, end - begin);
    }

    /// <summary>Calls worth a look: failed, retried, or more than twice the turn's average and over 3 s (<c>findAnomalies</c>).</summary>
    public static Dictionary<string, Anomaly> FindAnomalies(IReadOnlyList<TraceCallDto> calls, IEnumerable<SpanDto> spans)
    {
        var by = SpansByRequest(spans);
        var timed = calls.Where(c => c.TotalMs is not null).ToList();
        var mean = timed.Count > 0 ? timed.Sum(c => (double)(c.TotalMs ?? 0)) / timed.Count : 0;
        var output = new Dictionary<string, Anomaly>(StringComparer.Ordinal);
        foreach (var c in calls)
        {
            if (c.Status != "success") output[c.Id] = Anomaly.Error;
            else if ((by.GetValueOrDefault(c.Id) ?? []).Count(s => s.Name == TraceSpanNames.Attempt) > 1) output[c.Id] = Anomaly.Retry;
            else if (timed.Count >= 3 && (c.TotalMs ?? 0) > Math.Max(3000, 2 * mean)) output[c.Id] = Anomaly.Slow;
        }
        return output;
    }

    /// <summary>JS <c>Number(string)</c> for the shapes marks have: blank is 0, junk is NaN.</summary>
    internal static double JsNumber(string s)
    {
        var x = s.Trim();
        if (x.Length == 0) return 0;
        return double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}

/// <summary>What an agent is doing right now (<c>AgentStatus</c> kinds).</summary>
public enum AgentStatusKind { Upstream, Waiting, Thinking, Writing, Tool, Tools, Idle, Failed }

/// <summary>The agent's status: <see cref="Tool"/> for <see cref="AgentStatusKind.Tool"/>, <see cref="Tools"/> for <see cref="AgentStatusKind.Tools"/>.</summary>
public sealed record AgentStatus(AgentStatusKind Kind, double Since, string? Model = null, string? Tool = null, string? Tools = null)
{
    /// <summary>The chip is pulsing: the agent is not idle or failed.</summary>
    public bool Busy => Kind is not (AgentStatusKind.Idle or AgentStatusKind.Failed);

    /// <summary>The chip text (<c>AgentStatusChip</c>).</summary>
    public string Text(TraceText t) => Kind switch
    {
        AgentStatusKind.Tool => t("traces.agent.tool", ("name", Tool)),
        AgentStatusKind.Tools => t("traces.agent.tools", ("names", Tools)),
        _ => t("traces.agent." + Kind.ToString().ToLowerInvariant()),
    };
}
