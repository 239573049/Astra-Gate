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

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, KeysDir, LogsDir, BackupsDir, ClientBackupsDir, DbBackupsDir, BodiesDir })
        {
            Directory.CreateDirectory(dir);
        }
        RestrictToOwner(KeysDir);
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
