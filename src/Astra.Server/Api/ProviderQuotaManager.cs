using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Quota;

namespace Astra.Server.Api;

/// <summary>A provider's balance / quota query settings as the admin API shows them (secrets: names only).</summary>
public sealed record ProviderQuotaConfigDto(
    bool Enabled, string? Template, string? SuggestedTemplate, string? EffectiveTemplate,
    int? IntervalMinutes, int EffectiveIntervalMinutes, int TimeoutSec, string? BaseUrl,
    Dictionary<string, string> Params, List<string> Secrets, JsonObject? Request, JsonObject? Extract);

/// <summary>GET/POST <c>/api/providers/{id}/quota</c>: the settings plus the stored snapshot.</summary>
public sealed record ProviderQuotaDto(ProviderQuotaConfigDto Config, JsonObject? Snapshot, DateTimeOffset? CheckedAtUtc);

/// <summary>POST <c>/api/providers/{id}/quota/test</c>: one unsaved run (redacted URL and raw response included).</summary>
public sealed record ProviderQuotaTestDto(
    bool Ok, string? ErrorCode, string? Error, int? HttpStatus, string? Url, JsonNode? Raw, JsonObject? Snapshot, string? Template);

/// <summary>400 for an unusable quota configuration: <c>{ error, details: { errorCode } }</c> (the admin error envelope, typed).</summary>
public sealed record ProviderQuotaErrorDto(string Error, ProviderQuotaErrorDetailsDto Details);

public sealed record ProviderQuotaErrorDetailsDto(string? ErrorCode);

