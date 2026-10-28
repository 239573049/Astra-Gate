using Astra.Api.Contracts;
using Astra.Core.Requests;
using static Astra.Desktop.Core.Traces.SpanAttrs;
using static Astra.Desktop.Core.Traces.TraceAnalysis;

namespace Astra.Desktop.Core.Traces;

/// <summary>What a timeline row draws; the UI maps it to a bar style (turn = fill, llm = accent, sub = accent-2, tool = label-3, attempt-ok = accent 70 %, attempt-fail = red stripes, live = accent pulsing).</summary>
public enum TimelineRowKind { Turn, Llm, Sub, Tool, AttemptOk, AttemptFail, Live }

/// <summary>One row of the turn's timeline (<c>TimelineRow</c>); times are ms from the turn's first call.</summary>
public sealed record TimelineRow(string Id, string SelectId, string Label, int Depth, double StartMs, double EndMs, TimelineRowKind Kind,
    IReadOnlyList<PhaseSegment>? Segments = null, Anomaly? Flag = null, string? Detail = null)
{
    /// <summary>Row duration, the right-hand column.</summary>
    public double DurationMs => EndMs - StartMs;
}

/// <summary>The rows of a timeline and its length.</summary>
public sealed record Timeline(IReadOnlyList<TimelineRow> Rows, double TotalMs)
{
    /// <summary>A timeline with nothing on it.</summary>
    public static readonly Timeline Empty = new([], 0);
}

/// <summary>
/// The turn as rows on one time axis (ms from its first call): calls (with their phases), retried attempts, tool gaps, and the
/// calls still in flight growing up to now. Port of <c>buildTimeline</c> plus the axis helpers of <c>Timeline</c> in session.tsx.
/// </summary>
public static class TimelineBuilder
{
    /// <summary>Builds the timeline. <paramref name="nowMs"/> is unix ms (<c>Date.now()</c>).</summary>
    public static Timeline Build(IReadOnlyList<TraceCallDto> calls, IReadOnlyList<SpanDto> spans, TraceText t,
        IReadOnlyList<RequestSummaryDto>? pending, double nowMs)
    {
        pending ??= [];
        var by = SpansByRequest(spans);
        var ordered = calls.OrderBy(StartMs).ToList();
        var live = pending.Where(r => !ordered.Any(c => c.Id == r.Id)).ToList();
        if (ordered.Count == 0 && live.Count == 0) return Timeline.Empty;
        var anomalies = FindAnomalies(ordered, spans);
        var t0 = ordered.Select(StartMs).Concat(live.Select(StartMs)).Min();
        var ends = ordered.Select(EndMs).ToList();
        if (live.Count > 0) ends.Add(nowMs);
        var totalMs = ends.Max() - t0;
        var rows = new List<TimelineRow> { new("turn", "turn", t("traces.session.turnRow"), 0, 0, totalMs, TimelineRowKind.Turn) };
        foreach (var c in ordered)
        {
            var sub = c.CallKind is TraceCallKinds.Sidechain or TraceCallKinds.Aux;
            var parent = !string.IsNullOrEmpty(c.ParentRequestId) ? ordered.FirstOrDefault(p => p.Id == c.ParentRequestId) : null;
            if (parent is not null && !sub && (parent.CallKind ?? "main") == "main" && parent.ToolCalls > 0 && StartMs(c) > EndMs(parent))
            {
                var names = CallPhasesOf(by.GetValueOrDefault(parent.Id) ?? [])?.Tools ?? [];
                rows.Add(new TimelineRow($"gap:{c.Id}", $"tool:{c.Id}",
                    names.Count > 0 ? t("traces.session.toolRow", ("names", TraceFormat.ToolSummary(names))) : t("traces.session.toolGapRow"),
                    1, EndMs(parent) - t0, StartMs(c) - t0, TimelineRowKind.Tool));
            }
            var start = StartMs(c) - t0;
            var own = by.GetValueOrDefault(c.Id) ?? [];
            var phases = CallPhasesOf(own);
            rows.Add(new TimelineRow(c.Id, c.Id, $"{SessionBuilder.KindLabel(c, sub, t)} #{c.Step ?? 1} · {TraceFormat.ShortModel(c.UpstreamModel ?? c.RequestedModel)}",
                sub ? 2 : 1, start, EndMs(c) - t0, sub ? TimelineRowKind.Sub : TimelineRowKind.Llm,
                phases?.Segments.Select(s => new PhaseSegment(s.Kind, start + s.From, start + s.To)).ToList(),
                anomalies.TryGetValue(c.Id, out var a) ? a : null, PhaseLine(phases, t)));
            var attempts = Attempts(own);
            if (attempts.Count > 1)
                for (var i = 0; i < attempts.Count; i++)
                {
                    var at = attempts[i];
                    rows.Add(new TimelineRow($"{c.Id}:attempt:{i}", c.Id,
                        $"{t("traces.node.attempt", ("n", i + 1))} · {Str(at, "astra.account.id") ?? TraceFormat.Dash} · {(Num(at, "http.response.status_code") is { } s ? TraceFormat.Num(s) : TraceFormat.Dash)}",
                        (sub ? 2 : 1) + 1, start + at.StartUs / 1000.0, start + (at.StartUs + at.DurationUs) / 1000.0,
                        at.Status == TraceSpanStatus.Error ? TimelineRowKind.AttemptFail : TimelineRowKind.AttemptOk));
                }
        }
        foreach (var r in live)
        {
            var start = Math.Max(0, StartMs(r) - t0);
            rows.Add(new TimelineRow(r.Id, r.Id, $"LLM · {TraceFormat.ShortModel(r.UpstreamModel ?? r.RequestedModel)}", 1, start,
                Math.Max(start, r.TotalMs is { } tm && tm != 0 ? start + tm : nowMs - t0), TimelineRowKind.Live,
                LivePhases(r, nowMs).Select(s => new PhaseSegment(s.Kind, start + s.From, start + s.To)).ToList()));
        }
        return new Timeline(rows, totalMs);
    }

    /// <summary>A round tick step for an axis of <paramref name="raw"/> per tick (<c>niceStep</c>).</summary>
    public static double NiceStep(double raw)
    {
        var pow = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1))));
        var n = raw / pow;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
    }

    /// <summary>Axis tick positions in ms: about five steps over <paramref name="totalMs"/> (the <c>ticks</c> memo).</summary>
    public static IReadOnlyList<double> Ticks(double totalMs)
    {
        if (totalMs <= 0) return [];
        var step = NiceStep(totalMs / 5);
        var count = (int)Math.Floor(totalMs / step) + 1;
        return Enumerable.Range(0, count).Select(i => i * step).ToList();
    }

    /// <summary>Position on the axis as a 0..100 percentage (<c>pct</c>); 0 when the axis is empty.</summary>
    public static double Percent(double ms, double totalMs) => totalMs > 0 ? ms / totalMs * 100 : 0;
}
