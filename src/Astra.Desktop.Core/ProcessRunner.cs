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
    /// Starts the server detached, its stdout and stderr appended to <paramref name="logFile"/>. The redirect is the
    /// shell's, not a pipe the desktop holds: a pipe would break the server's console writes the moment the desktop
    /// exits. On Unix <c>exec</c> keeps the server's pid the child's pid; on Windows the child is cmd.exe, which is why
    /// runtime.json's pid cannot be the only link back to a spawn (see <see cref="RuntimeInfo.IsFrom"/>).
    /// </summary>
    public static Process StartDetached(string binary, string[] args, string logFile)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"\"\"{binary}\" {string.Join(' ', args)} >> \"{logFile}\" 2>&1\"");
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$0\" \"$@\" >> \"$ASTRA_LOG_FILE\" 2>&1");
            psi.ArgumentList.Add(binary);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["ASTRA_LOG_FILE"] = logFile;
        }
        return Process.Start(psi) ?? throw new InvalidOperationException($"failed to launch {binary}");
    }
}
