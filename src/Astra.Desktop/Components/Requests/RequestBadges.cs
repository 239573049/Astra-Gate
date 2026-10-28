using Astra.Core.Routing;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// The small status marks of the request log, built the way <c>web/src/pages/RequestsPage.tsx</c> draws them: the status badge, the
/// reasoning and smart-routing badges, the returned-model and protocol lines. What each says is decided by <see cref="RequestPresentation"/>.
/// </summary>
public static class RequestBadges
{
    private static TraceText T => TraceTexts.FromLoc;

    /// <summary>A text block with a 11px size (web <c>text-[11px]</c>), trimmed with an ellipsis.</summary>
    internal static TextBlock Small(string? text, ArcDesignTokenKind color = ArcDesignTokenKind.TextMuted)
        => KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);

    /// <summary>The web's <c>.num</c>: tabular figures, so a changing number does not jiggle its neighbours.</summary>
    internal static T Num<T>(this T text) where T : TextBlock
    {
        text.FontFeatures = FontFeatureCollection.Parse("tnum");
        return text;
    }

    /// <summary>A Lucide glyph of <paramref name="size"/> px in a color token.</summary>
    internal static LucideIcon Glyph(LucideIconKind kind, double size, ArcDesignTokenKind? color = null)
    {
        var icon = new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, c);
        return icon;
    }

    /// <summary>The status badge (web <c>StatusBadge</c>): spinner on accent while pending, check on green, ban on orange, cross on red.</summary>
    public static Control Status(string status, int? http, string? hint = null)
    {
        var info = RequestPresentation.Status(status, http, hint, T);
        var badge = new Badge { Size = BadgeSize.Small, Content = info.Label, HorizontalAlignment = HorizontalAlignment.Left };
        switch (info.Kind)
        {
            case RequestStatusKind.Pending:
                badge.Tone = BadgeTone.Info;
                badge.Icon = new SpinningIcon { Kind = LucideIconKind.LoaderCircle, Size = 12 };
                break;
            case RequestStatusKind.Success:
                badge.Tone = BadgeTone.Success;
                badge.Icon = new LucideIcon { Kind = LucideIconKind.CircleCheck, Size = 12 };
                break;
            case RequestStatusKind.Cancelled:
                badge.Tone = BadgeTone.Warning;
                badge.Icon = new LucideIcon { Kind = LucideIconKind.Ban, Size = 12 };
                break;
            default:
                badge.Tone = BadgeTone.Danger;
                badge.Icon = new LucideIcon { Kind = LucideIconKind.CircleX, Size = 12 };
                break;
        }
        if (info.Title is not null) Tooltip.SetTip(badge, info.Title);
        return badge;
    }

    /// <summary>The "what the client explicitly set" label next to the model name, or null when nothing was recorded (web <c>ReasoningBadge</c>).</summary>
    public static Control? Reasoning(string? effort, string? mode, long? budget)
    {
        var parts = RequestPresentation.ReasoningParts(effort, mode, budget, T);
        if (parts.Count == 0) return null;
        var badge = new Badge
        {
            Size = BadgeSize.Small,
            Tone = BadgeTone.Neutral,
            Icon = new LucideIcon { Kind = LucideIconKind.Brain, Size = 12 },
            Content = $"{Loc.T("requests.reasoning")}: {string.Join(" · ", parts)}",
            MaxWidth = 176,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Tooltip.SetTip(badge, $"{Loc.T("requests.reasoning.hint")} {string.Join(" · ", parts)}");
        return badge;
    }

    /// <summary>How smart routing sent a request to the virtual model, or null for every other request (web <c>RoutingBadge</c>).</summary>
    public static Control? Routing(RoutingReport? routing, double maxWidth = 224)
    {
        var info = RequestPresentation.Routing(routing, T);
        if (info is null) return null;
        var badge = new Badge
        {
            Size = BadgeSize.Small,
            Tone = info.Ok ? BadgeTone.Success : BadgeTone.Warning,
            Icon = new LucideIcon { Kind = info.Ok ? LucideIconKind.CircleCheck : LucideIconKind.CircleAlert, Size = 12 },
            Content = info.Text,
            MaxWidth = maxWidth,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Tooltip.SetTip(badge, info.Title);
        return badge;
    }

    /// <summary>The model the gateway sent in place of the requested one: "→ upstream", muted (web <c>MappedModel</c>); null when there was none.</summary>
    public static Control? MappedModel(string? requested, string? upstream)
    {
        var mapped = RequestPresentation.MappedModel(requested, upstream);
        if (mapped is null) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Glyph(LucideIconKind.ArrowRight, 12, ArcDesignTokenKind.TextMuted));
        row.Children.Add(KitStyle.Text(mapped, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted));
        Tooltip.SetTip(row, RequestPresentation.MappedModelTitle(mapped, T));
        return row;
    }

    /// <summary>The returned-model line: a check (matches) or arrow (differs) and "一致" / "不一致" when comparable (web <c>ResponseModelTag</c>).</summary>
    public static Control ResponseModel(string? requested, string? upstream, string? response, string status, bool labeled, double fontSize = 0)
    {
        var info = RequestPresentation.ResponseModel(requested, upstream, response, status, labeled, T);
        var color = !info.Comparable ? ArcDesignTokenKind.TextMuted : info.Matches ? ArcDesignTokenKind.Success : ArcDesignTokenKind.Warning;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (info.Comparable) row.Children.Add(Glyph(info.Matches ? LucideIconKind.Check : LucideIconKind.ArrowRight, 12, color));
        var text = fontSize > 0 ? KitStyle.Text(info.Text, ArcDesignTokenKind.TypeXS, color) : Small(info.Text, color);
        if (fontSize > 0) text.FontSize = fontSize;
        row.Children.Add(text);
        if (info.MatchWord is not null)
        {
            var word = KitStyle.Text(info.MatchWord, ArcDesignTokenKind.TypeXS, color);
            word.FontSize = 10;
            row.Children.Add(word);
        }
        Tooltip.SetTip(row, info.Title);
        return row;
    }

    /// <summary>The protocol line: one protocol, or inbound → upstream in the warning color when the call was translated (web <c>ProtocolTag</c>).</summary>
    public static Control Protocol(string inbound, string? upstream, bool passthrough, double fontSize = 0)
    {
        var info = RequestPresentation.Protocol(inbound, upstream, passthrough);
        var converted = info.Upstream is not null;
        var color = converted ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.TextMuted;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Label(string text)
        {
            var t = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);
            if (fontSize > 0) t.FontSize = fontSize;
            t.Margin = new Avalonia.Thickness(converted ? 2 : 4, 0, 0, 0);
            return t;
        }
        row.Children.Add(MarkOf(info.Inbound, color));
        row.Children.Add(Label(info.Inbound));
        if (converted)
        {
            row.Children.Add(Glyph(LucideIconKind.ArrowRight, 12, color));
            row.Children.Add(MarkOf(info.Upstream!, color));
            row.Children.Add(Label(info.Upstream!));
        }
        return row;
    }

    private static Control MarkOf(string protocol, ArcDesignTokenKind color)
    {
        var mark = BrandIcon.Protocol(protocol, 11);
        mark.Foreground = null;
        mark.Token(BrandIcon.ForegroundProperty, color);
        return mark;
    }

    /// <summary>The returned-model / protocol / reasoning "not recorded" value of the detail (web <c>ReasoningValue</c>): the settings, or an explicit muted "not recorded".</summary>
    public static Control ReasoningValue(string? effort, string? mode, long? budget)
    {
        var parts = RequestPresentation.ReasoningParts(effort, mode, budget, T);
        var text = parts.Count == 0
            ? KitStyle.Text(Loc.T("requests.reasoning.notRecorded"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted)
            : KitStyle.Text(string.Join(" · ", parts), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground);
        text.FontSize = KitStyle.Type12;
        Tooltip.SetTip(text, Loc.T("requests.reasoning.hint"));
        return text;
    }
}
