using System.Text;

namespace Astra.Clients.Config;

/// <summary>
/// Everything adapters and the applier need from the machine: home directory, OS, environment
/// variables and file IO. Production uses <see cref="Real"/>; tests inject a fake home directory
/// and an environment-variable map, so real user configuration is never touched.
/// </summary>
public sealed class ClientEnvironment
{
    private readonly Func<string, string?> _lookup;

    public ClientEnvironment(string homeDirectory, string os, Func<string, string?>? environmentVariableLookup = null)
    {
        HomeDirectory = homeDirectory;
        Os = os;
        _lookup = environmentVariableLookup ?? (_ => null);
    }

    /// <summary>The real machine environment (never used by tests).</summary>
    public static ClientEnvironment Real => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        Environment.GetEnvironmentVariable);

    /// <summary>The user's home directory (injected in tests).</summary>
    public string HomeDirectory { get; }

    /// <summary>"osx", "linux" or "windows".</summary>
    public string Os { get; }

    public string? GetEnvironmentVariable(string name) => _lookup(name);

    public string Combine(params string[] parts) => Path.Combine([HomeDirectory, .. parts]);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>Reads a file as text (BOM stripped), or null when it does not exist.</summary>
    public (string Text, bool HadBom)? ReadTextWithBom(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
        return (text, hadBom);
    }

    public string? ReadTextOrNull(string path) => ReadTextWithBom(path)?.Text;

    /// <summary>Writes text as UTF-8 (optionally with BOM) via a temp file and an atomic move.</summary>
    public void WriteTextAtomic(string path, string text, bool bom)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoding = bom ? new UTF8Encoding(true) : new UTF8Encoding(false);
        var tmp = path + ".astra-tmp";
        File.WriteAllText(tmp, text, encoding);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Copies bytes to a destination path, creating parent directories.</summary>
    public void CopyFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>Copies raw bytes to a destination path, creating parent directories.</summary>
    public void WriteAllBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public byte[]? ReadAllBytesOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Finds an executable on PATH (for detection only).</summary>
    public string? FindOnPath(string fileName)
    {
        var pathVar = _lookup("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        var isWindows = string.Equals(Os, "windows", StringComparison.OrdinalIgnoreCase);
        string[] extensions = isWindows ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, fileName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
