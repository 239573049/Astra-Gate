using Astra.Desktop.Core.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.Platform.Tray;

/// <summary>
/// The tray panel's window (desktop/src/trayPanel.ts <c>TrayPanel</c>; plan inventory GAP-7 — Avalonia's <c>TrayIcon</c> has no panel): frameless,
/// transparent (the card draws its own rounded background), always on top, out of the taskbar, positioned next to the tray icon, sized to its
/// content and hidden again when it loses focus. It is an AtomUI window like the main one, because the Arc controls inside open popups
/// (tooltips, the client-switch menu) that need AtomUI's overlay support (plan avalonia-client §11).
/// </summary>
internal sealed class TrayPopupWindow : AtomWindow
{
    private ScreenRect? _anchor;
    private double _contentHeight = TrayPanelLayout.DefaultHeight;
    private DateTime _hiddenAt;
    private int _blurHolds;

    public TrayPopupWindow(Control content)
    {
        Title = "Astra";
        Content = content;
        Width = TrayPanelLayout.Width;
        Height = _contentHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsTitleBarVisible = false;
        WindowDecorations = WindowDecorations.None;
        // No shadow box of our own: the window is transparent and the card is the visible surface.
        ExtendClientAreaToDecorationsHint = false;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Deactivated += (_, _) =>
        {
            if (_blurHolds > 0) return;
            HidePanel();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            HidePanel();
        };
        // The close box of the OS (Alt+F4) hides like the panel's own close button; the app quits only from Quit.
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.ApplicationShutdown) return;
            e.Cancel = true;
            HidePanel();
        };
    }

    /// <summary>The panel became visible / hidden (the presenter refreshes and resets the tab on show).</summary>
    public event Action<bool>? VisibilityChanged;

    /// <summary>Opens the panel at the icon, or closes it when it is open (a click right after a blur-hide is the same click: ignored).</summary>
    public void Toggle(ScreenRect? anchor)
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }
        if (DateTime.UtcNow - _hiddenAt < TrayPanelLayout.ReopenGuard) return;
        ShowPanel(anchor);
    }

    public void ShowPanel(ScreenRect? anchor)
    {
        _anchor = anchor;
        Place();
        Show();
        if (OperatingSystem.IsMacOS())
        {
            var ns = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            MacApp.JoinAllSpaces(ns);
            MacApp.Activate();
            MacApp.MakeKey(ns);
        }
        Activate();
        VisibilityChanged?.Invoke(true);
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
        VisibilityChanged?.Invoke(false);
    }

    /// <summary>The content reports its height; the window follows and stays next to the icon.</summary>
    public void SetContentHeight(double height)
    {
        _contentHeight = height;
        // Deferred: a resize requested from inside the first layout pass after Show is overwritten when Show finishes.
        if (IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(Place, Avalonia.Threading.DispatcherPriority.Background);
        else Height = height;
    }

    /// <summary>Keeps the panel open while a menu popped from it (which takes focus) is up.</summary>
    public async Task<T> HoldOpenAsync<T>(Func<Task<T>> pending)
    {
        _blurHolds++;
        try
        {
            return await pending();
        }
        finally
        {
            _blurHolds--;
            if (IsVisible) Activate();
        }
    }

    private void Place()
    {
        var screens = Screens;
        var anchor = _anchor;
        Screen? screen = null;
        if (anchor is { } a) screen = screens.ScreenFromPoint(new PixelPoint((int)a.X, (int)a.Y));
        screen ??= screens.Primary ?? screens.All.FirstOrDefault();
        if (screen is null)
        {
            Height = _contentHeight;
            return;
        }
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var area = new ScreenRect(work.X, work.Y, work.Width, work.Height);
        // No icon bounds and no cursor (Linux): the top right corner, where most desktops keep their indicators.
        var at = anchor ?? new ScreenRect(work.X + work.Width - TrayPanelLayout.Width * scale / 2, work.Y, 0, 0);
        var bounds = TrayPanelLayout.Bounds(at, area, TrayPanelLayout.Width * scale, _contentHeight * scale);
        Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;
    }
}
