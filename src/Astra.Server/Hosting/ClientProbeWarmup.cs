using Astra.Server.Api;

namespace Astra.Server.Hosting;

/// <summary>
/// Fills the client version caches right after start (login-shell PATH, <c>npm root -g</c>, every client's
/// <c>--version</c>), so the first <c>GET /api/clients</c> — which the desktop tray and the overview page issue
/// on launch — does not wait on a dozen processes.
/// </summary>
public sealed class ClientProbeWarmup(ClientInstallService installs, ILogger<ClientProbeWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: ExecuteAsync runs synchronously up to its first await.
        await Task.Yield();
        try
        {
            await installs.DescribeAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "客户端版本预热失败");
        }
    }
}
