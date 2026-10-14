using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Astra.Core;

namespace Astra.Server.Autostart;

/// <summary>
/// The Run key starts astra-server.exe — a console program — in a console window of its own, which would stay open for
/// as long as the server runs (and closing it would stop the server). Started by autostart, the server lets go of that
/// window right away and writes its console output to logs/server-stdout.log instead, as launchd does on macOS.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsConsole
{
    public static bool StartedByAutostart(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--started-by" && args[i + 1] == "autostart") return true;
        }
        return false;
    }

    public static void Detach(AstraPaths paths)
    {
        FreeConsole();
        try
        {
            Directory.CreateDirectory(paths.LogsDir);
            var stream = new FileStream(Path.Combine(paths.LogsDir, "server-stdout.log"), FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No console and no log file: the file logger under logs/ still records what matters.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int FreeConsole();
}
