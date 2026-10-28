namespace Astra.Desktop.Core.Update;

/// <summary>What a desktop-app self-update found and, once applied, what it did.</summary>
public sealed record DesktopUpdateOffer(string Version, string? Notes);

/// <summary>
/// SEAM, not implemented in updater batch 1: the Avalonia app replacing ITSELF (the server binary is handled by
/// <see cref="ServerUpdater"/>). Per-OS strategies from .plan/avalonia-client.md §4.3 / §5:
/// <list type="bullet">
/// <item><b>macOS</b>: download the zip named in the feed, verify sha256/sha512, and after this process exits let a small
/// AOT helper (or a pre-exit script) swap <c>Astra.app</c> keeping bundle id <c>io.astra.desktop</c> and the signature
/// (Gatekeeper); keep the old bundle as <c>.prev</c> for rollback.</item>
/// <item><b>Windows</b>: download the NSIS installer, run it silently (<c>/S</c>) and exit; the installer replaces the files.</item>
/// <item><b>Linux AppImage</b>: replace the file <c>$APPIMAGE</c> points at (rename a <c>.prev</c> beside it, move the new one in).</item>
/// <item><b>npm track</b>: keep delegating to <c>astra update --desktop</c> (the CLI's update-core is unchanged).</item>
/// </list>
/// Every strategy shares the plumbing in this namespace: feed + channel (<see cref="UpdateFeed"/>), version order
/// (<see cref="ReleaseVersion"/>), the persisted phase (<see cref="UpdateState"/>) and <c>desktopPath</c>/<c>desktopVersion</c>
/// in <see cref="InstallJson"/>.
/// </summary>
public interface IDesktopUpdater
{
    /// <summary>Whether this install can update itself (false for unpackaged dev runs and unsupported layouts).</summary>
    bool CanSelfUpdate { get; }

    /// <summary>The newer desktop release on <paramref name="channel"/>, or null.</summary>
    Task<DesktopUpdateOffer?> CheckAsync(string channel, CancellationToken ct = default);

    /// <summary>Downloads, verifies and stages <paramref name="offer"/>, then arranges the swap for after exit; the caller quits the app.</summary>
    Task ApplyAsync(DesktopUpdateOffer offer, CancellationToken ct = default);
}
