using System.Text.RegularExpressions;

namespace Astra.Desktop.Core;

/// <summary>Runs a command and returns its stdout, or null when it fails or cannot be run.</summary>
public delegate Task<string?> RunCommand(string command, string[] args);

/// <summary>
/// Finds the Astra server listening on a port when nothing on disk names it — a server whose runtime.json entry a
/// later start overwrote keeps the configured port, answers health checks, and cannot be stopped by token or recorded pid.
/// Port of desktop/src/shared/listener.ts.
/// </summary>
public static partial class ListenerLookup
{
    public static List<int> ParseLsofPids(string stdout)
        => stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(char.IsAsciiDigit))
                 .Select(l => int.TryParse(l, out var p) ? p : 0).Where(p => p > 0).ToList();

    /// <summary>
    /// <c>netstat -ano -p TCP</c> rows: proto, local, foreign, state, pid. The state column is localized on some Windows
    /// languages, so a listener is recognised by its unconnected foreign address instead.
    /// </summary>
    public static List<int> ParseNetstatListenerPids(string stdout, int port)
    {
        var pids = new List<int>();
        foreach (var line in stdout.Split('\n'))
        {
            var cols = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 5 || !cols[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!cols[1].EndsWith($":{port}", StringComparison.Ordinal) || !cols[2].EndsWith(":0", StringComparison.Ordinal)) continue;
            if (int.TryParse(cols[^1], out var pid) && pid > 0 && !pids.Contains(pid)) pids.Add(pid);
        }
        return pids;
    }

    /// <summary><c>tasklist /FO CSV /NH</c> row: the image name is the first quoted field.</summary>
    public static string? ParseTasklistImage(string stdout)
        => TasklistImage().Match(stdout.Trim()) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>`astra-server` (bundled / dev) and `astra-server-&lt;version&gt;` (managed), with .exe on Windows; a path is reduced to its file name.</summary>
    public static bool IsAstraServerExecutable(string nameOrPath)
    {
        var name = nameOrPath.Trim().Split('/', '\\')[^1];
        return ServerExecutable().IsMatch(name);
    }

    /// <summary>The pid of the astra-server listening on <paramref name="port"/>; null when none is found, the lookup tool is missing, or the listener is some other program (never a kill candidate).</summary>
    public static async Task<int?> FindServerListenerPidAsync(int port, RunCommand run)
    {
        if (OperatingSystem.IsWindows())
        {
            var netstat = await run("netstat", ["-ano", "-p", "TCP"]);
            var pids = netstat is null ? [] : ParseNetstatListenerPids(netstat, port);
            if (pids.Count != 1) return null;
            var row = await run("tasklist", ["/FI", $"PID eq {pids[0]}", "/FO", "CSV", "/NH"]);
            return row is not null && ParseTasklistImage(row) is { } image && IsAstraServerExecutable(image) ? pids[0] : null;
        }
        var lsof = OperatingSystem.IsMacOS() ? "/usr/sbin/lsof" : "lsof";
        var output = await run(lsof, ["-nP", "-t", $"-iTCP:{port}", "-sTCP:LISTEN"]);
        var found = output is null ? [] : ParseLsofPids(output);
        if (found.Count != 1) return null;
        var comm = await run("ps", ["-o", "comm=", "-p", found[0].ToString()]);
        return comm is not null && IsAstraServerExecutable(comm) ? found[0] : null;
    }

    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex TasklistImage();

    [GeneratedRegex(@"^astra-server(-[^\\/]*)?(\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ServerExecutable();
}
