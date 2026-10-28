using Astra.Desktop.Core.Shell;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// The seam between Settings › Tray panel and the tray panel itself. The web renders the real <c>TrayPanelView</c> as a live preview next
/// to the switches; that view belongs to the tray work package (WP10), which sets <see cref="Factory"/> to build it from the prefs and the
/// service state. Until it does, the preview card is simply not shown (there is nothing to preview).
/// </summary>
public static class TrayPreview
{
    /// <summary>Builds the panel preview for prefs and state; null = no preview available.</summary>
    public static Func<TrayPrefs, TrayPanelState, Control>? Factory { get; set; }
}
