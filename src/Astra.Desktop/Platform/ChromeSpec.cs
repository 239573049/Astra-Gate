namespace Astra.Desktop.Platform;

/// <summary>The three window-chrome families of desktop/src/shared/chrome.ts (<c>process.platform</c> darwin / win32 / other).</summary>
public enum DesktopOs { MacOS, Windows, Linux }

/// <summary>
/// The window chrome the Electron shell asks for, per platform (<c>windowChromeOptions</c>, <c>TOOLBAR_HEIGHT</c>,
/// <c>TRAFFIC_LIGHT_POSITION</c>, <c>themeBackground</c>, <c>sidebarRailWidth</c> in web/src/shell/bridge.ts). Pure data with
/// no Avalonia dependency, so the platform differences can be unit tested; <see cref="WindowChrome"/> applies it.
/// </summary>
/// <param name="Os">Platform family.</param>
/// <param name="ToolbarHeight">Floating toolbar / drag region / Windows title-bar overlay height (52).</param>
/// <param name="ExtendsIntoTitleBar">Content runs under the (hidden) title bar: macOS hiddenInset, Windows hidden + overlay.</param>
/// <param name="HidesManagedTitleBar">
/// The AtomUI title bar is hidden (<c>Window.IsTitleBarVisible = false</c>) and the shell draws under it. True on macOS only:
/// on Windows AtomUI draws the caption buttons inside its own title bar, and hides them together with it, so the Windows overlay
/// cannot be built on the public API yet (plan avalonia-client §22); Windows keeps AtomUI's managed title bar.
/// </param>
/// <param name="NativeTrafficLights">macOS: the system traffic lights stay and sit inside the sidebar.</param>
/// <param name="TrafficLightX">Left edge of the close button, in DIPs from the window's left edge.</param>
/// <param name="TrafficLightY">Top edge of the traffic-light cluster, in DIPs from the window's top edge.</param>
/// <param name="HasDragRegion">The toolbar and the sidebar's header row move the window (web <c>.drag</c>): macOS and Windows, never Linux.</param>
/// <param name="CaptionOverlayWidth">Windows: right padding the toolbar keeps free for the caption buttons (web TOOLBAR_RIGHT_RESERVE).</param>
/// <param name="SidebarRail">Collapsed sidebar width: 78 where the traffic lights live inside it, else 68.</param>
/// <param name="Background">Solid window background shown before the first paint (no white flash).</param>
public sealed record ChromeSpec(
    DesktopOs Os,
    double ToolbarHeight,
    bool ExtendsIntoTitleBar,
    bool HidesManagedTitleBar,
    bool NativeTrafficLights,
    double TrafficLightX,
    double TrafficLightY,
    bool HasDragRegion,
    double CaptionOverlayWidth,
    double SidebarRail,
    string Background)
{
    public const double Toolbar = 52;
    public const double TrafficLightLeft = 20;
    public const double TrafficLightTop = 20;
    /// <summary>Gap between traffic lights: three 14 px buttons, 20 px apart (web bridge.ts).</summary>
    public const double TrafficLightSpacing = 20;
    public const double SidebarExpanded = 224;
    /// <summary>Inset of the floating glass sidebar from the window edge (web PANEL_INSET).</summary>
    public const double PanelInset = 8;
    public const double SidebarRadius = 18;
    public const double CaptionButtonsReserve = 140;

    /// <summary>The shell's own drag region (toolbar + sidebar header) exists only where the managed title bar is hidden.</summary>
    public bool HasAppDragRegion => HasDragRegion && HidesManagedTitleBar;

    public static string ThemeBackground(bool dark) => dark ? "#1C1C1E" : "#F2F2F7";

    public static string OverlaySymbolColor(bool dark) => dark ? "#FFFFFF" : "#1D1D1F";

    public static DesktopOs CurrentOs()
        => OperatingSystem.IsMacOS() ? DesktopOs.MacOS : OperatingSystem.IsWindows() ? DesktopOs.Windows : DesktopOs.Linux;

    public static ChromeSpec For(DesktopOs os, bool dark)
    {
        var background = ThemeBackground(dark);
        return os switch
        {
            DesktopOs.MacOS => new(os, Toolbar, true, true, true, TrafficLightLeft, TrafficLightTop, true, 0, 78, background),
            DesktopOs.Windows => new(os, Toolbar, true, false, false, 0, 0, true, CaptionButtonsReserve, 68, background),
            // Linux keeps the system title bar; the renderer omits the drag region there.
            _ => new(os, Toolbar, false, false, false, 0, 0, false, 0, 68, background),
        };
    }
}
