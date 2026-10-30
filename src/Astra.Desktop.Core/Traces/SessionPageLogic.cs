using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Traces;

/// <summary>The two views of a session turn.</summary>
public enum SessionView { Canvas, Timeline }

/// <summary>What the session page shows for one poll of the server plus the live feed (the derived values at the top of <c>SessionView</c>).</summary>
/// <param name="TurnId">The shown turn: a live turn not stored yet, else the server's.</param>
/// <param name="LiveTurn">Id of a turn opened by a call in flight that the server has not stored (null when none or when a turn is picked).</param>
/// <param name="Calls">Calls of the shown turn (empty for a live turn).</param>
/// <param name="Spans">Spans of those calls.</param>
/// <param name="LatestTurn">The shown turn is the newest one (live calls may join it).</param>
/// <param name="Pending">Calls of this session in flight, or finished but not fetched yet, that belong to the shown turn.</param>
/// <param name="View">The view to show.</param>
/// <param name="TurnIndex">Zero-based index of the shown turn in the picker.</param>
public sealed record SessionPageState(string? TurnId, string? LiveTurn, IReadOnlyList<TraceCallDto> Calls, IReadOnlyList<SpanDto> Spans,
    bool LatestTurn, IReadOnlyList<RequestSummaryDto> Pending, SessionView View, int TurnIndex);

/// <summary>An entry of the turn picker.</summary>
public sealed record TurnOption(string Value, string Label);

/// <summary>
/// The view-model logic of <c>SessionView</c> / <c>SessionsList</c> in session.tsx that is not drawing: which turn is shown,
/// which live calls belong to it, canvas vs timeline, steps per row, anomaly jumping. The page keeps the selection / focus state.
/// </summary>
public static class SessionPageLogic
{
    /// <summary>A turn with more calls than this opens on the timeline (unless a view is asked for) (<c>LONG_TURN</c>).</summary>
    public const int LongTurn = 15;

    /// <summary>Derives the page state. <paramref name="mine"/> is the live feed filtered to this session; <paramref name="turnParam"/> the picked turn (<c>?turn=</c>).</summary>
    public static SessionPageState Resolve(TraceSessionDetailDto? data, string? turnParam, IReadOnlyList<RequestSummaryDto> mine, string? viewParam)
    {
        mine = data is null ? [] : mine;
        // A call in flight that opened a turn not stored yet is that turn's first step: show that turn (unless one is
        // picked) rather than hang the call off the previous turn.
        string? liveTurn = null;
        if (data is not null && string.IsNullOrEmpty(turnParam))
            liveTurn = mine.FirstOrDefault(r => !string.IsNullOrEmpty(r.TurnId) && !data.Turns.Any(x => x.TurnId == r.TurnId))?.TurnId;
        var turnId = liveTurn ?? data?.TurnId;
        IReadOnlyList<TraceCallDto> calls = data is not null && liveTurn is null ? data.Calls : [];
        IReadOnlyList<SpanDto> spans = data is not null && liveTurn is null ? data.Spans : [];
        var latest = liveTurn is not null || data is null || data.Turns.Count == 0 || data.Turns[^1].TurnId == data.TurnId;
        // Calls of the shown turn in flight, or finished but not fetched yet (an older server sends no turn: the latest).
        IReadOnlyList<RequestSummaryDto> pending = latest
            ? mine.Where(r => (string.IsNullOrEmpty(r.TurnId) || r.TurnId == turnId) && !calls.Any(c => c.Id == r.Id)).ToList()
            : [];
        var view = viewParam == "timeline" ? SessionView.Timeline : viewParam == "canvas" ? SessionView.Canvas
            : calls.Count > LongTurn ? SessionView.Timeline : SessionView.Canvas;
        var index = data is null ? 0 : liveTurn is not null ? data.Turns.Count : Math.Max(0, data.Turns.FindIndex(x => x.TurnId == data.TurnId));
        return new SessionPageState(turnId, liveTurn, calls, spans, latest, pending, view, index);
    }

    /// <summary>As many steps per row as fit at a readable zoom (the <c>columns</c> line); 4 until the width is measured.</summary>
    public static int Columns(double? width) =>
        width is { } w and > 0 ? (int)Math.Min(6, Math.Max(2, Math.Floor((w / 0.85 + 40) / SessionBuilder.StepPitch))) : 4;

