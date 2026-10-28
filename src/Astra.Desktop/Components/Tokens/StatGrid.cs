using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// The overview's number cards (web <c>Stat</c> in a <c>grid-cols-2 md:grid-cols-3 xl:grid-cols-6</c>): the column count follows the
/// window width with the web's viewport breakpoints (768 / 1280), because the web's utilities are viewport based too.
/// </summary>
public sealed class StatGrid : UserControl
{
    private readonly Grid _grid = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly List<Control> _tiles = [];
    private readonly int _wide;
    private int _columns;

    /// <summary>A grid of <paramref name="cards"/>; <paramref name="wideColumns"/> is the xl column count (6 on the overview, 5 on a token).</summary>
    public StatGrid(IReadOnlyList<StatCard> cards, int wideColumns)
    {
        _wide = wideColumns;
        foreach (var card in cards) _tiles.Add(Tile(card));
        Content = _grid;
        Layout(2);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Fit(TopLevel.GetTopLevel(this)?.ClientSize.Width ?? e.NewSize.Width);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Fit(TopLevel.GetTopLevel(this)?.ClientSize.Width ?? Bounds.Width);
    }

    private void Fit(double viewport)
    {
        var columns = viewport >= 1280 ? _wide : viewport >= 768 ? 3 : 2;
        if (_columns != columns) Layout(columns);
    }

    private void Layout(int columns)
    {
        _columns = columns;
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var r = 0; r < (_tiles.Count + columns - 1) / columns; r++) _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < _tiles.Count; i++)
        {
            Grid.SetColumn(_tiles[i], i % columns);
            Grid.SetRow(_tiles[i], i / columns);
            _grid.Children.Add(_tiles[i]);
        }
    }

    /// <summary>One stat tile: a 12px secondary label, a 22px semibold value, an 11px muted context line.</summary>
    public static Control Tile(StatCard card)
    {
        var label = KitStyle.Text(card.Label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        label.FontSize = KitStyle.Type12;
        var value = KitStyle.Text(card.Value, ArcDesignTokenKind.Type2XL, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        value.FontSize = 22;
        value.Margin = new Thickness(0, 4, 0, 0);
        ToolTip.SetTip(value, card.Value);
        var context = KitStyle.Text(card.Context, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        context.FontSize = 11;
        context.Margin = new Thickness(0, 4, 0, 0);
        ToolTip.SetTip(context, card.Context);
        var tile = new CardPanel(new StackPanel { Children = { label, value, context } }, new Thickness(16, 12))
        {
            MinWidth = 0,
        };
        return tile;
    }
}
