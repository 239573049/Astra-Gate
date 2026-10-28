using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Layout;

/// <summary>
/// Marks the parts of a page that drag the window (web: the <c>.drag</c> class on the page toolbar, active on macOS and
/// Windows only, where the window has no system title bar over the content). The attached property is a marker and does
/// nothing by itself: the window chrome (<c>MainWindow</c> / <c>Platform/**</c>, owned by the window-chrome work) reads it
/// when a press lands on the window and starts <c>BeginMoveDrag</c> when the pressed element, or an ancestor, is marked
/// and nothing interactive (button, input, link) sits between them. <see cref="PageScaffold"/> marks its toolbar.
/// Usage: <c>DragRegion.SetIsDragRegion(toolbar, true)</c>, or <c>components:DragRegion.IsDragRegion="True"</c> in XAML.
/// Plan avalonia-client §17 / §21 WP0b.
/// </summary>
public static class DragRegion
{
    /// <summary>True on an element whose empty area moves the window.</summary>
    public static readonly AttachedProperty<bool> IsDragRegionProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("IsDragRegion", false, inherits: false);

    /// <summary>Reads <see cref="IsDragRegionProperty"/>.</summary>
    public static bool GetIsDragRegion(Control element) => element.GetValue(IsDragRegionProperty);

    /// <summary>Writes <see cref="IsDragRegionProperty"/>.</summary>
    public static void SetIsDragRegion(Control element, bool value) => element.SetValue(IsDragRegionProperty, value);
}