    /// <summary>The turn picker entries, including the live turn after the stored ones (<c>options</c> of the <c>Select</c>).</summary>
    public static IReadOnlyList<TurnOption> TurnOptions(TraceSessionDetailDto data, string? liveTurn, TraceText t, Func<DateTimeOffset, string> formatDateTime)
    {
        var list = data.Turns.Select((x, i) => new TurnOption(x.TurnId, $"{t("traces.session.turn", ("n", i + 1))} · {formatDateTime(x.StartedAtUtc)}")).ToList();
        if (!string.IsNullOrEmpty(liveTurn))
            list.Add(new TurnOption(liveTurn, $"{t("traces.session.turn", ("n", data.Turns.Count + 1))} · {t("traces.request.pending")}"));
        return list;
    }

    /// <summary>The next anomaly to jump to, cycling from the focused one (<c>nextAnomaly</c>); null when there is none.</summary>
    public static string? NextAnomaly(IReadOnlyList<string> ids, string? focusId)
    {
        if (ids.Count == 0) return null;
        var at = focusId is null ? -1 : ids.ToList().IndexOf(focusId);
        return ids[(at + 1) % ids.Count];
    }

    /// <summary>Ids of the folded groups on a canvas (the <c>groups</c> line): "expand all" opens these.</summary>
    public static IReadOnlyList<string> GroupIds(TraceGraph graph) =>
        graph.Nodes.Where(n => n.Id.StartsWith("group:", StringComparison.Ordinal)).Select(n => n.Id).ToList();

    /// <summary>Whether two lists of live calls draw the same cards and bars, so traffic elsewhere does not relayout the canvas (<c>sameCards</c>).</summary>
    public static bool SameCards(IReadOnlyList<RequestSummaryDto> a, IReadOnlyList<RequestSummaryDto> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Status == p.Second.Status
            && p.First.ProviderId == p.Second.ProviderId && p.First.UpstreamModel == p.Second.UpstreamModel && p.First.TotalMs == p.Second.TotalMs);
}

/// <summary>Logic of the topology page and the live dots on its lines (<c>TopologyView</c> chips, <c>LiveDot</c> in live.tsx).</summary>
public static class TopologyPageLogic
{
    /// <summary>A request waiting this long for the upstream's first byte is drawn as slow (<c>SLOW_MS</c>).</summary>
    public const double SlowMs = 8000;

    /// <summary>Tailwind's <c>lg</c> breakpoint: below it the live legend is hidden (topology.tsx <c>max-lg:hidden</c>).</summary>
    public const double LegendMinWidth = 1024;

    /// <summary>The live legend shows in a window at least <see cref="LegendMinWidth"/> wide (the web's viewport is the window).</summary>
    public static bool ShowsLegend(double windowWidth) => windowWidth >= LegendMinWidth;

    /// <summary>The in-flight count of the chip: the live feed's when connected, else the server's gateway figure.</summary>
    public static int InFlight(bool liveConnected, IReadOnlyList<RequestSummaryDto> live, TraceTopologyDto? data) =>
        liveConnected ? live.Count(r => r.Status == "pending") : data?.Gateway.InFlight ?? 0;

    /// <summary>The error rate of the chip (<c>formatPercent(errors / requests)</c>).</summary>
    public static string ErrorRate(TraceGatewayDto g) => TraceFormat.Percent(g.Requests > 0 ? (double)g.Errors / g.Requests : 0);

    /// <summary>The topology shows its empty message: the window saw no traffic and nothing is in flight.</summary>
    public static bool IsEmpty(TraceTopologyDto? data, IReadOnlyList<RequestSummaryDto> live) => data is not null && data.Gateway.Requests == 0 && live.Count == 0;

    /// <summary>Spreads the dots of one line so they do not move in lockstep: a 0..1 phase offset from the request id (<c>offsetOf</c>).</summary>
    public static double DotOffset(string id)
    {
        uint h = 0;
        // JS iterates UTF-16 code units and does `(h * 31 + code) >>> 0`; C# string enumeration is the same units.
        foreach (var ch in id) h = unchecked(h * 31 + ch);
        return h % 1000 / 1000.0;
    }

    /// <summary>What a live dot does on a line.</summary>
    public enum DotMode { Hidden, Out, Back, Burst }

