using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// Web <c>grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3</c>: equal-width cells with a 12 px gap whose column count follows the
/// window (the web's breakpoints are viewport widths, sm = 640 and lg = 1024, not the container's), each row as tall as its tallest card.
/// </summary>
internal sealed class CardGrid : Panel
{
    /// <summary>Columns from the lg breakpoint (1024) up; the market has 3, the runtime tab 2.</summary>
    public int LargeColumns { get; init; } = 3;

    /// <summary>Columns from the sm breakpoint (640) up to lg; the market has 2, the runtime tab 1.</summary>
    public int MediumColumns { get; init; } = 2;

    /// <summary>The gap between cells in px (web gap-3 = 12, gap-4 = 16).</summary>
    public double Gap { get; init; } = 12;

    /// <summary>The column count for a window width (web sm / lg breakpoints).</summary>
    public int ColumnsFor(double windowWidth) => windowWidth >= 1024 ? LargeColumns : windowWidth >= 640 ? MediumColumns : 1;

    private int Columns => ColumnsFor(TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 1440);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = Math.Max(1, Math.Min(Columns, Children.Count == 0 ? 1 : Children.Count));
        var width = double.IsInfinity(availableSize.Width) ? 320 : (availableSize.Width - Gap * (cols - 1)) / cols;
        var rows = 0;
        double total = 0, rowHeight = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            if ((i + 1) % cols == 0 || i == Children.Count - 1)
            {
                total += rowHeight + (rows > 0 ? Gap : 0);
                rows++;
                rowHeight = 0;
            }
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width * cols + Gap * (cols - 1) : availableSize.Width, total);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Math.Max(1, Math.Min(Columns, Children.Count == 0 ? 1 : Children.Count));
        var width = (finalSize.Width - Gap * (cols - 1)) / cols;
        double y = 0;
        for (var start = 0; start < Children.Count; start += cols)
        {
            var end = Math.Min(start + cols, Children.Count);
            double height = 0;
            for (var i = start; i < end; i++) height = Math.Max(height, Children[i].DesiredSize.Height);
            for (var i = start; i < end; i++) Children[i].Arrange(new Rect((i - start) * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return finalSize;
    }
}
