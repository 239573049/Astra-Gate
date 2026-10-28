using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Traces;

/// <summary>
/// The details of one trace node (inspector.tsx <c>Inspector</c>): kind caption, icon, title and subtitle, label / value rows (values wrap,
/// never truncate: ids, retry reasons and timestamps must stay readable), a monospace block, the raw span data folded away, account lines
/// (topology provider) and links. Built from the model's <see cref="InspectorModel"/>.
/// </summary>
public static class TraceInspectorView
{
    /// <summary>Builds the inspector; <paramref name="open"/> follows an action link (a web path under <c>/traces</c>).</summary>
    public static Control Create(InspectorModel m, TracePalette palette, Action<TraceRoute> open, Func<string?, string> clientLabel)
    {
        var root = new StackPanel { Spacing = 12 };

        var head = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 24, 0) };
        if (m.Kind is not null) head.Children.Add(TraceUi.Text(m.Kind, 11, ArcDesignTokenKind.TextSecondary));
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        Control? icon = !string.IsNullOrEmpty(m.ClientIcon) ? BrandIcon.Client(m.ClientIcon, 32, fallbackName: clientLabel(m.ClientIcon))
            : m.ProviderIcon is { } p ? BrandIcon.Provider(p.Name, p.Icon, 32) : null;
        if (icon is not null) titleRow.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(TraceUi.Text(m.Title, 15, ArcDesignTokenKind.Foreground, FontWeight.SemiBold));
        if (!string.IsNullOrEmpty(m.Sub)) titles.Children.Add(TraceUi.Text(m.Sub, 12, ArcDesignTokenKind.TextSecondary));
        titleRow.Children.Add(titles);
        head.Children.Add(titleRow);
        root.Children.Add(head);

        if (m.Rows.Count > 0)
        {
            var rows = new StackPanel();
            for (var i = 0; i < m.Rows.Count; i++)
            {
                if (i > 0) rows.Children.Add(TraceUi.Divider());
                var (label, value) = m.Rows[i];
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
                var l = TraceUi.Text(label, 12, ArcDesignTokenKind.TextSecondary);
                l.VerticalAlignment = VerticalAlignment.Top;
                var v = new SelectableTextBlock
                {
                    Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right,
                };
                v.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
                Grid.SetColumn(v, 1);
                grid.Children.Add(l);
                grid.Children.Add(v);
                rows.Children.Add(grid);
            }
            root.Children.Add(rows);
        }

        if (m.Accounts.Count > 0)
        {
            var accounts = new StackPanel { Spacing = 8 };
            accounts.Children.Add(TraceUi.Text(Core.I18n.Loc.T("traces.account.title"), 11, ArcDesignTokenKind.TextSecondary));
            foreach (var a in m.Accounts)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
                grid.Children.Add(TraceUi.Text(a.Name, 12, ArcDesignTokenKind.Foreground));
                var text = new TextBlock { Text = a.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
                if (a.Muted) text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                else text.Foreground = new SolidColorBrush(palette.Orange);
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                accounts.Children.Add(grid);
            }
            // web: the provider inspector stacks the account list under the Inspector (gap 12)
            root.Children.Add(accounts);
        }

        if (m.Code is { Lines.Count: > 0 } code) root.Children.Add(Block(code, palette, folded: false));
        if (m.Raw is { Lines.Count: > 0 } raw) root.Children.Add(Block(raw, palette, folded: true));

        if (m.Actions.Count > 0)
        {
            var links = TraceUi.Wrap(8, 8);
            foreach (var a in m.Actions) links.Children.Add(TraceUi.Link(a.Label, () => open(a.Route)));
            root.Children.Add(links);
        }
        return root;
    }

    /// <summary>
    /// The monospace block (<c>code</c>: lines starting "- " red, "+ " green, others secondary) or the folded raw dump (web
    /// <c>&lt;details&gt;</c>: a clickable summary that opens it).
    /// </summary>
    private static Control Block(InspectorBlock block, TracePalette palette, bool folded)
    {
        var body = new StackPanel { Margin = folded ? new Thickness(0, 6, 0, 0) : default };
        foreach (var line in block.Lines)
        {
            var text = new SelectableTextBlock { Text = line, FontSize = folded ? 11 : 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = folded ? 18 : 19 };
            text.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            if (!folded && line.StartsWith("- ", StringComparison.Ordinal)) text.Foreground = new SolidColorBrush(palette.Red);
            else if (!folded && line.StartsWith("+ ", StringComparison.Ordinal)) text.Foreground = new SolidColorBrush(palette.Green);
            else text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            body.Children.Add(text);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = body };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        var stack = new StackPanel { Spacing = 6 };
        if (!folded)
        {
            stack.Children.Add(TraceUi.Text(block.Title, 11, ArcDesignTokenKind.TextSecondary));
            stack.Children.Add(box);
            return stack;
        }
        var summary = TraceUi.Text("▸ " + block.Title, 11, ArcDesignTokenKind.TextSecondary);
        summary.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        box.IsVisible = false;
        summary.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            box.IsVisible = !box.IsVisible;
            summary.Text = (box.IsVisible ? "▾ " : "▸ ") + block.Title;
        };
        summary.PointerEntered += (_, _) => summary.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        summary.PointerExited += (_, _) => summary.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        stack.Children.Add(summary);
        stack.Children.Add(box);
        return stack;
    }
}

/// <summary>A stacked bar of where time went, with its legend (inspector.tsx <c>TimeBar</c>), in a card.</summary>
public static class TimeBarView
{
    /// <summary>Builds the card for <paramref name="bar"/>.</summary>
    public static Control Create(TimeBar bar, TracePalette palette)
    {
        var root = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(TraceUi.Text(bar.Title, 12, ArcDesignTokenKind.Foreground, FontWeight.Bold));
        var total = TraceUi.Text(bar.Total, 12, ArcDesignTokenKind.TextSecondary);
        Grid.SetColumn(total, 1);
        head.Children.Add(total);
        root.Children.Add(head);

        // flex: max(value/sum, 0.004) 1 0 with a 3 px minimum and 2 px gaps: star columns carry the weights.
        var flex = bar.Flex();
        var strip = new Grid { Height = 14, ClipToBounds = true, ColumnSpacing = 2 };
        var col = 0;
        for (var i = 0; i < bar.Parts.Count; i++)
        {
            if (flex[i] is not { } w) continue;
            strip.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w, GridUnitType.Star)) { MinWidth = 3 });
            var part = bar.Parts[i];
            var seg = new Border { Background = new SolidColorBrush(palette.Swatch(part.Swatch)) };
            ToolTip.SetTip(seg, $"{part.Label} {part.Text}");
            Grid.SetColumn(seg, col++);
            strip.Children.Add(seg);
        }
        root.Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = strip });

        var legend = TraceUi.Wrap(16, 6);
        foreach (var part in bar.Parts)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(palette.Swatch(part.Swatch)), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            text.Inlines!.Add(new Run(part.Label + " "));
            var value = new Run(part.Text) { FontWeight = FontWeight.Bold };
            value.Bind(TextElement.ForegroundProperty, text.GetResourceObservable(ArcDesignTokenKind.Foreground));
            text.Inlines.Add(value);
            item.Children.Add(text);
            if (part.Note is not null)
            {
                var note = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(TracePalette.WithAlpha(palette.Accent2, 0.2)),
                    Child = TraceUi.Text(part.Note, 10.5, ArcDesignTokenKind.Foreground),
                };
                item.Children.Add(note);
            }
            legend.Children.Add(item);
        }
        root.Children.Add(legend);
        return TraceUi.Card(root, new Thickness(14));
    }
}
