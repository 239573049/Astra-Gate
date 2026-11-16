using System.Globalization;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Core.Requests;

/// <summary>How full the model's context window was (web <c>lib/context.ts</c> and <c>ContextFill.tsx</c>), after magpie's per-request context usage (MIT).</summary>
public static class ContextPresentation
{
    /// <summary>Colour class of a fill: comfortable below 70%, getting full up to 90%, nearly gone above.</summary>
    public enum Tone { Ok, Warn, Danger }

    /// <summary>The tone of a 0..1 fill.</summary>
    public static Tone ToneOf(double fill) => fill >= 0.9 ? Tone.Danger : fill >= 0.7 ? Tone.Warn : Tone.Ok;

    /// <summary>0.0123 as "1.2%", 0.5 as "50%", 0.0004 as "&lt;0.1%" (<c>formatFill</c>).</summary>
    public static string FillText(double? fill)
    {
        if (fill is not { } f || double.IsNaN(f) || double.IsInfinity(f)) return TraceFormat.Dash;
        var pct = f * 100;
        if (pct > 0 && pct < 0.1) return "<0.1%";
        return pct.ToString(pct >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>The list cell: the fill on top, "prompt / window" underneath; a dash when there is no fill to show.</summary>
    public static TwoLineCell Cell(ContextUsageDto? c, TraceText t)
    {
        if (c is null || c.Fill is not { } fill || c.Window is not { } window)
            return new(true, t("context.unknown"), TraceFormat.Dash, null, null, null);
        return new(false, null, FillText(fill), t("context.cellTooltip", ("prompt", TraceFormat.Tokens(c.PromptTokens)), ("window", TraceFormat.Tokens(window))),
            $"{TraceFormat.Tokens(c.PromptTokens, true)} / {TraceFormat.Tokens(window, true)}", null);
    }

    /// <summary>The window bar's segments in order: cached prompt, fresh prompt, output, and what is left of the window. Empty without a window.</summary>
    public static IReadOnlyList<(string Key, long Tokens)> WindowSegments(ContextUsageDto c)
    {
        if (c.Window is not { } window || window <= 0) return [];
        var cached = Math.Min(Math.Max(0, c.CacheReadTokens), c.PromptTokens);
        var fresh = Math.Max(0, c.PromptTokens - cached);
        var output = Math.Max(0, c.OutputTokens);
        var free = Math.Max(0, window - cached - fresh - output);
        return [("cached", cached), ("fresh", fresh), ("output", output), ("free", free)];
    }
}
