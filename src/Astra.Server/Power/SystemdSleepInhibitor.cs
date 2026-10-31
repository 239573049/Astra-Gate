using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Astra.Server.Power;

/// <summary>
/// Linux: <c>systemd-inhibit --what=idle:sleep --mode=block cat</c>, running for as long as the inhibitor is held. logind
/// keeps the lock while systemd-inhibit runs, and systemd-inhibit runs while <c>cat</c> does — which reads a pipe whose
/// other end only Astra holds, so it ends when Astra lets go (closes the pipe) or exits, however it exits. Without
/// systemd-inhibit (a container, no systemd) or when it cannot take the lock (no system bus, refused by polkit), this logs
/// once and holds nothing for the rest of the run. logind leaves the lid switch alone by default (LidSwitchIgnoreInhibited=yes).
/// Only Linux uses it (<see cref="SleepInhibitors.ForThisMachine"/>), but nothing in it is Linux-only: tests run it on any Unix
/// against a stand-in script.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class SystemdSleepInhibitor(string? executable, ILogger log) : ISleepInhibitor
{
    /// <summary>systemd-inhibit ending this soon after its start means it could not take the lock.</summary>
    private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Process? _process;
    private long _startedAt;
    private bool _unavailable;

    /// <summary>Whether systemd-inhibit turned out missing or unable to take the lock; nothing is tried again this run.</summary>
    internal bool Unavailable
    {
        get
        {
            lock (_gate) return _unavailable;
        }
    }

    /// <summary>systemd-inhibit's command line: the lock, then the command it holds the lock for.</summary>
    internal static IReadOnlyList<string> Arguments(string reason) =>
        ["--what=idle:sleep", "--who=Astra", "--why=" + reason, "--mode=block", "cat"];

    /// <summary>systemd-inhibit on PATH (or in /usr/bin, /bin); null when this machine has none.</summary>
    internal static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs.Concat(["/usr/bin", "/bin"]))
        {
            var path = Path.Combine(dir, "systemd-inhibit");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public bool Acquire(string reason)
    {
        lock (_gate)
        {
            if (_process is not null) return true;
            if (_unavailable) return false;
            if (executable is null) return GiveUp("systemd-inhibit was not found (no systemd, e.g. in a container)");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                // cat's stdin: Astra holds the write end, so cat — and the lock with it — ends when Astra closes it or exits.
                RedirectStandardInput = true,
                // cat echoes only what it reads, which is nothing; systemd-inhibit's errors explain a failure.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in Arguments(reason)) start.ArgumentList.Add(arg);
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                return GiveUp($"{executable} could not be started: {e.Message}");
            }
            if (process is null) return GiveUp($"{executable} could not be started");
            var errors = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 } line) return;
                lock (errors)
                {
                    if (errors.Length < 1024) errors.AppendLine(line);
                }
            };
            // Waits for _gate when it fires at once, so it always sees this process as _process.
            process.Exited += (_, _) => OnExited(process, errors);
            process.EnableRaisingEvents = true;
            process.BeginErrorReadLine();
            (_process, _startedAt) = (process, Stopwatch.GetTimestamp());
            return true;
        }
    }

    public void Release()
    {
        Process? process;
        lock (_gate)
        {
            (process, _process) = (_process, null);
        }
        if (process is null) return;
        try
        {
            // cat reads end-of-file and ends; systemd-inhibit follows, and logind drops the lock.
            process.StandardInput.Close();
            if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            log.LogDebug(e, "Stopping systemd-inhibit");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnExited(Process process, StringBuilder errors)
    {
        bool early;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process)) return; // let go on purpose (Release)
            _process = null;
            early = Stopwatch.GetElapsedTime(_startedAt) < StartWindow;
            if (early) _unavailable = true;
        }
        var code = -1;
        try
        {
            process.WaitForExit(); // also drains stderr
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }
        string detail;
        lock (errors)
        {
            detail = errors.ToString().Trim();
        }
        if (early)
            log.LogWarning("Sleep prevention is unavailable on this machine: systemd-inhibit exited with code {Code}: {Detail}", code, detail);
        else
            log.LogWarning("systemd-inhibit ended on its own (code {Code}); idle sleep is not prevented until the gateway is busy again after a pause", code);
        process.Dispose();
    }

    private bool GiveUp(string why)
    {
        _unavailable = true;
        log.LogInformation("Sleep prevention is unavailable on this machine: {Why}", why);
        return false;
    }
}
