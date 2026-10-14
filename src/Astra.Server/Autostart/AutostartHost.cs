using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Astra.Server.Tunnel;
using Microsoft.Win32;

namespace Astra.Server.Autostart;

/// <summary>What a command run by the autostart code exited with and printed. A program that cannot start exits -1.</summary>
public sealed record AutostartCommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Values under the per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).</summary>
public interface IAutostartRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

/// <summary>
/// The parts of the machine starting at login touches: the user's home (LaunchAgents, systemd user units), the commands
/// it runs (systemctl, schtasks) and the Run key. <see cref="Real"/> is this machine; tests point <see cref="UserHome"/>
/// at a temporary directory and fake the rest, so they never write a real login entry.
/// </summary>
public sealed class AutostartHost
{
    /// <summary>darwin | linux | windows | other.</summary>
    public required string Platform { get; init; }

    /// <summary>The user's home directory (not the Astra data dir).</summary>
    public required string UserHome { get; init; }

    /// <summary>What starts this server, before the subcommand: its executable, plus the dll under the dotnet host. Empty when unknown.</summary>
    public required IReadOnlyList<string> Launch { get; init; }

    /// <summary>A container's restart policy starts the server there; it has no login to hook.</summary>
    public bool InContainer { get; init; }

    /// <summary>systemd runs this machine (sd_booted: /run/systemd/system exists), so it has a user manager to ask.</summary>
    public bool HasSystemd { get; init; }

    public required Func<string, IReadOnlyList<string>, CancellationToken, Task<AutostartCommandResult>> Run { get; init; }

    /// <summary>Windows only.</summary>
    public IAutostartRunKey? RunKey { get; init; }

    public static AutostartHost Real()
    {
        var exe = Environment.ProcessPath;
        IReadOnlyList<string> launch = exe is null ? []
            // `dotnet astra-server.dll`: the entry needs the dll too (the published binary is its own executable).
            : string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? [exe, Path.GetFullPath(Environment.GetCommandLineArgs()[0])]
                : [exe];
        IAutostartRunKey? runKey = null;
        if (OperatingSystem.IsWindows()) runKey = new RegistryRunKey();
        return new AutostartHost
        {
            Platform = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other",
            UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Launch = launch,
            InContainer = TunnelService.InContainer(Environment.GetEnvironmentVariable),
            HasSystemd = OperatingSystem.IsLinux() && Directory.Exists("/run/systemd/system"),
            Run = RunAsync,
            RunKey = runKey,
        };
    }

    private static async Task<AutostartCommandResult> RunAsync(string program, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new AutostartCommandResult(-1, "", $"{program}: {ex.Message}");
        }
        if (process is null) return new AutostartCommandResult(-1, "", $"{program} did not start");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                if (ct.IsCancellationRequested) throw;
                return new AutostartCommandResult(-1, "", $"{program} timed out");
            }
            return new AutostartCommandResult(process.ExitCode, await stdout, await stderr);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class RegistryRunKey : IAutostartRunKey
    {
        private const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Get(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey);
            return key?.GetValue(name) as string;
        }

        public void Set(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void Delete(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
