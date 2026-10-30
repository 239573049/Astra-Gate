using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Astra.Desktop.Core.Update;

/// <summary>A newer desktop app on the feed: the file that installs it on this machine.</summary>
/// <param name="Version">The release's version.</param>
/// <param name="Notes">Release notes, when the feed has them.</param>
/// <param name="FileName">The file's name on the feed, e.g. <c>Astra-0.13.4-darwin-arm64.zip</c>.</param>
/// <param name="Url">Where to download it: <c>&lt;feed&gt;/&lt;channel&gt;/v/&lt;version&gt;/&lt;file&gt;</c>.</param>
/// <param name="Size">Its size in bytes (0 when the feed did not say).</param>
/// <param name="Sha256">Its sha256 (hex), computed by the docs site on upload: what decides whether a download is installed.</param>
public sealed record DesktopRelease(string Version, string? Notes, string FileName, string Url, long Size, string Sha256);

/// <summary>
/// The desktop half of the update feed: <c>&lt;feed&gt;/&lt;channel&gt;/releases.json</c> (docs/app/api/client-releases), which lists every release
/// the channel offers with its files, their platform and kind tags and their hashes. The tags are the ones release.yml's
/// "Upload to the docs site" step sends (<c>darwin|win32|linux</c>-<c>arm64|x64</c>; <c>zip</c> / <c>exe</c> / <c>appimage</c>), so the two
/// change together. Installers go up before npm and carry their own server, so an app offer does not wait for <c>latest.json</c>.
/// </summary>
public static class DesktopReleaseFeed
{
    /// <summary>This machine's platform tag on the feed, e.g. <c>darwin-arm64</c>, <c>win32-x64</c>, <c>linux-x64</c>.</summary>
    public static string PlatformTag()
    {
        var os = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";
        return $"{os}-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
    }

    /// <summary>The file kind that updates the app on this OS: the zip Squirrel.Mac used, the NSIS installer, the AppImage.</summary>
    public static string KindForThisOs() => OperatingSystem.IsMacOS() ? "zip" : OperatingSystem.IsWindows() ? "exe" : "appimage";

    /// <summary><c>&lt;feed&gt;/&lt;channel&gt;/releases.json</c>.</summary>
    public static string ReleasesUrl(string? feedUrl, string? channel)
        => $"{UpdateFeed.BaseUrl(feedUrl)}/{UpdateFeed.NormalizeChannel(channel)}/releases.json";

    /// <summary>
    /// The newest release in <paramref name="json"/> (a releases.json body) that is newer than <paramref name="currentVersion"/> and has a
    /// <paramref name="kind"/> file for <paramref name="platform"/> with a sha256; null when there is none. A newer release without this
    /// machine's file is passed over, so a partly uploaded release never hides an older complete one.
    /// </summary>
    public static DesktopRelease? Pick(string json, string? feedUrl, string? channel, string platform, string kind, string currentVersion)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new UpdateFeedException($"The release list is not valid JSON ({e.Message}).");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new UpdateFeedException("The release list has no versions.");
            DesktopRelease? best = null;
            foreach (var v in versions.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object || Str(v, "version") is not { Length: > 0 } version) continue;
                if (!ReleaseVersion.IsNewer(version, currentVersion)) continue;
                if (best is not null && !ReleaseVersion.IsNewer(version, best.Version)) continue;
                if (!v.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in files.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || Str(f, "platform") != platform || Str(f, "kind") != kind) continue;
                    if (Str(f, "name") is not { Length: > 0 } name || Str(f, "sha256") is not { Length: 64 } sha256) continue;
                    var size = f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                    best = new DesktopRelease(version, Str(v, "notes"), name, FileUrl(feedUrl, channel, version, name), size, sha256.ToLowerInvariant());
                    break;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// GETs the release list and picks the newer app for this machine (<see cref="Pick"/>). Throws <see cref="UpdateFeedException"/> when the
    /// feed cannot be read. Default timeout 15 s, like the manifest fetch.
    /// </summary>
    public static async Task<DesktopRelease?> CheckAsync(
        HttpClient http, string? feedUrl, string? channel, string platform, string kind, string currentVersion,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var url = ReleasesUrl(feedUrl, channel);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new UpdateFeedException($"Update feed responded {(int)response.StatusCode} {response.ReasonPhrase} for {url}.");
            body = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateFeedException($"Cannot reach the update feed at {url} (timed out).");
        }
        catch (HttpRequestException e)
        {
            throw new UpdateFeedException($"Cannot reach the update feed at {url} ({e.Message}).");
        }
        return Pick(body, feedUrl, channel, platform, kind, currentVersion);
    }

    private static string FileUrl(string? feedUrl, string? channel, string version, string name)
        => $"{UpdateFeed.BaseUrl(feedUrl)}/{UpdateFeed.NormalizeChannel(channel)}/v/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(name)}";

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
