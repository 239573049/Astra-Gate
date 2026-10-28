namespace Astra.Desktop.Core.Update;

/// <summary>
/// File-level swap steps of a server update, each with a <c>.prev</c> snapshot and a commit/restore pair. Port of
/// packages/update-core/src/swap.ts.
/// </summary>
public static class ServerSwap
{
    /// <summary>Rolling backups kept per subject.</summary>
    public const int MaxBackups = 3;

    private const string Libs = "-libs";
    private const string Prev = ".prev";

    /// <summary>
    /// Copies the installed binary (and its native companions, into <c>&lt;name&gt;-libs</c> beside it) into
    /// <c>backupsDir/server</c> before anything is replaced. Null when there is nothing to back up. The copy is the
    /// last-resort rollback anchor; the primary one is repointing install.json at the previous path, which is never deleted.
    /// </summary>
    public static string? BackupCurrentBinary(string? currentPath, string backupsDir, string? currentVersion, bool windows, int maxBackups = MaxBackups, Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath)) return null;
        var dir = Path.Combine(backupsDir, "server");
        Directory.CreateDirectory(dir);
        var stamp = string.IsNullOrEmpty(currentVersion) ? UpdateState.Stamp(now).Replace(':', '-').Replace('.', '-') : currentVersion;
        var backup = Path.Combine(dir, $"astra-server-{stamp}{(windows ? ".exe" : "")}");
        File.Copy(currentPath, backup, overwrite: true);
        var companions = NativeCompanions.Find(currentPath);
        if (companions.Count > 0)
        {
            var libsDir = backup + Libs;
            Directory.CreateDirectory(libsDir);
            foreach (var lib in companions) File.Copy(lib, Path.Combine(libsDir, Path.GetFileName(lib)), overwrite: true);
        }
        PruneBackups(dir, maxBackups);
        return backup;
    }

    /// <summary>Removes the oldest entries beyond <paramref name="keep"/> (ordinal name order); a backup and its <c>-libs</c> directory are one entry.</summary>
    public static void PruneBackups(string dir, int keep = MaxBackups)
    {
        string[] entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var units = entries.Where(e => !e.EndsWith(Libs, StringComparison.Ordinal) || !entries.Contains(e[..^Libs.Length])).ToList();
        for (var i = 0; i < units.Count - keep; i++) BestEffort(() => { DeleteAny(Path.Combine(dir, units[i])); DeleteAny(Path.Combine(dir, units[i]) + Libs); });
    }

    /// <summary>
    /// Copies the verified staged binary to its managed, version-suffixed home. A fresh file per version means a running
    /// binary is never replaced (and Windows file locks never matter); install.json is what makes the resolvers pick it.
    /// </summary>
    public static string InstallManagedBinary(string stagedPath, UpdateLayout layout, string version, bool windows)
    {
        var target = layout.ManagedBinaryPath(version, windows);
        Directory.CreateDirectory(layout.ServerDir);
        var tmp = target + ".tmp";
        File.Copy(stagedPath, tmp, overwrite: true);
        File.Move(tmp, target, overwrite: true);
        if (!windows && !OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101); // 0755
        return target;
    }

    /// <summary>
    /// Places the companion native libraries beside the managed binaries. Their file names are shared across versions and the
    /// running server has its copy loaded (Windows refuses to overwrite it), so call this only once the server is stopped.
    /// The replaced copy is kept as <c>&lt;name&gt;.prev</c> until <see cref="CommitNativeCompanions"/> / <see cref="RestoreNativeCompanions"/>;
    /// a library whose bytes did not change is left alone.
    /// </summary>
    public static void InstallNativeCompanions(IEnumerable<string> staged, string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        foreach (var lib in staged)
        {
            var dest = Path.Combine(serverDir, Path.GetFileName(lib));
            if (File.Exists(dest) && SameContent(lib, dest)) continue;
            var tmp = dest + ".tmp";
            var prev = dest + Prev;
            File.Copy(lib, tmp, overwrite: true);
            File.Delete(prev);
            if (File.Exists(dest)) File.Copy(dest, prev, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
        }
    }

    /// <summary>Drops the <c>.prev</c> companion snapshots once the update stuck.</summary>
    public static void CommitNativeCompanions(string serverDir)
    {
        foreach (var f in CompanionPrevs(serverDir)) File.Delete(f);
    }

    /// <summary>Puts every <c>.prev</c> companion snapshot back (no-op without one), so a rolled-back AOT binary finds its libraries.</summary>
    public static void RestoreNativeCompanions(string serverDir)
    {
        foreach (var prev in CompanionPrevs(serverDir)) File.Move(prev, prev[..^Prev.Length], overwrite: true);
    }

    /// <summary>
    /// Removes managed binaries older than <paramref name="keep"/> versions besides the current target (oldest by version
    /// first). Failures are ignored: a leftover old binary is harmless. Differs from the TS, which deleted by directory order.
    /// </summary>
    public static void PruneManagedBinaries(string serverDir, string currentTarget, int keep = 2)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(serverDir, "astra-server-*").ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var others = files
            .Where(f => Path.GetFullPath(f) != Path.GetFullPath(currentTarget) && char.IsAsciiDigit(Path.GetFileName(f)["astra-server-".Length..].FirstOrDefault()) && !f.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(f => f, Comparer<string>.Create(CompareManaged))
            .ToList();
        for (var i = 0; i < others.Count - keep; i++) BestEffort(() => File.Delete(others[i]));
    }

    /// <summary>
    /// The server serves the web UI from <c>wwwroot</c> beside its own executable, so a managed binary needs
    /// <c>serverDir/wwwroot</c>. Replaces it with the copy shipped next to <paramref name="binaryPath"/>; the previous one is kept
    /// as <c>wwwroot.prev</c> until commit/restore. False, touching nothing, when the package ships no wwwroot.
    /// </summary>
    public static bool InstallManagedWebRoot(string binaryPath, string serverDir)
    {
        var src = Path.Combine(Path.GetDirectoryName(binaryPath)!, "wwwroot");
        if (!Directory.Exists(src)) return false;
        var dest = Path.Combine(serverDir, "wwwroot");
        var tmp = dest + ".tmp";
        var prev = dest + Prev;
        Directory.CreateDirectory(serverDir);
        DeleteAny(tmp);
        CopyDirectory(src, tmp);
        DeleteAny(prev);
        if (Directory.Exists(dest)) Directory.Move(dest, prev);
        Directory.Move(tmp, dest);
        return true;
    }

    public static void CommitManagedWebRoot(string serverDir) => DeleteAny(Path.Combine(serverDir, "wwwroot" + Prev));

    public static void RestoreManagedWebRoot(string serverDir)
    {
        var dest = Path.Combine(serverDir, "wwwroot");
        var prev = dest + Prev;
        if (!Directory.Exists(prev)) return;
        DeleteAny(dest);
        Directory.Move(prev, dest);
    }

    private static IEnumerable<string> CompanionPrevs(string serverDir)
    {
        try
        {
            return Directory.EnumerateFiles(serverDir, "*" + Prev).Where(f => NativeCompanions.IsLibrary(Path.GetFileName(f)[..^Prev.Length])).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int CompareManaged(string a, string b)
    {
        static string V(string f) => Path.GetFileName(f)["astra-server-".Length..].Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        return ReleaseVersion.Compare(V(a), V(b)) ?? string.CompareOrdinal(a, b);
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var (x, y) = (new FileInfo(a), new FileInfo(b));
            return x.Length == y.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dest, Path.GetRelativePath(src, f)), overwrite: true);
    }

    private static void DeleteAny(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
