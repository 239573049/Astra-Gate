using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Packages;

/// <summary>
/// Installs an npm package by fetching its tarball straight from the registry — no Bun needed. Used
/// for packages that are only gateway middleware or an agent, which run in Astra's own process and
/// whose dependencies are bundled in (they may import nothing but their own files), so there is
/// nothing to resolve. The tarball is checked against the registry's integrity hash and unpacked with
/// regular files only, every path kept inside the package folder.
/// </summary>
public sealed class NpmTarballFetcher(Func<HttpClient> http, string registry = "https://registry.npmjs.org")
{
    private const long MaxTarball = 50L * 1024 * 1024;
    private const long MaxUnpacked = 200L * 1024 * 1024;

    /// <summary>The registry asked, without a trailing slash.</summary>
    public string Registry { get; } = registry.TrimEnd('/');

    /// <summary>
    /// npm's manifest of <paramref name="name"/> at <paramref name="versionOrTag"/> ("latest", "1.2.0"):
    /// its exact version and where its tarball is.
    /// </summary>
    public async Task<NpmVersion> ResolveAsync(string name, string versionOrTag, CancellationToken ct = default)
    {
        var url = $"{Registry}/{name.Replace("/", "%2F")}/{Uri.EscapeDataString(versionOrTag)}";
        using var res = await http().GetAsync(url, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"npm has no {name}@{versionOrTag}");
        res.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject
                  ?? throw new InvalidDataException($"npm's answer for {name} isn't an object");
        var version = doc["version"]?.GetValue<string>() ?? throw new InvalidDataException($"npm gave no version of {name}");
        var dist = doc["dist"] as JsonObject ?? throw new InvalidDataException($"npm gave no tarball for {name}@{version}");
        return new NpmVersion(name, version,
            dist["tarball"]?.GetValue<string>() ?? throw new InvalidDataException($"npm gave no tarball for {name}@{version}"),
            dist["integrity"]?.GetValue<string>(), dist["shasum"]?.GetValue<string>());
    }

    /// <summary>Downloads and verifies the tarball, then unpacks its <c>package/</c> folder into <paramref name="dest"/>
    /// (replaced whole: nothing of an older version is left beside the new one).</summary>
    public async Task InstallAsync(NpmVersion pkg, string dest, CancellationToken ct = default)
    {
        using var res = await http().GetAsync(pkg.Tarball, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > MaxTarball) throw new InvalidDataException($"{pkg.Name}@{pkg.Version}'s tarball is too large");
        using var tgz = new MemoryStream();
        await using (var body = await res.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (tgz.Length + n > MaxTarball) throw new InvalidDataException($"{pkg.Name}@{pkg.Version}'s tarball is too large");
                tgz.Write(buffer, 0, n);
            }
        }
        Verify(pkg, tgz.GetBuffer().AsSpan(0, (int)tgz.Length));

        tgz.Position = 0;
        await ExtractAsync(tgz, dest, ct);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to the file <paramref name="target"/> and checks it against the hashes the plugin
    /// market gave (sha256 always; sha512, base64, when there is one). A file that doesn't match is deleted.
    /// </summary>
    public async Task DownloadVerifiedAsync(string url, string sha256, string? sha512, string target, CancellationToken ct = default)
    {
        using var res = await GetFollowingRedirectsAsync(url, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > MaxTarball) throw new InvalidDataException("the plugin's tarball is too large");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var h256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var h512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        long total = 0;
        try
        {
            await using (var body = await res.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > MaxTarball) throw new InvalidDataException("the plugin's tarball is too large");
                    h256.AppendData(buffer, 0, n);
                    h512.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            if (!string.Equals(Convert.ToHexStringLower(h256.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the plugin's tarball doesn't match the sha256 the market lists for it");
            if (sha512 is not null && Convert.ToBase64String(h512.GetHashAndReset()) != sha512)
                throw new InvalidDataException("the plugin's tarball doesn't match the sha512 the market lists for it");
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target);
            throw;
        }
    }

    /// <summary>Most redirects a tarball address may go through (a GitHub Release asset takes one, to its storage).</summary>
    internal const int MaxRedirects = 5;

    /// <summary>
    /// Gets <paramref name="url"/>, following redirects itself: Astra's clients never do (a redirect could lead anywhere),
    /// but a market that lists GitHub Release assets points at an address that always redirects. Each hop must be https
    /// (or http on this machine), and what is downloaded is checked against the market's hashes whichever host it came from.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var res = await http().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is not (301 or 302 or 303 or 307 or 308) || res.Headers.Location is not { } next) return res;
            res.Dispose();
            if (hop >= MaxRedirects) throw new HttpRequestException($"{url} redirects too many times");
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
                throw new HttpRequestException($"{url} redirects to {uri.Scheme}://{uri.Host}, which isn't https");
        }
    }

