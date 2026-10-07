using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;

namespace Astra.Server.Hosting;

/// <summary>
/// Refreshes subscription access tokens before they expire (plan §5.4): every interval, active
/// accounts within the horizon are refreshed; failures mark the account expired/revoked so the
/// UI (and the tray) can ask for a re-login. Permanently revoking never blocks startup or requests.
/// </summary>
public sealed class SubscriptionRefreshWorker(
    AstraDatabase db,
    SubscriptionTokenService tokens,
    ILogger<SubscriptionRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Refresh accounts expiring within this window (lead + horizon, keeps a safety margin).</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(10) + SubscriptionTokenService.RefreshLead;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "订阅令牌后台刷新出错");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshDueAsync(CancellationToken ct)
    {
        var due = await db.Accounts.ListExpiringAsync(Horizon, ct);
        foreach (var account in due)
        {
            ct.ThrowIfCancellationRequested();
            var provider = await db.Providers.GetAsync(account.ProviderId, ct);
            if (provider is null) continue;
            try
            {
                await tokens.RefreshAsync(account, SubscriptionSupport.EffectiveConfig(provider), force: false, ct);
                logger.LogInformation("订阅账号 {Account} 的令牌已提前刷新", account.DisplayName);
            }
            catch (SubscriptionAuthException e)
            {
                logger.LogWarning("订阅账号 {Account} 已失效，需要重新登录：{Message}", account.DisplayName, e.Message);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "订阅账号 {Account} 刷新失败（保持现状，下轮重试）", account.DisplayName);
            }
        }
    }
}
