using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Makes an <see cref="AccordionItem"/>'s content as wide as the item. Arc's item template wraps the content in a left-aligned
/// <see cref="Border"/> that is only as wide as the content, so a <c>HorizontalAlignment.Stretch</c> child (a code block, a text area) stays
/// content-sized however it is set.
/// // ARC-GAP: no property on AccordionItem for the body's alignment.
/// </summary>
public static class AccordionFill
{
    /// <summary>Stretches the body of <paramref name="item"/> once its template exists; returns the item.</summary>
    public static AccordionItem FillWidth(this AccordionItem item)
    {
        item.TemplateApplied += (_, _) => Apply(item);
        item.AttachedToVisualTree += (_, _) => Apply(item);
        // The body frame can be created after both (the content presenter appears with the first layout on some platforms): retry until it is found.
        void Retry(object? sender, EventArgs e)
        {
            if (Apply(item)) item.LayoutUpdated -= Retry;
        }
        item.LayoutUpdated += Retry;
        return item;
    }

    private static bool Apply(AccordionItem item)
    {
        var found = false;
        // The content presenter's parent border is the body frame.
        foreach (var presenter in item.GetVisualDescendants().OfType<ContentPresenter>())
            if (presenter.GetVisualParent() is Border frame && ReferenceEquals(presenter.Content, item.Content))
            {
                found = true;
                if (frame.HorizontalAlignment == HorizontalAlignment.Left) frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        return found;
    }
}
