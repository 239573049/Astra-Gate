using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Update;
using Astra.Desktop.Core.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Astra.Desktop.Platform.Tray;

/// <summary>
/// Where the update card (<see cref="UpdateCardView"/>) is shown: in the bottom-right corner of the main window while that window is on screen
/// (its overlay layer, above open sheets — <see cref="ToastPresenter.AboveModals"/>), otherwise in a small always-on-top window of its own in the
/// bottom-right corner of the primary screen (<see cref="UpdateCardWindow"/>), which never takes focus. One card at a time: a newer state
/// replaces the one shown. <see cref="Rehost"/> moves it when the main window is shown or hidden, so a download that started in the window
/// keeps its card when the window goes to the tray. A result closes itself (<see cref="UpdateCardModel.AutoClose"/>); an offer shown in the
/// corner window closes itself after <see cref="CornerOfferLifetime"/> (it is the reminder's "later"); everything else stays until answered.
/// </summary>
internal sealed class UpdateNotice
{
    /// <summary>The card's distance from the window's or the screen's edges (DIP).</summary>
    public const double Gap = 16;

    /// <summary>How long an offer stays in the corner window when nobody answers it.</summary>
    public static readonly TimeSpan CornerOfferLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Grace period after the pointer leaves a card whose auto-close fired while it was hovered.</summary>
    public static readonly TimeSpan HoverGraceLifetime = TimeSpan.FromSeconds(10);

