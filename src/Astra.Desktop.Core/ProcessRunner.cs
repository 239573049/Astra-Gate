using System.Diagnostics;

namespace Astra.Desktop.Core;

internal static class ProcessRunner
{
    /// <summary>Is a process with this pid alive (also true for one owned by another user).</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>Runs a command with a 5 s limit; stdout, or null when it fails or cannot run.</summary>
    public static async Task<string?> RunAsync(string command, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? await stdout : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the server detached, its stdout and stderr appended to <paramref name="logFile"/>. The redirect is a file the child
    /// writes itself, not a pipe the desktop holds: a pipe would break the server's console writes the moment the desktop exits.
    /// On Unix the shell's <c>exec</c> keeps the server's pid the child's pid. Windows starts the server directly with the log's
    /// handle as its stdout/stderr — never through cmd.exe, which some machines have set to run as administrator
    /// (AppCompatFlags RUNASADMIN), so starting it from this unelevated app failed with "the requested operation requires elevation".
    /// </summary>
    public static Process StartDetached(string binary, string[] args, string logFile)
    {
        if (OperatingSystem.IsWindows())
        {
            using var log = File.OpenHandle(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var direct = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputHandle = log,
                StandardErrorHandle = log,
            };
            foreach (var a in args) direct.ArgumentList.Add(a);
            return Process.Start(direct) ?? throw new InvalidOperationException($"failed to launch {binary}");
        }
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, FileName = "/bin/sh" };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$0\" \"$@\" >> \"$ASTRA_LOG_FILE\" 2>&1");
        psi.ArgumentList.Add(binary);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["ASTRA_LOG_FILE"] = logFile;
        return Process.Start(psi) ?? throw new InvalidOperationException($"failed to launch {binary}");
    }
}
