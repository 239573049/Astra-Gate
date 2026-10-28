using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Tokens;

/// <summary>One budget's bar (web <c>LimitBar</c>): "period · metric" and "used / limit" above a 6px track that fills in the tone of how close it is to the cap.</summary>
public static class LimitBarView
{
    /// <summary>The bar of <paramref name="model"/>.</summary>
    public static Control Create(LimitBarModel model)
    {
        var label = KitStyle.Text(model.Label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        label.FontSize = 11.5;
        var used = KitStyle.Text(model.UsedText, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground);
        used.FontSize = 11.5;
        used.HorizontalAlignment = HorizontalAlignment.Right;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(used, 1);
        head.Children.Add(label);
        head.Children.Add(used);

        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Stretch };
        fill.Token(Border.BackgroundProperty, model.Tone switch
        {
            LimitTone.Danger => ArcDesignTokenKind.Danger,
            LimitTone.Warning => ArcDesignTokenKind.Warning,
            _ => ArcDesignTokenKind.Accent,
        });
        var bar = new Grid { Height = 6, ColumnDefinitions = new ColumnDefinitions($"{model.Percent}*,{100 - model.Percent}*") };
        bar.Children.Add(fill);
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = bar };
        CardPanel.Themed(track, CardPanel.SurfaceBrush);
        Avalonia.Automation.AutomationProperties.SetName(track, model.Label);

        var box = new StackPanel { Children = { head, track } };
        if (model.ResetText is { } reset)
        {
            var text = KitStyle.Text(reset, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            text.FontSize = 10.5;
            text.Margin = new Thickness(0, 4, 0, 0);
            box.Children.Add(text);
        }
        return box;
    }
}

/// <summary>Today and lifetime usage side by side (web <c>StatsTable</c>): one row per period, one column per metric, in a surface inset.</summary>
public static class StatsTableView
{
    /// <summary>The table of <paramref name="rows"/>.</summary>
    public static Control Create(IReadOnlyList<StatsTableRow> rows)
    {
        string[] heads =
        [
            "", Loc.T("tokens.cost"), Loc.T("tokens.requests"), Loc.T("tokens.tokens"), Loc.T("tokens.inOut"), Loc.T("tokens.cacheHit"), Loc.T("tokens.tps"),
        ];
        var table = new AutoTablePanel(heads.Length);
        for (var c = 0; c < heads.Length; c++)
        {
            var head = KitStyle.Text(heads[c], ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            head.FontSize = KitStyle.Type12;
            Cell(table, head, c);
            if (c == 6) ToolTip.SetTip(head, Loc.T("tokens.tpsContext"));
        }
        foreach (var row in rows)
        {
            string[] values = [row.Label, row.Cost, row.Requests, row.Tokens, row.InOut, row.CacheHit, row.Tps];
            for (var c = 0; c < values.Length; c++)
            {
                var color = c == 0 || c == 4 ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.Foreground;
                var text = KitStyle.Text(values[c], ArcDesignTokenKind.TypeXS, color, c == 1 ? FontWeight.SemiBold : c == 0 ? FontWeight.Medium : FontWeight.Normal);
                text.FontSize = KitStyle.Type12;
                if (c == 5) ToolTip.SetTip(text, row.CacheHitTip);
                Cell(table, text, c);
            }
        }
        // web: rounded-[var(--radius-control)] bg-[var(--surface)], overflow-x-auto
        var inset = new Border
        {
            CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = table },
        };
        CardPanel.Themed(inset, CardPanel.SurfaceBrush);
        return inset;
    }

    private static void Cell(AutoTablePanel table, TextBlock text, int column)
    {
        text.Margin = new Thickness(12, 6); // px-3 py-1.5
        text.TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right;
        text.HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        table.Children.Add(text);
    }
}

/// <summary>
/// A plain HTML <c>&lt;table class="w-full"&gt;</c>: children fill the cells row by row, each column takes its widest cell, and the width
/// left over is shared in proportion to those widths (the browser's auto table layout), so the columns sit where the web's do. The first
/// row is the header, ruled off by a hairline (<c>border-b border-[var(--border-subtle)]</c>).
/// </summary>
public sealed class AutoTablePanel : Panel
{
    private readonly int _columns;
    private readonly Border _rule = new() { Height = 1 };
    private double[] _widths = [];
    private double[] _heights = [];

    /// <summary>A table of <paramref name="columns"/> columns; add the cells to <see cref="Panel.Children"/> row by row.</summary>
    public AutoTablePanel(int columns)
    {
        _columns = columns;
        _rule.Token(Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
        Children.Add(_rule);
    }

    private int Cells => Children.Count - 1;

    private Control CellAt(int index) => Children[index + 1];

    private int Rows => (Cells + _columns - 1) / _columns;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _widths = new double[_columns];
        _heights = new double[Rows];
        for (var i = 0; i < Cells; i++)
        {
            var child = CellAt(i);
            child.Measure(Size.Infinity);
            _widths[i % _columns] = Math.Max(_widths[i % _columns], child.DesiredSize.Width);
            _heights[i / _columns] = Math.Max(_heights[i / _columns], child.DesiredSize.Height);
        }
        _rule.Measure(availableSize);
        var natural = _widths.Sum();
        var width = double.IsInfinity(availableSize.Width) ? natural : Math.Max(natural, availableSize.Width);
        return new Size(width, _heights.Sum() + (Rows > 0 ? 1 : 0));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var natural = _widths.Sum();
        var scale = natural > 0 && finalSize.Width > natural ? finalSize.Width / natural : 1;
        var y = 0.0;
        for (var r = 0; r < Rows; r++)
        {
            var x = 0.0;
            for (var c = 0; c < _columns; c++)
            {
                var index = r * _columns + c;
                var w = _widths[c] * scale;
                if (index < Cells) CellAt(index).Arrange(new Rect(x, y, w, _heights[r]));
                x += w;
            }
            y += _heights[r];
            if (r == 0)
            {
                _rule.Arrange(new Rect(0, y, finalSize.Width, 1));
                y += 1;
            }
        }
        if (Rows == 0) _rule.Arrange(default);
        return finalSize;
    }
}
