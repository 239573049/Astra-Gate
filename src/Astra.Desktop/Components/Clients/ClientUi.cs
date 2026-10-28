using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Requests;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// Builders the Clients page uses so its code reads like the web markup it ports (web/src/components/ui/controls.tsx): a <c>Button</c>
/// with its variant / size / icon / loading, the <c>Select</c> over (value, label, disabled) options, an <c>Alert</c>, a monospace code box,
/// small texts at the web's pixel sizes. Colors, radii and sizes are Arc tokens.
/// </summary>
internal static class ClientUi
{
    /// <summary>
    /// A button (web <c>Button</c>): primary fills with the accent (plan §9.1), plain = Arc ghost, glass = secondary, destructive = danger.
    /// <paramref name="icon"/> is a leading control (a Lucide glyph, a spinner).
    /// </summary>
    public static ArcButton Button(string? text, Action? onClick, ButtonVariant variant = ButtonVariant.Secondary, bool small = false, Control? icon = null, bool enabled = true)
    {
        var button = new ArcButton { Variant = variant, Size = small ? ControlSize.Small : ControlSize.Medium, IsEnabled = enabled };
        if (icon is not null && text is null) button.Content = icon;
        else if (icon is not null)
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        else button.Content = text;
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A Lucide glyph.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size = 14, ArcDesignTokenKind? color = null)
    {
        var icon = new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) icon.Token(TemplatedControl.ForegroundProperty, c);
        return icon;
    }

    /// <summary>The small spinning activity glyph (web <c>ProgressSpinner</c>).</summary>
    public static Control Spinner(double size = 14)
    {
        var icon = new SpinningIcon { Kind = LucideIconKind.LoaderCircle, Size = size, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return icon;
    }

    /// <summary>
    /// A select (web <c>Select</c>) over (value, label, disabled) options; <paramref name="onChange"/> fires only for the user's picks.
    /// Arc's 180 px minimum is reset and the select never scrolls the page to itself (plan §30 trap).
    /// </summary>
    public static Select Select(IEnumerable<(string Value, string Label, bool Disabled)> options, string? selected, Action<string> onChange, string? name = null, string? placeholder = null, double? width = null, double? maxWidth = null)
    {
        var control = new Select
        {
            ItemsSource = options.Select(o => new SelectOption(o.Value, o.Label, o.Disabled)).ToList(),
            Placeholder = placeholder,
            MinWidth = 0,
            AutoScrollToSelectedItem = false,
        };
        if (width is { } w) control.Width = w;
        // web `max-w-[300px]` on a select that is otherwise as wide as its current label (Arc Select sizes to its content)
        if (maxWidth is { } m) control.MaxWidth = m;
        if (name is not null) Avalonia.Automation.AutomationProperties.SetName(control, name);
        if (selected is not null) control.SelectedValue = selected;
        var current = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is not string value || value == current) return;
            current = value;
            onChange(value);
        };
        return control;
    }

    /// <summary>Plain (value, label) options with none disabled.</summary>
    public static IEnumerable<(string, string, bool)> Options(IEnumerable<(string Value, string Label)> options) => options.Select(o => (o.Value, o.Label, false));

    /// <summary>An Arc alert (web <c>Alert tone title</c>) with an optional body.</summary>
    public static Alert Alert(AlertTone tone, string title, object? body = null)
    {
        var alert = new Alert { Tone = tone, Title = title };
        // A string is Arc's own description line (web `.description`: 11 px, secondary, line height from the alert tokens).
        alert.Content = body;
        return alert;
    }

    /// <summary>Wrapping text of a token size and color.</summary>
    public static TextBlock Wrap(string? text, ArcDesignTokenKind size, ArcDesignTokenKind color, double? px = null)
    {
        var block = KitStyle.Text(text, size, color);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        if (px is { } p) block.FontSize = p;
        return block;
    }

    /// <summary>Single-line text at a pixel size (web <c>text-[Npx]</c>).</summary>
    public static TextBlock Text(string? text, double px, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, color, weight);
        block.FontSize = px;
        return block;
    }

    /// <summary>
    /// The web's code box (<c>rounded border bg-surface-muted px-3 py-2 font-mono text-[12px] break-all whitespace-pre-wrap selectable</c>).
    /// </summary>
    /// <param name="relaxed">Web <c>leading-relaxed</c> (1.625, the install log); otherwise the body's 1.45.</param>
    public static Border CodeBox(string text, double px = 12, double? maxHeight = null, double? minHeight = null, bool relaxed = false)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = px, LineHeight = Math.Round(px * (relaxed ? 1.625 : 1.45)) };
        block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        Control content = block;
        if (maxHeight is not null) content = new ScrollViewer { Content = block, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12, 8), Child = content }; // web `border` = 1px
        if (maxHeight is { } mh) box.MaxHeight = mh;
        if (minHeight is { } mn) box.MinHeight = mn;
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        return box;
    }

    /// <summary>A horizontal row of controls with a gap.</summary>
    public static StackPanel HRow(double spacing, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    /// <summary>Makes a free control (not a row) clickable with the hand cursor.</summary>
    public static T Clickable<T>(this T control, Action onClick) where T : Control
    {
        control.Cursor = new Cursor(StandardCursorType.Hand);
        control.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) onClick();
        };
        return control;
    }
}
