using Astra.Gateway.Pipeline;

namespace Astra.Server.Hosting;

/// <summary>
/// Periodically polls the update feed (plan §P1.4): 12h interval, doubling after failed checks
/// (cap 48h); with a container updater and <c>UpdateAutoApply</c> a newer version is installed through it (hourly until done). Never affects request handling — every failure is caught and logged.
/// </summary>
public sealed class UpdateCheckWorker(UpdateCheckService checks, ContainerUpdater updater, SettingsService settings, ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Interval;
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
                if (settings.Current.UpdateAutoCheck)
                {
                    var state = await checks.CheckAsync(stoppingToken);
                    interval = state.Error is null ? Interval : Backoff(interval);
                    // Docker with an updater beside it: install the new version without being asked. The image is pushed
                    // after the feed announces it, so until the container is replaced the next round comes within the hour.
                    if (state.Error is null && state.AvailableVersion is not null && updater.Configured && settings.Current.UpdateAutoApply)
                    {
                        if (ContainerUpdater.Due(state, DateTimeOffset.UtcNow)) await updater.RequestAsync(stoppingToken);
                        interval = ContainerUpdater.RetryAfter;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "更新检查后台任务出错");
                interval = Backoff(interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static TimeSpan Backoff(TimeSpan current) =>
        TimeSpan.FromTicks(Math.Min((current + Interval).Ticks, MaxInterval.Ticks));
}
