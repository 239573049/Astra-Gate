using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The small builders the account views share, in the vocabulary of the web markup they port (SubscriptionAccounts.tsx and friends):
/// a token color of a <see cref="TextTone"/>, text at the web's pixel sizes, a <c>Button size="sm"</c>, the detail sheet's section heading,
/// a quota bar. Every color comes from an Arc token, so light / dark and the accent follow.
/// </summary>
internal static class AccountsUi
{
    /// <summary>The token of a text tone.</summary>
    public static ArcDesignTokenKind TokenOf(TextTone tone) => tone switch
    {
        TextTone.Warning => ArcDesignTokenKind.Warning,
        TextTone.Danger => ArcDesignTokenKind.Danger,
        TextTone.Success => ArcDesignTokenKind.Success,
        TextTone.Accent => ArcDesignTokenKind.Accent,
        _ => ArcDesignTokenKind.TextMuted,
    };

    /// <summary>The kit tone of a badge tone name (neutral, accent, green, orange, red).</summary>
    public static Tone BadgeTone(string tone) => tone switch
    {
        "accent" => Tone.Accent,
        "green" => Tone.Green,
        "orange" => Tone.Orange,
        "red" => Tone.Red,
        _ => Tone.Neutral,
    };

    /// <summary>A badge with a tone name.</summary>
    public static Badge Badge(string text, string tone) => KitBadge.Create(text, BadgeTone(tone));

    /// <summary>Text at <paramref name="size"/> px in a token color; trims like the web's <c>truncate</c> unless <paramref name="wrap"/>.</summary>
    public static TextBlock Text(string? text, double size, ArcDesignTokenKind color = ArcDesignTokenKind.Foreground, FontWeight weight = FontWeight.Normal, bool wrap = false, bool mono = false)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color, weight);
        block.FontSize = size;
        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
            block.TextTrimming = TextTrimming.None;
        }
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>
    /// The color of uncolored web text in the provider page's tab panel: arc tabs <c>.content { color: var(--text-secondary) }</c> is
    /// inherited by everything without its own color class (the components' <c>text-[var(--text)]</c> names an undefined variable, so it
    /// inherits too); inside a sheet the same text is the dialog's foreground. Kit text sets its color explicitly, so the inline parts pass this.
    /// </summary>
    public const ArcDesignTokenKind Inline = ArcDesignTokenKind.TextSecondary;

    /// <summary>A Lucide glyph of <paramref name="size"/> px.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size = 14, ArcDesignTokenKind? color = null)
    {
        var icon = new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) icon.Token(TemplatedControl.ForegroundProperty, c);
        return icon;
    }

    /// <summary>
    /// A web <c>Button</c>: small by default (<c>size="sm"</c>), "plain" = ghost, "primary" filled with the accent (plan §9.1), an optional
    /// leading icon and the <c>loading</c> spinner.
    /// </summary>
    public static ArcButton Button(string text, Action? onClick, ButtonVariant variant = ButtonVariant.Secondary, LucideIconKind? icon = null,
        bool enabled = true, bool loading = false, ControlSize size = ControlSize.Small, ArcDesignTokenKind? foreground = null)
    {
        object content = icon is { } kind
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(kind), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } }
            : text;
        var button = new ArcButton { Content = content, Variant = variant, Size = size, IsEnabled = enabled, IsLoading = loading, VerticalAlignment = VerticalAlignment.Center };
        if (foreground is { } fg) button.Token(TemplatedControl.ForegroundProperty, fg);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The detail sheet's section heading (web <c>h4 text-[11px] font-medium text-[var(--text-secondary)]</c>).</summary>
    public static TextBlock Heading(string text) => Text(text, 11, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);

    /// <summary>A muted 11 px note (web <c>text-[11px] text-[var(--text-muted)]</c>), wrapping.</summary>
    public static TextBlock Note(string? text, double size = 11) => Text(text, size, ArcDesignTokenKind.TextMuted, wrap: true);

    /// <summary>Web <c>.card</c>: surface, hairline edge, panel radius.</summary>
    public static Border Card(Control child, Thickness padding = default)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = child, Padding = padding };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    /// <summary>Rows with a hairline between neighbours (web <c>divide-y</c>).</summary>
    public static StackPanel Divided(IEnumerable<Control> rows, ArcDesignTokenKind line = ArcDesignTokenKind.BorderSubtle)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
            {
                var rule = new Border { Height = 1 };
                rule.Token(Border.BackgroundProperty, line);
                stack.Children.Add(rule);
            }
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>A web <c>KV</c> row: label left (secondary), value right (selectable, trimmed; mono 11 px).</summary>
    public static Control KeyValue(string label, object? value, bool mono = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
        grid.Children.Add(Text(label, 12, ArcDesignTokenKind.TextSecondary));
        Control right = value as Control ?? Selectable(value as string ?? "", mono ? 11 : 12, mono);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Selectable text (web <c>selectable</c>).</summary>
    public static SelectableTextBlock Selectable(string text, double size, bool mono = false, ArcDesignTokenKind color = ArcDesignTokenKind.Foreground)
    {
        var block = new SelectableTextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        block.Token(TextBlock.ForegroundProperty, color);
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>A continuous quota bar (web <c>QuotaBar</c>): a 6 px pill track, the used share filled in the bar's tone.</summary>
    public static Control ProgressBar(int percent, TextTone tone, string? label)
    {
        var track = new Border { Height = 6, ClipToBounds = true, CornerRadius = new CornerRadius(3) };
        track.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.Token(Border.BackgroundProperty, TokenOf(tone));
        track.Child = fill;
        // The fill's width follows the track's (CSS width: n%).
        track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * percent / 100.0;
        if (label is not null) Avalonia.Automation.AutomationProperties.SetName(track, label);
        return track;
    }

    /// <summary>The card's segmented strip bar: ten 5 px segments with 3 px gaps, the lit ones in the bar's tone.</summary>
    public static Control Segments(QuotaBar bar)
    {
        var grid = new Grid { ColumnSpacing = 3, Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < QuotaViews.Segments; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var seg = new Border { Height = 5, CornerRadius = new CornerRadius(2) };
            seg.Token(Border.BackgroundProperty, i < bar.Lit ? TokenOf(bar.Tone) : ArcDesignTokenKind.SurfaceMuted);
            Grid.SetColumn(seg, i);
            grid.Children.Add(seg);
        }
        Avalonia.Automation.AutomationProperties.SetName(grid, bar.Label);
        return grid;
    }
}
