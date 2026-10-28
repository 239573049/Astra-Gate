using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// A label on the left and a right-aligned value on the right (web <c>KV</c>: 12 px, <c>py-1</c>, value selectable and trimmed), where the value can be
/// any control (the protocol line, the returned-model tag). The kit's <see cref="KeyValueRow"/> only takes text.
/// </summary>
public sealed class DetailRow : UserControl
{
    /// <summary>A row with a text value.</summary>
    public DetailRow(string label, string? value, bool mono = false)
        : this(label, TextValue(value, mono))
    {
    }

    /// <summary>A row with a control as value.</summary>
    public DetailRow(string label, Control value)
    {
        var text = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        text.FontSize = KitStyle.Type12;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
        Grid.SetColumn(value, 1);
        grid.Children.Add(text);
        grid.Children.Add(value);
        Content = grid;
    }

    private static Control TextValue(string? value, bool mono)
    {
        var block = new SelectableTextBlock
        {
            Text = value ?? "",
            FontSize = mono ? 11 : KitStyle.Type12,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }
}

/// <summary>Token composition inside the request dialog (web <c>TokenUsageSummary</c>). The bars divide existing totals, never add to them.</summary>
public static class TokenUsageSummaryView
{
    /// <summary>Builds the section for <paramref name="usage"/>.</summary>
    public static Control Create(UsageSplit usage)
    {
        var section = new DetailSection { Title = Loc.T("usage.title") };
        if (!usage.Reported)
        {
            var note = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(10), Child = KitStyle.Text(Loc.T("usage.missing"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary) };
            note.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            ((TextBlock)note.Child).FontSize = KitStyle.Type12;
            ((TextBlock)note.Child).TextWrapping = TextWrapping.Wrap;
            section.Body = note;
            return section;
        }
        var card = new StackPanel { Spacing = 0 };
        var counters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 12 };
        AddCounter(counters, 0, Loc.T("usage.inputTotal"), TraceFormat.Tokens(usage.Input));
        AddCounter(counters, 1, Loc.T("usage.outputTotal"), TraceFormat.Tokens(usage.Output));
        AddCounter(counters, 2, Loc.T("usage.hitRate"), TraceFormat.Percent(usage.HitRatio));
        AddCounter(counters, 3, Loc.T("usage.reasoning"), TraceFormat.Tokens(usage.Reasoning));
        card.Children.Add(counters);
        card.Children.Add(Composition(Loc.T("usage.inputTotal"),
        [
            (Loc.T("usage.uncachedInput"), usage.UncachedInput, ArcDesignTokenKind.Accent),
            (Loc.T("usage.cacheRead"), usage.Read, ArcDesignTokenKind.Success),
            (Loc.T("usage.cacheWrite"), usage.Write, ArcDesignTokenKind.Warning),
        ]));
        card.Children.Add(Composition(Loc.T("usage.outputTotal"),
        [
            (Loc.T("usage.regularOutput"), usage.RegularOutput, ArcDesignTokenKind.TextSecondary),
            (Loc.T("usage.reasoning"), usage.Reasoning, ArcDesignTokenKind.Accent),
        ]));
        var hint = KitStyle.Text(Loc.T("usage.totalsHint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        hint.FontSize = 11;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 8, 0, 0);
        card.Children.Add(hint);
        section.Body = Card(card);
        return section;
    }

    /// <summary>The web's <c>.card</c>: the surface fill, a half-pixel separator border and a 14 px radius, with 16 px of padding.</summary>
    internal static Border Card(Control child)
    {
        var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(14), Child = child, ClipToBounds = true };
        // Web .card: the translucent --surface with a --separator hairline (WebMaterial; the theme's Surface token is opaque).
        return WebMaterial.Apply(card);
    }

