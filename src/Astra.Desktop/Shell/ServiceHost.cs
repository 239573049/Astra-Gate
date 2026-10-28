using System.Reflection;
using Astra.Core;
using Astra.Desktop.Core;

namespace Astra.Desktop.Shell;

/// <summary>Builds the <see cref="ServiceManager"/> for this process: where the data dir is and where a bundled or dev server would be.</summary>
public static class ServiceHost
{
    public static ServiceManager Create(AstraPaths paths)
        => new(new ServiceManagerOptions
        {
            Paths = paths,
            RepoRoot = FindRepoRoot(),
            BundledServer = FindBundledServer(),
        });

    /// <summary>
    /// The server shipped inside the app (plan avalonia-client §4.4): macOS <c>Astra.app/Contents/Resources/server</c> (the executable
    /// is in <c>Contents/MacOS</c>), Windows / Linux <c>&lt;app&gt;/resources/server</c> beside the executable. The resolver
    /// checks that the file exists, so a path with nothing behind it is harmless.
    /// </summary>
    private static BundledServer FindBundledServer()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resources = OperatingSystem.IsMacOS() && Path.GetFileName(dir) == "MacOS"
            ? Path.Combine(Path.GetDirectoryName(dir)!, "Resources")
            : Path.Combine(dir, "resources");
        return new BundledServer(ServerBinaryResolver.BundledPath(resources), AppVersion);
    }

    /// <summary>This build's version: the <c>&lt;Version&gt;</c> of Directory.Build.props, which all of Astra's packages share.</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(ServiceHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }

    /// <summary>True when this binary runs from a source checkout (a dev build), false when installed: login items and updates need an installed app.</summary>
    public static bool IsDevRun => FindRepoRoot() is not null;

    /// <summary>The checkout this binary was built from (the dev server fallback), found by walking up to Astra.sln; null when installed.</summary>
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Astra.sln"))) return dir.FullName;
        return null;
    }
}