    /// <summary>
    /// What to draw for one request on a line of the given role (<c>LiveDot</c>): dots go out while routed and sent, come back once the upstream
    /// answers, burst where it ended. A provider line and an account line are both the upstream side.
    /// </summary>
    public static (DotMode Mode, bool Slow, double DurationSeconds) Dot(RequestSummaryDto r, string role, double nowMs)
    {
        var phase = TraceAnalysis.LivePhaseOf(r);
        var elapsed = TraceAnalysis.Elapsed(r, nowMs);
        var upstreamSide = role != "client";
        if (phase is LivePhase.Done or LivePhase.Failed)
        {
            // The request is over: a burst at the agent, or at the provider when the upstream turned it down.
            var refused = phase == LivePhase.Failed && (r.HttpStatus ?? 0) >= 400;
            return (upstreamSide ? !refused : refused) ? (DotMode.Hidden, false, 0) : (DotMode.Burst, false, 0);
        }
        if (upstreamSide && phase == LivePhase.Arriving) return (DotMode.Hidden, false, 0);
        var back = phase == LivePhase.Streaming;
        return (back ? DotMode.Back : DotMode.Out, phase == LivePhase.Upstream && elapsed > SlowMs, back ? 0.9 : 1.4);
    }

    /// <summary>What a request carries to the model, as a capsule label (<c>causeLabel</c>): the user's prompt, or which tool results; null when unknown.</summary>
    public static string? CauseLabel(Astra.Core.Requests.RequestCause? cause, TraceText t)
    {
        if (cause is null) return null;
        if (cause.Kind == "user") return t("traces.cause.user");
        var results = cause.ToolResults;
        if (results.Count == 1 && !string.IsNullOrEmpty(results[0].Name)) return $"{t("traces.cause.tool_result")} · {results[0].Name}";
        return results.Count > 1 ? t("traces.cause.results", ("count", results.Count)) : t("traces.cause.tool_result");
    }

    /// <summary>
    /// The capsule riding a line going out (<c>LiveDot</c>): what the request carries, prefixed "Retry" once a second attempt is under way, and
    /// its tone — retry (orange), slow (orange), result (green outline), out (cyan; a dot without a label when the cause is unknown).
    /// </summary>
    public static (string? Text, string Tone) OutCapsule(RequestSummaryDto r, bool slow, TraceText t)
    {
        var cause = CauseLabel(r.Cause, t);
        var retry = (r.Attempts?.Count ?? 0) > 1;
        var text = retry ? t("traces.cause.retry") + (cause is not null ? " · " + cause : "") : cause;
        var tone = retry ? "retry" : slow ? "slow" : r.Cause?.Kind == "tool_result" ? "result" : "out";
        return (text, tone);
    }

    /// <summary>What the model produces right now, as a capsule label and a tone: think, write, tool (its name), or back (no label).</summary>
    public static (string? Label, string Tone) StageCapsule(string? stage, TraceText t)
    {
        if (stage == "thinking") return (t("traces.output.think"), "think");
        if (stage == "writing") return (t("traces.output.text"), "write");
        if (stage is not null && stage.StartsWith("tool:", StringComparison.Ordinal))
            return (stage.Length > "tool:".Length ? stage["tool:".Length..] : t("traces.output.tool"), "tool");
        return (null, "back");
    }

    /// <summary>The label of the red capsule a refused attempt sends back to the hub: its status, else "failed".</summary>
    public static string FailedLabel(RequestSummaryDto r, TraceText t)
    {
        var failed = (r.Attempts ?? []).LastOrDefault(TopologyBuilder.AttemptFailed);
        return failed?.Status is { } s and not 0 ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : t("traces.cause.failed");
    }

    /// <summary>The hover title of a live dot: model, stage, HTTP status, elapsed (<c>title</c> in <c>LiveDot</c>).</summary>
    public static string DotTitle(RequestSummaryDto r, TraceText t, double nowMs) =>
        string.Join(" · ", new[] { r.RequestedModel, TraceAnalysis.StageText(r, t), r.HttpStatus is { } h and not 0 ? h.ToString() : null,
            TraceFormat.Ms(TraceAnalysis.Elapsed(r, nowMs)) }.Where(x => !string.IsNullOrEmpty(x)));
}
