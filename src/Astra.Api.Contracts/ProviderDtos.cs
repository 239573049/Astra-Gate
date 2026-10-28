using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Core;

namespace Astra.Api.Contracts;

/// <summary>Admin API payload: provider.</summary>
public sealed record ProviderDto(
    string Id, string? TemplateId, int? TemplateVersion, bool TemplateUpdateAvailable, string Name, string? Icon,
    string Category, List<ProviderEndpoint> Endpoints, List<ApiProtocol> PreferredUpstreamProtocols, string AuthScheme,
    bool HasApiKey, string? ApiKeyMasked, Dictionary<string, string> ExtraHeaders, string? HttpProxy, decimal PriceMultiplier,
    string? PriceKey, string? AdapterId, JsonObject Settings, bool Enabled, int SortOrder, string? Notes, string? Website,
    int ModelCount, IReadOnlyList<string> BoundClients, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ProviderQuotaConfigDto QuotaConfig, JsonObject? Quota, DateTimeOffset? QuotaCheckedAtUtc);

/// <summary>Admin API payload: model overrides.</summary>
public sealed record ModelOverridesDto(string? DisplayName, long? ContextWindow, long? MaxOutputTokens, ModelCapabilities? Capabilities, JsonNode? Pricing);

/// <summary>Admin API payload: effective model.</summary>
public sealed record EffectiveModelDto(string DisplayName, long? ContextWindow, long? MaxOutputTokens, ModelCapabilities Capabilities);

/// <summary>Usage priced by the request's saved model billing result; cache and reasoning are already included in the token totals.</summary>
public sealed record ProviderUsageTotalsDto(long Requests, long InputTokens, long OutputTokens, long CostNanoUsd)
{
    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>One account's usage; null AccountId represents a provider's own API key.</summary>
public sealed record ProviderAccountUsageDto(string? AccountId, ProviderUsageTotalsDto Total, ProviderUsageTotalsDto Today);

/// <summary>All current provider credentials, with the calendar day and time zone used for today's usage.</summary>
public sealed record ProviderUsageDto(string Day, string TimeZone, List<ProviderAccountUsageDto> Accounts);

/// <summary>Diff between the provider's snapshot template version and the shipped one.</summary>
public sealed record TemplateUpdateDto(int CurrentVersion, int LatestVersion, List<string> Changes);

/// <summary>Admin API payload: provider model.</summary>
public sealed record ProviderModelDto(long Id, string ProviderId, string ModelId, string? SystemModelId, bool Enabled, int SortOrder,
    ModelOverridesDto Overrides, EffectiveModelDto Effective, Dictionary<string, string> Origins, string PricingSource,
    string? PriceKey, decimal Multiplier, JsonNode? EffectivePricing);

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

/// <summary>Admin API payload: provider quota error details.</summary>
public sealed record ProviderQuotaErrorDetailsDto(string? ErrorCode);

/// <summary>Admin API payload: import endpoint.</summary>
public sealed record ImportEndpointDto(ApiProtocol Protocol, string BaseUrl);

/// <summary>Admin API payload: import existing.</summary>
public sealed record ImportExistingDto(string Id, string Name);

/// <summary>A candidate as the UI sees it. Never carries the API key: only a mask and a short fingerprint.</summary>
public sealed record ImportCandidateDto(
    string Ref, string Source, string Name, string? FromApp, List<ImportEndpointDto> Endpoints, string AuthScheme,
    bool HasKey, string? KeyMasked, string? KeyFingerprint, List<string> Models, List<string> Headers, string? TemplateId,
    string Status, ImportExistingDto? Existing, bool Off, string? SkipReason, List<string> IgnoredFields);

/// <summary>Admin API payload: import source.</summary>
public sealed record ImportSourceDto(string Id, string Name, string? Path, bool Found, string? Error, List<ImportCandidateDto> Items);

/// <summary>Admin API payload: import selection.</summary>
public sealed record ImportSelectionDto(string Source, string Ref);

/// <summary>Admin API payload: imported provider.</summary>
public sealed record ImportedProviderDto(string Id, string Name);

/// <summary>Admin API payload: import skipped.</summary>
public sealed record ImportSkippedDto(string Ref, string Reason);

/// <summary>Admin API payload: import result.</summary>
public sealed record ImportResultDto(List<ImportedProviderDto> Added, List<ImportSkippedDto> Skipped);

/// <summary>Admin API payload: remote model.</summary>
public sealed record RemoteModelDto(string Id, string? DisplayName, string? LinkedSystemModelId, bool AlreadyAdded,
    IReadOnlyList<ApiProtocol>? UpstreamProtocols = null);

/// <summary>Everything one test run needs, after validation. Every field is optional on the wire.</summary>
public sealed record ProviderTestOptions(string? ModelId, ApiProtocol? Protocol, bool Stream, string? Prompt, int MaxOutputTokens);

/// <summary>One event of a provider test stream, ready to be written as SSE.</summary>
public sealed record ProviderTestEvent(string Event, JsonNode Payload);

/// <summary>
/// <c>GET /api/provider-quota/templates</c> item: a built-in balance / quota query (one request plus a declarative extractor). Mirrors
/// <c>Astra.Providers.Quota.QuotaTemplate</c> field for field, so the wire JSON is unchanged.
/// </summary>
/// <param name="Id">Template id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind"><c>balance</c> (money left) or <c>plan</c> (usage windows of a coding plan).</param>
/// <param name="AppliesTo">Provider template ids this query is the default for.</param>
/// <param name="Hosts">Upstream hosts this query is the default for.</param>
/// <param name="Params">Extra values the query needs.</param>
/// <param name="Request">How the request is sent.</param>
/// <param name="Extract">The declarative extractor.</param>
public sealed record QuotaTemplateDto(
    string Id, string Name, string Kind, List<string> AppliesTo, List<string> Hosts, List<QuotaParamSpecDto> Params,
    QuotaRequestSpecDto Request, JsonObject Extract);

/// <summary>An extra value a quota template needs (e.g. New API's user id); secret values are stored encrypted.</summary>
/// <param name="Name">Parameter name (<c>{{name}}</c> in the request).</param>
/// <param name="Secret">Stored encrypted and never sent back.</param>
/// <param name="Required">The query fails without it.</param>
public sealed record QuotaParamSpecDto(string Name, bool Secret, bool Required);

/// <summary>How a quota request is sent; <c>{{name}}</c> placeholders are filled from the query variables.</summary>
/// <param name="Method"><c>GET</c> or <c>POST</c>.</param>
/// <param name="Url">Absolute URL template.</param>
/// <param name="Auth"><c>provider</c> (the provider's auth and key) or <c>none</c>.</param>
/// <param name="Headers">Extra headers.</param>
public sealed record QuotaRequestSpecDto(string Method, string Url, string Auth, Dictionary<string, string> Headers);
