using System.Runtime.Versioning;
using Astra.Desktop.Platform.Tray;
using Avalonia.Controls;

namespace Astra.Desktop.Platform;

/// <summary>
/// Puts the native traffic lights where Electron's <c>trafficLightPosition</c> does (<see cref="ChromeSpec.TrafficLightX"/> /
/// <see cref="ChromeSpec.TrafficLightY"/>, the cluster's top-left in DIPs from the window's top-left). A plain Avalonia window has no
/// offset API, so the three <c>NSWindow standardWindowButton:</c> views are moved directly; AppKit resets them whenever it lays the
/// title bar out (resize, full screen, activation), so the placement is applied again on those events.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacTrafficLights
{
    // NSWindowCloseButton, NSWindowMiniaturizeButton, NSWindowZoomButton.
    private static readonly long[] Kinds = [0, 1, 2];

    public static void Install(Window window, double x, double y, double spacing)
    {
        void Apply() => Place(window, x, y, spacing);
        window.Opened += (_, _) => Apply();
        window.SizeChanged += (_, _) => Apply();
        window.Activated += (_, _) => Apply();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Apply();
        };
    }

    private static void Place(Window window, double x, double y, double spacing)
    {
        var ns = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (ns == IntPtr.Zero) return;
        var left = x;
        foreach (var kind in Kinds)
        {
            var button = ObjC.SendLong(ns, ObjC.Sel("standardWindowButton:"), kind);
            if (button == IntPtr.Zero) continue;
            var parent = ObjC.Send(button, ObjC.Sel("superview"));
            if (parent == IntPtr.Zero) continue;
            var size = ObjC.GetRect(button, ObjC.Sel("frame"));
            var bounds = ObjC.GetRect(parent, ObjC.Sel("frame"));
            // Cocoa's origin is bottom-left; the title-bar container hugs the window's top edge.
            ObjC.SendDouble2(button, ObjC.Sel("setFrameOrigin:"), left, bounds.Height - y - size.Height);
            left += spacing;
        }
    }
}
