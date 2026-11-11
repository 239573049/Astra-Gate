using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>
/// Directory links and directory hashing for the Skill library (<c>.plan/resources-library.md</c> §5). A client directory gets a
/// link into the library rather than a copy; where a link cannot be made, a Windows junction, and last a copy.
/// </summary>
public static class FsLinks
{
    /// <summary>Marker written into a copied Skill, so a copy can be told from the user's own directory.</summary>
    public const string ManagedMarker = ".astra-managed";

    /// <summary>Makes <paramref name="linkPath"/> point at <paramref name="target"/>. Returns how it was made (a <see cref="PlacementHows"/> value).</summary>
    public static string Create(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return PlacementHows.Symlink;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) throw;
        }
        if (TryJunction(linkPath, target)) return PlacementHows.Junction;
        CopyDirectory(target, linkPath);
        File.WriteAllText(Path.Combine(linkPath, ManagedMarker), $"{{\"hash\":\"{HashDirectory(target)}\"}}");
        return PlacementHows.Copy;
    }

    /// <summary>A junction needs no privilege but only works for a local drive; <c>mklink /J</c> is the supported way to make one.</summary>
    private static bool TryJunction(string linkPath, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(target);
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(TimeSpan.FromSeconds(10));
            return process.HasExited && process.ExitCode == 0 && IsLink(linkPath);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Whether the path is a symbolic link or a junction (whatever it points at, even if that is gone).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The path a link points at, resolved against the link's own directory, or null when <paramref name="path"/> is not a link.</summary>
    public static string? LinkTargetOf(string path)
    {
        try
        {
            var raw = new DirectoryInfo(path).LinkTarget ?? new FileInfo(path).LinkTarget;
            if (raw is null) return null;
            var full = Path.IsPathRooted(raw) ? raw : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", raw);
            return Normalize(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is a link that points at <paramref name="target"/>.</summary>
    public static bool IsLinkTo(string path, string target) =>
        LinkTargetOf(path) is { } t && string.Equals(t, Normalize(target), PathComparison);

    /// <summary>
    /// Removes the link itself and nothing it points at: a non-recursive call, never one that walks into the target.
    /// </summary>
    public static void RemoveLink(string path)
    {
        if (!IsLink(path)) throw new InvalidOperationException($"{path} is not a link");
        if (OperatingSystem.IsWindows()) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // ---------------------------------------------------------------- content

    private static bool Ignored(string relativePath)
    {
        var first = relativePath.Split('/', 2)[0];
        var name = Path.GetFileName(relativePath);
        return first == ".git" || name is ".DS_Store" or ManagedMarker;
    }

    /// <summary>Every file below <paramref name="dir"/> (relative path, forward slashes), skipping links and the files <see cref="HashDirectory"/> ignores.</summary>
    public static IReadOnlyList<string> Files(string dir)
    {
        var result = new List<string>();
        void Walk(string current, string prefix)
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                var rel = prefix + entry.Name;
                if (entry.LinkTarget is not null || Ignored(rel)) continue;
                if (entry is DirectoryInfo d) Walk(d.FullName, rel + "/");
                else result.Add(rel);
            }
        }
        Walk(dir, "");
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>SHA-256 over the files' relative paths and bytes in path order; ignores .DS_Store, <see cref="ManagedMarker"/> and .git/.</summary>
    public static string HashDirectory(string dir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rel in Files(dir))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(rel + "\0"));
            using var stream = File.OpenRead(Path.Combine(dir, rel));
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Copies the files <see cref="Files"/> lists (so no links, no .git).</summary>
    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var rel in Files(source))
        {
            var to = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, rel), to, overwrite: true);
        }
    }

    /// <summary>Deletes a real directory tree (never a link: a link is removed with <see cref="RemoveLink"/>).</summary>
    public static void DeleteTree(string path)
    {
        if (IsLink(path)) throw new InvalidOperationException($"{path} is a link; remove it with RemoveLink");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
