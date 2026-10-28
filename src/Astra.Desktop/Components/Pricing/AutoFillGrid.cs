using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Pricing;

/// <summary>
/// CSS <c>grid-template-columns: repeat(auto-fill, minmax(min, 1fr))</c>: as many equal columns as fit at least
/// <see cref="MinColumnWidth"/> each, the leftover width shared. The pricing editor and the simulator lay their per-token-type
/// boxes out with it (web <c>grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-x-5 gap-y-2</c>).
/// </summary>
public sealed class AutoFillGrid : Panel
{
    /// <summary>Narrowest a column may get.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<AutoFillGrid, double>(nameof(MinColumnWidth), 200);

    /// <summary>Space between columns.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<AutoFillGrid, double>(nameof(ColumnSpacing), 20);

    /// <summary>Space between rows.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<AutoFillGrid, double>(nameof(RowSpacing), 8);

    static AutoFillGrid()
    {
        AffectsMeasure<AutoFillGrid>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <inheritdoc cref="MinColumnWidthProperty"/>
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    /// <inheritdoc cref="ColumnSpacingProperty"/>
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }

    /// <inheritdoc cref="RowSpacingProperty"/>
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width)) return Math.Max(1, Children.Count);
        return Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var cellWidth = double.IsInfinity(availableSize.Width) ? MinColumnWidth : (availableSize.Width - (columns - 1) * ColumnSpacing) / columns;
        var rowHeights = new List<double>();
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(cellWidth, double.PositiveInfinity));
            var row = i / columns;
            if (row == rowHeights.Count) rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], Children[i].DesiredSize.Height);
        }
        var height = rowHeights.Sum() + Math.Max(0, rowHeights.Count - 1) * RowSpacing;
        var width = double.IsInfinity(availableSize.Width) ? columns * cellWidth + (columns - 1) * ColumnSpacing : availableSize.Width;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cellWidth = (finalSize.Width - (columns - 1) * ColumnSpacing) / columns;
        var rowHeights = new List<double>();
        for (var i = 0; i < Children.Count; i++)
        {
            var row = i / columns;
            if (row == rowHeights.Count) rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], Children[i].DesiredSize.Height);
        }
        var y = 0.0;
        for (var row = 0; row < rowHeights.Count; row++)
        {
            for (var col = 0; col < columns && row * columns + col < Children.Count; col++)
                Children[row * columns + col].Arrange(new Rect(col * (cellWidth + ColumnSpacing), y, cellWidth, rowHeights[row]));
            y += rowHeights[row] + RowSpacing;
        }
        return finalSize;
    }
}
