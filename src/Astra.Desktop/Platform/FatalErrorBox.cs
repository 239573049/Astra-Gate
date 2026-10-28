using System.Diagnostics;
using System.Runtime.InteropServices;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Platform;

/// <summary>
/// The last-resort error box (Electron's <c>dialog.showErrorBox('Astra', 'Fatal error: …')</c> in desktop/src/main.ts): the UI framework may
/// be the thing that failed, so it does not use Avalonia — Windows <c>MessageBoxW</c>, macOS <c>osascript</c>'s <c>display alert</c>, Linux
/// <c>zenity</c> / <c>kdialog</c> when installed — and always writes the error to stderr as well. Never throws.
/// </summary>
internal static class FatalErrorBox
{
    // DllImport (not LibraryImport): the project has no unsafe code, like the libobjc imports in Tray/MacInterop.cs.
    [DllImport("user32.dll", EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.LPWStr)] string caption, uint type);

    /// <summary>Shows <paramref name="error"/> and returns once the user dismissed it.</summary>
    public static void Show(Exception error)
    {
        var text = UpdateDialogLabels.For(Loc.Language).FatalError(error.Message);
        Console.Error.WriteLine($"[astra] fatal: {error}");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = MessageBoxW(IntPtr.Zero, text, "Astra", 0x10 /* MB_ICONERROR */);
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                Run("/usr/bin/osascript", "-e", "on run argv", "-e", "display alert \"Astra\" message (item 1 of argv) as critical", "-e", "end run", text);
                return;
            }
            foreach (var (tool, args) in new[] { ("zenity", new[] { "--error", "--title=Astra", "--text", text }), ("kdialog", new[] { "--title", "Astra", "--error", text }) })
                if (Which(tool) is { } path)
                {
                    Run(path, args);
                    return;
                }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // stderr already has it.
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }
}