/// <summary>
/// Glue between the admin API / background worker and <see cref="ProviderQuotaService"/>: decrypts the
/// provider's API key and secret parameters, persists snapshots (keeping the last good one when a query
/// fails), and decides when a provider is due for a background refresh. Never touches provider state
/// other than the quota columns and <c>settings.quota</c>.
/// </summary>
public sealed class ProviderQuotaManager(
    AstraDatabase db,
    ISecretProtector protector,
    ProviderQuotaService service,
    QuotaTemplateCatalog catalog,
    SettingsService settings,
    TimeProvider? timeProvider = null)
{
    /// <summary>After this many consecutive failures the background interval is multiplied by <see cref="BackoffFactor"/>.</summary>
    public const int BackoffAfterFailures = 3;
    public const int BackoffFactor = 4;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Queries the upstream and stores the outcome. A failure keeps the last good snapshot and records the error.</summary>
    public async Task<(Provider Provider, QuotaQueryResult Result)> FetchAsync(Provider provider, CancellationToken ct = default)
    {
        var config = QuotaConfig.From(provider.Settings);
        var result = await service.QueryAsync(new QuotaQuery(provider, config, ApiKey(provider), Decrypt(config.Secrets)), ct);
        var now = _clock.GetUtcNow();
        var snapshot = Merge(provider.Quota, result, now);
        await db.Providers.UpdateQuotaAsync(provider.Id, snapshot, now, ct);
        provider.Quota = snapshot;
        provider.QuotaCheckedAtUtc = now;
        return (provider, result);
    }

    /// <summary>Runs an unsaved configuration once; nothing is persisted.</summary>
    public Task<QuotaQueryResult> TestAsync(Provider provider, QuotaConfig config, CancellationToken ct = default) =>
        service.QueryAsync(new QuotaQuery(provider, config, ApiKey(provider), Decrypt(config.Secrets)), ct);

    /// <summary>
    /// Builds a config from the admin API body (camelCase), on top of the stored one: secrets present in the
    /// body are encrypted (an empty string removes one), secrets absent from it are kept.
    /// </summary>
    /// <exception cref="AdminApiException">400 for an invalid body.</exception>
    public QuotaConfig FromInput(JsonObject body, QuotaConfig current)
    {
        var secrets = new Dictionary<string, string>(current.Secrets, StringComparer.Ordinal);
        if (body["secrets"] is JsonObject incoming)
        {
            foreach (var (name, value) in incoming)
            {
                var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
                if (text is null) continue;
                if (text.Length == 0) secrets.Remove(name);
                else secrets[name] = protector.Protect(text);
            }
        }
        else if (body.ContainsKey("secrets") && body["secrets"] is not null)
        {
            throw new AdminApiException(400, "secrets must be an object");
        }

        var node = new JsonObject
        {
            ["enabled"] = body.ContainsKey("enabled") ? body["enabled"]?.DeepClone() : current.Enabled,
            ["template"] = body.ContainsKey("template") ? body["template"]?.DeepClone() : current.Template,
            ["interval_minutes"] = body.ContainsKey("intervalMinutes") ? body["intervalMinutes"]?.DeepClone() : current.IntervalMinutes,
            ["timeout_sec"] = body.ContainsKey("timeoutSec") ? body["timeoutSec"]?.DeepClone() : current.TimeoutSec,
            ["base_url"] = body.ContainsKey("baseUrl") ? body["baseUrl"]?.DeepClone() : current.BaseUrl,
            ["params"] = body.ContainsKey("params") ? body["params"]?.DeepClone() : current.ToNode()["params"]?.DeepClone(),
            ["request"] = body.ContainsKey("request") ? body["request"]?.DeepClone() : current.ToNode()["request"]?.DeepClone(),
            ["extract"] = body.ContainsKey("extract") ? body["extract"]?.DeepClone() : current.Extract?.DeepClone(),
        };
        if (node["request"] is not null and not JsonObject) throw new AdminApiException(400, "request must be an object");
        if (node["extract"] is not null and not JsonObject) throw new AdminApiException(400, "extract must be an object");
        if (node["params"] is not null and not JsonObject) throw new AdminApiException(400, "params must be an object");
        var config = QuotaConfig.From(new JsonObject { [QuotaConfig.SettingsKey] = node }) with { Secrets = secrets };
        var errors = config.Validate(catalog);
        if (errors.Count > 0) throw new AdminApiException(400, string.Join("; ", errors));
        return config;
    }

    /// <summary>Background interval in minutes for this provider (0 = never in the background), including backoff.</summary>
    public int EffectiveIntervalMinutes(Provider provider)
    {
        var config = QuotaConfig.From(provider.Settings);
        var interval = config.IntervalMinutes ?? Math.Clamp(settings.Current.QuotaAutoIntervalMinutes, 0, QuotaConfig.MaxIntervalMinutes);
        return interval;
    }

    /// <summary>True when the worker should query this provider now.</summary>
    public bool IsDue(Provider provider, DateTimeOffset now)
    {
        if (!provider.Enabled || provider.AuthScheme == AuthSchemes.OAuthSubscription) return false;
        if (!QuotaConfig.From(provider.Settings).Enabled) return false;
        var interval = EffectiveIntervalMinutes(provider);
        if (interval <= 0) return false;
        if (Failures(provider.Quota) >= BackoffAfterFailures) interval *= BackoffFactor;
        return provider.QuotaCheckedAtUtc is not { } last || now - last >= TimeSpan.FromMinutes(interval);
    }

    public ProviderQuotaConfigDto ConfigDto(Provider provider)
    {
        var config = QuotaConfig.From(provider.Settings);
        JsonObject? request = null;
        if (config.Request is { } r)
        {
            request = new JsonObject { ["method"] = r.Method, ["url"] = r.Url, ["auth"] = r.Auth };
            var headers = new JsonObject();
            foreach (var (name, value) in r.Headers) headers[name] = value;
            request["headers"] = headers;
        }
        return new ProviderQuotaConfigDto(
            config.Enabled, config.Template, catalog.Suggest(provider)?.Id, service.Effective(provider, config)?.Id,
            config.IntervalMinutes, EffectiveIntervalMinutes(provider), config.TimeoutSec, config.BaseUrl,
            config.Params, [.. config.Secrets.Keys.Order(StringComparer.Ordinal)], request, config.Extract?.DeepClone().AsObject());
    }

    public ProviderQuotaDto Dto(Provider provider) => new(ConfigDto(provider), provider.Quota, provider.QuotaCheckedAtUtc);

    public static ProviderQuotaTestDto TestDto(QuotaQueryResult r) =>
        new(r.Ok, r.ErrorCode, r.Error, r.HttpStatus, r.Url, r.Raw, r.Snapshot, r.Template);

    /// <summary>Provider settings as the admin API shows them: <c>quota</c> is managed by its own endpoints.</summary>
    public static JsonObject PublicSettings(JsonObject settings)
    {
        var copy = settings.DeepClone().AsObject();
        copy.Remove(QuotaConfig.SettingsKey);
        return copy;
    }

    /// <summary>The snapshot to store: a fresh one on success; otherwise the previous one plus the error.</summary>
    public static JsonObject Merge(JsonObject? previous, QuotaQueryResult result, DateTimeOffset now)
    {
        if (result.Ok && result.Snapshot is { } fresh) return fresh.DeepClone().AsObject();
        var node = previous?.DeepClone().AsObject() ?? new JsonObject();
        node["error"] = result.Error;
        node["errorCode"] = result.ErrorCode;
        node["errorAtUtc"] = now.ToString("o");
        node["failures"] = Failures(previous) + 1;
        return node;
    }

    private static int Failures(JsonObject? snapshot) =>
        snapshot?["failures"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private string? ApiKey(Provider provider)
    {
        if (provider.ApiKeyEnc is null) return null;
        try
        {
            return protector.Unprotect(provider.ApiKeyEnc);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private Dictionary<string, string> Decrypt(Dictionary<string, string> encrypted)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in encrypted)
        {
            try
            {
                result[name] = protector.Unprotect(value);
            }
            catch (CryptographicException)
            {
                // A key ring change makes the value unreadable: treat it as not set (the query reports the missing parameter).
            }
        }
        return result;
    }
}
