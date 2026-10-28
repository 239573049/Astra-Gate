using Astra.Core.Tokens;

namespace Astra.Api.Contracts;

/// <summary>Usage numbers of one token for the token page (today from the request log, lifetime from the counters).</summary>
public sealed record TokenStatsDto(
    decimal CostUsd, long Requests, long InputTokens, long OutputTokens, long TotalTokens, long CacheReadTokens,
    long CacheWriteTokens, double? CacheHitRate, double? Tps);

/// <summary>One budget of a token as the admin API takes and shows it: a cost limit in USD, counts as they are.</summary>
public sealed record TokenBudgetDto(string Metric, string Period, decimal Limit);

/// <summary>A token's usage caps (<see cref="TokenLimits"/>) in the admin API.</summary>
public sealed record TokenLimitsDto(List<TokenBudgetDto> Budgets, int? Rpm);

/// <summary>Where a token stands against one budget (cost amounts in USD).</summary>
public sealed record TokenBudgetUsageDto(
    string Metric, string Period, decimal Limit, decimal Used, decimal Remaining, double UsedPercent, bool Exhausted,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Whether a pool member can take requests now: <c>healthy</c>, <c>cooling</c> (rate limited / credentials rejected),
/// <c>open</c> / <c>half_open</c> (circuit after repeated failures), <c>no_account</c> (an account-based provider without a
/// usable account), <c>disabled</c> or <c>missing</c> (the provider was deleted).
/// </summary>
public sealed record PoolMemberHealthDto(string State, DateTimeOffset? Until, string? Reason);

/// <summary>One provider of a token's pool in the admin API.</summary>
public sealed record TokenPoolMemberDto(string ProviderId, string? AccountId, int Weight, int Tier, bool Missing, PoolMemberHealthDto Health);

/// <summary>A token's provider pool (<see cref="TokenPool"/>, issue #2) in the admin API.</summary>
public sealed record TokenPoolDto(string Strategy, string Sticky, bool Failover, int MaxAttempts, List<TokenPoolMemberDto> Members);

/// <summary>The <c>pool</c> field of a create / patch body; omitted fields take the pool defaults.</summary>
public sealed record TokenPoolMemberInputDto(string? ProviderId, string? AccountId, int? Weight, int? Tier);

/// <summary>Admin API payload: token pool input.</summary>
public sealed record TokenPoolInputDto(string? Strategy, string? Sticky, bool? Failover, int? MaxAttempts, List<TokenPoolMemberInputDto>? Members);

/// <summary>A token as shown in the admin API: masked key only, never the plaintext.</summary>
/// <param name="AllowedClients">Client kinds the key may be used with; null = any, empty = direct calls only.</param>
/// <param name="RecentRequests">Requests let through during the last minute (what the RPM cap counts).</param>
/// <param name="Pool">The provider pool of direct calls; null = <paramref name="ProviderId"/> alone (or no direct calls).</param>
public sealed record TokenDto(
    string Id, string Name, bool IsDefault, bool Enabled, string KeyMasked, string? ProviderId, string? AccountId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt, IReadOnlyList<string> Clients,
    TokenStatsDto Today, TokenStatsDto Total, TokenLimitsDto? Limits, List<string>? AllowedClients,
    List<TokenBudgetUsageDto> LimitUsage, int RecentRequests, TokenPoolDto? Pool);

/// <summary>Admin API payload: token secret.</summary>
public sealed record TokenSecretDto(string Token);

/// <summary>Result of a change that rewrote client configs (reset / delete): the kinds rewritten and the kinds skipped.</summary>
public sealed record TokenMutationResultDto(TokenDto? Token, IReadOnlyList<string> Rewritten, IReadOnlyList<string> Skipped);
