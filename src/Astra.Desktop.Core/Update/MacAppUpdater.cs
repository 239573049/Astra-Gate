namespace Astra.Desktop.Core.Update;

/// <summary>
/// macOS self-update: the release zip (<c>ditto -c -k --keepParent Astra.app</c>, scripts/package-desktop.mjs) is downloaded and checked
/// against the feed's sha256, unpacked with ditto (which keeps the bundle's symlinks and signature intact), and accepted only with
/// bundle id <c>io.astra.desktop</c> and — when the running app is validly signed — a valid signature from the same team, so the swap
/// can never leave an app Gatekeeper refuses. <see cref="SwapScript"/> then waits for this process to exit and swaps the bundle by
/// renames inside its folder (rolling back when a step fails) and opens it. The files come from this process, so they carry no
/// quarantine flag.
/// </summary>
/// <param name="appBundle">The running <c>Astra.app</c>.</param>
/// <param name="stagingDir">Where downloads are unpacked (<see cref="UpdateLayout.DesktopStagingDir"/>).</param>
/// <param name="logFile">Where the hand-over shell writes what it did.</param>
public sealed class MacAppUpdater(string appBundle, string stagingDir, string logFile, HttpClient http, IUpdateProcesses processes, Action<string>? log = null)
    : IDesktopUpdater
{
    /// <summary>The bundle id every Astra release keeps (plan avalonia-client §5.1).</summary>
    public const string BundleId = "io.astra.desktop";

    /// <summary>
    /// The hand-over, run by <c>/bin/sh -c</c> with $1 = pid to wait for, $2 = installed app, $3 = staged app, $4 = log. Both renames
    /// stay inside the app's folder (.Astra.app.new / .Astra.app.old), so a folder the user can write is all it needs; any failure puts
    /// the old app back. It opens whichever app is in place at the end.
    /// </summary>
    public const string SwapScript = """
        exec >>"$4" 2>&1
        echo "$(date '+%Y-%m-%d %H:%M:%S') desktop update: waiting for pid $1"
        n=0
        while kill -0 "$1" 2>/dev/null; do
          n=$((n+1))
          if [ "$n" -gt 1200 ]; then echo "the app did not exit; update abandoned"; exit 1; fi
          sleep 0.1
        done
        dir=$(dirname "$2"); name=$(basename "$2")
        new="$dir/.$name.new"; old="$dir/.$name.old"
        rm -rf "$new" "$old"
        if ! mv "$3" "$new"; then echo "could not move the new app beside the old one"; open "$2"; exit 1; fi
        if ! mv "$2" "$old"; then echo "could not move the old app aside"; rm -rf "$new"; open "$2"; exit 1; fi
        if ! mv "$new" "$2"; then echo "could not move the new app in; restoring"; mv "$old" "$2"; open "$2"; exit 1; fi
        rm -rf "$old"
        echo "desktop update: swapped $2"
        open "$2"
        """;

    /// <inheritdoc />
    public string Kind => "zip";

    /// <inheritdoc />
    public string? CannotSelfUpdateReason()
    {
        if (!appBundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(appBundle))
            return "it is not running from an app bundle";
        var folder = Path.GetDirectoryName(appBundle)!;
        // A copy run from the dmg or a translocated download sits on a read-only path.
        return DesktopPayload.IsWritableDir(folder) ? null : $"{folder} is not writable";
    }

    /// <inheritdoc />
    public async Task ApplyAsync(DesktopRelease release, CancellationToken ct = default)
    {
        if (CannotSelfUpdateReason() is { } reason) throw new UpdateException($"This copy of Astra cannot update itself: {reason}.");
        DesktopPayload.Clean(stagingDir);
        var zip = Path.Combine(stagingDir, release.FileName);
        await DesktopPayload.DownloadAsync(http, release, zip, log, ct);

        var unpacked = Path.Combine(stagingDir, "unpacked");
        var ditto = await processes.RunAsync("/usr/bin/ditto", ["-x", "-k", zip, unpacked], ct);
        if (ditto.ExitCode != 0) throw new UpdateException($"Could not unpack {release.FileName}: {Last(ditto)}");
        DesktopPayload.TryDelete(zip);
        var apps = Directory.Exists(unpacked) ? Directory.GetDirectories(unpacked, "*.app") : [];
        if (apps.Length != 1) throw new UpdateException($"{release.FileName} does not contain exactly one app.");
        var staged = apps[0];

        var id = await processes.RunAsync("/usr/libexec/PlistBuddy", ["-c", "Print :CFBundleIdentifier", Path.Combine(staged, "Contents", "Info.plist")], ct);
        if (id.ExitCode != 0 || id.StdOut.Trim() != BundleId)
            throw new UpdateException($"{release.FileName} is not an Astra app (bundle id \"{id.StdOut.Trim()}\").");

        if (await IsValidlySigned(appBundle, ct))
        {
            if (!await IsValidlySigned(staged, ct)) throw new UpdateException($"The app in {release.FileName} is not validly signed.");
            var (current, next) = (await TeamOf(appBundle, ct), await TeamOf(staged, ct));
            if (current != next) throw new UpdateException($"The app in {release.FileName} is signed by another team ({next ?? "none"}, expected {current ?? "none"}).");
        }

        log?.Invoke($"Installing {release.Version} once Astra has quit.");
        processes.Start("/bin/sh", ["-c", SwapScript, "astra-update", Environment.ProcessId.ToString(), appBundle, staged, logFile]);
    }

    private async Task<bool> IsValidlySigned(string app, CancellationToken ct)
        => (await processes.RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], ct)).ExitCode == 0;

    /// <summary>The <c>TeamIdentifier</c> codesign reports (on stderr), or null for none / "not set".</summary>
    private async Task<string?> TeamOf(string app, CancellationToken ct)
    {
        var r = await processes.RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", app], ct);
        foreach (var line in (r.StdErr + "\n" + r.StdOut).Split('\n'))
        {
            if (!line.StartsWith("TeamIdentifier=", StringComparison.Ordinal)) continue;
            var team = line["TeamIdentifier=".Length..].Trim();
            return team is "" or "not set" ? null : team;
        }
        return null;
    }

    private static string Last(CommandResult r)
    {
        var text = (r.StdErr.Trim().Length > 0 ? r.StdErr : r.StdOut).Trim();
        return text.Length > 0 ? text.Split('\n')[^1] : $"exit code {r.ExitCode}";
    }
}
