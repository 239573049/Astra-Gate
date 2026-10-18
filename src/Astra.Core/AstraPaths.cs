namespace Astra.Core;

/// <summary>Resolves Astra's data directory (~/.astra, overridable with ASTRA_HOME).</summary>
public sealed class AstraPaths
{
    public AstraPaths(string? root = null)
    {
        Root = root
               ?? Environment.GetEnvironmentVariable("ASTRA_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".astra");
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "astra.db");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string RuntimeFile => Path.Combine(Root, "runtime.json");
    public string InstallFile => Path.Combine(Root, "install.json");
    public string KeysDir => Path.Combine(Root, "keys");
    public string LogsDir => Path.Combine(Root, "logs");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string ClientBackupsDir => Path.Combine(BackupsDir, "client-configs");
    public string DbBackupsDir => Path.Combine(BackupsDir, "db");
    public string BodiesDir => Path.Combine(Root, "bodies");

    /// <summary>Helper programs Astra downloads for itself (cloudflared for remote access): tools/&lt;name&gt;-&lt;version&gt;/.</summary>
    public string ToolsDir => Path.Combine(Root, "tools");

    /// <summary>State of <c>astra-connect</c> (the employee CLI): profile.json, state.json, backups/. The server never reads it.</summary>
    public string ConnectDir => Path.Combine(Root, "connect");

    /// <summary>
    /// Plugins: the one part of the data dir kept as files rather than in SQLite, because the plugin host
    /// (magpie's host.js) and Bun need files — the sign-ins in OpenCode's auth.json shape, the npm
    /// packages, Bun itself. Owner-only.
    /// </summary>
    public string PluginsDir => Path.Combine(Root, "plugins");

    /// <summary>Plugin sign-ins, OpenCode's auth.json in shape (plaintext by that format; the folder is owner-only).</summary>
    public string PluginAuthFile => Path.Combine(PluginsDir, "plugin-auth.json");

    /// <summary>Where <c>bun add</c> installs plugin packages (its package.json, node_modules, bun.lock).</summary>
    public string PluginPackagesDir => Path.Combine(PluginsDir, "packages");

    /// <summary>The Bun Astra downloads to run the plugin host: runtime/bun-&lt;version&gt;/.</summary>
    public string PluginRuntimeDir => Path.Combine(PluginsDir, "runtime");

    /// <summary>host.js and pi.js, written out of the server binary by content hash: host/&lt;hash&gt;/.</summary>
    public string PluginHostDir => Path.Combine(PluginsDir, "host");

    /// <summary>The models.dev catalog handed to the host, which builds OpenCode's provider info from it.</summary>
    public string PluginModelsDevFile => Path.Combine(PluginsDir, "models-dev.json");

    /// <summary>The plugin market's cached listings.</summary>
    public string PluginMarketDir => Path.Combine(PluginsDir, "market");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, KeysDir, LogsDir, BackupsDir, ClientBackupsDir, DbBackupsDir, BodiesDir, PluginsDir })
        {
            Directory.CreateDirectory(dir);
        }
        RestrictToOwner(KeysDir);
        RestrictToOwner(PluginsDir);
    }

    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (Directory.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else if (File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Best effort: permissions are hardening, not correctness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
