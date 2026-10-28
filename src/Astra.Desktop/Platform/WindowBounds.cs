namespace Astra.Desktop.Platform;

/// <summary>A window rectangle (<c>Box</c> in desktop/src/shared/chrome.ts); units are the caller's, as long as work areas use the same.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>Default size, minimum size and the remembered-bounds rule of the Electron main window.</summary>
public static class WindowBounds
{
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 820;
    public const double MinWidth = 960;
    public const double MinHeight = 640;

    /// <summary>How much of a remembered window must still overlap a display's work area for it to reopen there.</summary>
    public const double MinVisibleWidth = 120;
    public const double MinVisibleHeight = 40;

    /// <summary>
    /// Port of <c>restorableBounds</c>: the remembered bounds when enough of the window still lies on some display (screens can
    /// be unplugged while the app was gone), else null (the default size, centered).
    /// </summary>
    public static Box? Restorable(Box? bounds, IReadOnlyList<Box> workAreas)
    {
        if (bounds is not { } b || b.Width <= 0 || b.Height <= 0) return null;
        foreach (var area in workAreas)
        {
            var w = Math.Min(b.X + b.Width, area.X + area.Width) - Math.Max(b.X, area.X);
            var h = Math.Min(b.Y + b.Height, area.Y + area.Height) - Math.Max(b.Y, area.Y);
            if (w >= MinVisibleWidth && h >= MinVisibleHeight) return b;
        }
        return null;
    }

    /// <summary>
    /// Window size in the units of <c>Screen.WorkingArea</c> / <c>Window.Position</c> (the "screen units" the prefs file stores): a
    /// window's client size is in DIPs, a screen's coordinates are physical pixels on Windows / Linux (scaling 1.5 -> x1.5) but points on
    /// macOS (Avalonia reports scaling 1 there). Always convert with the <i>screen's</i> scaling, never the window's render scaling —
    /// on a Retina Mac that is 2 and saved a window twice as large as the screen, which the next launch restored full screen (plan §24).
    /// </summary>
    public static (double Width, double Height) ToScreenUnits(double clientWidth, double clientHeight, double screenScaling)
        => (clientWidth * screenScaling, clientHeight * screenScaling);

    /// <summary>The inverse of <see cref="ToScreenUnits"/>: a size in screen units as the DIPs to give <c>Window.Width</c> / <c>Height</c>.</summary>
    public static (double Width, double Height) FromScreenUnits(double width, double height, double screenScaling)
    {
        var s = screenScaling > 0 ? screenScaling : 1;
        return (width / s, height / s);
    }

    /// <summary>
    /// False for a remembered window that is larger than the display it sits on: a prefs file written by a build with the unit bug
    /// (see <see cref="ToScreenUnits"/>) or a display swapped for a smaller one. Such bounds are dropped (default size, centered) rather
    /// than reopened as a full-screen window.
    /// </summary>
    public static bool FitsDisplay(Box b, IReadOnlyList<Box> workAreas)
    {
        foreach (var area in workAreas)
        {
            var w = Math.Min(b.X + b.Width, area.X + area.Width) - Math.Max(b.X, area.X);
            var h = Math.Min(b.Y + b.Height, area.Y + area.Height) - Math.Max(b.Y, area.Y);
            if (w >= MinVisibleWidth && h >= MinVisibleHeight) return b.Width <= area.Width && b.Height <= area.Height;
        }
        return false;
    }

    /// <summary>A remembered size never goes below the minimum (a hand-edited prefs file must not produce an unusable window).</summary>
    public static Box ClampToMinimum(Box b) => b with { Width = Math.Max(b.Width, MinWidth), Height = Math.Max(b.Height, MinHeight) };
}
