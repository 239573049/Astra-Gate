using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>Usage numbers of one token for the token page (today from the request log, lifetime from the counters).</summary>
public sealed record TokenStatsDto(
    decimal CostUsd, long Requests, long InputTokens, long OutputTokens, long TotalTokens, long CacheReadTokens,
    long CacheWriteTokens, double? CacheHitRate, double? Tps);

/// <summary>A token as shown in the admin API: masked key only, never the plaintext.</summary>
public sealed record TokenDto(
    string Id, string Name, bool IsDefault, bool Enabled, string KeyMasked, string? ProviderId, string? AccountId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt, IReadOnlyList<string> Clients,
    TokenStatsDto Today, TokenStatsDto Total);

public sealed record TokenSecretDto(string Token);

/// <summary>Result of a change that rewrote client configs (reset / delete): the kinds rewritten and the kinds skipped.</summary>
public sealed record TokenMutationResultDto(TokenDto? Token, IReadOnlyList<string> Rewritten, IReadOnlyList<string> Skipped);

/// <summary>
/// Gateway token administration (plan: tokens). Clients write "&lt;token&gt;.&lt;kind&gt;"; resetting a key or deleting a
/// token rewrites the affected client configs through <see cref="ClientService"/> so the restore invariants hold.
/// </summary>
public sealed class TokenService(AstraDatabase db, ISecretProtector secrets, ClientService clients, SettingsService settings)
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
        return tokens.Select(t => ToDto(t,
            clientRows.Where(c => (c.TokenId ?? TokenIds.Default) == t.Id && c.Enabled).Select(c => c.Kind).ToList(),
            today.GetValueOrDefault(t.Id), totals.GetValueOrDefault(t.Id))).ToList();
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
        };
        await ApplyProviderAsync(token, body, ct);
        AssignKey(token, now);
        await db.Tokens.InsertAsync(token, ct);
        return await GetAsync(token.Id, ct);
    }

    /// <summary>
    /// Patch: <c>name</c>, <c>enabled</c>, <c>providerId</c> (null clears direct calls) and <c>accountId</c>
    /// (null / "" = the provider's default account). Never touches client configs.
    /// </summary>
    public async Task<TokenDto> UpdateAsync(string id, JsonObject body, CancellationToken ct)
    {
        var token = await RequireAsync(id, ct);
        if (body.ContainsKey("name")) token.Name = RequireName(ReadString(body, "name"));
        if (body.ContainsKey("enabled"))
            token.Enabled = body["enabled"] is JsonValue v && v.TryGetValue<bool>(out var enabled)
                ? enabled
                : throw new AdminApiException(400, "enabled must be a boolean");
        if (body.ContainsKey("providerId") || body.ContainsKey("accountId")) await ApplyProviderAsync(token, body, ct);
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

    private async Task<TokenRecord> RequireAsync(string id, CancellationToken ct) =>
        await db.Tokens.GetAsync(id, ct) ?? throw new AdminApiException(404, "Token not found");

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new AdminApiException(400, "name is required");
        if (trimmed.Length > MaxNameLength) throw new AdminApiException(400, $"name must be at most {MaxNameLength} characters");
        return trimmed;
    }

    private static string? ReadString(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new AdminApiException(400, $"{name} must be a string"),
    };

    private static TokenDto ToDto(TokenRecord t, IReadOnlyList<string> clientKinds, TokenUsage? today, TokenUsage? total) => new(
        t.Id, t.Name, t.IsDefault, t.Enabled, GatewayTokens.Mask(t.KeyPrefix), t.ProviderId, t.AccountId,
        t.CreatedAt, t.UpdatedAt, t.LastUsedAt, clientKinds, Stats(today), Stats(total));

    private static TokenStatsDto Stats(TokenUsage? u)
    {
        u ??= new TokenUsage();
        return new TokenStatsDto(Money.FromNanos(u.CostNanoUsd), u.Requests, u.InputTokens, u.OutputTokens,
            u.InputTokens + u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CacheHitRate, u.Tps);
    }
}
