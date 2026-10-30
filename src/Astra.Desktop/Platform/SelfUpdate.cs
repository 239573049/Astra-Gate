using Astra.Core;
using Astra.Desktop.Core.Update;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Platform;

/// <summary>
/// Which update track this copy is on and the self-update strategy for it (plan avalonia-client §4.3). Updates of every kind are
/// logged to <c>~/.astra/logs/desktop-update.log</c>, which the macOS hand-over shell writes to as well.
/// </summary>
internal static class SelfUpdate
{
    /// <summary>The track of this install: dev run, the CLI's install (node_modules / install.json), or a packaged app.</summary>
    public static UpdateTrack Track(AstraPaths paths)
        => UpdateTracks.Resolve(ServiceHost.IsDevRun, Environment.ProcessPath ?? AppContext.BaseDirectory,
            InstallJson.Read(paths.InstallFile)?.DesktopPath, OperatingSystem.IsMacOS());

    /// <summary>The strategy for a packaged app on this OS; null on the other tracks (and for a macOS run outside an app bundle).</summary>
    public static IDesktopUpdater? Create(UpdateTrack track, AstraPaths paths, HttpClient http)
    {
        if (!track.UpdatesAsWholeApp()) return null;
        var staging = UpdateLayout.For(paths).DesktopStagingDir;
        var processes = new SystemUpdateProcesses();
        Action<string> log = line => Log(paths, line);
        if (OperatingSystem.IsMacOS())
            return AppBundle() is { } app ? new MacAppUpdater(app, staging, LogFile(paths), http, processes, log) : null;
        if (OperatingSystem.IsWindows())
            return new WindowsInstallerUpdater(AppContext.BaseDirectory, staging, http, processes, log);
        return new AppImageUpdater(Environment.GetEnvironmentVariable("APPIMAGE"), http, processes, log);
    }

    /// <summary>Appends a line to the update log; a log that cannot be written is skipped.</summary>
    public static void Log(AstraPaths paths, string line)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDir);
            File.AppendAllText(LogFile(paths), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string LogFile(AstraPaths paths) => Path.Combine(paths.LogsDir, "desktop-update.log");

    /// <summary>The running bundle, <c>…/Astra.app</c>, from the executable's folder <c>…/Astra.app/Contents/MacOS</c>.</summary>
    private static string? AppBundle()
    {
        var macOs = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return macOs is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } app } } && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? app.FullName
            : null;
    }
}
