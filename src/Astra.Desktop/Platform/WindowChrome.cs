using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.Platform;

/// <summary>
/// Applies the Electron window chrome (<see cref="ChromeSpec"/>; plan avalonia-client §22) to the main window: the solid first-paint
/// background, the hidden title bar with the native traffic lights on macOS, the remembered bounds, and the drag region.
/// The window stays an <c>AtomUI.Desktop.Controls.Window</c> (plan §11); this class only sets properties on it.
/// </summary>
public static class WindowChrome
{
    /// <summary>The spec for this run; set by <see cref="Apply"/> before the shell view is created, which reads it.</summary>
    public static ChromeSpec Spec { get; private set; } = ChromeSpec.For(ChromeSpec.CurrentOs(), false);

    /// <summary>
    /// Vertical center of the traffic lights is the middle of the title-bar height hint, so a hint of 2 x (Y + button height / 2)
    /// puts a 14 px cluster at the Electron <c>trafficLightPosition.y</c>.
    /// </summary>
    public const double TrafficLightButtonHeight = 14;

    public static bool IsDark(DesktopPrefs prefs) => prefs.Theme switch
    {
        ThemeSource.Dark => true,
        ThemeSource.Light => false,
        _ => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark,
    };

    public static void Apply(AtomWindow window, DesktopPrefs prefs)
    {
        var dark = IsDark(prefs);
        Spec = ChromeSpec.For(ChromeSpec.CurrentOs(), dark);

        // Solid first paint (themeBackground): the wallpaper covers it, but until it is laid out the window is never white.
        SetSurface(window, Spec.Background);
        window.ActualThemeVariantChanged += (_, _) =>
            SetSurface(window, ChromeSpec.ThemeBackground(window.ActualThemeVariant == ThemeVariant.Dark));

        if (Spec.HidesManagedTitleBar) window.IsTitleBarVisible = false;
        if (Spec.NativeTrafficLights && OperatingSystem.IsMacOS()) ConfigureTrafficLights(window);

        RestoreBounds(window, prefs);
        TrackBounds(window, prefs);
    }

    private static void SetSurface(AtomWindow window, string hex)
    {
        var brush = new SolidColorBrush(Color.Parse(hex));
        window.Background = brush;
        window.TransparencyBackgroundFallback = brush;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void ConfigureTrafficLights(AtomWindow window)
    {
        // AtomUI centers the native buttons vertically in the title-bar height hint and lays the cluster out from OffsetX with
        // Spacing between buttons (MacStandardWindowButtons; Window.ConfigureMacOsWindow). Electron's trafficLightPosition is the
        // cluster's top-left, so the hint is chosen to center a 14 px cluster at y = 20.
        window.ExtendClientAreaToDecorationsHint = true;
        // AtomUI's theme picks BorderOnly for a hidden title bar, which also hides the native buttons; Full keeps them.
        window.WindowDecorations = WindowDecorations.Full;
        window.ExtendClientAreaTitleBarHeightHint = 2 * (Spec.TrafficLightY + TrafficLightButtonHeight / 2);
        AtomUI.Desktop.Controls.MacStandardWindowButtons.SetOffsetX(window, Spec.TrafficLightX);
        AtomUI.Desktop.Controls.MacStandardWindowButtons.SetSpacing(window, ChromeSpec.TrafficLightSpacing);
    }

    // ---------------------------------------------------------------- remembered bounds (restorableBounds)

    private static void RestoreBounds(AtomWindow window, DesktopPrefs prefs)
    {
        window.Width = WindowBounds.DefaultWidth;
        window.Height = WindowBounds.DefaultHeight;
        window.MinWidth = WindowBounds.MinWidth;
        window.MinHeight = WindowBounds.MinHeight;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (prefs.WindowBounds is not { } saved || window.Screens is not { } screens) return;

        var areas = screens.All.Select(s => new Box(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height)).ToList();
        if (WindowBounds.Restorable(saved, areas) is not { } bounds || !WindowBounds.FitsDisplay(bounds, areas)) return;

        var origin = new PixelPoint((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
        var scale = screens.ScreenFromPoint(origin)?.Scaling ?? 1.0;
        // Prefs hold screen units (Save below); the window's Width / Height are DIPs.
        var (dipW, dipH) = WindowBounds.FromScreenUnits(bounds.Width, bounds.Height, scale);
        var size = WindowBounds.ClampToMinimum(bounds with { Width = dipW, Height = dipH });
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = origin;
        window.Width = size.Width;
        window.Height = size.Height;
        if (prefs.WindowMaximized) window.Opened += (_, _) => window.WindowState = WindowState.Maximized;
    }

    private static void TrackBounds(AtomWindow window, DesktopPrefs prefs)
    {
        // Normal bounds only (Electron's getNormalBounds): a maximized or full-screen window must not overwrite them.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Save(window, prefs);
        };
        void Changed()
        {
            if (!window.IsVisible) return;
            timer.Stop();
            timer.Start();
        }
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Changed();
        };
        window.Closing += (_, _) => Save(window, prefs);
    }

    private static void Save(AtomWindow window, DesktopPrefs prefs)
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized) return;
        var maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (window.WindowState == WindowState.Normal)
        {
            // Screen units (see WindowBounds.ToScreenUnits): the screen's scaling, not the window's render scaling.
            var scale = window.Screens.ScreenFromWindow(window)?.Scaling ?? 1.0;
            var (w, h) = WindowBounds.ToScreenUnits(window.ClientSize.Width, window.ClientSize.Height, scale);
            prefs.WindowBounds = new Box(window.Position.X, window.Position.Y, w, h);
        }
        if (prefs.WindowBounds is null) return;
        prefs.WindowMaximized = maximized;
        prefs.Save();
    }

    // ---------------------------------------------------------------- drag region (web .drag)

    /// <summary>
    /// Makes <paramref name="zone"/> a window drag region like the web's <c>.drag</c>: pressing on it moves the window, double-clicking
    /// zooms it, and buttons / inputs inside it keep working. The zone needs a hit-testable background (a transparent one will do).
    /// No-op where the platform keeps its own title bar.
    /// </summary>
    public static void AttachDragRegion(Control zone)
    {
        if (!Spec.HasAppDragRegion) return;
        zone.PointerPressed += (_, e) =>
        {
            if (e.Handled || TopLevel.GetTopLevel(zone) is not Window window) return;
            if (!e.GetCurrentPoint(zone).Properties.IsLeftButtonPressed || IsInteractive(e.Source, zone)) return;
            if (e.ClickCount == 2)
            {
                if (window.WindowState != WindowState.FullScreen && window.CanResize)
                    window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                e.Handled = true;
                return;
            }
            window.BeginMoveDrag(e);
            e.Handled = true;
        };
    }

    /// <summary>True when the press landed on a control that wants it (CSS: <c>.drag button, input, a, [role=button]</c> are no-drag).</summary>
    private static bool IsInteractive(object? source, Control zone)
    {
        for (var v = source as Visual; v is not null && !ReferenceEquals(v, zone); v = v.GetVisualParent())
        {
            if (v is Button or TextBox or ComboBox or Slider or SelectingItemsControl or ToggleButton) return true;
            if (v is InputElement { Focusable: true, IsEnabled: true } input && input is TemplatedControl) return true;
        }
        return false;
    }
}