    private static void AddCounter(Grid grid, int column, string label, string value)
    {
        var stack = new StackPanel { Spacing = 0 };
        var l = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        var v = new SelectableTextBlock { Text = value, FontSize = 17, FontWeight = FontWeight.Medium, FontFeatures = FontFeatureCollection.Parse("tnum") };
        v.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        stack.Children.Add(l);
        stack.Children.Add(v);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static Control Composition(string label, (string Label, long Count, ArcDesignTokenKind Color)[] segments)
    {
        var total = segments.Sum(s => s.Count);
        var bar = new Grid { Height = 6, ClipToBounds = true };
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        bar.Children.Add(track);
        if (total > 0)
        {
            var fill = new Grid { ClipToBounds = true };
            var visible = segments.Where(s => s.Count > 0).ToArray();
            for (var i = 0; i < visible.Length; i++)
            {
                fill.ColumnDefinitions.Add(new ColumnDefinition(visible[i].Count, GridUnitType.Star));
                var part = new Border();
                part.Token(Border.BackgroundProperty, visible[i].Color);
                Grid.SetColumn(part, i);
                fill.Children.Add(part);
            }
            var clip = new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = fill };
            bar.Children.Add(clip);
        }
        Avalonia.Automation.AutomationProperties.SetName(bar, label);
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var s in segments)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            dot.Token(Border.BackgroundProperty, s.Color);
            item.Children.Add(dot);
            var text = new SelectableTextBlock { FontSize = 11, FontFeatures = FontFeatureCollection.Parse("tnum"), VerticalAlignment = VerticalAlignment.Center };
            text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            text.Inlines!.Add(new Run(s.Label + " "));
            text.Inlines.Add(new Run(TraceFormat.Tokens(s.Count)));
            item.Children.Add(text);
            legend.Children.Add(item);
        }
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(bar);
        stack.Children.Add(legend);
        return stack;
    }
}

/// <summary>One captured body (web <c>RequestBodyPanel</c>): the preview the detail carries, and for a cut one a "load all" that fetches the whole part as plain text.</summary>
public sealed class RequestBodyPanel : UserControl
{
    private readonly RequestBodyViewModel _body;
    private readonly string _preview;
    private readonly long? _size;
    private readonly long? _limit;

    /// <summary>A panel over <paramref name="body"/>; <paramref name="preview"/> is what the detail carried, <paramref name="size"/> the part's full size and <paramref name="limit"/> the preview cut.</summary>
    public RequestBodyPanel(RequestBodyViewModel body, string preview, long? size, long? limit)
    {
        _body = body;
        _preview = preview;
        _size = size;
        _limit = limit;
        Render();
        body.Query.PropertyChanged += OnQuery;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _body.Query.PropertyChanged -= OnQuery;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _body.Query.PropertyChanged -= OnQuery;
        _body.Query.PropertyChanged += OnQuery;
    }

    private void OnQuery(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Render();

    private static Control Code(string text)
        => new CodeBlock { Code = text, Language = "json", IsCollapsible = true, MaxLines = Math.Max(4, (int)Math.Round(420 / 22.0)), CopyLabel = Loc.T("common.copy") };

    private void Render()
    {
        var truncated = RequestPresentation.IsTruncated(_size, _limit);
        // Cut bodies are plain text: no pretty-printing or highlighting, which is what made large bodies cost hundreds of MB.
        var shown = truncated ? _preview : RequestFormat.Pretty(_preview);
        if (!truncated)
        {
            Content = Code(shown);
            return;
        }
        var q = _body.Query;
        var stack = new StackPanel { Spacing = 8 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var note = KitStyle.Text(Loc.T("requests.body.truncated", ("shown", RequestFormat.Bytes(_limit!.Value)), ("total", RequestFormat.Bytes(_size!.Value))),
            ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        note.FontSize = KitStyle.Type12;
        note.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(note);
        if (!(q.HasData && q.Data is not null))
        {
            var load = new ArcButton { Size = ControlSize.Small, Variant = ButtonVariant.Secondary, Content = Loc.T("requests.body.loadAll"), IsLoading = q.IsFetching && q.Enabled };
            load.Click += (_, _) => _body.LoadAll();
            Grid.SetColumn(load, 1);
            head.Children.Add(load);
        }
        stack.Children.Add(head);
        if (q.IsError) stack.Children.Add(Danger(q.Error?.Message));
        if (q.HasData && q.Data is { } whole) stack.Children.Add(Plain(whole));
        else stack.Children.Add(Code(shown));
        Content = stack;
    }

    private static Control Danger(string? message) => new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = message };

    /// <summary>The whole body as selectable monospace text in a muted, scrolling box (web <c>pre.selectable</c>).</summary>
    private static Control Plain(string text)
    {
        var block = new SelectableTextBlock { Text = text, FontSize = 11, LineHeight = 16.5, TextWrapping = TextWrapping.NoWrap };
        block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = block,
        };
        var box = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Child = scroll };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        return box;
    }
}
