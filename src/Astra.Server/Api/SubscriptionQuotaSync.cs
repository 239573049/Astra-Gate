using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;

namespace Astra.Server.Api;

/// <summary>
/// Background quota refresh of subscription accounts (plan: alerts-webhooks-token-limits §4), driven by
/// <see cref="Hosting.ProviderQuotaWorker"/>. An account is due when its provider is an enabled OAuth subscription with a
/// quota probe, the account is enabled and active, and its last attempt (the snapshot's <c>fetchedAtUtc</c> or the last
/// failure) is older than <c>AppSettings.SubscriptionQuotaIntervalMinutes</c> (×4 after repeated failures). Failures are
/// kept under <c>extra.quotaSync</c> (<c>{ checkedAtUtc, failures, error }</c>) next to the last good snapshot, which they
/// never replace; a success clears them. The alert engine reads both.
/// <para>
/// An account cooling down after a 429 is looked at sooner: <see cref="RecheckCooledAsync"/> re-queries it every
/// <see cref="CooledRecheck"/> and lifts the cooldown as soon as its snapshot shows room again (a reset credit was spent,
/// the window rolled over early), instead of waiting for the reset time the 429 named.
/// </para>
/// </summary>
public sealed class SubscriptionQuotaSync(
    AstraDatabase db,
    SubscriptionQuotaService quota,
    SettingsService settings,
    ILogger<SubscriptionQuotaSync> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Lower bound of the interval: the upstream usage endpoints rate-limit.</summary>
    public const int MinIntervalMinutes = 10;

    /// <summary>Key in <see cref="ProviderAccount.Extra"/> holding the failure state.</summary>
    public const string SyncKey = "quotaSync";

    /// <summary>How often a cooling account is re-queried to see whether it can be used again.</summary>
    public static readonly TimeSpan CooledRecheck = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The effective interval in minutes; 0 = off.</summary>
    public int IntervalMinutes => settings.Current.SubscriptionQuotaIntervalMinutes is var m && m <= 0 ? 0 : Math.Max(m, MinIntervalMinutes);

    public static int Failures(ProviderAccount account) =>
        account.Extra[SyncKey]?["failures"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    public bool IsDue(Provider provider, ProviderAccount account, DateTimeOffset now)
    {
        var interval = IntervalMinutes;
        if (interval <= 0 || !provider.Enabled || provider.AuthScheme != AuthSchemes.OAuthSubscription) return false;
        if (!account.Enabled || account.Status != AccountStatus.Active || !SubscriptionQuotaService.CanProbe(provider, account)) return false;
        if (Failures(account) >= ProviderQuotaManager.BackoffAfterFailures) interval *= ProviderQuotaManager.BackoffFactor;
        var last = Max(Time(account.Extra["quota"]?["fetchedAtUtc"]), Time(account.Extra[SyncKey]?["checkedAtUtc"]));
        return last is null || now - last >= TimeSpan.FromMinutes(interval);
    }

    /// <summary>
    /// A subscription account that is cooling down and whose last quota attempt is older than <see cref="CooledRecheck"/>
    /// (×<see cref="ProviderQuotaManager.BackoffFactor"/> after repeated failures). Off together with the background
    /// refresh (<see cref="IntervalMinutes"/> = 0).
    /// </summary>
    public bool IsCooledDue(Provider provider, ProviderAccount account, DateTimeOffset now)
    {
        if (IntervalMinutes <= 0 || !provider.Enabled || provider.AuthScheme != AuthSchemes.OAuthSubscription) return false;
        if (!account.Enabled || account.Status != AccountStatus.Active || !SubscriptionQuotaService.CanProbe(provider, account)) return false;
        if (!(account.CooldownUntilUtc > now) || IsAuthCooldown(account)) return false;
        var wait = Failures(account) >= ProviderQuotaManager.BackoffAfterFailures ? CooledRecheck * ProviderQuotaManager.BackoffFactor : CooledRecheck;
        var last = Max(Time(account.Extra["quota"]?["fetchedAtUtc"]), Time(account.Extra[SyncKey]?["checkedAtUtc"]));
        return last is null || now - last >= wait;
    }

    /// <summary>
    /// True when the stored snapshot shows at least one usage window and none of them is used up. A snapshot without
    /// any percentage says nothing, so it never lifts a cooldown.
    /// </summary>
    public static bool HasHeadroom(ProviderAccount account)
    {
        var any = false;
        foreach (var key in new[] { "session", "weekly", "credits" })
        {
            if (account.Extra["quota"]?[key]?["usedPercent"] is not JsonValue v || !v.TryGetValue<double>(out var used)) continue;
            if (used >= 100) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Lifts the cooldown of <paramref name="accountId"/> when it is still cooling and its freshly stored snapshot has
    /// room (<see cref="HasHeadroom"/>). An auth cooldown cannot be recovered by quota headroom. If the upstream still
    /// rate-limits, the next 429 simply cools it again. True when lifted.
    /// </summary>
    public async Task<bool> ReleaseIfRecoveredAsync(string accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.GetAsync(accountId, ct);
        if (account is null || !(account.CooldownUntilUtc > _clock.GetUtcNow()) || IsAuthCooldown(account) || !HasHeadroom(account)) return false;
        await db.Accounts.SetCooldownAsync(accountId, null, null, ct);
        logger.LogInformation("订阅账号 {Account} 的额度已恢复，提前解除限流冷却", account.DisplayName);
        return true;
    }

    /// <summary>
    /// Re-queries at most <paramref name="max"/> cooling accounts that are due (<see cref="IsCooledDue"/>), least recently
    /// checked first, and lifts the cooldown of those with room again. Returns how many were queried.
    /// </summary>
    public async Task<int> RecheckCooledAsync(int max, CancellationToken ct)
    {
        if (max <= 0 || IntervalMinutes <= 0) return 0;
        var now = _clock.GetUtcNow();
        var providers = (await db.Providers.ListAsync(ct)).ToDictionary(p => p.Id);
        var due = (await db.Accounts.ListAsync(ct: ct))
            .Where(a => providers.TryGetValue(a.ProviderId, out var p) && IsCooledDue(p, a, now))
            .OrderBy(a => Max(Time(a.Extra["quota"]?["fetchedAtUtc"]), Time(a.Extra[SyncKey]?["checkedAtUtc"])) ?? DateTimeOffset.MinValue)
            .Take(max)
            .ToList();
        foreach (var account in due)
        {
            ct.ThrowIfCancellationRequested();
            if (await RefreshAsync(providers[account.ProviderId], account, ct))
                await ReleaseIfRecoveredAsync(account.Id, CancellationToken.None);
        }
        return due.Count;
    }

    /// <summary>Queries at most <paramref name="max"/> due accounts, least recently checked first. Returns how many were queried.</summary>
    public async Task<int> RefreshDueAsync(int max, CancellationToken ct)
    {
        if (max <= 0 || IntervalMinutes <= 0) return 0;
        var now = _clock.GetUtcNow();
        var providers = (await db.Providers.ListAsync(ct)).ToDictionary(p => p.Id);
        var due = (await db.Accounts.ListAsync(ct: ct))
            .Where(a => providers.TryGetValue(a.ProviderId, out var p) && IsDue(p, a, now))
            .OrderBy(a => Max(Time(a.Extra["quota"]?["fetchedAtUtc"]), Time(a.Extra[SyncKey]?["checkedAtUtc"])) ?? DateTimeOffset.MinValue)
            .Take(max)
            .ToList();
        foreach (var account in due)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshAsync(providers[account.ProviderId], account, ct);
        }
        return due.Count;
    }

    /// <summary>One query with its bookkeeping; true on success.</summary>
    public async Task<bool> RefreshAsync(Provider provider, ProviderAccount account, CancellationToken ct)
    {
        string? error;
        try
        {
            await quota.FetchAsync(provider, account, SubscriptionSupport.EffectiveConfig(provider), ct);
            error = null;
        }
        catch (Exception e) when (e is SubscriptionAuthException or OAuthProtocolException or HttpRequestException
                                       || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            error = e.Message;
            logger.LogInformation("订阅账号 {Account}（{Provider}）额度查询失败：{Error}", account.DisplayName, provider.Name, error);
        }

        // Re-read: the fetch (and a token refresh it may have caused) wrote the account meanwhile.
        var current = await db.Accounts.GetAsync(account.Id, CancellationToken.None);
        if (current is null) return error is null;
        if (error is null)
        {
            if (current.Extra.Remove(SyncKey)) await db.Accounts.UpdateAsync(current, CancellationToken.None);
            return true;
        }
        current.Extra[SyncKey] = new JsonObject
        {
            ["checkedAtUtc"] = _clock.GetUtcNow().ToString("o"),
            ["failures"] = Failures(current) + 1,
            ["error"] = error,
        };
        await db.Accounts.UpdateAsync(current, CancellationToken.None);
        return false;
    }

    // Auth rejection reasons start with their HTTP status (GatewayPipeline.SendAsync). A successful usage query
    // authenticates a different endpoint and cannot prove that the inference model/channel accepts this account.
    private static bool IsAuthCooldown(ProviderAccount account) =>
        account.LastError is { } reason
        && (reason.StartsWith("401 ", StringComparison.Ordinal) || reason.StartsWith("403 ", StringComparison.Ordinal));

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<DateTimeOffset>(out var at)
                                || v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out at))
            ? at
            : null;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
