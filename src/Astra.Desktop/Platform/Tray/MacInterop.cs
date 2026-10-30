using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Astra.Desktop.Platform.Tray;

/// <summary>A Cocoa point (two doubles; blittable).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NSPoint(double X, double Y);

/// <summary>A Cocoa size.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NSSize(double Width, double Height);

/// <summary>A Cocoa rectangle (origin bottom-left, screen points).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NSRect(double X, double Y, double Width, double Height);

/// <summary>
/// The few Objective-C runtime calls the menu-bar item needs (Avalonia's <c>TrayIcon</c> has no title next to the icon, no icon bounds and
/// no separate left / right click on macOS). Plain blittable <c>DllImport</c>s into libobjc (no unsafe code, Native AOT safe); callbacks are
/// delegates kept alive for the process lifetime. Every call runs on the main thread, which is Avalonia's UI thread on macOS.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] public static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] public static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a, long b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendLong(IntPtr receiver, IntPtr sel, long a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble(IntPtr receiver, IntPtr sel, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble2(IntPtr receiver, IntPtr sel, double a, double b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendBool(IntPtr receiver, IntPtr sel, byte a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendSize(IntPtr receiver, IntPtr sel, NSSize a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long GetLong(IntPtr receiver, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte GetBool(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSPoint GetPoint(IntPtr receiver, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte PopUp(IntPtr menu, IntPtr sel, IntPtr item, NSPoint location, IntPtr view);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern NSRect GetRectArm(IntPtr receiver, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] private static extern NSRect GetRectX64(IntPtr receiver, IntPtr sel);

    /// <summary>A method returning an <see cref="NSRect"/>: <c>objc_msgSend_stret</c> on x86_64 (a struct over 16 bytes), plain on arm64.</summary>
    public static NSRect GetRect(IntPtr receiver, IntPtr sel)
        => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? GetRectX64(receiver, sel) : GetRectArm(receiver, sel);

    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Class(string name) => objc_getClass(name);

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr Str(string? s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s ?? "");
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>An autoreleased NSData copy of <paramref name="bytes"/>.</summary>
    public static IntPtr Data(byte[] bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return Send(Class("NSData"), Sel("dataWithBytes:length:"), buffer, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary><c>[[cls alloc] init]</c> (owned: +1).</summary>
    public static IntPtr New(string cls) => Send(Send(Class(cls), Sel("alloc")), Sel("init"));

    /// <summary><c>[NSApplication sharedApplication]</c>.</summary>
    public static IntPtr App => Send(Class("NSApplication"), Sel("sharedApplication"));

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Send(obj, Sel("release"));
    }
}

/// <summary>macOS app-level switches: the Dock icon (menu-bar-only "accessory" mode) and bringing the app forward.</summary>
[SupportedOSPlatform("macos")]
internal static class MacApp
{
    /// <summary>Regular (Dock icon, app menu) or accessory (menu bar only), Electron's <c>app.dock.show/hide</c>.</summary>
    public static void SetDockVisible(bool visible)
    {
        try
        {
            ObjC.SendLong(ObjC.App, ObjC.Sel("setActivationPolicy:"), visible ? 0 : 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Sets the Dock icon from PNG bytes. A bare executable (<c>dotnet run</c>, an unbundled AOT binary) has no Info.plist / .icns, so the Dock
    /// shows the generic "exec" icon and <c>WindowIcon</c> does not reach it on macOS; inside Astra.app the bundle's own icon is kept, and
    /// <paramref name="png"/> (a 1024 px render) is never called.
    /// </summary>
    public static void SetDockIcon(Func<byte[]> png)
    {
        if (AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal)) return;
        try
        {
            var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), ObjC.Data(png()));
            if (image == IntPtr.Zero) return;
            ObjC.Send(ObjC.App, ObjC.Sel("setApplicationIconImage:"), image);
            ObjC.Release(image);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>The standard about panel (Electron's role <c>about</c>): name, version and icon from the bundle.</summary>
    public static void ShowAboutPanel()
    {
        try
        {
            Activate();
            ObjC.Send(ObjC.App, ObjC.Sel("orderFrontStandardAboutPanel:"), IntPtr.Zero);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Activates the app (coming back from accessory mode it is not active).</summary>
    public static void Activate()
    {
        try
        {
            // macOS 14+: -activate (cooperative); older: -activateIgnoringOtherApps:.
            if (ObjC.GetBool(ObjC.App, ObjC.Sel("respondsToSelector:"), ObjC.Sel("activate")) != 0) ObjC.Send(ObjC.App, ObjC.Sel("activate"));
            else ObjC.SendBool(ObjC.App, ObjC.Sel("activateIgnoringOtherApps:"), 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Makes a window key and front (Electron's <c>win.focus()</c>), so it can lose focus again and close.</summary>
    public static void MakeKey(IntPtr nsWindow)
    {
        if (nsWindow != IntPtr.Zero) ObjC.Send(nsWindow, ObjC.Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
    }

    /// <summary>The mouse position in Avalonia's screen coordinates (points, top-left origin of the primary screen).</summary>
    public static (double X, double Y) MouseLocation()
    {
        var p = ObjC.GetPoint(ObjC.Class("NSEvent"), ObjC.Sel("mouseLocation"));
        return (p.X, PrimaryHeight() - p.Y);
    }

    /// <summary>Converts a Cocoa screen rectangle (bottom-left origin) to Avalonia's (top-left origin).</summary>
    public static (double X, double Y, double W, double H) FlipRect(NSRect r) => (r.X, PrimaryHeight() - r.Y - r.Height, r.Width, r.Height);

    private static double PrimaryHeight()
    {
        var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("screens"));
        var main = ObjC.SendLong(screens, ObjC.Sel("objectAtIndex:"), 0);
        return ObjC.GetRect(main, ObjC.Sel("frame")).Height;
    }

    /// <summary>
    /// Lets a window open on the current Space, also over full-screen apps (Electron's <c>setVisibleOnAllWorkspaces(true, { visibleOnFullScreen })</c>):
    /// <c>NSWindowCollectionBehaviorCanJoinAllSpaces | FullScreenAuxiliary</c>.
    /// </summary>
    public static void JoinAllSpaces(IntPtr nsWindow)
    {
        if (nsWindow == IntPtr.Zero) return;
        ObjC.SendLong(nsWindow, ObjC.Sel("setCollectionBehavior:"), (1 << 0) | (1 << 8));
    }
}
