using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// Builders for Arc's <see cref="Table"/> in the web's <c>DataColumn</c> vocabulary (key, label, numeric, width, render):
/// typed value selectors only (no reflection bindings, AOT), text cells by default and a <c>DataCell</c> template where a column
/// needs more than text.
/// </summary>
public static class Tables
{
    /// <summary>
    /// A text column. <paramref name="value"/> is the sort value (a number sorts as a number); <paramref name="format"/> turns it into the
    /// cell text; <paramref name="width"/> is pixels (null = share the remaining width).
    /// </summary>
    public static DataTableColumn Column<T>(string header, string key, Func<T, object?> value, Func<object?, string>? format = null,
        double? width = null, bool numeric = false, bool sortable = true) where T : class
    {
        var column = DataTableColumn.Create<T>(header, value, key);
        column.IsSortable = sortable;
        if (numeric) column.IsNumeric = true;
        if (format is not null) column.Formatter = format;
        column.Width = width is { } w ? new GridLength(w) : new GridLength(1, GridUnitType.Star);
        return column;
    }

    /// <summary>A column with custom cell content (the sort value still comes from <paramref name="value"/>).</summary>
    public static DataTableColumn Cell<T>(string header, string key, Func<T, object?> value, Func<T, Control?> render,
        double? width = null, bool numeric = false, bool sortable = true) where T : class
    {
        var column = Column(header, key, value, null, width, numeric, sortable);
        column.CellTemplate = DataCell<T>.Create(render);
        return column;
    }

    /// <summary>
    /// A table of <paramref name="rows"/>. <paramref name="rowKey"/> keeps row identity across refreshes; a <paramref name="sortKey"/>
    /// sorts on load (null keeps the order given).
    /// </summary>
    public static Table Create<T>(string caption, IReadOnlyList<T> rows, Func<T, object> rowKey, string emptyMessage,
        IEnumerable<DataTableColumn> columns, string? sortKey = null, bool descending = false) where T : class
    {
        var table = new Table
        {
            Caption = caption,
            EmptyMessage = emptyMessage,
            ItemsSource = rows.ToList(),
            RowKeySelector = item => rowKey((T)item),
        };
        foreach (var column in columns) table.Columns.Add(column);
        WebSurface(table);
        if (sortKey is not null)
        {
            table.SortKey = sortKey;
            table.SortDirection = descending ? DataTableSortDirection.Descending : DataTableSortDirection.Ascending;
        }
        return table;
    }

    /// <summary>
    /// The web table's colours (<c>sortable-data-table.module.css</c>): the translucent card <c>--surface</c> instead of Arc's opaque
    /// Surface, and the sorted column's tint, which the web paints twice (the band and the cell base, both <c>--surface-muted</c> =
    /// label at 6%), so it reads as label at ~10%. ARC-GAP: <c>ColumnTintBrush</c> is internal; it is set through the property registry
    /// (no reflection), as <c>PricingUi.WithoutCompactLayout</c> does for <c>CompactBreakpoint</c>.
    /// </summary>
    public static Table WebSurface(Table table)
    {
        void Apply()
        {
            var dark = table.ActualThemeVariant == ThemeVariant.Dark;
            table.Background = CardPanel.SurfaceBrush(table.ActualThemeVariant);
            if (AvaloniaPropertyRegistry.Instance.FindRegistered(table, "ColumnTintBrush") is { } tint)
                table.SetValue(tint, new SolidColorBrush(dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0)));
        }
        Apply();
        table.ActualThemeVariantChanged += (_, _) => Apply();
        return table;
    }
}
