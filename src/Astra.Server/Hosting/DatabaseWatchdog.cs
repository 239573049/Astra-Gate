using Astra.Data;

namespace Astra.Server.Hosting;

/// <summary>
/// Reads the database every <see cref="Interval"/> the way a request does. A disk I/O error (SQLITE_IOERR) is logged with
/// both of its codes — the message alone cannot say what failed — and the process's pooled connections are closed, so the
/// next open starts from the file again. In a container whose data volume hiccuped, every worker and request kept failing
/// on the same pooled handles until the container was restarted; this makes the process do what the restart did. Other
/// failures (a busy database, say) are left to the callers: they resolve themselves.
/// </summary>
public sealed class DatabaseWatchdog(AstraDatabase db, ILogger<DatabaseWatchdog> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One round; true when the database answered (possibly after a reset).</summary>
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        var failure = await db.Factory.ProbeAsync(ct);
        if (failure is null || !SqliteFailures.IsIoError(failure)) return true;

        logger.LogError("数据库读取失败，关闭连接池后重试：{Failure}（数据库 {Path}）", SqliteFailures.Describe(failure), db.Factory.DbPath);
        SqliteConnectionFactory.ResetConnections();
        var again = await db.Factory.ProbeAsync(ct);
        if (again is null)
        {
            logger.LogWarning("数据库已恢复：重新打开连接后读取正常");
            return true;
        }
        logger.LogError("数据库仍然读取失败：{Failure}。数据目录所在的磁盘或卷可能不可用，重启容器前请先检查它", SqliteFailures.Describe(again));
        return false;
    }
}