    private readonly UpdateCardView _card = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Gap) };
    private readonly Func<Window?> _visibleMain;
    private OverlayLayer? _overlay;
    private Panel? _overlayHost;
    private UpdateCardWindow? _window;
    private IDisposable? _autoClose;
    private UpdateCardModel? _deferredCloseModel;

    /// <param name="visibleMain">The main window when it is on screen, else null.</param>
    public UpdateNotice(Func<Window?> visibleMain)
    {
        _visibleMain = visibleMain;
        _card.Acted += a => Acted?.Invoke(a);
        _card.PointerExited += (_, _) => OnPointerExited();
    }

    /// <summary>A button of the card was pressed (or its ×).</summary>
    public event Action<UpdateCardAction>? Acted;

    /// <summary>A card is on screen.</summary>
    public bool IsShown { get; private set; }

    /// <summary>The state on screen, or null when no card is.</summary>
    public UpdateCardModel? Model => IsShown ? _card.Model : null;

    /// <summary>The card itself (tests).</summary>
    internal UpdateCardView Card => _card;

    /// <summary>Whether the card is in the main window (true) or the corner window (false); null when hidden (tests).</summary>
    internal bool? InWindow => !IsShown ? null : _overlayHost is not null && _card.Parent == _overlayHost;

    /// <summary>Shows <paramref name="model"/>, replacing what the card showed.</summary>
    public void Show(UpdateCardModel model)
    {
        var previous = IsShown ? _card.Model?.Kind : null;
        _card.Render(model);
        IsShown = true;
        Place();
        // Download ticks re-render the same kind of card and keep its timer (it has none); a new kind (or a new offer) starts its own.
        if (previous != model.Kind || model.Kind == UpdateCardKind.Available) ScheduleAutoClose(model);
    }

    /// <summary>Takes the card off the screen.</summary>
    public void Hide()
    {
        IsShown = false;
        _autoClose?.Dispose();
        _autoClose = null;
        _deferredCloseModel = null;
        DetachFromOverlay();
        CloseWindow();
    }

    /// <summary>The main window was shown or hidden: the card follows (into it, or out to the corner window).</summary>
    public void Rehost()
    {
        if (IsShown) Place();
    }

    private void ScheduleAutoClose(UpdateCardModel model, TimeSpan? overrideDelay = null)
    {
        _autoClose?.Dispose();
        _autoClose = null;
        _deferredCloseModel = null;
        var corner = model.Kind == UpdateCardKind.Available;
        var after = overrideDelay ?? model.AutoClose ?? (corner ? CornerOfferLifetime : (TimeSpan?)null);
        if (after is not { } delay) return;
        _autoClose = DispatcherTimer.RunOnce(() =>
        {
            _autoClose = null;
            if (!IsShown || _card.Model?.Kind != model.Kind) return;
            // An unanswered offer closes only from the corner window; in the main window it waits for the user.
            if (model.AutoClose is null && InWindow == true) return;
            // Do not snatch the card away while the user is hovering/scrolling the release notes.
            if (_card.IsPointerOver)
            {
                _deferredCloseModel = model;
                return;
            }
            Hide();
        }, delay);
    }

    private void OnPointerExited()
    {
        if (_deferredCloseModel is not { } model || !IsShown || _card.Model?.Kind != model.Kind) return;
        var grace = model.AutoClose is { } auto && auto < HoverGraceLifetime ? auto : HoverGraceLifetime;
        ScheduleAutoClose(model, grace);
    }

    private void Place()
    {
        if (_visibleMain() is { } main && AttachTo(main))
        {
            CloseWindow();
            return;
        }
        DetachFromOverlay();
        if (_window is null)
        {
            DetachCard();
            _window = new UpdateCardWindow(_card);
            _window.Show();
        }
    }

    private bool AttachTo(Window main)
    {
        if (OverlayLayer.GetOverlayLayer(main) is not { } overlay) return false;
        if (_overlayHost is not null && ReferenceEquals(_overlay, overlay) && _card.Parent == _overlayHost) return true;
        DetachFromOverlay();
        CloseWindow();
        DetachCard();
        // The overlay is a canvas: a host sized to it lets the card sit in its bottom-right corner (as ErrorToasts' column hangs from the bottom).
        // No background, so only the card takes clicks; above Arc's sheets and dialogs (issue #41).
        var host = new Panel { ZIndex = ToastPresenter.AboveModals };
        host.Children.Add(_card);
        _overlay = overlay;
        _overlayHost = host;
        Fit(overlay.Bounds);
        overlay.PropertyChanged += OnOverlayChanged;
        overlay.Children.Add(host);
        return true;
    }

    private void OnOverlayChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && e.NewValue is Rect bounds) Fit(bounds);
    }

    private void Fit(Rect bounds)
    {
        if (_overlayHost is not { } host) return;
        var top = TopLevel.GetTopLevel(_overlay);
        host.Width = bounds.Width > 0 ? bounds.Width : top?.ClientSize.Width ?? 0;
        host.Height = bounds.Height > 0 ? bounds.Height : top?.ClientSize.Height ?? 0;
    }

    private void DetachFromOverlay()
    {
        if (_overlay is { } overlay)
        {
            overlay.PropertyChanged -= OnOverlayChanged;
            if (_overlayHost is { } host) overlay.Children.Remove(host);
        }
        _overlayHost?.Children.Remove(_card);
        _overlay = null;
        _overlayHost = null;
    }

    private void CloseWindow()
    {
        if (_window is not { } window) return;
        _window = null;
        window.Content = null;
        window.Close();
    }

    private void DetachCard()
    {
        switch (_card.Parent)
        {
            case Panel panel:
                panel.Children.Remove(_card);
                break;
            case ContentControl control:
                control.Content = null;
                break;
        }
    }
}

/// <summary>
/// The update card's own window, for when no Astra window is on screen (a tray-only app, the window closed mid-download): frameless,
/// transparent around the card, always on top, out of the taskbar, never focused (<c>ShowActivated = false</c>, like <see cref="NoticeWindow"/>),
/// kept in the bottom-right corner of the primary screen's working area as the card grows and shrinks.
/// </summary>
internal sealed class UpdateCardWindow : Window
{
    public UpdateCardWindow(Control card)
    {
        Title = "Astra";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = card;
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        // The card's own margin (UpdateNotice.Gap) is the distance from the edges.
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        Position = new PixelPoint(area.Right - width, area.Bottom - height);
    }
}
