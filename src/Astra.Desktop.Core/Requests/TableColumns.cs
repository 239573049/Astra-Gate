namespace Astra.Desktop.Core.Requests;

/// <summary>
/// Column geometry of web Arc's <c>SortableDataTable</c> when every column has a pixel width (its <c>colgroup</c> under
/// <c>table-layout: fixed; width: 100%; min-width: 560px</c>): the table is as wide as its container but never narrower than the
/// columns' sum (then it scrolls sideways), and room beyond the sum is shared out in proportion to the widths, as browsers do for
/// a fixed-layout table.
/// </summary>
public static class TableColumns
{
    /// <summary>The web table's <c>min-width</c>.</summary>
    public const double MinTableWidth = 560;

    /// <summary>The table width and each column's left edge and width for a container <paramref name="viewport"/> wide.</summary>
    public static (double Total, (double X, double Width)[] Columns) Layout(IReadOnlyList<double> widths, double viewport)
    {
        var sum = widths.Sum();
        var total = Math.Max(Math.Max(sum, MinTableWidth), double.IsFinite(viewport) ? viewport : 0);
        var scale = sum > 0 ? total / sum : 1;
        var columns = new (double X, double Width)[widths.Count];
        var x = 0d;
        for (var i = 0; i < widths.Count; i++)
        {
            var w = widths[i] * scale;
            columns[i] = (x, w);
            x += w;
        }
        return (total, columns);
    }
}
