using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Astra.Core;

namespace Astra.Desktop.Core.Update;

/// <summary>
/// Everything the updater owns under the Astra home (port of paths.ts). <c>update-state.json</c> lives in the home, not the
/// database, because the executor runs while the server is stopped.
/// </summary>
public sealed record UpdateLayout(string StagingDir, string BackupsDir, string ServerDir, string StateFile, string InstallFile)
{
    public static UpdateLayout For(AstraPaths paths) => new(
        Path.Combine(paths.Root, "updates", "staging"),
        paths.BackupsDir,
        Path.Combine(paths.Root, "server"),
        Path.Combine(paths.Root, "update-state.json"),
        paths.InstallFile);

    /// <summary>~/.astra/server/astra-server-&lt;version&gt;[.exe] — a fresh file per version, so a running binary is never replaced.</summary>
    public string ManagedBinaryPath(string version, bool windows)
        => Path.Combine(ServerDir, $"astra-server-{version}{(windows ? ".exe" : "")}");
}

/// <summary>The manifest's platform key for this machine: RID prefix + architecture ("osx-arm64", "win-x64", "linux-x64").</summary>
public static class PlatformKey
{
    public static string Current()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }
}

/// <summary>Native companion libraries beside a Native AOT binary (AGENTS.md "Native AOT"); mirrors NATIVE_LIBRARY in scripts/pack-platform.mjs.</summary>
public static partial class NativeCompanions
{
    [GeneratedRegex(@"\.(dylib|so(\.\d+)*|dll)$", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPattern();

    public static bool IsLibrary(string fileName) => LibraryPattern().IsMatch(fileName);

    /// <summary>
    /// The companion libraries sitting beside <paramref name="binaryPath"/>. The executable itself never counts, and neither
    /// do installer <c>*.tmp</c> / rollback <c>*.prev</c> siblings (the pattern anchors on the library extension).
    /// </summary>
    public static IReadOnlyList<string> Find(string binaryPath)
    {
        var dir = Path.GetDirectoryName(binaryPath)!;
        var name = Path.GetFileName(binaryPath);
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => Path.GetFileName(f) != name && IsLibrary(Path.GetFileName(f)))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
