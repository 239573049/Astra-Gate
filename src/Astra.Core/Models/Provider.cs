using System.Text.Json.Nodes;

namespace Astra.Core.Models;

public sealed class ProviderEndpoint
{
    public ApiProtocol Protocol { get; set; }
    public string BaseUrl { get; set; } = "";

    /// <summary>When true, <see cref="BaseUrl"/> is the complete request URL (no path is appended).</summary>
    public bool FullUrl { get; set; }
}

public static class AuthSchemes
{
    public const string Bearer = "bearer";
    public const string XApiKey = "x-api-key";
    public const string XGoogApiKey = "x-goog-api-key";
    public const string QueryKey = "query-key";
    public const string None = "none";

    /// <summary>Upstream auth comes from an OAuth subscription account (plan §5.4); see Astra.Providers/Subscription.</summary>
    public const string OAuthSubscription = "oauth-subscription";

    public static readonly IReadOnlyList<string> All =
        [Bearer, XApiKey, XGoogApiKey, QueryKey, None, OAuthSubscription];
}

/// <summary>A configured AI provider instance.</summary>
public sealed class Provider
{
    public string Id { get; set; } = "";
    public string? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    public string? TemplateSnapshotJson { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string Category { get; set; } = "custom";
    public List<ProviderEndpoint> Endpoints { get; set; } = [];
    public List<ApiProtocol> PreferredUpstreamProtocols { get; set; } = [];
    public string AuthScheme { get; set; } = AuthSchemes.Bearer;
    public string? ApiKeyEnc { get; set; }
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    public string? HttpProxy { get; set; }

    /// <summary>Scales inherited (system) prices for this provider instance.</summary>
    public decimal PriceMultiplier { get; set; } = 1m;

    /// <summary>Provider-scoped price table: null inherits the template id; empty selects only the official default price.</summary>
    public string? PriceKey { get; set; }

    public string? AdapterId { get; set; }

    /// <summary>Adapter / behavior settings (e.g. auto_cache_control).</summary>
    public JsonObject Settings { get; set; } = new();

    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public string? Website { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Last balance / quota snapshot (column <c>quota_json</c>); written only through
    /// <c>ProviderRepository.UpdateQuotaAsync</c>, never by the full-row update.
    /// </summary>
    public JsonObject? Quota { get; set; }

    /// <summary>Last balance / quota query attempt, successful or not (column <c>quota_checked_at_utc</c>).</summary>
    public DateTimeOffset? QuotaCheckedAtUtc { get; set; }

    public ProviderEndpoint? EndpointFor(ApiProtocol protocol) => Endpoints.FirstOrDefault(e => e.Protocol == protocol);

    public bool SettingFlag(string key, bool defaultValue) =>
        Settings.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : defaultValue;
}

/// <summary>A model offered by a provider. Only <see cref="ModelId"/> is required; everything else inherits.</summary>
public sealed class ProviderModel
{
    public long Id { get; set; }
    public string ProviderId { get; set; } = "";

    /// <summary>Upstream model id sent to the provider.</summary>
    public string ModelId { get; set; } = "";

    /// <summary>Linked system model (auto-matched or chosen manually).</summary>
    public string? SystemModelId { get; set; }

    public ModelOverrides Overrides { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}
