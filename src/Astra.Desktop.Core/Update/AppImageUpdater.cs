namespace Astra.Desktop.Core.Update;

/// <summary>
/// Linux self-update: the new AppImage is downloaded beside the running one (<c>$APPIMAGE</c>), checked against the feed's sha256, made
/// executable and renamed over it — the same path, so launchers and the autostart entry keep working; the running copy keeps its open
/// file until it exits. A shell then starts the new AppImage once this process has exited (the single-instance lock is free by then).
/// </summary>
/// <param name="appImage">The file <c>$APPIMAGE</c> names, or null when this is not an AppImage run.</param>
public sealed class AppImageUpdater(string? appImage, HttpClient http, IUpdateProcesses processes, Action<string>? log = null) : IDesktopUpdater
{
    /// <summary>The hand-over, run by <c>/bin/sh -c</c> with $1 = pid to wait for, $2 = the AppImage to start.</summary>
    public const string RelaunchScript = """
        n=0
        while kill -0 "$1" 2>/dev/null; do
          n=$((n+1))
          if [ "$n" -gt 1200 ]; then exit 1; fi
          sleep 0.1
        done
        exec "$2"
        """;

    /// <inheritdoc />
    public string Kind => "appimage";

    /// <inheritdoc />
    public string? CannotSelfUpdateReason()
    {
        if (string.IsNullOrWhiteSpace(appImage) || !File.Exists(appImage)) return "it is not running as an AppImage";
        var folder = Path.GetDirectoryName(appImage)!;
        return DesktopPayload.IsWritableDir(folder) ? null : $"{folder} is not writable";
    }

    /// <inheritdoc />
    public async Task ApplyAsync(DesktopRelease release, CancellationToken ct = default)
    {
        if (CannotSelfUpdateReason() is { } reason) throw new UpdateException($"This copy of Astra cannot update itself: {reason}.");
        var target = appImage!;
        // Same folder, so the final step is a rename (atomic) and not a copy.
        var download = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.update");
        await DesktopPayload.DownloadAsync(http, release, download, log, ct);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(download, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.Move(download, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DesktopPayload.TryDelete(download);
            throw new UpdateException($"Could not replace {target} ({e.Message}).");
        }
        log?.Invoke($"Replaced {target} with {release.Version}; starting it once Astra has quit.");
        processes.Start("/bin/sh", ["-c", RelaunchScript, "astra-update", Environment.ProcessId.ToString(), target]);
    }
}
