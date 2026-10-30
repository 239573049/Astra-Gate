using System.Runtime.InteropServices;
using System.Text.Json;
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

    /// <summary>How this app was installed; it decides what an update replaces (<see cref="UpdateTracks.UpdatesAsWholeApp"/>).</summary>
    public UpdateTrack Track { get; init; } = UpdateTrack.Npm;

    /// <summary>The app's self-update for <see cref="UpdateTrack.Dmg"/> / <see cref="UpdateTrack.Installer"/>; null = point at the download page.</summary>
    public IDesktopUpdater? Desktop { get; init; }

    /// <summary>The feed file kind of this OS's app (<see cref="DesktopReleaseFeed.KindForThisOs"/>) when <see cref="Desktop"/> is null.</summary>
    public string? DesktopKind { get; init; }

    /// <summary>This machine's platform tag on the feed (<see cref="DesktopReleaseFeed.PlatformTag"/>).</summary>
    public string DesktopPlatform { get; init; } = DesktopReleaseFeed.PlatformTag();

    /// <summary>The feed; null = <see cref="UpdateFeed.DefaultFeedUrl"/>.</summary>
    public string? FeedUrl { get; init; }

    /// <summary>Test seam for the package download; null = the npm registry.</summary>
    public IServerPackageSource? PackageSource { get; init; }

    /// <summary>The release channel when the server cannot say (it normally answers with the user's choice).</summary>
    public string? Channel { get; init; }

    /// <summary>Log sink.</summary>
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// <see cref="IDesktopHost.CheckForUpdateAsync"/> / <see cref="IDesktopHost.ApplyUpdateAsync"/> over the Desktop.Core updater, per install track
/// (desktop/src/update.ts). An app that ships its own server (dmg, installer) checks ITS version against the feed and replaces itself
/// (<see cref="IDesktopUpdater"/>) — never the server alone, which is how the app and the server used to drift apart (the server moved on
/// with every update while the app stayed). The npm track and dev runs check the server and swap it (<see cref="ServerUpdater"/>).
/// </summary>
public sealed class UpdateCoordinator(UpdateCoordinatorOptions o)
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _gate = new();
    private Task<UpdateSummary>? _check;
    private ServerUpdateCheck? _pendingServer;
    private DesktopRelease? _pendingDesktop;

    /// <summary>A check or an apply is running (the app must not restart under it).</summary>
    public bool IsBusy => _busy.CurrentCount == 0;

    /// <summary>The last check that completed; a failed check leaves the previous answer.</summary>
    public UpdateSummary? Last { get; private set; }

    /// <summary>
    /// Checks the feed (through the running server when there is one). Never throws: a failure is the message. A check asked for while one
    /// runs shares it, so the tray's background check and the Settings page never see "already running" instead of the answer.
    /// </summary>
    public Task<UpdateSummary> CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_check is { IsCompleted: false } running) return running;
            if (!_busy.Wait(0)) return Task.FromResult(new UpdateSummary("An update is already running.", UpdateAction.None));
            _check = CheckAndReleaseAsync(ct);
            return _check;
        }
    }

    private async Task<UpdateSummary> CheckAndReleaseAsync(CancellationToken ct)
    {
        try
        {
            return await RunCheckAsync(ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private Task<UpdateSummary> RunCheckAsync(CancellationToken ct)
        => o.Track.UpdatesAsWholeApp() ? CheckAppAsync(ct) : CheckServerAsync(ct);

    private async Task<UpdateSummary> CheckServerAsync(CancellationToken ct)
    {
        try
        {
            var current = (await o.Manager.ProbeAsync()).Version ?? o.AppVersion;
            var check = await ServerUpdateChecker.CheckAsync(o.Http, current, o.ApiBase(), channel: o.Channel, ct: ct);
            _pendingServer = check.AvailableVersion is null ? null : check;
            if (check.AvailableVersion is null) return Remember(new UpdateSummary("Astra is up to date.", UpdateAction.None));
            var message = $"Astra {check.AvailableVersion} is available (current {current}).";
            // The CLI's desktop install cannot swap its files from inside the app: the server moves, the app follows with the CLI.
            if (o.Track == UpdateTrack.Npm) message += "\n\nUpdate the desktop app too with `astra update --desktop`.";
            return Remember(new UpdateSummary(message, UpdateAction.Server, check.AvailableVersion, check.Notes));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            o.Log($"update check failed: {e.Message}");
            return new UpdateSummary($"Update check failed: {e.Message}", UpdateAction.None);
        }
    }

    private async Task<UpdateSummary> CheckAppAsync(CancellationToken ct)
    {
        try
        {
            var kind = o.Desktop?.Kind ?? o.DesktopKind ?? DesktopReleaseFeed.KindForThisOs();
            var channel = await ChannelAsync(ct);
            var release = await DesktopReleaseFeed.CheckAsync(o.Http, o.FeedUrl, channel, o.DesktopPlatform, kind, o.AppVersion, ct: ct);
            _pendingDesktop = release;
            if (release is null)
            {
                // Up to date: what an earlier update downloaded (a Windows installer that ran) is no longer needed.
                DesktopPayload.Clean(UpdateLayout.For(o.Paths).DesktopStagingDir);
                return Remember(new UpdateSummary("Astra is up to date.", UpdateAction.None));
            }
            var message = $"Astra {release.Version} is available (current {o.AppVersion}).";
            if (BlockedReason() is { } reason)
            {
                return Remember(new UpdateSummary(
                    $"{message}\n\nThis copy cannot update itself ({reason}). Install the new version from {UpdateFeed.DownloadPageUrl}.",
                    UpdateAction.DesktopDownload, release.Version, release.Notes));
            }
            return Remember(new UpdateSummary(message, UpdateAction.DesktopDmg, release.Version, release.Notes));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            o.Log($"update check failed: {e.Message}");
            return new UpdateSummary($"Update check failed: {e.Message}", UpdateAction.None);
        }
    }

    private string? BlockedReason() => o.Desktop is null ? "self-update is not supported here" : o.Desktop.CannotSelfUpdateReason();

    private UpdateSummary Remember(UpdateSummary summary)
    {
        Last = summary;
        return summary;
    }

    /// <summary>The user's channel as the server reports it (<c>/api/update/status</c>), else the configured fallback.</summary>
    private async Task<string> ChannelAsync(CancellationToken ct)
    {
        if (o.ApiBase() is { Length: > 0 } baseUrl)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var res = await o.Http.GetAsync($"{baseUrl.TrimEnd('/')}/api/update/status", cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String)
                        return UpdateFeed.NormalizeChannel(c.GetString());
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
        return UpdateFeed.NormalizeChannel(o.Channel);
    }

    /// <summary>
    /// Applies the pending update (checking first when none is pending). App that ships its server: replaces itself
    /// (<see cref="UpdateApplyOutcome.Desktop"/>: the caller quits the app now) or, when it cannot, sends the user to the download page
    /// (<see cref="UpdateApplyOutcome.Download"/>). Otherwise the server: stage, swap, restart, roll back on failure.
    /// Throws <see cref="UpdateException"/> (a user-facing message) on failure; <see cref="UpdateApplyOutcome.None"/> when nothing is newer.
    /// </summary>
    public async Task<UpdateApplyOutcome> ApplyAsync(CancellationToken ct = default)
    {
        var running = _check;
        if (running is { IsCompleted: false }) await running;
        if (!await _busy.WaitAsync(0, ct)) throw new UpdateException("An update is already running.");
        try
        {
            return o.Track.UpdatesAsWholeApp() ? await ApplyAppAsync(ct) : await ApplyServerAsync(ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<UpdateApplyOutcome> ApplyAppAsync(CancellationToken ct)
    {
        if (_pendingDesktop is null) await CheckAppAsync(ct);
        if (_pendingDesktop is not { } release) return UpdateApplyOutcome.None;
        if (BlockedReason() is not null || o.Desktop is null) return UpdateApplyOutcome.Download;
        try
        {
            await o.Desktop.ApplyAsync(release, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            o.Log($"desktop update failed: {e.Message}");
            throw new UpdateException($"The update could not be prepared: {e.Message}");
        }
        o.Log($"desktop update {o.AppVersion} -> {release.Version} staged; quitting to install");
        _pendingDesktop = null;
        return UpdateApplyOutcome.Desktop;
    }

    private async Task<UpdateApplyOutcome> ApplyServerAsync(CancellationToken ct)
    {
        if (_pendingServer is null) await CheckServerAsync(ct);
        if (_pendingServer is not { AvailableVersion: not null } pending) return UpdateApplyOutcome.None;
        var manifest = pending.Manifest
            ?? await UpdateFeed.FetchManifestAsync(o.Http, o.FeedUrl ?? UpdateFeed.DefaultFeedUrl, UpdateFeed.NormalizeChannel(pending.Channel), null, null, ct);
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
        _pendingServer = null;
        Last = null;
        return UpdateApplyOutcome.Server;
    }

    /// <summary>The npm package that carries the server for this machine, e.g. <c>@aidotnet/server-darwin-arm64</c> (npm/server-*).</summary>
    public static string ServerPackageName()
    {
        var os = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"@aidotnet/server-{os}-{arch}";
    }
}
