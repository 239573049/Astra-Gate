using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// Small builders the Settings and Privacy pages share, in the web's vocabulary (<c>Button size="sm"</c>, a muted 12px note, a
/// right-aligned form footer). They only compose Arc controls and kit tokens; nothing here is a control of its own.
/// </summary>
public static class Ui
{
    /// <summary>A small button: text and/or a Lucide icon (web <c>Button size="sm"</c>; "plain" = <see cref="ButtonVariant.Ghost"/>).</summary>
    public static ArcButton Button(
        string? text, Action onClick, ButtonVariant variant = ButtonVariant.Secondary, LucideIconKind? icon = null, bool loading = false,
        bool enabled = true, string? tip = null, ControlSize size = ControlSize.Small)
    {
        object? content = text;
        if (icon is { } kind)
        {
            var glyph = new LucideIcon { Kind = kind, Size = 14 };
            content = text is null
                ? glyph
                : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        }
        var button = new ArcButton { Content = content, Variant = variant, Size = size, IsLoading = loading, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
            Avalonia.Automation.AutomationProperties.SetName(button, tip);
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>An icon-only ghost button (web <c>Button variant="plain" icon aria-label</c>).</summary>
    public static ArcButton IconButton(LucideIconKind icon, string label, Action onClick, bool enabled = true)
        => Button(null, onClick, ButtonVariant.Ghost, icon, enabled: enabled, tip: label);

    /// <summary>A muted explanation line (web <c>text-[12px] text-[var(--text-muted)]</c>).</summary>
    public static TextBlock Note(string text, ArcDesignTokenKind color = ArcDesignTokenKind.TextMuted, double size = KitStyle.Type12)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);
        block.FontSize = size;
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>A text field of the form width the web uses (<c>className="w-64"</c>); password fields use Arc's password field.</summary>
    public static Input Field(string label, string text, Action<string> onChange, double width = 256, bool mono = false, string? placeholder = null)
    {
        var input = new Input { Text = text, Width = width, PlaceholderText = placeholder, Size = ControlSize.Small };
        if (mono) input.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        input.TextChanged += (_, _) => onChange(input.Text ?? "");
        return input;
    }

    /// <summary>A password box (web <c>&lt;Input type="password"&gt;</c>: masked, no reveal button); typed text only leaves through <paramref name="onChange"/>.</summary>
    public static Input Secret(string label, string text, Action<string> onChange, double width = 256, string? placeholder = null, bool mono = false)
    {
        var input = new Input { PasswordChar = '•', Text = text, Width = width, PlaceholderText = placeholder, Size = ControlSize.Small };
        if (mono) input.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        input.TextChanged += (_, _) => onChange(input.Text ?? "");
        return input;
    }

    /// <summary>A row of buttons at the right edge of a group (web <c>flex justify-end gap-2 px-4 py-2</c>).</summary>
    public static Control ButtonBar(IEnumerable<Control> buttons, HorizontalAlignment align = HorizontalAlignment.Right)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = align };
        foreach (var b in buttons) panel.Children.Add(b);
        return new Border { Padding = new Thickness(16, 8), Child = panel };
    }

    /// <summary>A full-width line inside a group card with the web's <c>px-4 py-2</c> padding (a note, a button).</summary>
    public static Control GroupLine(Control content, double top = 8, double bottom = 8)
    {
        // A button on its own line keeps its natural width at the left (web: <div className="px-4 py-2"><Button/></div>).
        if (content is ArcButton) content.HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Padding = new Thickness(16, top, 16, bottom), Child = content };
    }

    /// <summary>A line of text inside a group card (web <c>p className="px-4 py-2 text-[12px] ..."</c>).</summary>
    public static Control GroupNote(string text, ArcDesignTokenKind color = ArcDesignTokenKind.TextMuted, double size = KitStyle.Type12) => GroupLine(Note(text, color, size));

    /// <summary>An alert line inside a group card (web: an <c>Alert</c> in <c>px-3 py-2</c>).</summary>
    public static Control GroupAlert(AlertTone tone, string? title, string? body = null)
        => new Border { Padding = new Thickness(12, 8), Child = new Alert { Tone = tone, Title = title, Content = body } };

    /// <summary>The red error text of a failed query inside a page (web <c>text-[12px] text-[var(--danger)]</c>).</summary>
    public static Control ErrorText(string text)
    {
        var note = Note(text, ArcDesignTokenKind.Danger);
        note.Margin = new Thickness(0, 0, 0, 20);
        return note;
    }

    /// <summary>The kit tone of a logic <see cref="Astra.Desktop.Core.Settings.DotTone"/>.</summary>
    public static Tone ToKit(Astra.Desktop.Core.Settings.DotTone tone) => tone switch
    {
        Astra.Desktop.Core.Settings.DotTone.Green => Tone.Green,
        Astra.Desktop.Core.Settings.DotTone.Orange => Tone.Orange,
        Astra.Desktop.Core.Settings.DotTone.Red => Tone.Red,
        _ => Tone.Neutral,
    };

    /// <summary>A status dot of a <see cref="Astra.Desktop.Core.Settings.DotTone"/>.</summary>
    public static StatusDot Dot(Astra.Desktop.Core.Settings.DotTone tone) => new(ToKit(tone));

    /// <summary>One entry of <see cref="Menu"/>.</summary>
    public sealed record MenuItem(string Label, LucideIconKind? Icon, Action OnSelect, bool Destructive = false, bool SeparatorBefore = false);

    /// <summary>A "more" dropdown (web <c>Menu</c> with <c>items</c>): an icon button that opens the entries.</summary>
    public static DropdownMenu Menu(string label, LucideIconKind icon, IEnumerable<MenuItem> items)
    {
        var menu = new DropdownMenu { Trigger = IconButton(icon, label, () => { }) };
        foreach (var item in items)
        {
            var entry = new MenuEntry { Header = item.Label, IsDestructive = item.Destructive, HasSeparatorBefore = item.SeparatorBefore };
            if (item.Icon is { } kind) entry.Icon = Lucide.Geometry(kind);
            var select = item.OnSelect;
            entry.Click += (_, _) => select();
            menu.Items.Add(entry);
        }
        return menu;
    }

    /// <summary>A badge from a logic tone name (neutral, accent, green, orange, red).</summary>
    public static Badge Badge(string text, string tone) => KitBadge.Create(text, tone switch
    {
        "accent" => Tone.Accent,
        "green" => Tone.Green,
        "orange" => Tone.Orange,
        "red" => Tone.Red,
        _ => Tone.Neutral,
    });

    /// <summary>The small busy mark of a row whose data is loading (web <c>Spinner className="size-3.5"</c>).</summary>
    public static Control Spinner()
    {
        var icon = new LucideIcon { Kind = LucideIconKind.LoaderCircle, Size = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        return icon;
    }

    /// <summary>A vertical stack with a gap.</summary>
    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    /// <summary>A horizontal stack, vertically centred.</summary>
    public static StackPanel Inline(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    /// <summary>A card like the web's <c>.card</c> (surface, hairline edge, panel radius) around <paramref name="content"/>.</summary>
    public static Border Card(Control content)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = content };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    /// <summary>A hairline across the width of its container.</summary>
    public static Border Divider()
    {
        var line = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    /// <summary>A form field's caption + control + optional hint (web <c>Field</c>).</summary>
    public static Control Labeled(string label, Control editor, string? hint = null)
    {
        var stack = Stack(6, KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, FontWeight.Medium), editor);
        ((TextBlock)stack.Children[0]).FontSize = KitStyle.Type12;
        if (!string.IsNullOrEmpty(hint)) stack.Children.Add(Note(hint, ArcDesignTokenKind.TextSecondary));
        return stack;
    }
}
