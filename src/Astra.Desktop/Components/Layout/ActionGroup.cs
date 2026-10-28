using Avalonia.Controls;
using Avalonia.Layout;

namespace Astra.Desktop.Components.Layout;

/// <summary>
/// Lays out related toolbar buttons side by side (web <c>ButtonGroup</c>: an inline flex with a 4px gap). Pass a spacing of
/// 8 for the page toolbar's own slot, where groups sit apart (web: the actions container's <c>gap-2</c>).
/// </summary>
public sealed class ActionGroup : StackPanel
{
    /// <summary>A group with the web's 4px gap.</summary>
    public ActionGroup() : this(4)
    {
    }

    /// <summary>A group with <paramref name="spacing"/> between its items.</summary>
    public ActionGroup(double spacing, params Control[] items)
    {
        Orientation = Orientation.Horizontal;
        Spacing = spacing;
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var item in items) Children.Add(item);
    }
}
