using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Hosting;

/// <summary>Update feed on the official website (astra-gate.si); overridable (or disabled) via the updateFeedUrl setting.</summary>
public static class UpdateFeed
{
    public const string DefaultUrl = "https://astra-gate.si/api/client-releases";
}

/// <summary>
/// Release channels of the feed (<c>&lt;feed&gt;/&lt;channel&gt;/latest.json</c>). Stable is the default; preview is opt-in and
/// offers x.y.z-preview.N releases plus every stable (the feed merges them), so it never falls behind stable.
/// </summary>
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Preview = "preview";

    /// <summary>stable | preview, or null for anything else; "beta", the old name of preview, reads as preview.</summary>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Stable => Stable,
        Preview or "beta" => Preview,
        _ => null,
    };

    /// <summary>The stored setting as a channel the feed serves (stable when unrecognized).</summary>
    public static string Normalize(string? value) => Parse(value) ?? Stable;
}

/// <summary>latest.json on the feed. camelCase JSON, shared contract with the desktop app and CLI.</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>Oldest version the release notes consider updatable in one hop; informational.</summary>
    public string? MinUpdateable { get; set; }
}

/// <summary>Persisted result of the last update check (settings key "update.state").</summary>
public sealed class UpdateCheckState
{
    /// <summary>Version offered by the feed; null when the current install is up to date.</summary>
    public string? AvailableVersion { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }

    /// <summary>Release notes from the manifest; only set while an update is available.</summary>
    public string? Notes { get; set; }

    /// <summary>Last failure ("feed-not-configured" when the feed was disabled), null on success.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Fetches the update feed manifest and records whether a newer version exists (plan §P1.4).
/// Requests carry only a plain User-Agent — no machine identifier, matching the privacy stance.
/// A failed check keeps the previous state and sets <see cref="UpdateCheckState.Error"/>.
/// </summary>
public sealed class UpdateCheckService(AstraDatabase db, SettingsService settings, IHttpClientFactory factory, ILogger<UpdateCheckService> logger)
{
    public const string HttpClientName = "update-check";
    public const string StateKey = "update.state";

    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>Empty updateFeedUrl setting = disabled; null/whitespace = built-in default feed.</summary>
    public string? EffectiveFeedUrl
    {
        get
        {
            var configured = settings.Current.UpdateFeedUrl;
            return configured is null ? UpdateFeed.DefaultUrl : configured.Trim() switch { "" => null, var v => v };
        }
    }

    /// <summary>Runs one check, serializing concurrent worker/endpoint invocations.</summary>
    public async Task<UpdateCheckState> CheckAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var state = await db.Settings.GetAsync<UpdateCheckState>(StateKey, ct) ?? new UpdateCheckState();
            var feed = EffectiveFeedUrl;
            if (feed is null)
            {
                state.Error = "feed-not-configured";
                state.LastCheckAt = DateTimeOffset.UtcNow;
                await db.Settings.SetAsync(StateKey, state, ct);
                return state;
            }

            try
            {
                var channel = UpdateChannels.Normalize(settings.Current.UpdateChannel);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{feed.TrimEnd('/')}/{channel}/latest.json");
                req.Headers.UserAgent.ParseAdd($"Astra/{ServerOptions.Version}");
                using var resp = await factory.CreateClient(HttpClientName).SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct);
                var manifest = Json.DeserializeApi<UpdateManifest>(JsonNode.Parse(text))
                    ?? throw new InvalidOperationException("Update manifest is empty");
                if (string.IsNullOrWhiteSpace(manifest.Version))
                    throw new InvalidOperationException("Update manifest has no version");
                state.Error = null;
                if (IsNewer(manifest.Version, ServerOptions.Version))
                {
                    state.AvailableVersion = manifest.Version;
                    state.Notes = manifest.Notes;
                }
                else
                {
                    state.AvailableVersion = null;
                    state.Notes = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "更新检查失败（保留上次结果，下轮重试）");
                state.Error = e.Message;
            }

            state.LastCheckAt = DateTimeOffset.UtcNow;
            await db.Settings.SetAsync(StateKey, state, ct);
            return state;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Semver precedence (build metadata ignored): a release is newer than its prereleases
    /// (0.10.0 &gt; 0.10.0-preview.2 &gt; 0.10.0-preview.1), so a preview install moves on to the stable that follows,
    /// and a stable install is never offered an older preview. Unparsable versions never count as newer.
    /// </summary>
    internal static bool IsNewer(string? candidate, string current)
    {
        if (Parse(candidate) is not { } c || Parse(current) is not { } cur) return false;
        if (c.Core != cur.Core) return c.Core > cur.Core;
        if (c.Pre.Length == 0 || cur.Pre.Length == 0) return c.Pre.Length == 0 && cur.Pre.Length > 0;
        for (var i = 0; i < Math.Min(c.Pre.Length, cur.Pre.Length); i++)
        {
            var (a, b) = (c.Pre[i], cur.Pre[i]);
            if (a == b) continue;
            var (na, nb) = (long.TryParse(a, out var x), long.TryParse(b, out var y));
            if (na && nb) return x > y;
            if (na != nb) return nb; // numeric identifiers sort below alphanumeric ones
            return string.CompareOrdinal(a, b) > 0;
        }
        return c.Pre.Length > cur.Pre.Length;
    }

    private static (Version Core, string[] Pre)? Parse(string? v)
    {
        var text = (v ?? "").Trim().TrimStart('v').Split('+', 2)[0];
        var parts = text.Split('-', 2);
        if (!Version.TryParse(parts[0], out var core)) return null;
        // Version treats a missing build/revision as -1; compare x.y.z only.
        core = new Version(core.Major, core.Minor, Math.Max(core.Build, 0));
        return (core, parts.Length > 1 ? parts[1].Split('.') : []);
    }
}
