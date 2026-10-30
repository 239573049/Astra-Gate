namespace Astra.Desktop.Core.Update;

/// <summary>
/// How this desktop app was installed, which decides what an update replaces (port of desktop/src/shared/updateTrack.ts).
/// An app that carries its own server (<see cref="Dmg"/>, <see cref="Installer"/>) updates as a whole and never replaces the
/// server alone: the new app's bundled server takes over at launch, so the app and the server stay on one version.
/// </summary>
public enum UpdateTrack
{
    /// <summary>A dev run from a source checkout: the app never replaces itself (the server half works as on <see cref="Npm"/>).</summary>
    Disabled,

    /// <summary>The macOS app bundle from the dmg or zip.</summary>
    Dmg,

    /// <summary>Installed by the CLI under ~/.astra/desktop: the CLI updates the app (<c>astra update --desktop</c>), the app updates the server.</summary>
    Npm,

    /// <summary>The Windows NSIS installer or the Linux AppImage.</summary>
    Installer,
}

/// <summary>The track rule (desktop/src/shared/updateTrack.ts <c>resolveUpdateTrack</c>).</summary>
public static class UpdateTracks
{
    /// <summary>The track of this install.</summary>
    /// <param name="devRun">Running from a source checkout.</param>
    /// <param name="executablePath">The running executable, symlinks resolved where the caller can.</param>
    /// <param name="installDesktopPath">install.json's <c>desktopPath</c> (the CLI's desktop install), when recorded.</param>
    /// <param name="macOS">Whether this is macOS.</param>
    public static UpdateTrack Resolve(bool devRun, string executablePath, string? installDesktopPath, bool macOS)
    {
        if (devRun) return UpdateTrack.Disabled;
        var exe = executablePath.Replace('\\', '/');
        var npmManaged = exe.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
                         || (!string.IsNullOrWhiteSpace(installDesktopPath)
                             && exe.StartsWith(installDesktopPath.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        if (npmManaged) return UpdateTrack.Npm;
        return macOS ? UpdateTrack.Dmg : UpdateTrack.Installer;
    }

    /// <summary>True when the app ships its own server, so an update replaces the whole app and never the server alone.</summary>
    public static bool UpdatesAsWholeApp(this UpdateTrack track) => track is UpdateTrack.Dmg or UpdateTrack.Installer;
}
