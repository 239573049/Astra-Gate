using System.Runtime.InteropServices;
using Astra.Desktop.Core.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Astra.Desktop.Platform.Tray;

/// <summary>The OS tray / menu-bar icon as the tray runtime uses it.</summary>
internal interface ITrayIcon : IDisposable
{
    /// <summary>Text next to the icon (macOS only; elsewhere it goes into the tooltip, which the controller already does).</summary>
    void SetTitle(string title);

    /// <summary>Hover text.</summary>
    void SetToolTip(string text);

    /// <summary>The icon's menu.</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect);

    /// <summary>Opens the icon's menu now (false = this platform cannot; the caller shows it some other way).</summary>
    bool PopUpMenu();

    /// <summary>The icon's screen rectangle, or the cursor position as an empty rectangle, or null.</summary>
    ScreenRect? Anchor();
}

/// <summary>The macOS menu-bar item (<see cref="MacStatusItem"/>): primary click opens the panel, secondary click the menu.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class MacTrayIcon(Action<ScreenRect?> activate) : ITrayIcon
{
    private MacStatusItem? _item;

    public void Create()
    {
        _item = new MacStatusItem(TrayIconData.Template, TrayIconData.Template2x, secondary =>
        {
            if (secondary) _item?.PopUpMenu();
            else activate(Anchor());
        });
    }

    public void SetTitle(string title) => _item?.SetTitle(title);

    public void SetToolTip(string text) => _item?.SetToolTip(text);

    public void SetMenu(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect) => _item?.SetMenu(items, onSelect);

    public bool PopUpMenu()
    {
        _item?.PopUpMenu();
        return _item is not null;
    }

    public ScreenRect? Anchor() => _item?.Bounds();

    public void Dispose() => _item?.Dispose();
}

/// <summary>
/// Windows / Linux: Avalonia's <see cref="TrayIcon"/> (a notify icon on Windows, a StatusNotifierItem over D-Bus on Linux). Windows: a left click
/// opens the panel, the right click its menu. Linux: most desktops only show the menu and never deliver a click, which is why the menu carries
/// "Show Tray Panel" there (desktop/src/tray.ts). Neither has a title next to the icon; the controller puts it in the tooltip.
/// </summary>
internal sealed class AvaloniaTrayIcon : ITrayIcon
{
    private readonly TrayIcon _icon;

    /// <param name="app">The application the icon belongs to.</param>
    /// <param name="activate">A left click (Windows; most Linux desktops never deliver one).</param>
    /// <param name="menuFailed">Windows: the right click's menu came up empty (<see cref="TrayMenuWatch"/>); shows something in its place.</param>
    public AvaloniaTrayIcon(Application app, Action<ScreenRect?> activate, Action? menuFailed = null)
    {
        using var stream = new MemoryStream(TrayIconData.Color);
        _icon = new TrayIcon { Icon = new WindowIcon(new Bitmap(stream)), ToolTipText = "Astra", IsVisible = true };
        _icon.Clicked += (_, _) => activate(Anchor());
        if (OperatingSystem.IsWindows() && menuFailed is not null) TrayMenuWatch.Install(menuFailed);
        TrayIcon.SetIcons(app, [_icon]);
    }

    public void SetTitle(string title)
    {
    }

    public void SetToolTip(string text) => _icon.ToolTipText = text;

    public void SetMenu(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect) => _icon.Menu = Build(items, onSelect);

    // Avalonia cannot open a tray icon's menu programmatically; the panel's "more" button shows the same items in a popup menu instead.
    public bool PopUpMenu() => false;

    /// <summary>The cursor: the click happened there (Windows has no icon bounds through Avalonia; Linux has neither, so null and the runtime picks a corner).</summary>
    public ScreenRect? Anchor() => OperatingSystem.IsWindows() && Win32.CursorPos() is { } p ? new ScreenRect(p.X, p.Y, 0, 0) : null;

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    /// <summary>The menu as Avalonia's <see cref="NativeMenu"/>: on Windows Avalonia draws it itself (<see cref="TrayMenuWatch"/>).</summary>
    internal static NativeMenu Build(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            if (item.Kind == TrayMenuItemKind.Separator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var entry = new NativeMenuItem(item.Label) { IsEnabled = item.Enabled };
            if (item.Kind is TrayMenuItemKind.Checkbox or TrayMenuItemKind.Radio)
            {
                entry.ToggleType = item.Kind == TrayMenuItemKind.Radio ? MenuItemToggleType.Radio : MenuItemToggleType.CheckBox;
                entry.IsChecked = item.Checked;
            }
            if (item.Submenu is { } sub) entry.Menu = Build(sub, onSelect);
            else if (item.Action is { } action) entry.Click += (_, _) => onSelect(action);
            menu.Items.Add(entry);
        }
        return menu;
    }
}

/// <summary>
/// Windows: Avalonia draws a tray icon's right-click menu itself, in a window of its own (Avalonia.Win32 <c>TrayIconImpl.TrayPopupRoot</c>, sized
/// to a <see cref="MenuFlyoutPresenter"/> of the <see cref="NativeMenu"/>), so the menu shows only when Avalonia's menu controls have a theme.
/// Arc has none for them; up to 0.13.1 the window measured 0 x 0 and a right click showed nothing, leaving no visible way to quit (issue #18).
/// KitStyles themes them now (UiTests <c>TrayMenuWindowTests</c> checks it on Avalonia's own window). Should that window ever open without a
/// size again, it is closed and the fallback runs in its place (the tray panel: Open and Quit, drawn by Arc's own controls).
/// </summary>
internal static class TrayMenuWatch
{
    /// <summary>The type of Avalonia's tray menu window: private to Avalonia.Win32, so matched by name (Avalonia is pinned exactly).</summary>
    internal const string MenuWindowType = "Avalonia.Win32.TrayIconImpl+TrayPopupRoot";

    private static Action? _fallback;

    /// <summary>Watches every window Avalonia opens for its tray menu window; <paramref name="fallback"/> replaces an empty one. Idempotent.</summary>
    public static void Install(Action fallback)
    {
        if (_fallback is null) Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnOpening(window));
        _fallback = fallback;
    }

    /// <summary>Whether <paramref name="window"/> is Avalonia's tray menu window.</summary>
    internal static bool IsMenuWindow(Window window) => window.GetType().FullName == MenuWindowType;

    /// <summary>Whether the window came up with something to show: it sizes to its menu, so an unlaid-out menu leaves it 0 x 0.</summary>
    internal static bool HasContent(Window window) => window.ClientSize is { Width: >= 1, Height: >= 1 };

    private static void OnOpening(Window window)
    {
        if (!IsMenuWindow(window)) return;
        // Raised as Show begins; Opened follows once the window is laid out and on screen.
        void Opened(object? sender, EventArgs e)
        {
            window.Opened -= Opened;
            if (HasContent(window)) return;
            // Not from inside Show.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                window.Close();
                _fallback?.Invoke();
            });
        }
        window.Opened += Opened;
    }
}

/// <summary>The one Win32 call the tray needs: where the cursor is (the panel opens next to the click).</summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetCursorPos(out Point point);

    /// <summary>The cursor in physical screen pixels, or null.</summary>
    public static (int X, int Y)? CursorPos()
    {
        try
        {
            return GetCursorPos(out var p) != 0 ? (p.X, p.Y) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
