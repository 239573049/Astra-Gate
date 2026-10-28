using Astra.Core;

namespace Astra.Desktop.Core.Update;

/// <summary>A refused or failed update. <see cref="Hint"/> carries the last failure message when the lock-out refuses a retry.</summary>
public sealed class UpdateException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>Process controls the orchestrator needs (TS <c>ServerControl</c>); <see cref="ServiceManagerServerControl"/> is the real one.</summary>
public interface IServerControl
{
    Task<bool> IsRunningAsync();
    Task StopAsync();
    Task StartAsync();
    /// <summary>Version the server reports after (re)start; null counts as no answer.</summary>
    Task<string?> ProbeVersionAsync();
}

/// <summary><see cref="IServerControl"/> over the desktop's <see cref="ServiceManager"/>: start resolves install.json, so it lands on the binary the swap just wrote.</summary>
public sealed class ServiceManagerServerControl(ServiceManager manager) : IServerControl
{
    public async Task<bool> IsRunningAsync() => (await manager.ProbeAsync()).Running;
    public async Task StopAsync() => await manager.StopServiceAsync();
    public async Task StartAsync() => await manager.EnsureRunningAsync();
    public async Task<string?> ProbeVersionAsync() => (await manager.ProbeAsync()).Version;
}

public sealed class ServerUpdateOptions
{
    public required AstraPaths Paths { get; init; }
    public required UpdateManifest Manifest { get; init; }
    /// <summary>Key into <c>Manifest.Platforms</c>, e.g. "osx-arm64" (<see cref="PlatformKey.Current"/>).</summary>
    public required string PlatformKey { get; init; }
    /// <summary>Used when the manifest does not name a server package for this platform.</summary>
    public required string ServerPackage { get; init; }
    public required string? CurrentServerPath { get; init; }
    public required string CurrentVersion { get; init; }
    public required IServerControl Control { get; init; }
    public required IServerPackageSource PackageSource { get; init; }
    public int ExpectedApiMajor { get; init; } = ReleaseVersion.ExpectedApiMajor;
    /// <summary>Decides the .exe suffix.</summary>
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Retry despite the consecutive-failure lock.</summary>
    public bool Force { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    /// <summary>How long a restarted server may take to answer before the update counts as failed.</summary>
    public TimeSpan RestartProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RestartProbeInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed record ServerUpdateResult(string From, string To, string ServerPath);

/// <summary>
/// The staged-swap state machine: downloading → swapping → restarting → idle, with a rollback leg that repoints install.json
/// at the previous binary and restarts; every transition is persisted to update-state.json. Port of
/// packages/update-core/src/apply.ts (<c>applyServerUpdate</c>) — same state file, same install.json, same order of effects.
/// Rails: refuses a manifest that is not newer or whose apiVersion major differs; refuses after
/// <see cref="UpdateState.MaxConsecutiveFailures"/> failed runs unless forced; the new binary lands on a fresh version-suffixed
/// path, so a running binary is never replaced in place.
/// </summary>
public static class ServerUpdater
{
    public static async Task<ServerUpdateResult> ApplyAsync(ServerUpdateOptions o, CancellationToken ct = default)
    {
        var layout = UpdateLayout.For(o.Paths);
        var manifest = o.Manifest;
        var state = UpdateState.Read(layout.StateFile) ?? UpdateState.Initial(o.Now);
        if (!o.Force && state.FailedCount >= UpdateState.MaxConsecutiveFailures)
            throw new UpdateException($"The last {state.FailedCount} update attempts failed. Fix the problem and run the update with --retry.", state.Error);
        if (!ReleaseVersion.IsNewer(manifest.Version, o.CurrentVersion))
            throw new UpdateException($"Already on version {o.CurrentVersion} ({manifest.Version} is not newer).");
        if (!string.IsNullOrEmpty(manifest.ApiVersion) && !ReleaseVersion.IsApiVersionCompatible(manifest.ApiVersion, o.ExpectedApiMajor))
            throw new UpdateException($"The update targets API major {manifest.ApiVersion} but this client expects {o.ExpectedApiMajor}. Update the whole Astra install manually.");

        void SetState(UpdatePhase phase, string? step = null, string? error = null) =>
            UpdateState.Write(layout.StateFile, state with
            {
                Phase = phase,
                TargetVersion = manifest.Version,
                PreviousVersion = o.CurrentVersion,
                Step = step ?? state.Step,
                Error = error,
            }, o.Now);

        var previousInstall = InstallJson.Read(layout.InstallFile);
        var swapped = false;
        var webRootSwapped = false;
        string? backupPath = null;

        try
        {
            SetState(UpdatePhase.Downloading, "downloading");
            var platformInfo = manifest.Platform(o.PlatformKey);
            var staged = await ServerStaging.StageAsync(o.PackageSource, layout.StagingDir, platformInfo?.Server ?? o.ServerPackage,
                manifest.Version, platformInfo?.ServerSha256, o.IsWindows, o.Log, ct);

            SetState(UpdatePhase.Swapping, "swapping");
            var wasRunning = await o.Control.IsRunningAsync();
            backupPath = ServerSwap.BackupCurrentBinary(o.CurrentServerPath, layout.BackupsDir, o.CurrentVersion, o.IsWindows, now: o.Now);
            if (backupPath is not null) o.Log($"Backed up the current binary to {backupPath}");
            var serverPath = ServerSwap.InstallManagedBinary(staged.Path, layout, manifest.Version, o.IsWindows);
            InstallJson.WithServer(previousInstall, serverPath, manifest.Version, o.Now).Write(layout.InstallFile);
            ServerSwap.PruneManagedBinaries(layout.ServerDir, serverPath);
            swapped = true;

            SetState(UpdatePhase.Restarting, "restarting");
            if (wasRunning)
            {
                o.Log("Restarting the Astra server…");
                await o.Control.StopAsync();
            }
            // Companions keep their file name across versions and the old server has its copy loaded, so (Windows) they
            // can only be replaced now that it is down.
            ServerSwap.InstallNativeCompanions(staged.NativeLibraries, layout.ServerDir);
            // The web UI is read from wwwroot beside the binary. Best effort: a failure keeps the previous UI.
            try
            {
                webRootSwapped = ServerSwap.InstallManagedWebRoot(staged.Path, layout.ServerDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                o.Log($"Could not update the web UI files: {e.Message}");
            }
            await o.Control.StartAsync();
            var reported = await ProbeRestartedVersionAsync(o, ct);
            if (reported != manifest.Version)
                throw new UpdateException($"The server reports {reported ?? "no version"} after the restart; expected {manifest.Version}.");

            // Success: free the ~90 MB staging prefix now (it is wiped on the next run anyway).
            ServerStaging.DeleteDir(layout.StagingDir);
            if (webRootSwapped) ServerSwap.CommitManagedWebRoot(layout.ServerDir);
            ServerSwap.CommitNativeCompanions(layout.ServerDir);
            UpdateState.Write(layout.StateFile, UpdateState.Initial(o.Now), o.Now);
            o.Log($"Astra updated to {manifest.Version}.");
            return new ServerUpdateResult(o.CurrentVersion, manifest.Version, serverPath);
        }
        catch (Exception e)
        {
            SetState(UpdatePhase.RollingBack, "rolling-back", e.Message);
            if (swapped) await RollbackAsync(o, layout, previousInstall, backupPath, webRootSwapped, SetState);
            UpdateState.Write(layout.StateFile, new UpdateState(UpdatePhase.Failed, state.FailedCount + 1, UpdateState.Stamp(o.Now),
                manifest.Version, o.CurrentVersion, Error: e.Message), o.Now);
            if (e is UpdateException) throw;
            throw new UpdateException(e.Message);
        }
    }

    /// <summary>
    /// The version the restarted server reports. A server that is up but still starting can miss one probe, so no answer
    /// (null or a throw) is retried until the deadline; any version it does report is final.
    /// </summary>
    private static async Task<string?> ProbeRestartedVersionAsync(ServerUpdateOptions o, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + o.RestartProbeTimeout;
        while (true)
        {
            string? reported;
            try
            {
                reported = await o.Control.ProbeVersionAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                reported = null;
            }
            if (reported is not null || DateTime.UtcNow >= deadline) return reported;
            await Task.Delay(o.RestartProbeInterval, ct);
        }
    }

    private static async Task RollbackAsync(ServerUpdateOptions o, UpdateLayout layout, InstallJson? previousInstall, string? backupPath,
        bool webRootSwapped, Action<UpdatePhase, string?, string?> setState)
    {
        var restorePath = previousInstall?.ServerPath ?? backupPath;
        try
        {
            if (restorePath is not null)
            {
                o.Log($"Rolling back to {restorePath}");
                InstallJson.WithServer(previousInstall, restorePath, null, o.Now).Write(layout.InstallFile);
            }
            else
            {
                // No managed binary existed before (the server ships inside the app): install.json still points at the
                // failed binary and, being newer, would keep winning — drop the pointer so the bundled one is used.
                o.Log("Rolling back to the server bundled with the app");
                InstallJson.ClearServer(layout.InstallFile, previousInstall, o.Now);
            }
            // Unconditional: a failed server too busy starting to pass the health check still holds the port.
            await o.Control.StopAsync();
            if (webRootSwapped) ServerSwap.RestoreManagedWebRoot(layout.ServerDir);
            ServerSwap.RestoreNativeCompanions(layout.ServerDir);
            await o.Control.StartAsync();
        }
        catch (Exception e)
        {
            setState(UpdatePhase.Failed, null, $"Rollback failed: {e.Message}");
            o.Log("Rollback failed — see the server log. `astra restore-all` still works offline.");
        }
    }
}
