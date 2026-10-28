using System.Runtime.InteropServices;

namespace Astra.Desktop.Platform;

/// <summary>
/// Gives memory back to the OS while the app lives in the tray (no window). Measured 2026-10-27/28 (<c>.agents/memory/desktop-page-lifetime.md</c>):
/// after the window is hidden the live managed set is ~20 MB, but the GC keeps the heap it grew while the window was used and the native
/// heaps keep their free pages, so a hidden app stayed at its visible size. A compacting collection plus the platform's heap trim brings it
/// down; what the GPU / window server hold is not reachable from here.
/// </summary>
internal static class MemoryRelief
{
    /// <summary>Collects and compacts the managed heap, returns its free memory, then asks the native allocator to do the same.</summary>
    public static void Release()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try
        {
            if (OperatingSystem.IsMacOS()) _ = malloc_zone_pressure_relief(IntPtr.Zero, UIntPtr.Zero);
            else if (OperatingSystem.IsLinux()) _ = malloc_trim(UIntPtr.Zero);
            // Windows: trims the working set (what Task Manager shows); pages come back on demand when the window is opened again.
            else if (OperatingSystem.IsWindows()) _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A libc without the call (musl has no malloc_trim): the managed half still happened.
        }
    }

    // DllImport (not LibraryImport): the project has no unsafe code, like Tray/MacInterop.cs.
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern UIntPtr malloc_zone_pressure_relief(IntPtr zone, UIntPtr goal);

    [DllImport("libc")]
    private static extern int malloc_trim(UIntPtr pad);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
