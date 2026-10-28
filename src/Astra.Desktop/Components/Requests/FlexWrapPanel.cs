using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// A wrapping row whose items have a flex basis and grow factor, like the web's <c>flex flex-wrap gap-2</c> with <c>flex-[2_1_240px]</c> /
/// <c>flex-[1_1_9rem]</c> children (the request filter bar): items fill a line up to the available width, the line's leftover space is shared by
/// grow factor, and an item that no longer fits starts the next line. Avalonia has no flex layout and Arc ships none.
/// </summary>
public sealed class FlexWrapPanel : Panel
{
    /// <summary>The width an item asks for before growing (CSS <c>flex-basis</c>).</summary>
    public static readonly AttachedProperty<double> BasisProperty = AvaloniaProperty.RegisterAttached<FlexWrapPanel, Control, double>("Basis", 0);

    /// <summary>Share of a line's leftover space the item takes (CSS <c>flex-grow</c>).</summary>
    public static readonly AttachedProperty<double> GrowProperty = AvaloniaProperty.RegisterAttached<FlexWrapPanel, Control, double>("Grow", 0);

    /// <summary>Gap between items and between lines (web <c>gap-2</c>).</summary>
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FlexWrapPanel, double>(nameof(Spacing), 8);

    static FlexWrapPanel()
    {
        AffectsMeasure<FlexWrapPanel>(SpacingProperty);
        BasisProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FlexWrapPanel)?.InvalidateMeasure());
        GrowProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FlexWrapPanel)?.InvalidateMeasure());
    }

    /// <summary>Sets the flex basis of <paramref name="control"/>.</summary>
    public static void SetBasis(Control control, double value) => control.SetValue(BasisProperty, value);

    /// <summary>Gets the flex basis of <paramref name="control"/>.</summary>
    public static double GetBasis(Control control) => control.GetValue(BasisProperty);

    /// <summary>Sets the grow factor of <paramref name="control"/>.</summary>
    public static void SetGrow(Control control, double value) => control.SetValue(GrowProperty, value);

    /// <summary>Gets the grow factor of <paramref name="control"/>.</summary>
    public static double GetGrow(Control control) => control.GetValue(GrowProperty);

    /// <inheritdoc cref="SpacingProperty"/>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private List<(int From, int To, double[] Widths)> Lines(double available)
    {
        var lines = new List<(int, int, double[])>();
        var items = Children.Where(c => c.IsVisible).ToList();
        var start = 0;
        while (start < items.Count)
        {
            var end = start;
            var used = 0.0;
            while (end < items.Count)
            {
                var basis = Math.Max(items[end].MinWidth, GetBasis(items[end]));
                var next = used + (end > start ? Spacing : 0) + basis;
                if (end > start && next > available) break;
                used = next;
                end++;
            }
            var widths = new double[end - start];
            var grow = 0.0;
            for (var i = start; i < end; i++)
            {
                widths[i - start] = Math.Max(items[i].MinWidth, GetBasis(items[i]));
                grow += GetGrow(items[i]);
            }
            var free = available - used;
            if (double.IsFinite(available) && free > 0 && grow > 0)
                for (var i = start; i < end; i++) widths[i - start] += free * GetGrow(items[i]) / grow;
            else if (double.IsFinite(available) && widths.Length == 1 && widths[0] > available)
                widths[0] = available;
            lines.Add((start, end, widths));
            start = end;
        }
        return lines;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Children.Where(c => c.IsVisible).ToList();
        var height = 0.0;
        var width = 0.0;
        foreach (var (from, to, widths) in Lines(availableSize.Width))
        {
            var lineHeight = 0.0;
            var lineWidth = 0.0;
            for (var i = from; i < to; i++)
            {
                items[i].Measure(new Size(widths[i - from], double.PositiveInfinity));
                lineHeight = Math.Max(lineHeight, items[i].DesiredSize.Height);
                lineWidth += widths[i - from] + (i > from ? Spacing : 0);
            }
            height += lineHeight + (height > 0 ? Spacing : 0);
            width = Math.Max(width, lineWidth);
        }
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Children.Where(c => c.IsVisible).ToList();
        var y = 0.0;
        foreach (var (from, to, widths) in Lines(finalSize.Width))
        {
            var lineHeight = 0.0;
            for (var i = from; i < to; i++) lineHeight = Math.Max(lineHeight, items[i].DesiredSize.Height);
            var x = 0.0;
            for (var i = from; i < to; i++)
            {
                items[i].Arrange(new Rect(x, y, widths[i - from], lineHeight));
                x += widths[i - from] + Spacing;
            }
            y += lineHeight + Spacing;
        }
        return finalSize;
    }
}
