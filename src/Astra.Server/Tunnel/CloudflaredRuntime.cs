using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Astra.Core;

namespace Astra.Server.Tunnel;

/// <summary>
/// The cloudflared remote access runs on: <c>ASTRA_CLOUDFLARED</c> when set, else the pinned release Astra downloads
/// into <see cref="AstraPaths.ToolsDir"/> the first time the tunnel starts, checked against the release's SHA-256
/// digests (the same pattern as the plugins' Bun runtime).
/// </summary>
public sealed class CloudflaredRuntime
{
    public const string HttpClientName = "astra-tools";

    /// <summary>The cloudflared Astra downloads. Update <see cref="Sums"/> with it (the release assets' sha256 digests).</summary>
    public const string Version = "2026.10.0";

    private static readonly Dictionary<string, string> Sums = new()
    {
        ["cloudflared-darwin-arm64.tgz"] = "a2f79ff7b9420aa537d74af239f376da170bbabeb529aec416002adac6a72e70",
        ["cloudflared-darwin-amd64.tgz"] = "903845b81828c8cb3c5d13d816a2de71c06a3da5785469df8eb0e1b736d92f9f",
        ["cloudflared-linux-amd64"] = "d33ff2d14475178d2012c2c56beba87389ac5ded27649519f198a7d3134a99db",
        ["cloudflared-linux-arm64"] = "e6422b9d4f72d3194bc5a38676f13667c06666523217b842a877d72a80b5ac08",
        ["cloudflared-windows-amd64.exe"] = "86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c",
    };

    private const long MaxDownload = 120L * 1024 * 1024;

    private readonly AstraPaths _paths;
    private readonly Func<HttpClient> _http;
    private readonly Func<string, string?> _getEnv;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _error;
    private volatile bool _downloading;
    private long _received;
    private long _total;

    /// <param name="http">The client downloads go through (Astra's proxy-aware one).</param>
    /// <param name="releaseBase">Where cloudflared's releases are; tests point it at a local server.</param>
    public CloudflaredRuntime(AstraPaths paths, Func<HttpClient> http, Func<string, string?>? getEnv = null,
        string releaseBase = "https://github.com/cloudflare/cloudflared/releases/download")
    {
        _paths = paths;
        _http = http;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        ReleaseBase = releaseBase.TrimEnd('/');
    }

    public string ReleaseBase { get; }

    /// <summary>The cloudflared the user named (<c>ASTRA_CLOUDFLARED</c>), used as it is and never downloaded over.</summary>
    public string? Override => _getEnv("ASTRA_CLOUDFLARED") is { Length: > 0 } c ? c : null;

    /// <summary>Where the downloaded cloudflared lives.</summary>
    public string DownloadedPath =>
        Path.Combine(_paths.ToolsDir, "cloudflared-" + Version, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    /// <summary>The cloudflared at hand without a download, or null.</summary>
    public string? Current => Override ?? (File.Exists(DownloadedPath) ? DownloadedPath : null);

    /// <summary>What the remote access page shows: where cloudflared is, the download's progress, why it failed.</summary>
    public CloudflaredStatus Status
    {
        get
        {
            var total = Interlocked.Read(ref _total);
            double? progress = _downloading && total > 0 ? Math.Min(1, (double)Interlocked.Read(ref _received) / total) : null;
            return new CloudflaredStatus(Current, Override is null ? Version : null, _downloading, progress, _error, Asset(out _));
        }
    }

    /// <summary>The cloudflared to run, downloading it first when there is none yet.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        if (Current is { } path) return path;
        await _lock.WaitAsync(ct);
        try
        {
            if (Current is { } again) return again;
            _downloading = true;
            _error = null;
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _total, 0);
            await DownloadAsync(ct);
            return DownloadedPath;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _error = e.Message;
            throw new InvalidOperationException($"downloading cloudflared {Version}: {e.Message}", e);
        }
        finally
        {
            _downloading = false;
            _lock.Release();
        }
    }

    /// <summary>
    /// The release asset for this machine; null with the reason when there is none. Windows on Arm runs the x64 build
    /// (cloudflared publishes no native one).
    /// </summary>
    public static string? Asset(out string? why)
    {
        why = null;
        var arch = RuntimeInformation.OSArchitecture;
        string? asset = null;
        if (OperatingSystem.IsMacOS()) asset = arch switch { Architecture.Arm64 => "cloudflared-darwin-arm64.tgz", Architecture.X64 => "cloudflared-darwin-amd64.tgz", _ => null };
        else if (OperatingSystem.IsLinux()) asset = arch switch { Architecture.Arm64 => "cloudflared-linux-arm64", Architecture.X64 => "cloudflared-linux-amd64", _ => null };
        else if (OperatingSystem.IsWindows()) asset = arch is Architecture.X64 or Architecture.Arm64 ? "cloudflared-windows-amd64.exe" : null;
        if (asset is null) why = $"cloudflared has no build for {RuntimeInformation.OSDescription} ({arch})";
        return asset;
    }

    // GitHub answers a release download with a redirect to its CDN; Astra's clients don't follow redirects on their
    // own, so the download follows a few itself — https only, and the checksum checks whatever it ends at.
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

    private async Task DownloadAsync(CancellationToken ct)
    {
        var asset = Asset(out var why) ?? throw new PlatformNotSupportedException(why);
        var want = Sums[asset];
        var url = $"{ReleaseBase}/{Version}/{asset}";
        using var res = await GetFollowingAsync(url, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{url}: {(int)res.StatusCode} {res.ReasonPhrase}");
        if (res.Content.Headers.ContentLength is { } length)
        {
            if (length > MaxDownload) throw new InvalidDataException($"{url}: too large");
            Interlocked.Exchange(ref _total, length);
        }

        using var data = new MemoryStream();
        await using (var body = await res.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + n > MaxDownload) throw new InvalidDataException($"{url}: too large");
                data.Write(buffer, 0, n);
                Interlocked.Add(ref _received, n);
            }
        }
        if (!Convert.ToHexStringLower(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length))).Equals(want, StringComparison.Ordinal))
            throw new InvalidDataException("the download's checksum doesn't match the release's");

        var exe = DownloadedPath;
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var part = exe + ".part";
        data.Position = 0;
        if (asset.EndsWith(".tgz", StringComparison.Ordinal))
        {
            await using var gz = new GZipStream(data, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == "cloudflared")
                    break;
            }
            if (entry?.DataStream is not { } stream) throw new InvalidDataException($"{asset} has no cloudflared");
            await using var dst = File.Create(part);
            await stream.CopyToAsync(dst, ct);
        }
        else
        {
            await using var dst = File.Create(part);
            await data.CopyToAsync(dst, ct);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(part, exe, overwrite: true);
    }
}

/// <summary>cloudflared as the remote access page shows it.</summary>
/// <param name="Path">The cloudflared in use, null when none is at hand yet.</param>
/// <param name="Version">The pinned version Astra downloads; null when ASTRA_CLOUDFLARED names one.</param>
/// <param name="Downloading">Whether it is being downloaded now.</param>
/// <param name="Progress">0–1 while downloading with a known size.</param>
/// <param name="Error">Why the last download failed.</param>
/// <param name="Asset">This machine's release asset; null when cloudflared has none.</param>
public sealed record CloudflaredStatus(string? Path, string? Version, bool Downloading, double? Progress, string? Error, string? Asset);
