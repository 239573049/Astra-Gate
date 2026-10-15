using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

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

public sealed record TokenSecretDto(string Token);

/// <summary>Result of a change that rewrote client configs (reset / delete): the kinds rewritten and the kinds skipped.</summary>
public sealed record TokenMutationResultDto(TokenDto? Token, IReadOnlyList<string> Rewritten, IReadOnlyList<string> Skipped);

/// <summary>
/// Gateway token administration (plan: tokens). Clients write "&lt;token&gt;.&lt;kind&gt;"; resetting a key or deleting a
/// token rewrites the affected client configs through <see cref="ClientService"/> so the restore invariants hold.
/// </summary>
public sealed class TokenService(
    AstraDatabase db, ISecretProtector secrets, ClientService clients, SettingsService settings, TokenUsageLedger ledger,
    ProviderHealth health, ProviderPoolScheduler poolScheduler)
{
    private const int MaxNameLength = 64;

    /// <summary>
    /// Makes sure the default token exists and has a key (the 0006 migration inserts it without one). Runs at startup;
    /// only touches the database.
    /// </summary>
    public async Task EnsureDefaultAsync(CancellationToken ct = default)
    {
        var token = await db.Tokens.GetAsync(TokenIds.Default, ct);
        if (token is { KeyEnc: not null }) return;
        var now = DateTimeOffset.UtcNow;
        var name = settings.Current.Locale == "en" ? "Default token" : "默认令牌";
        if (token is null)
        {
            token = new TokenRecord { Id = TokenIds.Default, Name = name, IsDefault = true, Enabled = true, CreatedAt = now };
            AssignKey(token, now);
            await db.Tokens.InsertAsync(token, ct);
            return;
        }
        token.Name = name;
        AssignKey(token, now);
        await db.Tokens.UpdateAsync(token, ct);
    }

    public async Task<IReadOnlyList<TokenDto>> ListAsync(CancellationToken ct = default)
    {
        var tokens = await db.Tokens.ListAsync(ct);
        var (_, from, to) = RequestEndpoints.Range("today");
        var today = (await db.Tokens.UsageAsync(from, to, ct)).ToDictionary(u => u.TokenId);
        var totals = (await db.Tokens.ListTotalsAsync(ct)).ToDictionary(u => u.TokenId);
        var clientRows = await db.Clients.ListAsync(ct);
        var members = new PoolMemberLookup(db, health);
        var list = new List<TokenDto>(tokens.Count);
        foreach (var t in tokens)
        {
            var limits = t.Limits();
            var usage = limits is null ? [] : await ledger.StatusAsync(t.Id, limits, ct);
            var pool = t.Pool() is { } p ? await members.DtoAsync(p, ct) : null;
            list.Add(ToDto(t,
                clientRows.Where(c => (c.TokenId ?? TokenIds.Default) == t.Id && c.Enabled).Select(c => c.Kind).ToList(),
                today.GetValueOrDefault(t.Id), totals.GetValueOrDefault(t.Id), limits, usage, ledger.RecentRequests(t.Id), pool));
        }
        return list;
    }

    public async Task<TokenDto> GetAsync(string id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(t => t.Id == id) ?? throw new AdminApiException(404, "Token not found");

    public async Task<TokenDto> CreateAsync(JsonObject body, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new TokenRecord
        {
            Id = Ulid.NewUlid(),
            Name = RequireName(ReadString(body, "name")),
            Enabled = true,
            CreatedAt = now,
            LimitsJson = body.ContainsKey("limits") ? ReadLimits(body["limits"]) : null,
            AllowedClientsJson = body.ContainsKey("allowedClients") ? ReadAllowedClients(body["allowedClients"]) : null,
        };
        RejectPoolWithProvider(body);
        if (body.ContainsKey("pool")) await ApplyPoolAsync(token, body["pool"], ct);
        else await ApplyProviderAsync(token, body, ct);
        AssignKey(token, now);
        await db.Tokens.InsertAsync(token, ct);
        return await GetAsync(token.Id, ct);
    }

    /// <summary>
    /// Patch: <c>name</c>, <c>enabled</c>, <c>providerId</c> (null clears direct calls), <c>accountId</c>
    /// (null / "" = the provider's default account), <c>pool</c> (<see cref="TokenPoolInputDto"/>; replaces
    /// providerId / accountId, which a patch may not send alongside it — a providerId patch without it drops the pool),
    /// <c>limits</c> (<see cref="TokenLimitsDto"/>; null = unlimited) and <c>allowedClients</c> (client kinds; null = any,
    /// [] = direct calls only). Never touches client configs.
    /// </summary>
    public async Task<TokenDto> UpdateAsync(string id, JsonObject body, CancellationToken ct)
    {
        var token = await RequireAsync(id, ct);
        if (body.ContainsKey("name")) token.Name = RequireName(ReadString(body, "name"));
        if (body.ContainsKey("enabled"))
            token.Enabled = body["enabled"] is JsonValue v && v.TryGetValue<bool>(out var enabled)
                ? enabled
                : throw new AdminApiException(400, "enabled must be a boolean");
        RejectPoolWithProvider(body);
        if (body.ContainsKey("pool")) await ApplyPoolAsync(token, body["pool"], ct);
        else if (body.ContainsKey("providerId") || body.ContainsKey("accountId"))
        {
            await ApplyProviderAsync(token, body, ct);
            token.PoolJson = null;
            poolScheduler.Forget(token.Id);
        }
        if (body.ContainsKey("limits")) token.LimitsJson = ReadLimits(body["limits"]);
        if (body.ContainsKey("allowedClients")) token.AllowedClientsJson = ReadAllowedClients(body["allowedClients"]);
        token.UpdatedAt = DateTimeOffset.UtcNow;
        await db.Tokens.UpdateAsync(token, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Generates a new key; every enabled client using the token gets its config rewritten.</summary>
    public async Task<TokenMutationResultDto> ResetAsync(string id, CancellationToken ct)
    {
        var token = await RequireAsync(id, ct);
        AssignKey(token, DateTimeOffset.UtcNow);
        await db.Tokens.UpdateAsync(token, ct);
        var result = await clients.RewriteForTokenAsync(id, CancellationToken.None);
        return new TokenMutationResultDto(await GetAsync(id, CancellationToken.None), result.Rewritten, result.Skipped);
    }

    /// <summary>
    /// Deletes a token (never the default one). Its clients move to the default token and get their configs rewritten;
    /// the request log keeps the token id and name snapshot.
    /// </summary>
    public async Task<TokenMutationResultDto> DeleteAsync(string id, CancellationToken ct)
    {
        var token = await RequireAsync(id, ct);
        if (token.IsDefault) throw new AdminApiException(409, "The default token cannot be deleted");
        var moved = await db.Clients.ReassignTokenAsync(id, TokenIds.Default, ct);
        await db.Tokens.DeleteAsync(id, CancellationToken.None);
        ledger.Forget(id);
        poolScheduler.Forget(id);
        var result = moved.Count == 0
            ? new TokenRewriteResult([], [])
            : await clients.RewriteForTokenAsync(TokenIds.Default, CancellationToken.None);
        return new TokenMutationResultDto(null,
            result.Rewritten.Where(moved.Contains).ToList(), result.Skipped.Where(moved.Contains).ToList());
    }

    /// <summary>The plaintext token (bare, for direct calls).</summary>
    public async Task<TokenSecretDto> RevealAsync(string id, CancellationToken ct)
    {
        var token = await RequireAsync(id, ct);
        if (token.KeyEnc is null) throw new AdminApiException(409, "Token has no key yet");
        return new TokenSecretDto(secrets.Unprotect(token.KeyEnc));
    }

    private void AssignKey(TokenRecord token, DateTimeOffset now)
    {
        var key = GatewayTokens.Generate();
        token.KeyEnc = secrets.Protect(key);
        token.KeyHash = GatewayTokens.Hash(key);
        token.KeyPrefix = GatewayTokens.PrefixOf(key);
        token.UpdatedAt = now;
    }

    private async Task ApplyProviderAsync(TokenRecord token, JsonObject body, CancellationToken ct)
    {
        var providerId = body.ContainsKey("providerId") ? ReadString(body, "providerId") : token.ProviderId;
        var accountId = body.ContainsKey("accountId") ? ReadString(body, "accountId") : token.AccountId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            token.ProviderId = null;
            token.AccountId = null;
            return;
        }
        // A provider change drops a pin that was not restated for the new provider.
        if (providerId != token.ProviderId && !body.ContainsKey("accountId")) accountId = null;
        await clients.RequireProviderAsync(providerId, ct);
        token.ProviderId = providerId;
        token.AccountId = string.IsNullOrWhiteSpace(accountId) ? null : await clients.RequireAccountAsync(providerId, accountId, ct);
    }

    private static void RejectPoolWithProvider(JsonObject body)
    {
        if (body.ContainsKey("pool") && (body.ContainsKey("providerId") || body.ContainsKey("accountId")))
            throw new AdminApiException(400, "Send either pool or providerId / accountId, not both");
    }

    /// <summary>
    /// The <c>pool</c> body field (issue #2). Stored canonically: no members clears direct calls; one member is just
    /// <c>provider_id</c> / <c>account_id</c>; two or more are <c>pool_json</c>, with the first member also written to
    /// <c>provider_id</c> / <c>account_id</c> so a binary without pools still routes. A disabled provider may stay in a
    /// pool it is already in; a new member must be enabled.
    /// </summary>
    private async Task ApplyPoolAsync(TokenRecord token, JsonNode? node, CancellationToken ct)
    {
        TokenPoolInputDto? dto;
        try
        {
            dto = node is null ? null : Json.DeserializeApi<TokenPoolInputDto>(node);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new AdminApiException(400, "pool must be { strategy, sticky, failover, maxAttempts, members: [{ providerId, accountId, weight, tier }] }");
        }
        var pool = new TokenPool
        {
            Strategy = dto?.Strategy ?? TokenPoolStrategies.Failover,
            Sticky = dto?.Sticky ?? TokenPoolSticky.Session,
            Failover = dto?.Failover ?? true,
            MaxAttempts = dto?.MaxAttempts ?? TokenPool.DefaultMaxAttempts,
            Members = (dto?.Members ?? []).Select(m => new TokenPoolMember
            {
                ProviderId = m.ProviderId?.Trim() ?? "",
                AccountId = string.IsNullOrWhiteSpace(m.AccountId) ? null : m.AccountId,
                Weight = m.Weight ?? 1,
                Tier = m.Tier ?? 1,
            }).ToList(),
        };
        var errors = pool.Validate();
        if (errors.Count > 0) throw new AdminApiException(400, string.Join("; ", errors));

        var current = token.Pool()?.Members.Select(m => m.ProviderId).ToHashSet() ?? [];
        if (token.ProviderId is not null) current.Add(token.ProviderId);
        foreach (var member in pool.Members)
        {
            if (!current.Contains(member.ProviderId)) await clients.RequireProviderAsync(member.ProviderId, ct);
            else if (await db.Providers.GetAsync(member.ProviderId, ct) is null) throw new AdminApiException(404, "Provider not found");
            if (member.AccountId is not null) member.AccountId = await clients.RequireAccountAsync(member.ProviderId, member.AccountId, ct);
        }

        var first = pool.Members.FirstOrDefault();
        token.ProviderId = first?.ProviderId;
        token.AccountId = first?.AccountId;
        token.PoolJson = pool.Members.Count > 1 ? Json.Serialize(pool) : null;
        poolScheduler.Forget(token.Id);
    }

    private async Task<TokenRecord> RequireAsync(string id, CancellationToken ct) =>
        await db.Tokens.GetAsync(id, ct) ?? throw new AdminApiException(404, "Token not found");

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new AdminApiException(400, "name is required");
        if (trimmed.Length > MaxNameLength) throw new AdminApiException(400, $"name must be at most {MaxNameLength} characters");
        return trimmed;
    }

    /// <summary>The <c>limits</c> body field as the stored JSON; null for null or no caps at all.</summary>
    private static string? ReadLimits(JsonNode? node)
    {
        if (node is null) return null;
        TokenLimitsDto? dto;
        try
        {
            dto = Json.DeserializeApi<TokenLimitsDto>(node);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new AdminApiException(400, "limits must be { budgets: [{ metric, period, limit }], rpm }");
        }
        if (dto is null) return null;
        var limits = new TokenLimits
        {
            Budgets = (dto.Budgets ?? []).Select(b => new TokenBudget
            {
                Metric = b.Metric,
                Period = b.Period,
                Limit = b.Metric == TokenLimitMetrics.Cost ? Money.ToNanos(b.Limit) : (long)decimal.Truncate(b.Limit),
            }).ToList(),
            Rpm = dto.Rpm,
        };
        var errors = limits.Validate();
        if (errors.Count > 0) throw new AdminApiException(400, string.Join("; ", errors));
        return limits.IsEmpty ? null : Json.Serialize(limits);
    }

    /// <summary>The <c>allowedClients</c> body field as the stored JSON; null = any client.</summary>
    private static string? ReadAllowedClients(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonArray array) throw new AdminApiException(400, "allowedClients must be an array of client kinds or null");
        var kinds = new List<string>();
        foreach (var item in array)
        {
            var kind = item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (kind is null || !ClientKinds.IsKnown(kind)) throw new AdminApiException(400, $"unknown client kind {item}");
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        return Json.Serialize(kinds);
    }

    private static string? ReadString(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new AdminApiException(400, $"{name} must be a string"),
    };

    private static TokenDto ToDto(TokenRecord t, IReadOnlyList<string> clientKinds, TokenUsage? today, TokenUsage? total,
        TokenLimits? limits, IReadOnlyList<TokenBudgetStatus> usage, int recentRequests, TokenPoolDto? pool) => new(
        t.Id, t.Name, t.IsDefault, t.Enabled, GatewayTokens.Mask(t.KeyPrefix), t.ProviderId, t.AccountId,
        t.CreatedAt, t.UpdatedAt, t.LastUsedAt, clientKinds, Stats(today), Stats(total),
        limits is null ? null : new TokenLimitsDto(limits.Budgets.Select(b => new TokenBudgetDto(b.Metric, b.Period, Amount(b.Metric, b.Limit))).ToList(), limits.Rpm),
        t.AllowedClients(),
        usage.Select(s => new TokenBudgetUsageDto(s.Budget.Metric, s.Budget.Period, Amount(s.Budget.Metric, s.Budget.Limit),
            Amount(s.Budget.Metric, s.Used), Amount(s.Budget.Metric, s.Remaining), s.UsedPercent, s.Exhausted, s.ResetsAt)).ToList(),
        recentRequests, pool);

    private static decimal Amount(string metric, long value) => metric == TokenLimitMetrics.Cost ? Money.FromNanos(value) : value;

    private static TokenStatsDto Stats(TokenUsage? u)
    {
        u ??= new TokenUsage();
        return new TokenStatsDto(Money.FromNanos(u.CostNanoUsd), u.Requests, u.InputTokens, u.OutputTokens,
            u.InputTokens + u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CacheHitRate, u.Tps);
    }
}

/// <summary>
/// The pool members of the token list with their health, loading each provider and each account list once per listing.
/// The state mirrors what pool routing decides (<see cref="PoolRouting"/>): API-key providers from <see cref="ProviderHealth"/>,
/// account-based ones from their accounts.
/// </summary>
internal sealed class PoolMemberLookup(AstraDatabase db, ProviderHealth health)
{
    private readonly Dictionary<string, Provider?> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ProviderAccount>> _accounts = new(StringComparer.Ordinal);

    public async Task<TokenPoolDto> DtoAsync(TokenPool pool, CancellationToken ct)
    {
        var members = new List<TokenPoolMemberDto>(pool.Members.Count);
        foreach (var m in pool.Members)
        {
            var provider = await ProviderAsync(m.ProviderId, ct);
            members.Add(new TokenPoolMemberDto(m.ProviderId, m.AccountId, m.Weight, m.Tier, provider is null, await HealthAsync(provider, m.AccountId, ct)));
        }
        return new TokenPoolDto(pool.Strategy, pool.Sticky, pool.Failover, pool.MaxAttempts, members);
    }

    private async Task<Provider?> ProviderAsync(string id, CancellationToken ct)
    {
        if (!_providers.TryGetValue(id, out var provider)) _providers[id] = provider = await db.Providers.GetAsync(id, ct);
        return provider;
    }

    private async Task<PoolMemberHealthDto> HealthAsync(Provider? provider, string? pinned, CancellationToken ct)
    {
        if (provider is null) return new PoolMemberHealthDto("missing", null, null);
        if (!ClientRouting.IsUsable(provider)) return new PoolMemberHealthDto("disabled", null, null);
        if (!AuthSchemes.UsesAccounts(provider.AuthScheme))
        {
            var status = health.Status(provider.Id);
            return new PoolMemberHealthDto(status.State, status.Until, status.Reason);
        }
        if (!_accounts.TryGetValue(provider.Id, out var accounts)) _accounts[provider.Id] = accounts = await db.Accounts.ListAsync(provider.Id, ct);
        var now = DateTimeOffset.UtcNow;
        var relevant = pinned is null ? accounts : accounts.Where(a => a.Id == pinned).ToList();
        if (relevant.Any(a => SubscriptionSupport.IsUsable(a, now))) return new PoolMemberHealthDto(ProviderHealthStates.Healthy, null, null);
        // Only cooling accounts left: usable again when the first one's cooldown ends.
        var cooling = relevant.Where(a => a.Enabled && a.Status == AccountStatus.Active && a.CooldownUntilUtc > now)
            .Select(a => a.CooldownUntilUtc).Min();
        return cooling is { } until
            ? new PoolMemberHealthDto(ProviderHealthStates.Cooling, until, "rate_limited")
            : new PoolMemberHealthDto("no_account", null, null);
    }
}
