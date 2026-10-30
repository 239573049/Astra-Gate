using System.Diagnostics;
using System.Security.Cryptography;

namespace Astra.Desktop.Core.Update;

/// <summary>
/// The app replacing ITSELF on the tracks that ship their own server (<see cref="UpdateTrack.Dmg"/>, <see cref="UpdateTrack.Installer"/>;
/// plan avalonia-client §4.3). The server is never updated alone there: the new app's bundled server takes over at launch
/// (<see cref="ServerBinaryResolver"/>). Strategies:
/// <list type="bullet">
/// <item><b>macOS</b> (<see cref="MacAppUpdater"/>): the release zip, checked against the feed's sha256, unpacked with ditto, required to keep
/// bundle id <c>io.astra.desktop</c> and the running app's signing team; a shell waits for this process to exit, swaps the bundle and opens it.</item>
/// <item><b>Windows</b> (<see cref="WindowsInstallerUpdater"/>): the NSIS installer, run with <c>/S --updated --force-run</c> once this process has exited.</item>
/// <item><b>Linux</b> (<see cref="AppImageUpdater"/>): the new AppImage replaces the file <c>$APPIMAGE</c> names and is started once this process has exited.</item>
/// </list>
/// The npm track keeps delegating to <c>astra update --desktop</c> (the CLI's update-core is unchanged).
/// </summary>
public interface IDesktopUpdater
{
    /// <summary>The feed file kind this strategy installs (<c>zip</c>, <c>exe</c>, <c>appimage</c>).</summary>
    string Kind { get; }

    /// <summary>Why this copy cannot replace itself (a read-only location, not an installed copy), or null when it can.</summary>
    string? CannotSelfUpdateReason();

    /// <summary>
    /// Downloads and verifies <paramref name="release"/>, then starts the hand-over that finishes once this process has exited. The caller
    /// quits the app right after; nothing of the installed app is touched before that. Throws <see cref="UpdateException"/> on failure.
    /// </summary>
    Task ApplyAsync(DesktopRelease release, CancellationToken ct = default);
}

/// <summary>A finished command: exit code and what it printed.</summary>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>The OS commands the updaters run (ditto, codesign, the hand-over shell); a seam so tests run without them.</summary>
public interface IUpdateProcesses
{
    /// <summary>Runs <paramref name="file"/> to completion (2 minutes at most) and returns what it printed.</summary>
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);

    /// <summary>Starts <paramref name="file"/> and leaves it running after this process exits.</summary>
    void Start(string file, IReadOnlyList<string> args);
}

/// <summary>The real <see cref="IUpdateProcesses"/>.</summary>
public sealed class SystemUpdateProcesses : IUpdateProcesses
{
    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var p = Process.Start(psi) ?? throw new UpdateException($"Could not start {file}.");
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                throw new UpdateException($"{Path.GetFileName(file)} did not finish in time.");
            }
            return new CommandResult(p.ExitCode, await stdout, await stderr);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new UpdateException($"Could not start {file} ({e.Message}).");
        }
    }

    /// <inheritdoc />
    public void Start(string file, IReadOnlyList<string> args)
    {
        // No redirects: a pipe this process holds would break once it exits. The child writes its own log where it has one.
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new UpdateException($"Could not start {file}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new UpdateException($"Could not start {file} ({e.Message}).");
        }
    }
}

/// <summary>Downloading a release file and checking it against the feed before anything uses it.</summary>
public static class DesktopPayload
{
    /// <summary>
    /// Streams <paramref name="release"/> to <paramref name="destination"/>, hashing as it goes; the file appears only when its size and sha256
    /// match the feed's, so a cut-off or altered download never lands. Throws <see cref="UpdateException"/> otherwise.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, DesktopRelease release, string destination, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        log?.Invoke($"Downloading {release.FileName}…");
        try
        {
            long total = 0;
            string actual;
            // Headers only: the client's timeout then covers the connection, not a long download on a slow line.
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"Downloading {release.FileName} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    total += n;
                }
                actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (release.Size > 0 && total != release.Size)
                throw new UpdateException($"The download of {release.FileName} is incomplete ({total} of {release.Size} bytes).");
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException($"Checksum mismatch for {release.FileName} (expected {release.Sha256}, got {actual}).");
            File.Move(part, destination, overwrite: true);
            log?.Invoke($"Downloaded and verified {release.FileName}.");
        }
        catch (HttpRequestException e)
        {
            TryDelete(part);
            throw new UpdateException($"Downloading {release.FileName} failed ({e.Message}).");
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Empties the desktop staging dir (left-overs of an earlier update); a file still in use stays.</summary>
    public static void Clean(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingDir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A Windows installer that is still running holds its file; the next update removes it.
            }
        }
    }

    /// <summary>Whether this process can create entries in <paramref name="dir"/> (where a swap needs to rename things).</summary>
    public static bool IsWritableDir(string dir)
    {
        var probe = Path.Combine(dir, $".astra-update-probe-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
