namespace Astra.Desktop.Core;

public enum ServerBinarySource { Install, Env, Bundled, Dev }

/// <param name="Version">The version the binary is expected to report, when known up front (install.json, the bundled app); null for env and dev targets.</param>
public sealed record ServerBinary(string Path, ServerBinarySource Source, string? Version);

public sealed record BundledServer(string Path, string Version);

/// <summary>Where the server binary comes from, and when a running server has to make way for it. Port of desktop/src/shared/resolveServerBinary.ts.</summary>
public static class ServerBinaryResolver
{
    /// <summary>Dev fallback: &lt;repo&gt;/src/Astra.Server/bin/Debug/net11.0/astra-server.</summary>
    public static string DevPath(string repoRoot)
        => Path.Combine(repoRoot, "src", "Astra.Server", "bin", "Debug", "net11.0", ExeName);

    /// <summary>The server shipped inside the app: macOS Contents/Resources/server, Windows and Linux resources/server (beside the executable).</summary>
    public static string BundledPath(string resourcesDir) => Path.Combine(resourcesDir, "server", ExeName);

    private static string ExeName => OperatingSystem.IsWindows() ? "astra-server.exe" : "astra-server";

    /// <summary>
    /// Priority: install.json's serverPath (unless it records a version older than the bundled server, so a stale
    /// managed binary does not keep winning after an installer upgrade), ASTRA_SERVER_BIN, the bundled server, the dev build.
    /// Only a candidate that <paramref name="exists"/> accepts is returned.
    /// </summary>
    public static ServerBinary? Resolve(
        InstallInfo? install, string? envServerBin, BundledServer? bundled, string? repoRoot, Func<string, bool> exists)
    {
        var bundledHere = bundled is not null && exists(bundled.Path) ? bundled : null;
        var installIsStale = bundledHere is not null
                             && !string.IsNullOrEmpty(install?.ServerVersion)
                             && ReleaseVersion.IsNewer(bundledHere.Version, install.ServerVersion);

        var candidates = new List<ServerBinary>();
        if (!string.IsNullOrWhiteSpace(install?.ServerPath) && !installIsStale)
            candidates.Add(new ServerBinary(install.ServerPath, ServerBinarySource.Install, install.ServerVersion));
        if (!string.IsNullOrWhiteSpace(envServerBin))
            candidates.Add(new ServerBinary(envServerBin, ServerBinarySource.Env, null));
        if (bundledHere is not null)
            candidates.Add(new ServerBinary(bundledHere.Path, ServerBinarySource.Bundled, bundledHere.Version));
        if (!string.IsNullOrEmpty(repoRoot))
            candidates.Add(new ServerBinary(DevPath(repoRoot), ServerBinarySource.Dev, null));
        return candidates.FirstOrDefault(c => exists(c.Path));
    }

    /// <summary>
    /// True when a running server must be stopped and replaced: only a target with a known version decides, and only
    /// when it differs from the running one. Env/dev targets and unreadable running versions adopt the server instead —
    /// the desktop must never kill a process it cannot vouch for.
    /// </summary>
    public static bool ShouldReplaceRunning(string? runningVersion, ServerBinary? target)
        => runningVersion is not null && target?.Version is { } v && v != runningVersion;
}
