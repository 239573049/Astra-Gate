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

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<DateTimeOffset>(out var at)
                                || v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out at))
            ? at
            : null;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