    /// <summary>Unpacks the tarball file <paramref name="tgzFile"/> into <paramref name="dest"/> (replaced whole), with the same limits as an npm install.</summary>
    public async Task ExtractFileAsync(string tgzFile, string dest, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(tgzFile);
        await ExtractAsync(stream, dest, ct);
    }

    private static async Task ExtractAsync(Stream tgz, string dest, CancellationToken ct)
    {
        var staging = dest + ".part";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        await Unpack(tgz, staging, ct);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(staging, dest);
    }

    // npm's integrity is an SRI string ("sha512-<base64>"); older packages only have a sha1 shasum.
    private static void Verify(NpmVersion pkg, ReadOnlySpan<byte> tgz)
    {
        if (pkg.Integrity is { } sri)
        {
            foreach (var part in sri.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = part.IndexOf('-');
                if (dash < 0) continue;
                byte[]? hash = part[..dash] switch
                {
                    "sha512" => SHA512.HashData(tgz),
                    "sha384" => SHA384.HashData(tgz),
                    "sha256" => SHA256.HashData(tgz),
                    _ => null,
                };
                if (hash is null) continue;
                if (Convert.ToBase64String(hash) == part[(dash + 1)..]) return;
                throw new InvalidDataException($"{pkg.Name}@{pkg.Version}'s tarball doesn't match npm's integrity hash");
            }
        }
        if (pkg.Shasum is { } sha1)
        {
#pragma warning disable CA5350 // npm's own legacy checksum; used only when no SRI hash is given
            if (Convert.ToHexStringLower(SHA1.HashData(tgz)) == sha1) return;
#pragma warning restore CA5350
            throw new InvalidDataException($"{pkg.Name}@{pkg.Version}'s tarball doesn't match npm's checksum");
        }
        throw new InvalidDataException($"npm gave no checksum for {pkg.Name}@{pkg.Version}");
    }

    private static async Task Unpack(Stream tgz, string dest, CancellationToken ct)
    {
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        long total = 0;
        await using var gz = new GZipStream(tgz, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            // only files and folders: links could point anywhere
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                continue;
            var name = entry.Name.Replace('\\', '/');
            // npm packs everything under one top folder, "package/" by convention (some use another name)
            var slash = name.IndexOf('/');
            if (slash < 0) continue;
            var rel = name[(slash + 1)..];
            if (rel.Length == 0) continue;
            var path = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"the tarball has a path outside the package: {entry.Name}");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            total += entry.Length;
            if (total > MaxUnpacked) throw new InvalidDataException("the package is too large unpacked");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (entry.DataStream is { } data)
            {
                await using var file = File.Create(path);
                await data.CopyToAsync(file, ct);
            }
            else
            {
                await File.WriteAllBytesAsync(path, [], ct);
            }
        }
    }
}

/// <summary>One version of an npm package, as the registry describes it.</summary>
public sealed record NpmVersion(string Name, string Version, string Tarball, string? Integrity, string? Shasum);
