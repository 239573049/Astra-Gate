using System.Net.Http.Headers;
using System.Text.Json;

namespace Astra.Desktop.Core.Update;

/// <summary>One platform's entry of the feed manifest (<c>platforms["osx-arm64"]</c>).</summary>
/// <param name="Server">npm package carrying the self-contained server binary for this platform.</param>
/// <param name="Desktop">npm package carrying the desktop app (npm-track installs); unused by the Avalonia client.</param>
/// <param name="ServerSha256">sha256 (hex) of the server binary inside the server package.</param>
public sealed record UpdatePlatformInfo(string? Server, string? Desktop, string? ServerSha256, long? ServerSize);

/// <summary>
/// The update feed manifest (<c>latest.json</c>), camelCase JSON at <c>&lt;feedUrl&gt;/&lt;channel&gt;/latest.json</c>.
/// Port of packages/update-core/src/manifest.ts; parsed with <see cref="JsonDocument"/> so it stays AOT-safe and tolerant
/// of fields this client does not know.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string? ApiVersion = null,
    string? ReleasedAt = null,
    string? Notes = null,
    string? MinUpdateable = null,
    IReadOnlyDictionary<string, UpdatePlatformInfo>? Platforms = null)
{
    /// <summary>The entry for <paramref name="platformKey"/>, or null.</summary>
    public UpdatePlatformInfo? Platform(string platformKey)
        => Platforms is not null && Platforms.TryGetValue(platformKey, out var p) ? p : null;

    /// <summary>Parses a manifest; throws <see cref="UpdateFeedException"/> for invalid JSON or a missing/blank version.</summary>
    public static UpdateManifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new UpdateFeedException($"Update feed returned invalid JSON ({e.Message}).");
        }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || Str(o, "version") is not { } version || version.Trim() == "")
                throw new UpdateFeedException("Update manifest has no version.");
            Dictionary<string, UpdatePlatformInfo>? platforms = null;
            if (o.TryGetProperty("platforms", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                platforms = [];
                foreach (var p in ps.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    var v = p.Value;
                    platforms[p.Name] = new UpdatePlatformInfo(
                        Str(v, "server"), Str(v, "desktop"), Str(v, "serverSha256"),
                        v.TryGetProperty("serverSize", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) ? n : null);
                }
            }
            return new UpdateManifest(version, Str(o, "apiVersion"), Str(o, "releasedAt"), Str(o, "notes"), Str(o, "minUpdateable"), platforms);
        }
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>The feed is unreachable, answered an error, or returned something that is not a manifest.</summary>
public sealed class UpdateFeedException(string message) : Exception(message);

/// <summary>Feed location and channel rules. Port of manifest.ts (constants, <c>normalizeChannel</c>, <c>fetchManifest</c>).</summary>
public static class UpdateFeed
{
    /// <summary>Built-in feed location; overridable via the server's updateFeedUrl setting.</summary>
    public const string DefaultFeedUrl = "https://astra-gate.si/api/client-releases";

    public const string DefaultChannel = "stable";

    /// <summary>Opt-in channel: x.y.z-preview.N releases plus every stable, which the feed merges in.</summary>
    public const string PreviewChannel = "preview";

    /// <summary>stable | preview; "beta" (the old name of preview) reads as preview, anything else as stable.</summary>
    public static string NormalizeChannel(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v is PreviewChannel or "beta" ? PreviewChannel : DefaultChannel;
    }

    /// <summary>Where installer-track users get a newer app when it cannot replace itself (update.ts <c>DOWNLOAD_PAGE_URL</c>).</summary>
    public const string DownloadPageUrl = "https://astra-gate.si/download";

    /// <summary>The feed location without trailing slashes; the built-in one when <paramref name="feedUrl"/> is blank.</summary>
    public static string BaseUrl(string? feedUrl)
        => (string.IsNullOrWhiteSpace(feedUrl) ? DefaultFeedUrl : feedUrl.Trim()).TrimEnd('/');

    /// <summary><c>&lt;feed&gt;/&lt;channel&gt;/latest.json</c> with trailing slashes of the feed dropped.</summary>
    public static string ManifestUrl(string? feedUrl, string? channel)
        => $"{BaseUrl(feedUrl)}/{NormalizeChannel(channel)}/latest.json";

    /// <summary>
    /// GETs the manifest. <paramref name="http"/> is the injectable HTTP source (tests pass a fake handler);
    /// <paramref name="userAgent"/> identifies the caller, e.g. "Astra/0.13.0". Default timeout 15 s like the TS.
    /// </summary>
    public static async Task<UpdateManifest> FetchManifestAsync(
        HttpClient http, string? feedUrl = null, string? channel = null, string? userAgent = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var url = ManifestUrl(feedUrl, channel);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(userAgent)) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
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
        return UpdateManifest.Parse(body);
    }
}

/// <summary>Outcome of <see cref="ServerUpdateChecker.CheckAsync"/>. <c>Source</c> is "server" or "feed"; <c>Manifest</c> only for "feed".</summary>
public sealed record ServerUpdateCheck(string? AvailableVersion, string? Notes, string Source, string Channel, UpdateManifest? Manifest);

/// <summary>Port of packages/update-core/src/check.ts.</summary>
public static class ServerUpdateChecker
{
    /// <summary>
    /// Prefers the running server's /api/update/status (it caches feed results and works when the feed is unreachable
    /// from this machine); falls back to the feed. <c>Channel</c> is the one that answered.
    /// </summary>
    public static async Task<ServerUpdateCheck> CheckAsync(
        HttpClient http, string currentVersion, string? baseUrl = null, string? feedUrl = null, string? channel = null,
        TimeSpan? timeout = null, string? userAgent = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(baseUrl))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
                using var res = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/update/status", cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
                    var o = doc.RootElement;
                    if (o.ValueKind == JsonValueKind.Object)
                    {
                        return new ServerUpdateCheck(Str(o, "available"), Str(o, "notes"), "server",
                            UpdateFeed.NormalizeChannel(Str(o, "channel") ?? channel), null);
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                // Fall through to the feed (but a cancelled caller stays cancelled).
                ct.ThrowIfCancellationRequested();
            }
        }

        var normalized = UpdateFeed.NormalizeChannel(channel);
        var manifest = await UpdateFeed.FetchManifestAsync(http, feedUrl ?? UpdateFeed.DefaultFeedUrl, normalized, userAgent, timeout, ct);
        return new ServerUpdateCheck(ReleaseVersion.IsNewer(manifest.Version, currentVersion) ? manifest.Version : null, manifest.Notes, "feed", normalized, manifest);
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
