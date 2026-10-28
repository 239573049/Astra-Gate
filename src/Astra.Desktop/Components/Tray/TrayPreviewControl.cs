using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Astra.Desktop.Components.Tray;

/// <summary>
/// The live panel preview on Settings › Tray panel (web TraySettings renders <c>TrayPanelView preview</c> with real data, a 360px card with a
/// drop shadow). Plugged into WP9's seam <see cref="TrayPreview.Factory"/> by <see cref="Install"/>; the factory has no context argument, so
/// the preview takes the page's <see cref="PageContext"/> from the page view model it inherits as DataContext once it is on screen, and builds
/// its own <see cref="TrayDataService"/> over the page's API and cache (keys shared with the popup and the pages).
/// </summary>
public sealed class TrayPreviewControl : ContentControl
{
    private readonly TrayPrefs _prefs;
    private readonly TrayPanelState _state;
    private readonly TrayPanelView _view = new(preview: true);
    private TrayDataService? _data;
    private TrayPanelPresenter? _presenter;

    /// <summary>Points the Settings tab's preview seam at this control (idempotent).</summary>
    public static void Install() => TrayPreview.Factory ??= (prefs, state) => new TrayPreviewControl(prefs, state);

    /// <summary>A preview of <paramref name="prefs"/> in <paramref name="state"/>.</summary>
    public TrayPreviewControl(TrayPrefs prefs, TrayPanelState state)
    {
        _prefs = prefs;
        _state = state;
        // shadow-[0_12px_40px_rgb(0_0_0/0.25)] on the card.
        Content = new Border { Child = _view, CornerRadius = new CornerRadius(14), BoxShadow = BoxShadows.Parse("0 12 40 0 #40000000") };
        // Visible at once, before any data: the stopped / loading states need no context.
        _view.Update(prefs, state, TrayData.Empty, DateTimeOffset.UtcNow, TimeSpan.Zero);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not PageViewModel { Context: { } ctx }) return;
        _data = new TrayDataService(ctx.Api, ctx.Queries, ctx.Clock);
        _presenter = TrayPanelPresenter.ForPreview(_view, _data, ctx.Clock, _prefs, _state);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _presenter?.Dispose();
        _data?.Dispose();
        _presenter = null;
        _data = null;
    }

    /// <summary>Waits until the preview's data settled (visual tests).</summary>
    internal TrayPanelView View => _view;
}
