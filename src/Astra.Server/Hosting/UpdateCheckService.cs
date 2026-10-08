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
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{feed.TrimEnd('/')}/{settings.Current.UpdateChannel}/latest.json");
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

    /// <summary>Semver comparison ignoring -prerelease/+build suffixes; unparsable candidates never count as newer.</summary>
    internal static bool IsNewer(string? candidate, string current)
    {
        if (!Version.TryParse(Core(candidate), out var c)) return false;
        if (!Version.TryParse(Core(current), out var cur)) return false;
        return c > cur;
    }

    private static string Core(string? v) => (v ?? "").Trim().Split('-', 2)[0].Split('+', 2)[0];
}
