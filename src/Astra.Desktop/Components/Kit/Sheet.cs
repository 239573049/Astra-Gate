using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Primitives;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Web <c>Sheet</c> (overlays.tsx): a modal panel with a title, a description, a body and a right-aligned footer of buttons.
/// In the web it is a centered dialog (Radix <c>Dialog</c>, 460px wide, nested ones stacking), not a side drawer, so this is
/// Arc's <see cref="Dialog"/> with the web's vocabulary: <see cref="SheetWidth"/> is the panel's exact width (web
/// <c>width: min(100vw - 2rem, Wpx)</c>; Arc only has 360 / 440 / 560, so the surface's theme <c>MaxWidth</c> is overridden by a local
/// value — ARC-GAP: Dialog has no free width) and <see cref="Footer"/> takes the buttons, right-aligned; a footer may put an item at the
/// left (web <c>mr-auto</c>) with <see cref="SheetFooter.Split"/>. A button in the footer closes the
/// sheet with a result when it carries <c>Dialog.Result</c> (<c>Dialog.SetResult(button, true)</c>).
/// Declare it in the page's view (it renders nothing in place) and bind <c>IsOpen</c>, or call <c>ShowAsync(owner)</c>.
/// <see cref="SideSheet"/> is the same thing as an edge drawer for panels that should keep the page visible.
/// </summary>
public class Sheet : Dialog
{
    /// <summary>Web <c>width</c> in px (default 460): the panel's width (narrower only when the window is).</summary>
    public static readonly StyledProperty<double> SheetWidthProperty = AvaloniaProperty.Register<Sheet, double>(nameof(SheetWidth), 460);

    /// <summary>The footer buttons, laid out right-aligned with an 8px gap (web <c>footer</c>); same slot as <c>Actions</c>.</summary>
    public static readonly StyledProperty<IEnumerable<Control>?> FooterProperty = AvaloniaProperty.Register<Sheet, IEnumerable<Control>?>(nameof(Footer));

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Dialog);

    /// <inheritdoc cref="SheetWidthProperty"/>
    public double SheetWidth { get => GetValue(SheetWidthProperty); set => SetValue(SheetWidthProperty, value); }

    /// <inheritdoc cref="FooterProperty"/>
    public IEnumerable<Control>? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    private DialogSurface? _surface;

    /// <summary>A sheet of the web's default width (460).</summary>
    public Sheet() => Size = ControlSize.Large;

    /// <inheritdoc />
    protected override ModalSurface CreateSurface()
    {
        var surface = (DialogSurface)base.CreateSurface();
        _surface = surface;
        // A local value wins over the theme's per-size MaxWidth setter; the surface stretches to it, so the panel is exactly SheetWidth.
        surface.MaxWidth = SheetWidth;
        surface.TemplateApplied += (_, e) =>
        {
            // Web Sheet: the footer gets its mt-6 only below children; a sheet with only a footer (the confirm) has no gap above it.
            if (Content is null && e.NameScope.Find<Avalonia.Controls.Presenters.ContentPresenter>("PART_Actions") is { } actions)
                actions.Margin = new Thickness(0);
        };
        return surface;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SheetWidthProperty)
        {
            // Large keeps Arc's own widest setter out of the way of a page that reads Size; the local MaxWidth decides.
            Size = ControlSize.Large;
            if (_surface is not null) _surface.MaxWidth = SheetWidth;
        }
        else if (change.Property == FooterProperty) Actions = Footer is null ? null : SheetFooter.Row(Footer);
    }
}

/// <summary>The same sheet as an edge drawer (Arc <see cref="Drawer"/>): for details that sit beside the page instead of over it.</summary>
public class SideSheet : Drawer
{
    /// <summary>The footer buttons, right-aligned with an 8px gap.</summary>
    public static readonly StyledProperty<IEnumerable<Control>?> FooterProperty = AvaloniaProperty.Register<SideSheet, IEnumerable<Control>?>(nameof(Footer));

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Drawer);

    /// <inheritdoc cref="FooterProperty"/>
    public IEnumerable<Control>? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FooterProperty) Actions = Footer is null ? null : SheetFooter.Row(Footer);
    }
}

/// <summary>Builds a sheet footer row (web: <c>flex flex-wrap justify-end gap-2</c>).</summary>
public static class SheetFooter
{
    /// <summary>Right-aligned buttons with an 8px gap.</summary>
    public static Control Row(IEnumerable<Control> buttons)
    {
        // A footer that is one Split already spans the panel with its own right-aligned group.
        if (buttons is IReadOnlyList<Control> { Count: 1 } only && only[0] is SplitRow split) return split;
        var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>Right-aligned buttons with an 8px gap.</summary>
    public static Control Row(params Control[] buttons) => Row((IEnumerable<Control>)buttons);

    /// <summary>
    /// <paramref name="leading"/> at the left and the buttons at the right (web: an item with <c>mr-auto</c> first in the footer), for a
    /// <see cref="Sheet.Footer"/>: <c>Footer = [SheetFooter.Split(hint, [cancel, save])]</c>.
    /// </summary>
    public static Control Split(Control leading, IEnumerable<Control> buttons)
    {
        var grid = new SplitRow { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        leading.HorizontalAlignment = HorizontalAlignment.Left;
        leading.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(leading);
        var row = Row(buttons);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }
}

/// <summary>The row <see cref="SheetFooter.Split"/> builds (recognized by <see cref="SheetFooter.Row(IEnumerable{Control})"/>).</summary>
public sealed class SplitRow : Grid
{
    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Arc right-aligns the dialog's actions presenter; a split footer spans the panel instead so its leading item sits at the left
        // (web "mr-auto"). Only this row widens it: an Actions set directly keeps Arc's alignment.
        if (this.GetVisualParent() is Avalonia.Controls.Presenters.ContentPresenter presenter) presenter.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
