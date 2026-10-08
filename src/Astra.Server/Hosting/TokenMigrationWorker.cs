using Astra.Core.Tokens;
using Astra.Data;
using Astra.Server.Api;

namespace Astra.Server.Hosting;

/// <summary>
/// One-time follow-up of the 0006 tokens migration: enabled clients still hold their old per-client key, which the
/// gateway no longer accepts, so their configs are rewritten with "&lt;default token&gt;.&lt;kind&gt;". Drifted or unreadable
/// configs are left alone (the client page shows them; re-enabling fixes them). The marker is cleared once the
/// rewrite ran; a failure keeps it so the next start retries.
/// </summary>
public sealed class TokenMigrationWorker(AstraDatabase db, ClientService clients, ILogger<TokenMigrationWorker> logger)
    : BackgroundService
{
    /// <summary>Settings key written by 0006_tokens.sql when enabled clients need their configs rewritten.</summary>
    public const string MarkerKey = "token_migration";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "令牌迁移：重写客户端配置失败，下次启动重试");
        }
    }

    /// <summary>Rewrites the clients when the marker is present; returns the outcome, or null when nothing was pending.</summary>
    public async Task<TokenRewriteResult?> RunAsync(CancellationToken ct)
    {
        if (!(await db.Settings.GetAllAsync(ct)).ContainsKey(MarkerKey)) return null;
        var result = await clients.RewriteForTokenAsync(TokenIds.Default, ct);
        if (result.Rewritten.Count > 0)
            logger.LogInformation("令牌迁移：已改用默认令牌重写客户端配置：{Clients}", string.Join(", ", result.Rewritten));
        if (result.Skipped.Count > 0)
            logger.LogWarning("令牌迁移：以下客户端的配置被用户修改或无法读取，未重写，需要在「客户端」页面重新启用：{Clients}",
                string.Join(", ", result.Skipped));
        await db.Settings.DeleteAsync(MarkerKey, CancellationToken.None);
        return result;
    }
}
