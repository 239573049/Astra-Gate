using System.ComponentModel;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// Small builders the Plugins and Smart routing pages share, so their code reads like the web markup it ports: a button in the
/// web's <c>Button</c> vocabulary (variant, size, icon), muted and monospace text at the web's pixel sizes, a bordered card,
/// a link-looking text. Every color, radius and font size comes from an Arc token (follows light/dark and the accent).
/// </summary>
internal static class PageUi
{
    /// <summary>A button: web <c>Button</c> (variant plain = Arc ghost, glass = secondary, destructive = danger) with an optional leading icon.</summary>
    public static ArcButton Button(string? text, Action? onClick, ButtonVariant variant = ButtonVariant.Secondary, ControlSize size = ControlSize.Medium, LucideIconKind? icon = null, bool enabled = true)
    {
        var button = new ArcButton { Variant = variant, Size = size, IsEnabled = enabled };
        if (text is null && icon is { } only) button.Content = Icon(only, size == ControlSize.Small ? 14 : 16);
        else if (icon is { } lead)
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(lead, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        else button.Content = text;
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A square icon-only ghost button with an accessible name (web <c>Button variant="plain" aria-label</c>).</summary>
    public static ArcButton IconButton(LucideIconKind icon, string label, Action? onClick, ControlSize size = ControlSize.Small, bool enabled = true)
    {
        var button = Button(null, onClick, ButtonVariant.Ghost, size, icon, enabled);
        button.Padding = new Thickness(0);
        button.Width = size == ControlSize.Small ? 28 : 32;
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    /// <summary>
    /// Keeps a self-scrolling control (a segmented control, tabs) from scrolling the page: a <c>SelectingItemsControl</c> asks to bring its
    /// selected item into view and that request bubbles out of its own scroller into the page's, which then jumps to the control. The
    /// control's own scroller has already used the request by the time it reaches here. Requests from other content inside the control
    /// (a tab page's focused field) still travel on.
    /// </summary>
    public static T ContainBringIntoView<T>(this T control) where T : ItemsControl
    {
        control.AddHandler(Control.RequestBringIntoViewEvent, (object? _, RequestBringIntoViewEventArgs e) =>
        {
            if (e.TargetObject is Control target && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(target), control)) e.Handled = true;
        });
        return control;
    }

    /// <summary>A Lucide glyph of <paramref name="size"/> px in the current foreground.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size = 14) => new() { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Text of a token size and color.</summary>
    public static TextBlock Text(string? text, ArcDesignTokenKind size, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal, bool wrap = false, bool mono = false)
    {
        var block = KitStyle.Text(text, size, color, weight);
        if (wrap)
        {
            block.TextWrapping = TextWrapping.Wrap;
            block.TextTrimming = TextTrimming.None;
        }
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>Web <c>text-[12px] text-[var(--text-muted)]</c> (notes under a list).</summary>
    public static TextBlock Note(string? text)
    {
        var block = Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, wrap: true);
        block.FontSize = KitStyle.Type12;
        block.Margin = new Thickness(4, 0);
        return block;
    }

    /// <summary>Selectable text (web <c>selectable</c>): values a user may copy.</summary>
    public static SelectableTextBlock Selectable(string? text, ArcDesignTokenKind size, ArcDesignTokenKind color, bool mono = false)
    {
        var block = new SelectableTextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        block.Token(TextBlock.FontSizeProperty, size).Token(TextBlock.ForegroundProperty, color);
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>
    /// Web <c>.card</c>: the surface fill, a 0.5 px edge and the panel radius, clipping its content to the corners. Rows inside are
    /// separated by <see cref="Divided"/>.
    /// </summary>
    public static Border Card(Control child, Thickness? padding = null)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = child, Padding = padding ?? new Thickness(0) };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    /// <summary>Web <c>divide-y divide-[var(--border-subtle)]</c>: the rows with a hairline between neighbours.</summary>
    public static StackPanel Divided(IEnumerable<Control> rows)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
            {
                var line = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
                line.Token(Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
                stack.Children.Add(line);
            }
            first = false;
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>A vertical stack with the web's <c>gap-n</c>.</summary>
    public static StackPanel VStack(double spacing, params Control[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var c in children) stack.Children.Add(c);
        return stack;
    }

    /// <summary>A horizontal wrapping line (web <c>flex flex-wrap items-center gap-x gap-y</c>).</summary>
    public static WrapPanel Wrap(double horizontal, double vertical, params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = horizontal, LineSpacing = vertical };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    /// <summary>
    /// An Arc alert (web <c>Alert</c>): a tone, a title and optional content. A string is Arc's own description line (web
    /// <c>.description</c>: 11 px, secondary); a control should be built from <see cref="AlertText"/> so it inherits the same.
    /// </summary>
    public static Alert Alert(AlertTone tone, string? title, object? content = null)
        => new() { Tone = tone, Title = title, Content = content };

    /// <summary>
    /// Text inside an alert's description (web: children of <c>.description</c>): size and color are inherited from the alert (11 px,
    /// <c>--text-secondary</c>) unless given, with the web's 1.45 line height.
    /// </summary>
    public static TextBlock AlertText(string? text, ArcDesignTokenKind? color = null, double? size = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = Math.Round((size ?? 11) * 1.45) };
        if (color is { } c) block.Token(TextBlock.ForegroundProperty, c);
        if (size is { } px) block.FontSize = px;
        return block;
    }

    /// <summary>A bulleted list for an alert (web <c>ul.list-disc.pl-4</c>), in the alert's inherited description text.</summary>
    public static Control Bullets(IEnumerable<string> lines, double spacing = 2)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var line in lines)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
            var dot = AlertText("•");
            dot.VerticalAlignment = VerticalAlignment.Top;
            var text = AlertText(line);
            text.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>Accent-colored text that acts like a link (web <c>text-[var(--accent)] hover:underline</c>).</summary>
    public static TextBlock Link(string text, Action onClick, double size = 12)
    {
        var link = new TextBlock { Text = text, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center, Focusable = true };
        link.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        link.FontSize = size;
        link.LineHeight = Math.Round(size * 1.45); // the web's body line height
        link.PointerEntered += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.PointerExited += (_, _) => link.TextDecorations = null;
        link.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        link.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return link;
    }

    /// <summary>Arc badge tone for a plugin tone.</summary>
    public static Tone ToKitTone(PluginTone tone) => tone switch
    {
        PluginTone.Accent => Tone.Accent,
        PluginTone.Green => Tone.Green,
        PluginTone.Orange => Tone.Orange,
        _ => Tone.Neutral,
    };

    /// <summary>A badge for a plugin badge model.</summary>
    public static Badge Badge(PluginBadge badge) => KitBadge.Create(badge.Text, ToKitTone(badge.Tone));

    /// <summary>
    /// A text input inside a row or toolbar, the way web <c>Input</c> sits there: no visible label (the label is the accessible name),
    /// an optional placeholder, monospace for ids and keys.
    /// </summary>
    public static Input Field(string name, string? text, Action<string> changed, string? placeholder = null, bool mono = false, double? width = null)
    {
        var input = new Input { Label = name, IsLabelVisible = false, Placeholder = placeholder, Text = text ?? "" };
        if (width is { } w) input.Width = w;
        if (mono) input.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        input.TextChanged += (_, _) => changed(input.Text ?? "");
        return input;
    }

    /// <summary>A multi-line text input (web <c>TextArea</c>) of about <paramref name="rows"/> rows.</summary>
    public static Textarea Area(string name, string? text, Action<string> changed, int rows, string? placeholder = null, bool mono = false)
    {
        var area = new Textarea { Label = name, IsLabelVisible = false, Placeholder = placeholder, Text = text ?? "" };
        // Arc's textarea draws at least ~110 px whatever Height says (a shorter one overlaps what follows), so only taller boxes get a height.
        if (16 + rows * 20 > 110) area.Height = 16 + rows * 20;
        if (mono) area.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        area.TextChanged += (_, _) => changed(area.Text ?? "");
        return area;
    }

    /// <summary>Subscribes to <paramref name="source"/> until the control leaves the tree; used by the views' watchers.</summary>
    public static IDisposable On(INotifyPropertyChanged source, PropertyChangedEventHandler handler)
    {
        source.PropertyChanged += handler;
        return new Unsub(() => source.PropertyChanged -= handler);
    }

    private sealed class Unsub(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>
/// A view whose content is rebuilt from a model: it watches the sources it names while it is in the visual tree (and rebuilds once on
/// attach and on a language switch), so a page never holds a subscription to a query of a page that is gone.
/// </summary>
internal abstract class LiveView : UserControl
{
    private readonly List<(INotifyPropertyChanged Source, Func<string?, bool> Relevant)> _sources = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _attached;

    /// <summary>Rebuild when <paramref name="source"/> raises a property for which <paramref name="relevant"/> says yes (null: any).</summary>
    protected void Watch(INotifyPropertyChanged source, params string[] properties)
    {
        _sources.Add((source, name => properties.Length == 0 || (name is not null && properties.Contains(name))));
        if (_attached) Subscribe(_sources[^1]);
    }

    /// <summary>Builds the content from the model's current state.</summary>
    protected abstract void Rebuild();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        foreach (var s in _sources) Subscribe(s);
        Loc.LanguageChanged += OnLanguage;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        Loc.LanguageChanged -= OnLanguage;
        Detached();
    }

    /// <summary>The view left the visual tree: release anything <see cref="Rebuild"/> subscribed to.</summary>
    protected virtual void Detached() { }

    private void Subscribe((INotifyPropertyChanged Source, Func<string?, bool> Relevant) s)
        => _subscriptions.Add(PageUi.On(s.Source, (_, e) =>
        {
            if (s.Relevant(e.PropertyName)) Rebuild();
        }));

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
}
