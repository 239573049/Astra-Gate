using System.Runtime.Versioning;
using Astra.Desktop.Platform.Tray;

namespace Astra.Desktop.Platform;

/// <summary>
/// Puts an image on the macOS pasteboard right away. Avalonia's <c>SetBitmapAsync</c> only <em>promises</em> an image there
/// (<c>NSPasteboardWritingPromised</c>: the PNG is asked for when another app pastes), so a copy is lost once its bitmap is disposed or
/// this process ends — and the desktop ends itself 15 s after its window closes (the tray relaunch). The plain
/// <c>clearContents</c> + <c>setData:forType:</c> here hands the bytes over at once: a PNG, plus a TIFF rendering for apps that only read that.
/// Main thread only (Avalonia's UI thread on macOS).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPasteboard
{
    /// <summary>
    /// Replaces the contents of the general pasteboard (or the one named <paramref name="name"/>, tests) with <paramref name="png"/>;
    /// false when the image could not be written.
    /// </summary>
    public static bool SetImage(byte[] png, string? name = null)
    {
        try
        {
            var board = name is null
                ? ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"))
                : ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.Str(name));
            if (board == IntPtr.Zero) return false;
            ObjC.Send(board, ObjC.Sel("clearContents"));
            var data = ObjC.Data(png);
            var setData = ObjC.Sel("setData:forType:");
            if (ObjC.GetBool(board, setData, data, ObjC.Str("public.png")) == 0) return false;
            var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
            if (image != IntPtr.Zero)
            {
                var tiff = ObjC.Send(image, ObjC.Sel("TIFFRepresentation"));
                if (tiff != IntPtr.Zero) ObjC.GetBool(board, setData, tiff, ObjC.Str("public.tiff"));
                ObjC.Release(image);
            }
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The length of the <paramref name="type"/> data on the pasteboard named <paramref name="name"/>, or -1 (tests read back with it).</summary>
    internal static long DataLength(string name, string type)
    {
        var board = ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.Str(name));
        var data = ObjC.Send(board, ObjC.Sel("dataForType:"), ObjC.Str(type));
        return data == IntPtr.Zero ? -1 : ObjC.GetLong(data, ObjC.Sel("length"));
    }

    /// <summary>Drops the pasteboard named <paramref name="name"/> (tests).</summary>
    internal static void Release(string name)
        => ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.Str(name)), ObjC.Sel("releaseGlobally"));
}
