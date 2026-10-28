using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// The usage views the request log and its detail share (web <c>TokenUsage.tsx</c>): a billing token type with its raw id underneath,
/// the compact cache and totals cells of a request row, and the token composition block of a request's detail. Missing usage is
/// never shown as a 0% cache hit. The math is <c>Astra.Desktop.Core.Overview.UsageMath</c>.
/// </summary>
public static class TokenUsage
{
    /// <summary>A token type as a human label with the exact type in mono underneath (<c>TokenTypeLabel</c>).</summary>
    public static Control TypeLabel(string type)
    {
        var key = UsageLabels.TokenTypeKey(type);
        var label = KitStyle.Text(key is null ? type : Loc.T(key), ArcDesignTokenKind.TypeSM, ToneKind(UsageLabels.TokenTypeTone(type)));
        var stack = new StackPanel { Children = { label } };
        if (key is not null)
        {
            var raw = KitStyle.Text(type, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            raw.FontSize = 10.5;
            raw.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            stack.Children.Add(raw);
        }
        return stack;
    }

    /// <summary>The compact two-line cache cell: "hit 16K 51%" over "write 3K"; a dash when the upstream reported nothing or nothing was cached (<c>CacheUsageCell</c>).</summary>
    public static Control CacheCell(RequestSummaryDto request, bool selected = false)
    {
        var u = UsageMath.Split(request);
        if (!u.Reported || (u.Read == 0 && u.Write == 0))
        {
            var dash = new TextBlock { Text = StatsFormat.Dash, Opacity = 0.5, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
            ToolTip.SetTip(dash, Loc.T(u.Reported ? "usage.noCache" : "usage.missing"));
            return dash;
        }
        var hit = Loc.T("usage.hitShort") + " " + StatsFormat.Tokens(u.Read, true) + (u.HitRatio is { } r ? "  " + StatsFormat.Percent(r) : "");
        var write = Loc.T("usage.writeShort") + " " + StatsFormat.Tokens(u.Write, true);
        var line1 = KitStyle.Text(hit, ArcDesignTokenKind.TypeXS, selected ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.Success);
        var line2 = KitStyle.Text(write, ArcDesignTokenKind.TypeXS, selected ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextSecondary);
        ToolTip.SetTip(line1, Loc.T("usage.hitTooltip", ("read", StatsFormat.Tokens(u.Read)), ("total", StatsFormat.Tokens(u.Input))));
        ToolTip.SetTip(line2, Loc.T("usage.writeTooltip", ("tokens", StatsFormat.Tokens(u.Write))));
        return new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Spacing = 1, Children = { line1, line2 } };
    }

    /// <summary>"input / output" with the reasoning tokens under it (<c>TokenTotalsCell</c>).</summary>
    public static Control TotalsCell(RequestSummaryDto request, bool selected = false)
    {
        var u = UsageMath.Split(request);
        if (!u.Reported)
        {
            var dash = new TextBlock { Text = StatsFormat.Dash, Opacity = 0.5, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
            ToolTip.SetTip(dash, Loc.T("usage.missing"));
            return dash;
        }
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Spacing = 1 };
        stack.Children.Add(new TextBlock { Text = StatsFormat.Tokens(u.Input, true) + " / " + StatsFormat.Tokens(u.Output, true), TextAlignment = TextAlignment.Right });
        if (u.Reasoning > 0)
        {
            var reasoning = KitStyle.Text(Loc.T("usage.reasoningShort") + " " + StatsFormat.Tokens(u.Reasoning, true), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            if (selected) reasoning.Opacity = 0.75;
            stack.Children.Add(reasoning);
        }
        ToolTip.SetTip(stack, Loc.T("usage.totalsHint"));
        return stack;
    }

    /// <summary>
    /// The composition block of a request's detail (<c>TokenUsageSummary</c>): four counters, then the input and output split as thin
    /// bars that divide the existing totals and never add to them.
    /// </summary>
    public static Control Summary(UsageSplit u)
    {
        var section = new DetailSection { Title = Loc.T("usage.title") };
        if (!u.Reported)
        {
            var note = KitStyle.Text(Loc.T("usage.missing"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            note.FontSize = KitStyle.Type12;
            note.TextWrapping = TextWrapping.Wrap;
            note.TextTrimming = TextTrimming.None;
            var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = note };
            CardPanel.Themed(box, CardPanel.SurfaceMutedBrush);
            section.Body = box;
            return section;
        }
        var counters = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        counters.Children.Add(Counter(Loc.T("usage.inputTotal"), StatsFormat.Tokens(u.Input)));
        counters.Children.Add(Counter(Loc.T("usage.outputTotal"), StatsFormat.Tokens(u.Output)));
        counters.Children.Add(Counter(Loc.T("usage.hitRate"), StatsFormat.Percent(u.HitRatio)));
        counters.Children.Add(Counter(Loc.T("usage.reasoning"), StatsFormat.Tokens(u.Reasoning)));
        var hint = KitStyle.Text(Loc.T("usage.totalsHint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        hint.FontSize = 11;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 8, 0, 0);
        var card = new CardPanel(new StackPanel
        {
            Children =
            {
                counters,
                Composition(Loc.T("usage.inputTotal"),
                [
                    (Loc.T("usage.uncachedInput"), u.UncachedInput, ArcDesignTokenKind.Accent),
                    (Loc.T("usage.cacheRead"), u.Read, ArcDesignTokenKind.Success),
                    (Loc.T("usage.cacheWrite"), u.Write, ArcDesignTokenKind.Warning),
                ]),
                Composition(Loc.T("usage.outputTotal"),
                [
                    (Loc.T("usage.regularOutput"), u.RegularOutput, ArcDesignTokenKind.TextSecondary),
                    (Loc.T("usage.reasoning"), u.Reasoning, ArcDesignTokenKind.Accent),
                ]),
                hint,
            },
        }, new Thickness(16));
        section.Body = card;
        return section;
    }

    private static Control Counter(string label, string value)
    {
        var l = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        l.FontSize = 11;
        var v = KitStyle.Text(value, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        v.FontSize = 17;
        return new StackPanel { Children = { l, v } };
    }

    private static Control Composition(string label, (string Label, long Count, ArcDesignTokenKind Color)[] segments)
    {
        var total = segments.Sum(s => s.Count);
        var shown = segments.Where(s => s.Count > 0).ToList();
        var bar = new Grid { Height = 6 };
        if (total > 0)
            for (var i = 0; i < shown.Count; i++)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition(shown[i].Count, GridUnitType.Star));
                var piece = new Border();
                piece.Token(Border.BackgroundProperty, shown[i].Color);
                Grid.SetColumn(piece, i);
                bar.Children.Add(piece);
            }
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = bar, Margin = new Thickness(0, 12, 0, 0) };
        CardPanel.Themed(track, CardPanel.SurfaceMutedBrush);
        Avalonia.Automation.AutomationProperties.SetName(track, label);

        var legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var s in segments)
        {
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            dot.Token(Border.BackgroundProperty, s.Color);
            var text = KitStyle.Text(s.Label + " " + StatsFormat.Tokens(s.Count), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            text.FontSize = 11;
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, text } });
        }
        return new StackPanel { Children = { track, legend } };
    }

    private static ArcDesignTokenKind ToneKind(string? tone) => tone switch
    {
        "green" => ArcDesignTokenKind.Success,
        "orange" => ArcDesignTokenKind.Warning,
        _ => ArcDesignTokenKind.Foreground,
    };
}
