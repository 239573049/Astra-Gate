using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Tray;

/// <summary>
/// The small pieces the tray panel is drawn with (web components/tray/TrayPanelView.tsx: <c>IconButton</c>, <c>Pill</c>, <c>Alert</c>, <c>Chip</c>,
/// <c>Tile</c>, the plain text buttons). The panel is a compact menu-bar surface with its own sizes (12.5 / 13 / 14 / 20 px text, 32px icon
/// buttons, 7–10px radii), which Arc's form controls do not come in, so they are composed from Avalonia primitives bound to Arc tokens.
/// </summary>
internal static class TrayUi
{
    /// <summary>Text with a literal size (the panel's Tailwind sizes) and a token color; trims like <c>truncate</c>.</summary>
    public static TextBlock Text(string? text, double size, ArcDesignTokenKind color = ArcDesignTokenKind.Foreground, FontWeight weight = FontWeight.Normal)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        return t.Token(TextBlock.ForegroundProperty, color);
    }

    /// <summary>A Lucide glyph in a token color.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size, ArcDesignTokenKind? color = null)
    {
        var icon = new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) icon.Token(LucideIcon.ForegroundProperty, c);
        return icon;
    }

    /// <summary>
    /// A clickable area with the web's hover (<c>hover:bg-[var(--surface-muted)]</c>) and disabled (<c>opacity-40</c>) looks; a tooltip and an
    /// accessible name. The default cursor, like the web's <c>cursor-default</c> (a menu-bar surface, not a page).
    /// </summary>
    public static Border Clickable(Control content, Action? onClick, string? label = null, bool enabled = true, bool hoverFill = true)
    {
        var box = new Border { Child = content, Background = Brushes.Transparent, Focusable = enabled && onClick is not null };
        if (label is not null)
        {
            ToolTip.SetTip(box, label);
            AutomationProperties.SetName(box, label);
        }
        if (!enabled) box.Opacity = 0.4;
        if (!enabled || onClick is null) return box;
        box.PointerEntered += (_, _) =>
        {
            if (hoverFill) Fill(box, ArcDesignTokenKind.SurfaceMuted);
        };
        box.PointerExited += (_, _) =>
        {
            if (hoverFill) box.Background = Brushes.Transparent;
        };
        box.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || !box.IsPointerOver) return;
            e.Handled = true;
            onClick();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return box;
    }

    /// <summary>
    /// Fills a border with a token color. The token binding does not win over a local value, and <see cref="Clickable"/> sets a local transparent
    /// background (so the whole box is hit-testable), so the local value is cleared first.
    /// </summary>
    public static T Fill<T>(T border, ArcDesignTokenKind token) where T : Border
    {
        border.ClearValue(Border.BackgroundProperty);
        return border.Token(Border.BackgroundProperty, token);
    }

    /// <summary>The 32px square icon button (web <c>IconButton</c>: 8px radius, secondary glyph).</summary>
    public static Border IconButton(LucideIconKind icon, string label, Action? onClick, bool enabled = true, double glyph = 16)
    {
        var b = Clickable(Icon(icon, glyph, ArcDesignTokenKind.TextSecondary), onClick, label, enabled);
        b.Width = 32;
        b.Height = 32;
        b.CornerRadius = new CornerRadius(8);
        ((Control)b.Child!).HorizontalAlignment = HorizontalAlignment.Center;
        return b;
    }

    /// <summary>A color of the tone with an alpha (web <c>color-mix(in oklab, color N%, transparent)</c>, which for a mix with transparent is just the alpha).</summary>
    public static ArcDesignTokenKind ToneToken(Tone tone) => tone switch
    {
        Tone.Red => ArcDesignTokenKind.Danger,
        Tone.Orange => ArcDesignTokenKind.Warning,
        Tone.Green => ArcDesignTokenKind.Success,
        Tone.Accent => ArcDesignTokenKind.Accent,
        _ => ArcDesignTokenKind.TextSecondary,
    };

    /// <summary>A rounded box tinted with a tone: text in the color, border at 45%, fill at 10% (web <c>Pill</c> / <c>Alert</c>).</summary>
    public static TintBorder Tinted(Tone tone, Control content, double radius, Thickness padding) =>
        new(ToneToken(tone)) { Child = content, CornerRadius = new CornerRadius(radius), Padding = padding, BorderThickness = new Thickness(1) };

    /// <summary>A horizontal row with a gap.</summary>
    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A vertical stack with a gap.</summary>
    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A hairline above (web <c>border-t border-[var(--border)]</c>).</summary>
    public static Border TopRule(Control content, double paddingTop)
    {
        var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, paddingTop, 0, 0), Child = content };
        return b.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
    }

    /// <summary>A grid with fixed / star columns (web <c>grid-cols-[…]</c>) and a column gap.</summary>
    public static Grid Columns(string columns, double gap, params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(columns), ColumnSpacing = gap };
        var col = 0;
        foreach (var c in cells)
        {
            var span = Avalonia.Controls.Grid.GetColumnSpan(c);
            Avalonia.Controls.Grid.SetColumn(c, col);
            g.Children.Add(c);
            col += Math.Max(1, span);
        }
        return g;
    }
}

/// <summary>A border whose fill and outline are a token color at 10% / 45% alpha, re-resolved when the theme changes.</summary>
internal sealed class TintBorder : Border
{
    private readonly ArcDesignTokenKind _token;

    public TintBorder(ArcDesignTokenKind token) => _token = token;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnTheme;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var c = ArcTokens.GetColor(this, _token);
        Background = new SolidColorBrush(Color.FromArgb((byte)(0.10 * 255), c.R, c.G, c.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(0.45 * 255), c.R, c.G, c.B));
    }
}

/// <summary>A fill that is a token color at an alpha (the chart's <c>color-mix(foreground 32%)</c> bars), re-resolved when the theme changes.</summary>
internal sealed class AlphaFill : Border
{
    private readonly ArcDesignTokenKind _token;
    private readonly double _alpha;

    public AlphaFill(ArcDesignTokenKind token, double alpha)
    {
        _token = token;
        _alpha = alpha;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnTheme;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var c = ArcTokens.GetColor(this, _token);
        Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_alpha * 255), c.R, c.G, c.B));
    }
}
