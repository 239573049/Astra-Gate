using System.Text;

namespace Astra.Desktop.Core.Update;

/// <summary>
/// Windows self-update: the NSIS installer (src/Astra.Desktop/Packaging/installer.nsi, per-user, no elevation) is downloaded and checked
/// against the feed's sha256, then run silently with electron-updater's arguments (<c>/S --updated --force-run</c>: it stops what runs from
/// its folder, installs over it and starts the app again). It is started by PowerShell once this process has exited, so the app quits on
/// its own terms (stopping the server it started) before the installer touches its files; never through cmd.exe (see
/// <c>ProcessRunner.StartDetached</c>). Only a copy the installer put there (its uninstaller beside the executable) updates itself.
/// </summary>
/// <param name="appDir">The folder of the running executable.</param>
/// <param name="stagingDir">Where the installer is downloaded (<see cref="UpdateLayout.DesktopStagingDir"/>).</param>
public sealed class WindowsInstallerUpdater(string appDir, string stagingDir, HttpClient http, IUpdateProcesses processes, Action<string>? log = null)
    : IDesktopUpdater
{
    /// <summary>The uninstaller the installer writes (installer.nsi <c>UNINSTALLER</c>): its presence marks an installed copy.</summary>
    public const string Uninstaller = "Uninstall Astra.exe";

    /// <summary>The installer's silent-update arguments (installer.nsi: <c>/S</c>, <c>--force-run</c>).</summary>
    public static readonly IReadOnlyList<string> InstallerArgs = ["/S", "--updated", "--force-run"];

    /// <inheritdoc />
    public string Kind => "exe";

    /// <inheritdoc />
    public string? CannotSelfUpdateReason()
        => File.Exists(Path.Combine(appDir, Uninstaller)) ? null : "it was not installed by the Astra installer";

    /// <inheritdoc />
    public async Task ApplyAsync(DesktopRelease release, CancellationToken ct = default)
    {
        if (CannotSelfUpdateReason() is { } reason) throw new UpdateException($"This copy of Astra cannot update itself: {reason}.");
        DesktopPayload.Clean(stagingDir);
        var installer = Path.Combine(stagingDir, release.FileName);
        await DesktopPayload.DownloadAsync(http, release, installer, log, ct);
        log?.Invoke($"Installing {release.Version} once Astra has quit.");
        processes.Start(PowerShell(), ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Encode(HandOver(Environment.ProcessId, installer))]);
    }

    /// <summary>The PowerShell hand-over: wait (2 minutes at most) for <paramref name="pid"/> to exit, then start the installer.</summary>
    public static string HandOver(int pid, string installer)
    {
        var args = string.Join(",", InstallerArgs.Select(Quote));
        return $"Wait-Process -Id {pid} -Timeout 120 -ErrorAction SilentlyContinue; Start-Process -FilePath {Quote(installer)} -ArgumentList {args}";
    }

    /// <summary>A PowerShell single-quoted literal.</summary>
    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary><c>-EncodedCommand</c> takes base64 of UTF-16LE: no quoting of paths through a command line.</summary>
    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string PowerShell()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
