using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Traces;

/// <summary>Semantic colour of a time-bar part; the UI maps it (accent, accent-2, label-3, orange, red, accent 45 % / 35 %, and the four phase colours).</summary>
public enum TraceSwatch { Accent, Accent2, Muted, Orange, Red, AccentSoft45, AccentSoft35, PhaseWait, PhaseThink, PhaseWrite, PhaseTool }

/// <summary>One part of a stacked time bar (<c>TimeBar</c> parts in inspector.tsx).</summary>
public sealed record TimeBarPart(string Label, double Value, string Text, TraceSwatch Swatch, string? Note = null);

/// <summary>A stacked bar of where time went, with its legend. <see cref="Flex"/> is the segment weights of the bar.</summary>
public sealed record TimeBar(string Title, string Total, IReadOnlyList<TimeBarPart> Parts)
{
    /// <summary>
    /// Relative width of each part: parts with no time are hidden (null), the others get <c>max(value/sum, 0.004)</c>
    /// (the <c>flex</c> of <c>TimeBar</c>; the UI also gives each a 3 px minimum width).
    /// </summary>
    public IReadOnlyList<double?> Flex()
    {
        var sum = Parts.Sum(p => Math.Max(0, p.Value));
        if (sum == 0) sum = 1;
        return Parts.Select(p => p.Value > 0 ? (double?)Math.Max(p.Value / sum, 0.004) : null).ToList();
    }

    /// <summary>The phase swatch of a <see cref="PhaseKind"/> (<c>PHASE_COLOR</c>).</summary>
    public static TraceSwatch Swatch(PhaseKind k) => k switch
    {
        PhaseKind.Wait => TraceSwatch.PhaseWait,
        PhaseKind.Think => TraceSwatch.PhaseThink,
        PhaseKind.Write => TraceSwatch.PhaseWrite,
        _ => TraceSwatch.PhaseTool,
    };
}

/// <summary>A session turn's time bar plus the overhead line under it.</summary>
public sealed record SessionTimeBar(TimeBar? Bar, string? Overhead);

/// <summary>Builds the time bars of the request and session views (the <c>TimeBar</c> props in request.tsx and session.tsx).</summary>
public static class TimeBarBuilder
{
    /// <summary>Where one request's time went; null when the request has no attempt yet.</summary>
    public static TimeBar? ForRequest(IReadOnlyList<SpanDto> spans, TraceText t)
    {
        var split = TraceAnalysis.SplitRequestTime(spans);
        if (split is null) return null;
        var gen = TraceAnalysis.GenerationSplit(split, TraceAnalysis.CallPhasesOf(spans));
        var astra = Math.Max(0, split.AstraUs - split.RoutingUs);
        var parts = new List<TimeBarPart> { new(t("traces.request.time.astra"), astra, TraceFormat.Us(astra), TraceSwatch.Accent2) };
        if (split.RoutingUs > 0) parts.Add(new TimeBarPart(t("traces.request.time.routing"), split.RoutingUs, TraceFormat.Us(split.RoutingUs), TraceSwatch.Orange));
        parts.Add(new TimeBarPart(t("traces.request.time.retries"), split.RetryUs, TraceFormat.Us(split.RetryUs), TraceSwatch.Red));
        parts.Add(new TimeBarPart(t("traces.request.time.ttfb"), split.TtfbUs, TraceFormat.Us(split.TtfbUs), TraceSwatch.Muted));
        parts.Add(new TimeBarPart(t("traces.request.time.firstToken"), split.FirstTokenUs, TraceFormat.Us(split.FirstTokenUs), TraceSwatch.AccentSoft45));
        if (gen is { } g)
        {
            parts.Add(new TimeBarPart(t("traces.phase.think"), g.Think, TraceFormat.Us(g.Think), TraceSwatch.PhaseThink));
            parts.Add(new TimeBarPart(t("traces.phase.write"), g.Write, TraceFormat.Us(g.Write), TraceSwatch.PhaseWrite));
            parts.Add(new TimeBarPart(t("traces.phase.tool"), g.Tool, TraceFormat.Us(g.Tool), TraceSwatch.PhaseTool));
            if (g.Other >= 1000) parts.Add(new TimeBarPart(t("traces.request.time.generationOther"), g.Other, TraceFormat.Us(g.Other), TraceSwatch.AccentSoft35));
        }
        else parts.Add(new TimeBarPart(t("traces.request.time.generation"), split.GenerationUs, TraceFormat.Us(split.GenerationUs), TraceSwatch.Accent));
        return new TimeBar(t("traces.request.time.title"), TraceFormat.Us(split.TotalUs), parts);
    }

    /// <summary>Where a turn's time went; <see cref="SessionTimeBar.Bar"/> is null for a turn with no time.</summary>
    public static SessionTimeBar ForSession(IReadOnlyList<TraceCallDto> calls, IReadOnlyList<SpanDto> spans, TraceText t)
    {
        var time = TraceAnalysis.SplitTurnTime(calls, spans);
        // the model's time split by phase, scaled to the model part of the bar (calls of sub-agents overlap the main ones)
        var sum = TraceAnalysis.TurnPhases(calls, spans);
        var total = sum.Total;
        var parts = new List<TimeBarPart>();
        if (total > 0 && time.ModelMs > 0)
            foreach (var k in new[] { PhaseKind.Wait, PhaseKind.Think, PhaseKind.Write, PhaseKind.Tool })
            {
                var ms = sum.Of(k) / total * time.ModelMs;
                parts.Add(new TimeBarPart(t("traces.phase." + k.ToString().ToLowerInvariant()), ms, TraceFormat.Ms(ms), TimeBar.Swatch(k)));
            }
        else parts.Add(new TimeBarPart(t("traces.session.time.model"), time.ModelMs, TraceFormat.Ms(time.ModelMs), TraceSwatch.Accent));
        parts.Add(new TimeBarPart(t("traces.session.time.tools"), time.ToolMs, TraceFormat.Ms(time.ToolMs), TraceSwatch.Muted,
            t("traces.session.time.needsTelemetry")));
        parts.Add(new TimeBarPart(t("traces.session.time.retries"), time.RetryMs, TraceFormat.Ms(time.RetryMs), TraceSwatch.Red));
        return new SessionTimeBar(
            time.TotalMs > 0 ? new TimeBar(t("traces.session.time.title"), TraceFormat.Ms(time.TotalMs), parts) : null,
            time.OverheadMs > 0 ? t("traces.session.time.overhead", ("ms", TraceFormat.Ms(time.OverheadMs))) : null);
    }
}
