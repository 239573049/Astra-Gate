using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>A rectangle in screen units (the units of the display's work area).</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    /// <summary>True when the rectangle has an area (a tray icon whose bounds are known).</summary>
    public bool HasArea => Width > 0 && Height > 0;
}

/// <summary>
/// Where the tray panel goes and how big it is: desktop/src/shared/trayPanel.ts ported (<c>trayPanelBounds</c>, the fixed width, the
/// pre-measurement height), plus the command names the panel sends.
/// </summary>
public static class TrayPanelLayout
{
    /// <summary>Fixed panel width (<c>TRAY_PANEL_WIDTH</c>); the height follows the content.</summary>
    public const double Width = 360;

    /// <summary>Height before the panel has measured itself (<c>TRAY_PANEL_DEFAULT_HEIGHT</c>).</summary>
    public const double DefaultHeight = 520;

    /// <summary>A click on the icon right after the panel hid itself on deactivation is the same click: do not reopen (<c>REOPEN_GUARD_MS</c>).</summary>
    public static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private const double Gap = 6;
    private const double EdgeSlop = 4;

    /// <summary>
    /// Panel bounds next to the tray icon (<c>trayPanelBounds</c>). The icon's position relative to the work area tells where the menu bar /
    /// taskbar is: above it (macOS menu bar, a top taskbar) the panel drops down; below it (Windows default) it opens upward; on a left / right
    /// taskbar it opens sideways. An anchor inside the work area (Linux without icon bounds: the cursor) opens below it, or above when it does
    /// not fit. Always clamped into the work area and never taller than it.
    /// </summary>
    public static ScreenRect Bounds(ScreenRect anchor, ScreenRect workArea, double width, double height)
    {
        var w = Math.Min(width, workArea.Width - 2 * Gap);
        var h = Math.Max(1, Math.Min(height, workArea.Height - 2 * Gap));
        var left = workArea.X;
        var top = workArea.Y;
        var right = workArea.X + workArea.Width;
        var bottom = workArea.Y + workArea.Height;
        var cx = anchor.X + anchor.Width / 2;
        var cy = anchor.Y + anchor.Height / 2;
        double ClampX(double x) => Math.Min(Math.Max(x, left + Gap), right - w - Gap);
        double ClampY(double y) => Math.Min(Math.Max(y, top + Gap), bottom - h - Gap);

        double x, y;
        if (anchor.Y + anchor.Height <= top + EdgeSlop)
        {
            x = ClampX(cx - w / 2);
            y = top + Gap;
        }
        else if (anchor.Y >= bottom - EdgeSlop)
        {
            x = ClampX(cx - w / 2);
            y = bottom - h - Gap;
        }
        else if (anchor.X + anchor.Width <= left + EdgeSlop)
        {
            x = left + Gap;
            y = ClampY(cy - h / 2);
        }
        else if (anchor.X >= right - EdgeSlop)
        {
            x = right - w - Gap;
            y = ClampY(cy - h / 2);
        }
        else
        {
            x = ClampX(cx - w / 2);
            var below = anchor.Y + anchor.Height + Gap;
            y = below + h <= bottom - Gap ? below : ClampY(anchor.Y - h - Gap);
        }
        return new ScreenRect(JsRound(x), JsRound(y), JsRound(w), JsRound(h));
    }

    /// <summary>The content height the panel reports, as the window takes it (<c>setContentHeight</c>): finite and positive, at most 2000, rounded up.</summary>
    public static double? ContentHeight(double height)
        => double.IsFinite(height) && height > 0 ? Math.Ceiling(Math.Min(height, 2000)) : null;

    /// <summary>The panel command's wire name (<c>TrayPanelCommand</c>).</summary>
    public static string Name(TrayCommand c) => c switch
    {
        TrayCommand.Open => "open",
        TrayCommand.OpenSettings => "open-settings",
        TrayCommand.Start => "start",
        TrayCommand.Stop => "stop",
        TrayCommand.Restart => "restart",
        TrayCommand.Quit => "quit",
        TrayCommand.Hide => "hide",
        TrayCommand.Menu => "menu",
        _ => "check-updates",
    };

    private static double JsRound(double v) => Math.Floor(v + 0.5);
}
