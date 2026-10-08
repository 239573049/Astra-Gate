using Astra.Data;
using Astra.Server.Api;

namespace Astra.Server.Hosting;

/// <summary>
/// Background balance / quota refresh for API-key providers (cc-switch's "auto query interval"): every
/// minute, enabled providers whose query is turned on and whose last attempt is older than their interval
/// (provider override → global <c>QuotaAutoIntervalMinutes</c>; 0 = off; ×4 after repeated failures) are
/// queried one after another, at most <see cref="MaxPerRound"/> per round. Failures are stored on the
/// snapshot and never change the provider itself.
/// </summary>
public sealed class ProviderQuotaWorker(
    AstraDatabase db,
    ProviderQuotaManager quota,
    ILogger<ProviderQuotaWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    public const int MaxPerRound = 10;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

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
                logger.LogWarning(e, "提供商余额后台查询出错");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round: queries the providers that are due. Returns how many were queried.</summary>
    public async Task<int> RefreshDueAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var due = (await db.Providers.ListAsync(ct)).Where(p => quota.IsDue(p, now))
            .OrderBy(p => p.QuotaCheckedAtUtc ?? DateTimeOffset.MinValue)
            .Take(MaxPerRound)
            .ToList();
        foreach (var provider in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (_, result) = await quota.FetchAsync(provider, ct);
                if (!result.Ok) logger.LogInformation("提供商 {Provider} 余额查询失败：{Error}", provider.Name, result.Error);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "提供商 {Provider} 余额查询出错（下轮重试）", provider.Name);
            }
        }
        return due.Count;
    }
}
