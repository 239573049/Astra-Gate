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
    private readonly bool _readLoginShellPath;
    private readonly Lock _shellPathLock = new();
    private (IReadOnlyList<string> Dirs, DateTimeOffset At)? _shellPath;

    public ClientEnvironment(string homeDirectory, string os, Func<string, string?>? environmentVariableLookup = null,
        IReadOnlyList<string>? extraToolDirectories = null, IReadOnlyList<string>? applicationDirectories = null,
        bool readLoginShellPath = false)
    {
        HomeDirectory = homeDirectory;
        Os = os;
        _lookup = environmentVariableLookup ?? (_ => null);
        ExtraToolDirectories = extraToolDirectories ?? [];
        ApplicationDirectories = applicationDirectories ?? [];
        _readLoginShellPath = readLoginShellPath;
    }

    /// <summary>The real machine environment (never used by tests).</summary>
    public static ClientEnvironment Real
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            IReadOnlyList<string> apps = os switch
            {
                "osx" => ["/Applications", Path.Combine(home, "Applications")],
                "windows" => Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? [local] : [],
                _ => [],
            };
            return new ClientEnvironment(home, os, Environment.GetEnvironmentVariable,
                CommonToolDirectories(home, os, Environment.GetEnvironmentVariable), apps, readLoginShellPath: true);
        }
    }

    /// <summary>
    /// Where CLI clients and npm usually live even when they are missing from PATH: a desktop app launched from
    /// Finder / the Dock inherits a minimal PATH (no /usr/local/bin, no Homebrew, no ~/.local/bin), so tool
    /// lookups for install / update / version probing also search these.
    /// </summary>
    private static IReadOnlyList<string> CommonToolDirectories(string home, string os, Func<string, string?> lookup)
    {
        if (os == "windows")
        {
            return lookup("APPDATA") is { Length: > 0 } appData ? [Path.Combine(appData, "npm")] : [];
        }
        var dirs = new List<string>
        {
            "/usr/local/bin", "/opt/homebrew/bin", "/home/linuxbrew/.linuxbrew/bin",
            Path.Combine(home, ".local", "bin"), Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".volta", "bin"),
        };
        // nvm keeps one directory per Node version; prefer the newest.
        var nvm = Path.Combine(home, ".nvm", "versions", "node");
        if (Directory.Exists(nvm))
        {
            dirs.AddRange(Directory.GetDirectories(nvm)
                .OrderByDescending(d => System.Version.TryParse(Path.GetFileName(d).TrimStart('v'), out var v) ? v : new System.Version(0, 0))
                .Select(d => Path.Combine(d, "bin")));
        }
        return dirs;
    }

    /// <summary>Directories searched after PATH by <see cref="FindTool"/> (empty in tests).</summary>
    public IReadOnlyList<string> ExtraToolDirectories { get; }

    /// <summary>Where desktop apps are installed (/Applications on macOS, %LOCALAPPDATA% on Windows; empty in tests).</summary>
    public IReadOnlyList<string> ApplicationDirectories { get; }

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
    public string? FindOnPath(string fileName) => FindIn(PathDirectories(), fileName);

    /// <summary>
    /// The search path for running client tools (install / update / version probes): the user's login-shell PATH
    /// (what a terminal resolves first — see <see cref="LoginShellPath"/>), this process's PATH, then
    /// <paramref name="extraDirectories"/>, then <see cref="ExtraToolDirectories"/>, without duplicates.
    /// </summary>
    public IReadOnlyList<string> ToolSearchPath(params string[] extraDirectories) =>
        LoginShellPath().Concat(PathDirectories()).Concat(extraDirectories).Concat(ExtraToolDirectories)
            .Where(d => d.Length > 0).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

    /// <summary>Finds a client tool (or npm) on <see cref="ToolSearchPath"/>.</summary>
    public string? FindTool(string fileName, params string[] extraDirectories) => FindIn(ToolSearchPath(extraDirectories), fileName);

    /// <summary>Every copy of a tool on <see cref="ToolSearchPath"/>, in search order (the first is the one that runs).</summary>
    public IReadOnlyList<string> FindAllTools(string fileName, params string[] extraDirectories) =>
        ToolSearchPath(extraDirectories).Select(dir => FindIn([dir], fileName)).OfType<string>().ToList();

    private const string PathBegin = "__ASTRA_PATH_BEGIN__";
    private const string PathEnd = "__ASTRA_PATH_END__";

    /// <summary>
    /// PATH as the user's interactive login shell sets it (macOS / Linux; cached for five minutes). A server started
    /// by the desktop app inherits launchd's minimal PATH, so without this Astra would judge a different copy of a
    /// client than the one the user runs in a terminal (e.g. a fresh copy in ~/.npm-global/bin while the terminal
    /// still runs an older one from ~/.local/bin). Empty in tests, on Windows, for non-POSIX shells, or on failure.
    /// </summary>
    public IReadOnlyList<string> LoginShellPath()
    {
        if (!_readLoginShellPath || Os == "windows") return [];
        lock (_shellPathLock)
        {
            if (_shellPath is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Dirs;
            var dirs = ReadLoginShellPath();
            _shellPath = (dirs, DateTimeOffset.UtcNow);
            return dirs;
        }
    }

    /// <summary>Forgets the cached login-shell PATH (after an install that may have changed it).</summary>
    public void RefreshLoginShellPath()
    {
        lock (_shellPathLock) _shellPath = null;
    }

    private IReadOnlyList<string> ReadLoginShellPath()
    {
        var shell = _lookup("SHELL") is { Length: > 0 } s && File.Exists(s) ? s : Os == "osx" ? "/bin/zsh" : "/bin/bash";
        // fish / nushell do not take POSIX "$PATH" syntax; better no shell PATH than a wrong one.
        if (Path.GetFileName(shell) is not ("zsh" or "bash" or "sh" or "ksh" or "dash")) return [];
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(shell)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.Exists(HomeDirectory) ? HomeDirectory : "/",
                },
            };
            // -i and -l: PATH additions live in .zprofile / .zshrc / .bash_profile / .bashrc alike.
            process.StartInfo.ArgumentList.Add("-ilc");
            process.StartInfo.ArgumentList.Add($"printf '{PathBegin}%s{PathEnd}' \"$PATH\"");
            process.Start();
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // rc-file noise
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return [];
            }
            return ParseMarkedPath(stdout.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The PATH printed between the markers (rc files may print banners around it), split into absolute directories.</summary>
    public static IReadOnlyList<string> ParseMarkedPath(string output)
    {
        var start = output.LastIndexOf(PathBegin, StringComparison.Ordinal);
        if (start < 0) return [];
        start += PathBegin.Length;
        var end = output.IndexOf(PathEnd, start, StringComparison.Ordinal);
        if (end < 0) return [];
        return output[start..end].Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted).ToList();
    }

    private IEnumerable<string> PathDirectories()
    {
        var pathVar = _lookup("PATH");
        return string.IsNullOrEmpty(pathVar)
            ? []
            : pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string? FindIn(IEnumerable<string> directories, string fileName)
    {
        var isWindows = string.Equals(Os, "windows", StringComparison.OrdinalIgnoreCase);
        string[] extensions = isWindows ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in directories)
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
