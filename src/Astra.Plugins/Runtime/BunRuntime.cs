using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Astra.Core;

namespace Astra.Plugins.Runtime;

/// <summary>
/// The Bun the plugin host and package installs run on: <c>ASTRA_BUN</c> when set, else the pinned
/// release Astra downloads into <see cref="AstraPaths.PluginRuntimeDir"/> the first time a plugin
/// needs it, checked against Bun's own SHA-256 sums (ported from magpie's bun.go).
/// </summary>
public sealed class BunRuntime
{
    /// <summary>The Bun Astra downloads; magpie's pin, so plugins run on what their authors test with.
    /// Update <see cref="Sums"/> (from the release's SHASUMS256.txt) and ci.yml's bun-version with it.</summary>
    public const string Version = "1.4.2";

    private static readonly Dictionary<string, string> Sums = new()
    {
        ["bun-darwin-aarch64.zip"] = "90987a3a16d7db556d886ac3d551e7b6d3edf0a1cf43acaed622e8676be1d12f",
        ["bun-darwin-x64.zip"] = "80520d7e17526308c9185d261679ac6d27798d3803a0e9f7ff9121ab8affb012",
        ["bun-linux-aarch64.zip"] = "54328bbc2d9c8e0c9f892c544d66c57a83b84139e34909e5ee81758f1ac8fda7",
        ["bun-linux-x64-baseline.zip"] = "c678040f14fe0440eb839d37cbd0ce4c051a32da72806ac97de6a6aab6bf728f",
        ["bun-windows-x64-baseline.zip"] = "78c221c2376f79731ccf4e4af0b3bb46d81fefa3296c5abee09ad8a1b21e68c6",
        ["bun-windows-aarch64.zip"] = "a7a16b876a305fd1029c66dbd27007b4f6112ae896532f675878731a21e50cfd",
    };

    private const long MaxDownload = 200L * 1024 * 1024;

    private readonly AstraPaths _paths;
    private readonly Func<HttpClient> _http;
    private readonly Func<string, string?> _getEnv;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _error;
    private volatile bool _downloading;

    /// <param name="http">The client downloads go through (Astra's proxy-aware one).</param>
    /// <param name="releaseBase">Where Bun's releases are; tests point it at a local server.</param>
    public BunRuntime(AstraPaths paths, Func<HttpClient> http, Func<string, string?>? getEnv = null,
        string releaseBase = "https://github.com/oven-sh/bun/releases/download")
    {
        _paths = paths;
        _http = http;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        ReleaseBase = releaseBase.TrimEnd('/');
    }

    public string ReleaseBase { get; }

    /// <summary>The Bun the user named (<c>ASTRA_BUN</c>), used as it is and never downloaded over.</summary>
    public string? Override => _getEnv("ASTRA_BUN") is { Length: > 0 } b ? b : null;

    /// <summary>Where the downloaded Bun lives.</summary>
    public string DownloadedPath =>
        Path.Combine(_paths.PluginRuntimeDir, "bun-" + Version, OperatingSystem.IsWindows() ? "bun.exe" : "bun");

    /// <summary>The Bun at hand without a download, or null.</summary>
    public string? Current => Override ?? (File.Exists(DownloadedPath) ? DownloadedPath : null);

    /// <summary>What the plugins page shows: where Bun is, whether it is being fetched, why it failed.</summary>
    public BunStatus Status => new(Current, Override is null ? Version : null, _downloading, _error, Target(out _));

    /// <summary>The Bun to run, downloading it first when there is none yet.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        if (Current is { } bun) return bun;
        await _lock.WaitAsync(ct);
        try
        {
            if (Current is { } again) return again;
            _downloading = true;
            _error = null;
            await DownloadAsync(ct);
            return DownloadedPath;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _error = e.Message;
            throw new InvalidOperationException($"downloading Bun {Version} to run plugins: {e.Message}", e);
        }
        finally
        {
            _downloading = false;
            _lock.Release();
        }
    }

    // GitHub answers a release download with a redirect to its CDN; Astra's clients don't follow redirects on
    // their own (an API key must never ride one), so the download follows a few itself — https only, and the
    // checksum checks whatever it ends at.
    private async Task<HttpResponseMessage> GetFollowingAsync(string url, CancellationToken ct)
    {
        for (var hops = 0; ; hops++)
        {
            var res = await _http().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is < 300 or >= 400 || res.Headers.Location is not { } next || hops == 5) return res;
            res.Dispose();
            var target = next.IsAbsoluteUri ? next : new Uri(new Uri(url), next);
            if (target.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException($"{url} redirects to {target}, which isn't https");
            url = target.ToString();
        }
    }

    /// <summary>
    /// The name of Bun's build for this machine (bun-darwin-aarch64 and the like); null with the reason
    /// when there is none. x64 Linux and Windows get the baseline build, which runs without AVX2 too.
    /// </summary>
    public static string? Target(out string? why)
    {
        why = null;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x64",
            _ => null,
        };
        var os = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : null;
        if (arch is null || os is null)
        {
            why = $"Bun has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
            return null;
        }
        return $"bun-{os}-{arch}" + (arch == "x64" && os != "darwin" ? "-baseline" : "");
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        var target = Target(out var why) ?? throw new PlatformNotSupportedException(why);
        var want = Sums[target + ".zip"];
        var url = $"{ReleaseBase}/bun-v{Version}/{target}.zip";
        using var res = await GetFollowingAsync(url, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)res.StatusCode} {res.ReasonPhrase}");
        if (res.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException($"{url}: too large");

        using var zip = new MemoryStream();
        await using (var body = await res.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (zip.Length + n > MaxDownload) throw new InvalidDataException($"{url}: too large");
                zip.Write(buffer, 0, n);
            }
        }
        if (!Convert.ToHexStringLower(SHA256.HashData(zip.GetBuffer().AsSpan(0, (int)zip.Length))).Equals(want, StringComparison.Ordinal))
            throw new InvalidDataException("the download's checksum doesn't match the release's");

        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var exeName = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        var entry = archive.Entries.FirstOrDefault(e => e.Name == exeName && !e.FullName.EndsWith('/'))
                    ?? throw new InvalidDataException($"{target}.zip has no {exeName}");
        var exe = DownloadedPath;
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var part = exe + ".part";
        await using (var src = entry.Open())
        await using (var dst = File.Create(part))
            await src.CopyToAsync(dst, ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(part, exe, overwrite: true);
    }
}

/// <summary>Bun as the plugins page shows it.</summary>
/// <param name="Path">The Bun in use, null when none is at hand yet.</param>
/// <param name="Version">The pinned version Astra downloads; null when ASTRA_BUN names one.</param>
/// <param name="Downloading">Whether it is being downloaded now.</param>
/// <param name="Error">Why the last download failed.</param>
/// <param name="Target">This machine's build name; null when Bun has none.</param>
public sealed record BunStatus(string? Path, string? Version, bool Downloading, string? Error, string? Target);
