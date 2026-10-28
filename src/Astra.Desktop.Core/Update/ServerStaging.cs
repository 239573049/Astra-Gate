using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Astra.Desktop.Core.Update;

/// <summary>
/// Where a server platform package comes from. The TS shells out to npm; the desktop client has no Node, so the source is
/// an interface: <see cref="NpmRegistryPackageSource"/> downloads the registry tarball, tests supply a fake that writes files.
/// </summary>
public interface IServerPackageSource
{
    /// <summary>Materialises package <paramref name="package"/>@<paramref name="version"/> into <paramref name="packageRoot"/> (so <c>bin/astra-server</c> ends up beneath it).</summary>
    Task FetchAsync(string package, string version, string packageRoot, CancellationToken ct);
}

/// <summary>
/// Downloads <c>&lt;registry&gt;/&lt;@scope/name&gt;/-/&lt;name&gt;-&lt;version&gt;.tgz</c> and unpacks its <c>package/</c> tree. The tarball's
/// own integrity is npm's; the trust anchor for the binary is the manifest's sha256, verified by <see cref="ServerStaging"/>.
/// </summary>
public sealed class NpmRegistryPackageSource(HttpClient http, string registry = "https://registry.npmjs.org") : IServerPackageSource
{
    public async Task FetchAsync(string package, string version, string packageRoot, CancellationToken ct)
    {
        var unscoped = package[(package.LastIndexOf('/') + 1)..];
        var url = $"{registry.TrimEnd('/')}/{package}/-/{unscoped}-{version}.tgz";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Downloading {package}@{version} failed: {(int)response.StatusCode} {response.ReasonPhrase} for {url}.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(body, CompressionMode.Decompress);
        await Extract(gzip, packageRoot, ct);
    }

    internal static async Task Extract(Stream tar, string packageRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(packageRoot);
        var root = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            // npm tarballs wrap everything in "package/"; anything escaping the root is refused (zip-slip).
            var relative = entry.Name.Replace('\\', '/');
            if (relative.StartsWith("package/", StringComparison.Ordinal)) relative = relative["package/".Length..];
            var dest = Path.GetFullPath(Path.Combine(packageRoot, relative));
            if (!dest.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException($"The package contains an entry outside its directory ({entry.Name}).");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await entry.ExtractToFileAsync(dest, overwrite: true, ct);
        }
    }
}

/// <summary>The binary staged by <see cref="ServerStaging.StageAsync"/> and the native libraries beside it.</summary>
public sealed record StagedServerBinary(string Path, string Version, IReadOnlyList<string> NativeLibraries);

/// <summary>Port of packages/update-core/src/staging.ts (the npm install step is the <see cref="IServerPackageSource"/>).</summary>
public static class ServerStaging
{
    public static string BinaryName(bool windows) => windows ? "astra-server.exe" : "astra-server";

    /// <summary>sha256 of a file, hex lowercase, streamed (binaries are ~90 MB).</summary>
    public static async Task<string> Sha256FileAsync(string file, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    /// <summary>
    /// Downloads the platform package into the staging dir and verifies the binary checksum BEFORE anything of the running
    /// installation is touched. Staging is wiped first and on every failure, so a failed run leaves nothing stale.
    /// </summary>
    public static async Task<StagedServerBinary> StageAsync(
        IServerPackageSource source, string stagingDir, string serverPackage, string version, string? sha256, bool windows,
        Action<string>? log = null, CancellationToken ct = default)
    {
        DeleteDir(stagingDir);
        Directory.CreateDirectory(stagingDir);
        log?.Invoke($"Downloading {serverPackage}@{version}…");
        // Same layout npm gives a prefix install (node_modules/<@scope>/<name>), so the two stagings look alike.
        var packageRoot = Path.Combine([stagingDir, "node_modules", .. serverPackage.Split('/')]);
        try
        {
            await source.FetchAsync(serverPackage, version, packageRoot, ct);
        }
        catch
        {
            DeleteDir(stagingDir);
            throw;
        }

        var staged = Path.Combine(packageRoot, "bin", BinaryName(windows));
        if (!File.Exists(staged))
        {
            DeleteDir(stagingDir);
            throw new InvalidOperationException($"The staged package does not contain a server binary ({staged}).");
        }
        if (!string.IsNullOrEmpty(sha256))
        {
            log?.Invoke("Verifying checksum…");
            var actual = await Sha256FileAsync(staged, ct);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteDir(stagingDir);
                throw new InvalidOperationException($"Checksum mismatch for the staged server binary (expected {sha256}, got {actual}).");
            }
        }
        // A package built without AOT has no companions: normal for pre-AOT releases, so a log line, never an error.
        var libs = NativeCompanions.Find(staged);
        if (libs.Count == 0) log?.Invoke("The staged package ships no native companion libraries (normal for a non-AOT build).");
        return new StagedServerBinary(staged, version, libs);
    }

    internal static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
