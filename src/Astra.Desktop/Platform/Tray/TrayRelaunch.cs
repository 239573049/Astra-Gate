using System.Diagnostics;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Platform.Tray;

/// <summary>
/// The app restarting itself into the tray. A window that has been on screen leaves ~120–170 MB of GPU / window-server memory behind
/// that neither hiding nor closing it gives back (measured 2026-10-28: tray after a close 263–286 MB, a start that never showed a
/// window 105 MB), so after a while in the tray the app hands over to a fresh <c>--hidden</c> process and exits. The successor waits for
/// this process to end (<see cref="AfterArg"/>, before it claims the single-instance lock), adopts the server this run spawned as its own
/// (<see cref="OwnedServerArg"/>, so Quit still stops it) and opens on the page that was shown (<see cref="RouteArg"/>).
/// </summary>
internal static class TrayRelaunch
{
    /// <summary><c>--after=&lt;pid&gt;</c>: wait for the predecessor to exit first.</summary>
    public const string AfterArg = "--after=";

    /// <summary><c>--owned-server=&lt;pid&gt;</c>: the server the predecessor spawned.</summary>
    public const string OwnedServerArg = "--owned-server=";

    /// <summary><c>--route=&lt;AppRoute&gt;[/&lt;param&gt;]</c>: the page to open on.</summary>
    public const string RouteArg = "--route=";

    private static readonly TimeSpan PredecessorGrace = TimeSpan.FromSeconds(10);

    /// <summary>The successor's command line.</summary>
    public static string[] Args(int self, PageViewModel? page, int? ownedServer)
    {
        var args = new List<string> { LoginItemService.HiddenArg, AfterArg + self };
        if (ownedServer is { } pid) args.Add(OwnedServerArg + pid);
        if (page is not null) args.Add(RouteArg + page.Route + (page.Param is { } p ? "/" + p : ""));
        return [.. args];
    }

    /// <summary>Starts the successor; false (and this process keeps running) when it could not be started.</summary>
    public static bool TryStart(string[] args)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            return Process.Start(start) is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>In the successor, before anything else: waits (bounded) for the predecessor named by <see cref="AfterArg"/> to exit.</summary>
    public static void WaitForPredecessor(IEnumerable<string> args)
    {
        if (Value(args, AfterArg) is not { } text || !int.TryParse(text, out var pid)) return;
        var deadline = DateTime.UtcNow + PredecessorGrace;
        while (IsAlive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(50);
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The server pid handed over by <see cref="OwnedServerArg"/>.</summary>
    public static int? OwnedServer(IEnumerable<string> args)
        => Value(args, OwnedServerArg) is { } text && int.TryParse(text, out var pid) && pid > 0 ? pid : null;

    /// <summary>The page handed over by <see cref="RouteArg"/>.</summary>
    public static (AppRoute Route, string? Param)? Route(IEnumerable<string> args)
    {
        if (Value(args, RouteArg) is not { Length: > 0 } text) return null;
        var slash = text.IndexOf('/');
        var name = slash < 0 ? text : text[..slash];
        if (!Enum.TryParse<AppRoute>(name, ignoreCase: false, out var route) || !Enum.IsDefined(route)) return null;
        return (route, slash < 0 ? null : text[(slash + 1)..]);
    }

    private static string? Value(IEnumerable<string> args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
