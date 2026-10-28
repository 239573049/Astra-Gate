using Astra.Core.Privacy;

namespace Astra.Api.Contracts;



/// <summary>Request body: dry run.</summary>
public sealed record DryRunRequest(string? Text, string? Json, string? ClientKind);

/// <summary>Admin API payload: privacy event.</summary>
public sealed record PrivacyEventDto(
    string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
    string? RequestedModel, string Status, bool DryRun, bool Blocked, int Redactions, IReadOnlyList<PrivacyHit> Hits);

/// <summary>Admin API payload: privacy category stat.</summary>
public sealed record PrivacyCategoryStatDto(
    string Category, long Requests, long Hits, long WarnHits, long BlockHits, long RedactHits);

/// <summary>Admin API payload: privacy event stats.</summary>
public sealed record PrivacyEventStatsDto(
    long Events, long Blocked, long DryRun, long Redactions, IReadOnlyList<PrivacyCategoryStatDto> Categories);

/// <summary>Admin API payload: privacy rule.</summary>
public sealed record PrivacyRuleDto(string Id, string Category, string Description);

/// <summary>Admin API payload: privacy policy.</summary>
public sealed record PrivacyPolicyDto(PrivacySettings Settings, IReadOnlyList<PrivacyRuleDto> Rules);

/// <summary>Admin API payload: privacy dry run result.</summary>
public sealed record PrivacyDryRunResultDto(
    bool Blocked, IReadOnlyList<PrivacyHit> Hits, int WouldRedact, IReadOnlyList<string> Categories);
