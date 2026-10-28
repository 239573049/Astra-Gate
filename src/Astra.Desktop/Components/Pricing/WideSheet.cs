using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Primitives;

namespace Astra.Desktop.Components.Pricing;

/// <summary>
/// The kit's <see cref="Sheet"/> with the web's exact width. Arc's dialog has three panel widths (360 / 440 / 560) and the kit maps
/// <c>SheetWidth</c> to the nearest, but the web's sheets are 460 / 540 / 660 / 680 / 720 px wide and their forms (the price editor's
/// rule rows, the sync diff) are laid out for it. The panel's width is a style setter on <c>MaxWidth</c>, so a local
/// <c>MaxWidth</c> on the surface wins; everything else (title, footer, scrolling, overlay) is the kit's.
/// </summary>
// ARC-GAP: Dialog has no free width (only Small / Medium / Large); the kit Sheet should grow this itself, then this class goes away.
public sealed class WideSheet : Sheet
{
    private DialogSurface? _surface;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Dialog);

    /// <inheritdoc />
    protected override ModalSurface CreateSurface()
    {
        var surface = (DialogSurface)base.CreateSurface();
        _surface = surface;
        surface.MaxWidth = SheetWidth;
        return surface;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SheetWidthProperty && _surface is not null) _surface.MaxWidth = SheetWidth;
    }
}
