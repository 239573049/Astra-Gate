using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.Primitives;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The kit <see cref="Sheet"/> at the web's exact width (460 by default; the detail page also uses 480, 560, 720 and the 1040 px test dialog).
/// Arc's dialog only has three panel widths, so the surface's <c>MaxWidth</c> is set locally, like <c>Components/Pricing/WideSheet</c> (sealed,
/// hence this copy that pages can derive from). Subclasses build their body in <see cref="Build"/>, called on every <see cref="Show"/>.
/// </summary>
// ARC-GAP: Dialog has no free width (only Small / Medium / Large).
public abstract class PdSheet : Sheet
{
    private DialogSurface? _surface;

    /// <summary>A sheet of the web's default width.</summary>
    protected PdSheet()
    {
        SheetWidth = 460;
        Size = ControlSize.Large;
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Dialog);

    /// <inheritdoc />
    protected override ModalSurface CreateSurface()
    {
        var surface = (DialogSurface)base.CreateSurface();
        _surface = surface;
        surface.MaxWidth = SheetWidth;
        // ARC-GAP: the actions presenter is right-aligned, so a footer cannot put an item at the left (web "mr-auto"); stretch it, the kit's
        // footer row aligns itself right anyway.
        surface.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Avalonia.Controls.Presenters.ContentPresenter>("PART_Actions") is { } actions)
                actions.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        };
        return surface;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SheetWidthProperty)
        {
            // the kit maps the width to Arc's nearest size; always take the largest so the local MaxWidth decides
            Size = ControlSize.Large;
            if (_surface is not null) _surface.MaxWidth = SheetWidth;
        }
    }

    /// <summary>(Re)builds the title, body and footer from the current state.</summary>
    protected abstract void Build();

    /// <summary>The field <see cref="Show"/> puts the caret in once the sheet is up (web <c>autoFocus</c>); <see cref="Build"/> sets it.</summary>
    protected Control? InitialFocus { get; set; }

    /// <summary>Rebuilds and opens, then focuses <see cref="InitialFocus"/>.</summary>
    public void Show()
    {
        InitialFocus = null;
        Build();
        Open();
        if (InitialFocus is { } target) FocusWhenShown(target);
    }

    /// <summary>
    /// Focuses <paramref name="target"/> once it is on screen (web <c>autoFocus</c>): the sheet's surface is attached after <c>Open</c> returns,
    /// and the overlay moves focus into itself first, so the call waits for the attachment and then for the next input-priority turn.
    /// </summary>
    public static void FocusWhenShown(Control target)
    {
        if (target.IsAttachedToVisualTree())
        {
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Background);
            return;
        }
        void Attached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            target.AttachedToVisualTree -= Attached;
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Background);
        }
        target.AttachedToVisualTree += Attached;
    }

    /// <summary>Rebuilds in place while open (a state change).</summary>
    public void Refresh()
    {
        if (IsOpen) Build();
    }
}
