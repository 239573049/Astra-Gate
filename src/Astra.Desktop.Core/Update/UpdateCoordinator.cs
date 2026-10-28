using System.Runtime.InteropServices;
using Astra.Core;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Update;

/// <summary>What the coordinator needs from its surroundings; everything is a seam so tests run without a server or network.</summary>
public sealed class UpdateCoordinatorOptions
{
    /// <summary>The data dir.</summary>
    public required AstraPaths Paths { get; init; }

    /// <summary>The server lifecycle (stop / start around a swap).</summary>
    public required ServiceManager Manager { get; init; }

    /// <summary>The HTTP client for the feed and the registry.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>This app's version (the version compared when the running server's own cannot be read).</summary>
    public required string AppVersion { get; init; }

    /// <summary>The running server's address (<c>http://127.0.0.1:port</c>), or null while disconnected.</summary>
    public required Func<string?> ApiBase { get; init; }

    /// <summary>Test seam for the package download; null = the npm registry.</summary>
    public IServerPackageSource? PackageSource { get; init; }

    /// <summary>The release channel when the server cannot say (it normally answers with the user's choice).</summary>
    public string? Channel { get; init; }

    /// <summary>Log sink.</summary>
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// <see cref="IDesktopHost.CheckForUpdateAsync"/> / <see cref="IDesktopHost.ApplyUpdateAsync"/> over the Desktop.Core updater: the
/// server half of desktop/src/update.ts (feed check through the running server, then <see cref="ServerUpdater"/> for the staged swap).
/// Not supported yet: the desktop app replacing itself (<see cref="IDesktopUpdater"/> has no implementation), so a newer app is
/// not detected or applied here.
/// </summary>
public sealed class UpdateCoordinator(UpdateCoordinatorOptions o)
{
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>A check or an apply is running (the app must not restart under it).</summary>
    public bool IsBusy => _busy.CurrentCount == 0;
    private ServerUpdateCheck? _pending;

    /// <summary>Checks the feed (through the running server when there is one). Never throws: a failure is the message.</summary>
    public async Task<UpdateSummary> CheckAsync(CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct)) return new UpdateSummary("An update check is already running.", UpdateAction.None);
        try
        {
            return await RunCheckAsync(ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<UpdateSummary> RunCheckAsync(CancellationToken ct)
    {
        try
        {
            var current = (await o.Manager.ProbeAsync()).Version ?? o.AppVersion;
            var check = await ServerUpdateChecker.CheckAsync(o.Http, current, o.ApiBase(), channel: o.Channel, ct: ct);
            _pending = check.AvailableVersion is null ? null : check;
            return check.AvailableVersion is null
                ? new UpdateSummary("Astra is up to date.", UpdateAction.None)
                : new UpdateSummary($"Astra {check.AvailableVersion} is available (current {current}).", UpdateAction.Server);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            o.Log($"update check failed: {e.Message}");
            return new UpdateSummary($"Update check failed: {e.Message}", UpdateAction.None);
        }
    }

    /// <summary>
    /// Applies the pending server update (checking first when none is pending): stage, swap, restart, roll back on failure.
    /// Throws <see cref="UpdateException"/> (a user-facing message) on failure; <see cref="UpdateApplyOutcome.None"/> when nothing is newer.
    /// </summary>
    public async Task<UpdateApplyOutcome> ApplyAsync(CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct)) throw new UpdateException("An update is already running.");
        try
        {
            if (_pending is null) await RunCheckAsync(ct);
            if (_pending is not { AvailableVersion: not null } pending) return UpdateApplyOutcome.None;
            var manifest = pending.Manifest
                ?? await UpdateFeed.FetchManifestAsync(o.Http, UpdateFeed.DefaultFeedUrl, UpdateFeed.NormalizeChannel(pending.Channel), null, null, ct);
            var installed = InstallJson.Read(UpdateLayout.For(o.Paths).InstallFile);
            var probe = await o.Manager.ProbeAsync();
            var result = await ServerUpdater.ApplyAsync(new ServerUpdateOptions
            {
                Paths = o.Paths,
                Manifest = manifest,
                PlatformKey = PlatformKey.Current(),
                ServerPackage = ServerPackageName(),
                CurrentServerPath = installed?.ServerPath,
                CurrentVersion = probe.Version ?? o.AppVersion,
                Control = new ServiceManagerServerControl(o.Manager),
                PackageSource = o.PackageSource ?? new NpmRegistryPackageSource(o.Http),
                Log = o.Log,
            }, ct);
            o.Log($"server updated {result.From} -> {result.To}");
            _pending = null;
            return UpdateApplyOutcome.Server;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>The npm package that carries the server for this machine, e.g. <c>@aidotnet/server-darwin-arm64</c> (npm/server-*).</summary>
    public static string ServerPackageName()
    {
        var os = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"@aidotnet/server-{os}-{arch}";
    }
}
