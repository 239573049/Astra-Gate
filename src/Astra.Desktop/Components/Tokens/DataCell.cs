using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Controls.Templates;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// A table cell with custom content: Arc's table recycles row containers, so a cell control must (re)read its content whenever its
/// data context (the row item) changes. <see cref="Create{T}"/> builds the template Arc's <c>DataTableColumn.CellTemplate</c> takes.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class DataCell<T> : Decorator where T : class
{
    private readonly Func<T, Control?> _render;

    private DataCell(Func<T, Control?> render) => _render = render;

    /// <summary>A cell template that shows <paramref name="render"/> of the row.</summary>
    public static IDataTemplate Create(Func<T, Control?> render)
        => new FuncDataTemplate<T>((_, _) => new DataCell<T>(render), supportsRecycling: false);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Child = DataContext is T row ? _render(row) : null;
    }
}
