using Astra.Gateway.Pipeline;

namespace Astra.Server.Hosting;

/// <summary>
/// Periodically polls the update feed (plan §P1.4): 12h interval, doubling after failed checks
/// (cap 48h). Never affects request handling — every failure is caught and logged.
/// </summary>
public sealed class UpdateCheckWorker(UpdateCheckService checks, SettingsService settings, ILogger<UpdateCheckWorker> logger) : BackgroundService
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
