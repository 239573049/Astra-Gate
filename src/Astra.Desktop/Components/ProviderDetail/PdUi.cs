using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The web vocabulary the provider detail page is written in (web/src/components/ui/controls.tsx: <c>Button</c> variants, <c>Input</c>,
/// <c>NumberInput</c>, <c>Select</c>, <c>Switch</c>, <c>Badge</c>, <c>Row</c>, the <c>.card</c> look), composed from Arc controls and kit tokens.
/// Nothing here is a control of its own.
/// </summary>
public static class PdUi
{
    /// <summary>Web <c>Button</c> variants (primary = accent fill, glass = the default bordered one, plain = no chrome).</summary>
    public enum Look
    {
        /// <summary>The main action (accent fill).</summary>
        Primary,

        /// <summary>The default bordered button.</summary>
        Glass,

        /// <summary>No chrome.</summary>
        Plain,
    }

    /// <summary>A button; <paramref name="text"/> null makes it icon-only with <paramref name="name"/> as tooltip and accessible name.</summary>
    public static ArcButton Button(string? text, Look look = Look.Glass, LucideIconKind? icon = null, bool small = false, Action? click = null, string? name = null)
    {
        var button = new ArcButton
        {
            Variant = look switch { Look.Primary => ButtonVariant.Primary, Look.Plain => ButtonVariant.Ghost, _ => ButtonVariant.Secondary },
            Size = small ? ControlSize.Small : ControlSize.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (text is null)
        {
            button.Padding = new Thickness(0);
            button.Width = small ? 28 : 32;
            button.Content = icon is { } i ? Icon(i, 14) : null;
        }
        else if (icon is { } k)
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(k, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        else button.Content = text;
        var label = name ?? text;
        if (label is not null) AutomationProperties.SetName(button, label);
        if (text is null && name is not null) ToolTip.SetTip(button, name);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A Lucide glyph in the current foreground.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size) => new() { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A Lucide glyph in a token color.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size, ArcDesignTokenKind color)
    {
        var icon = Icon(kind, size);
        icon.Token(TemplatedControl.ForegroundProperty, color);
        return icon;
    }

    /// <summary>A text field with its accessible name (web <c>Input aria-label</c>); <paramref name="mono"/> = the monospace look.</summary>
    public static Input TextBox(string? text, Action<string>? changed, double? width = null, bool mono = false, string? name = null, string? placeholder = null, bool visibleLabel = false)
    {
        var input = new Input { Text = text ?? "", Label = name, IsLabelVisible = visibleLabel, Placeholder = placeholder, VerticalAlignment = VerticalAlignment.Center };
        if (width is { } w) input.Width = w;
        if (mono) Mono(input);
        if (name is not null) AutomationProperties.SetName(input, name);
        if (changed is not null) input.TextChanged += (_, _) => changed(input.Text ?? "");
        return input;
    }

    /// <summary>A password field (web <c>Input type="password"</c>).</summary>
    public static PasswordField Secret(string? text, Action<string> changed, double width, string name, string? placeholder = null)
    {
        var input = new PasswordField { Text = text ?? "", Width = width, Label = name, IsLabelVisible = false, Placeholder = placeholder, VerticalAlignment = VerticalAlignment.Center };
        Mono(input);
        AutomationProperties.SetName(input, name);
        input.TextChanged += (_, _) => changed(input.Text ?? "");
        return input;
    }

    /// <summary>The monospace look (web <c>font-mono</c>).</summary>
    public static T Mono<T>(T control) where T : Avalonia.Controls.TextBox
    {
        control.Token(Avalonia.Controls.TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        control.FontSize = 12;
        return control;
    }

    /// <summary>A multi-line text box (web <c>TextArea</c>).</summary>
    public static Textarea Area(string? text, Action<string>? changed, string? label, double height, bool mono = false, string? placeholder = null)
    {
        var area = new Textarea { Text = text ?? "", Label = label, IsLabelVisible = label is not null, Placeholder = placeholder, FrameHeight = height, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        if (mono) Mono(area);
        if (label is not null) AutomationProperties.SetName(area, label);
        if (changed is not null) area.TextChanged += (_, _) => changed(area.Text ?? "");
        return area;
    }

    /// <summary>An optional decimal (web <c>NumberInput</c>; empty = null).</summary>
    public static NumberBox Number(decimal? value, Action<decimal?> changed, double? width, string name, bool integer = false, decimal? minimum = null, string? placeholder = null)
    {
        var box = new NumberBox { IsInteger = integer, Minimum = minimum, VerticalAlignment = VerticalAlignment.Center, Label = name, Placeholder = placeholder };
        if (width is { } w) box.Width = w;
        AutomationProperties.SetName(box, name);
        box.Value = value;
        box.ValueEdited += (_, v) => changed(v);
        return box;
    }

    /// <summary>A select (web <c>Select</c>) over (value, label, detail) options; <paramref name="changed"/> fires only for the person's pick.</summary>
    public static Select Choice(IEnumerable<(string Value, string Label, string? Detail)> options, string? value, Action<string> changed, double? width = null, string? name = null, string? placeholder = null)
    {
        var list = options.Select(o => new SelectOption(o.Value, o.Label) { Meta = o.Detail, Keywords = o.Detail }).ToList();
        var select = new Select { ItemsSource = list, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, AutoScrollToSelectedItem = false };
        if (placeholder is not null) select.Placeholder = placeholder;
        if (width is { } w) select.Width = w;
        if (name is not null) AutomationProperties.SetName(select, name);
        if (value is not null && list.Any(o => Equals(o.Value, value))) select.SelectedValue = value;
        var current = value;
        select.SelectionChanged += (_, _) =>
        {
            if (select.SelectedValue is not string v || v == current) return;
            current = v;
            changed(v);
        };
        return select;
    }

    /// <summary>A select over (value, label) options.</summary>
    public static Select Choice(IEnumerable<(string Value, string Label)> options, string? value, Action<string> changed, double? width = null, string? name = null, string? placeholder = null)
        => Choice(options.Select(o => (o.Value, o.Label, (string?)null)), value, changed, width, name, placeholder);

    /// <summary>A switch (web <c>Switch</c>); <paramref name="changed"/> fires for the person's toggles.</summary>
    public static Switch Toggle(bool on, Action<bool> changed, string name)
    {
        var sw = new Switch { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(sw, name);
        sw.IsCheckedChanged += (_, _) => changed(sw.IsChecked == true);
        return sw;
    }

    /// <summary>A switch with its caption after it (web <c>label className="flex items-center gap-2 text-[12px]"</c>).</summary>
    public static Control LabeledToggle(bool on, Action<bool> changed, string label, bool captionFirst = false)
    {
        var caption = Text(label, 12, ArcDesignTokenKind.TextSecondary);
        var sw = Toggle(on, changed, label);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (captionFirst) { row.Children.Add(caption); row.Children.Add(sw); }
        else { row.Children.Add(sw); row.Children.Add(caption); }
        return row;
    }

    /// <summary>Text of a pixel size and token color; wraps when <paramref name="wrap"/>.</summary>
    public static TextBlock Text(string? text, double size, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal, bool wrap = false, bool mono = false)
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

    /// <summary>11px muted wrapped hint (web <c>text-[11px] text-[var(--text-muted)]</c>).</summary>
    public static TextBlock Hint(string text) => Text(text, 11, ArcDesignTokenKind.TextMuted, wrap: true);

    /// <summary>A small badge in a web tone.</summary>
    public static Badge Badge(string text, Tone tone = Tone.Neutral) => KitBadge.Create(text, tone);

    /// <summary>The web's <c>.card</c>: surface fill, hairline edge, panel radius.</summary>
    public static Border Card(Control content, Thickness? padding = null)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = content, Padding = padding ?? default };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    /// <summary>A hairline.</summary>
    public static Border Divider()
    {
        var line = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    /// <summary>Children separated by hairlines (web <c>divide-y</c>).</summary>
    public static StackPanel Divided(IEnumerable<Control> rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0) stack.Children.Add(Divider());
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>A kit row (web <c>Row</c>) with a label, an optional detail and trailing controls.</summary>
    /// <remarks>
    /// Every caller sits in a tab panel, where web <c>Row</c>'s uncolored 13 px label inherits text-secondary (arc tabs <c>.content</c>)
    /// and its 11 px detail is text-secondary, both at the inherited 1.4 line height; text is built here at those sizes rather than the
    /// kit row's 14 / 12 px foreground defaults, which made every row 7 px taller and darker than the web's.
    /// </remarks>
    public static SettingsRow Row(object? label, object? detail = null, params Control[] trailing)
    {
        if (label is string l) label = RowText(l, 13);
        if (detail is string d) detail = RowText(d, 11);
        var row = new SettingsRow { Label = label, Detail = detail };
        foreach (var c in trailing) row.Trailing.Add(c);
        return row;
    }

    /// <summary>Row text: <paramref name="size"/> px text-secondary at line height 1.4, cut with an ellipsis (web <c>truncate</c>).</summary>
    public static TextBlock RowText(string text, double size)
    {
        var block = Text(text, size, ArcDesignTokenKind.TextSecondary);
        block.LineHeight = size * 1.4;
        return block;
    }

    /// <summary>Content inside a group card with the web's <c>px-4 py-3</c> padding.</summary>
    public static Border Pad(Control content, double x = 16, double y = 12) => new() { Padding = new Thickness(x, y), Child = content };

    /// <summary>A horizontal, vertically centred stack.</summary>
    public static StackPanel Inline(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    /// <summary>A vertical stack with a gap.</summary>
    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    /// <summary>A form field: 12px caption, the control, an optional hint (web <c>Field</c>).</summary>
    public static Control Field(string label, Control editor, string? hint = null)
    {
        var stack = Stack(6, Text(label, 13, ArcDesignTokenKind.Foreground, FontWeight.Medium), editor);
        if (!string.IsNullOrEmpty(hint)) stack.Children.Add(Hint(hint));
        return stack;
    }

    /// <summary>A danger / warning / success alert.</summary>
    public static Alert Alert(AlertTone tone, string? title, object? content = null) => new() { Tone = tone, Title = title, Content = content };

    /// <summary>A sheet footer "Cancel" button that closes <paramref name="sheet"/>.</summary>
    public static ArcButton Cancel(Sheet sheet) => Button(Astra.Desktop.Core.I18n.Loc.T("common.cancel"), click: () => sheet.Close());
}

/// <summary>Inline configuration of a freshly built control.</summary>
internal static class PdExtensions
{
    /// <summary>Runs <paramref name="configure"/> on <paramref name="value"/> and returns it.</summary>
    public static T With<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
