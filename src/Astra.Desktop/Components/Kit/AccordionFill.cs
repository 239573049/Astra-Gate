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
        item.TemplateApplied += (_, e) => Apply(item, e);
        item.AttachedToVisualTree += (_, _) => Apply(item, null);
        return item;
    }

    private static void Apply(AccordionItem item, TemplateAppliedEventArgs? _)
    {
        // The content presenter's parent border is the body frame.
        foreach (var presenter in item.GetVisualDescendants().OfType<ContentPresenter>())
            if (presenter.GetVisualParent() is Border { HorizontalAlignment: HorizontalAlignment.Left } frame && ReferenceEquals(presenter.Content, item.Content))
                frame.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
