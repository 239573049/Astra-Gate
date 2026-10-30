using System.Security.Cryptography;
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
    private bool _shellPathRefreshing;

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

    public bool FileExists(string path)
    {
        var exists = File.Exists(path);
        Track(path, FileProbeKind.FileExists, exists ? Present : null);
        return exists;
    }

    public bool DirectoryExists(string path)
    {
        var exists = Directory.Exists(path);
        Track(path, FileProbeKind.DirectoryExists, exists ? Present : null);
        return exists;
    }

    /// <summary>Reads a file as text (BOM stripped), or null when it does not exist.</summary>
    public (string Text, bool HadBom)? ReadTextWithBom(string path)
    {
        if (ReadAllBytesOrNull(path) is not { } bytes) return null;
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

    public byte[]? ReadAllBytesOrNull(string path)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (t_reads is not null) Track(path, FileProbeKind.Content, bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)));
        return bytes;
    }

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
    /// PATH as the user's interactive login shell sets it (macOS / Linux; cached, and re-read in the background once
    /// older than five minutes — the stale value is served meanwhile, since a slow rc file can take seconds). A server started
    /// by the desktop app inherits launchd's minimal PATH, so without this Astra would judge a different copy of a
    /// client than the one the user runs in a terminal (e.g. a fresh copy in ~/.npm-global/bin while the terminal
    /// still runs an older one from ~/.local/bin). Empty in tests, on Windows, for non-POSIX shells, or on failure.
    /// </summary>
    public IReadOnlyList<string> LoginShellPath()
    {
        if (!_readLoginShellPath || Os == "windows") return [];
        lock (_shellPathLock)
        {
            if (_shellPath is { } cached)
            {
                if (DateTimeOffset.UtcNow - cached.At >= TimeSpan.FromMinutes(5) && !_shellPathRefreshing)
                {
                    _shellPathRefreshing = true;
                    _ = Task.Run(() =>
                    {
                        var fresh = ReadLoginShellPath();
                        lock (_shellPathLock)
                        {
                            _shellPath = (fresh, DateTimeOffset.UtcNow);
                            _shellPathRefreshing = false;
                        }
                    });
                }
                return cached.Dirs;
            }
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
                if (FileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- read tracking

    private const string Present = "1";

    /// <summary>The probes of the innermost open <see cref="TrackReads"/> scope on this thread.</summary>
    [ThreadStatic] private static List<FileProbe>? t_reads;

    /// <summary>
    /// Records every path the code inside the scope looks at through this class — the content hash of each file it reads,
    /// whether each file or directory it checks exists — so a caller can later tell (<see cref="Unchanged"/>) whether
    /// running that code again would see the same files. Scopes nest, and record the calling thread only: client plans
    /// are built synchronously.
    /// </summary>
    public static ReadTracking TrackReads() => new();

    /// <summary>
    /// True when every probe would see the same thing now: the file holds the same bytes, the path still exists or is
    /// still missing. A path that cannot be read counts as changed.
    /// </summary>
    public static bool Unchanged(IEnumerable<FileProbe> probes)
    {
        try
        {
            return probes.All(p => p.Seen == p.Kind switch
            {
                FileProbeKind.Content => ContentHash(p.Path),
                FileProbeKind.FileExists => File.Exists(p.Path) ? Present : null,
                _ => Directory.Exists(p.Path) ? Present : null,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>SHA-256 of a file's bytes (hex), or null when it does not exist. Streams it, so a large file allocates nothing large.</summary>
    public static string? ContentHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void Track(string path, FileProbeKind kind, string? seen) => t_reads?.Add(new FileProbe(path, kind, seen));

    /// <summary>An open <see cref="TrackReads"/> scope; disposing it closes the scope and hands its probes to the enclosing one.</summary>
    public sealed class ReadTracking : IDisposable
    {
        private readonly List<FileProbe>? _outer = t_reads;
        private readonly List<FileProbe> _reads = [];

        internal ReadTracking() => t_reads = _reads;

        /// <summary>What the scope has looked at so far, in order (a path may appear more than once).</summary>
        public IReadOnlyList<FileProbe> Reads => _reads;

        public void Dispose()
        {
            t_reads = _outer;
            _outer?.AddRange(_reads);
        }
    }
}

/// <summary>How <see cref="ClientEnvironment"/> looked at a path.</summary>
public enum FileProbeKind
{
    /// <summary>Read its bytes.</summary>
    Content,

    /// <summary>Checked that a file exists.</summary>
    FileExists,

    /// <summary>Checked that a directory exists.</summary>
    DirectoryExists,
}

/// <summary>
/// One look at a path (<see cref="ClientEnvironment.TrackReads"/>): <see cref="Seen"/> is the SHA-256 of the bytes read for
/// <see cref="FileProbeKind.Content"/>, "1" for an existing path otherwise, and null for a missing one.
/// </summary>
public readonly record struct FileProbe(string Path, FileProbeKind Kind, string? Seen);
