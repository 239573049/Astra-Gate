using System.Text.Json.Nodes;
using Astra.Gateway.Http;

namespace Astra.Server.Api;

public static class ProviderBackupEndpoints
{
    /// <summary>
    /// Mapped onto the <c>/api/providers</c> group like <see cref="ImportEndpoints"/>, so it shares the group's error filter and
    /// write lock. A backup with API keys is only made on this machine; reading a file back works from any admin session.
    /// </summary>
    public static void MapBackupEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/backup/export", async (HttpContext ctx, JsonObject body, ProviderBackupService backup, CancellationToken ct) =>
            Results.Ok(await backup.ExportAsync(body, RequestOrigin.IsLocal(ctx), ct)));
        group.MapPost("/backup/preview", async (JsonObject body, ProviderBackupService backup, CancellationToken ct) =>
            Results.Ok(await backup.PreviewAsync(body, ct)));
        group.MapPost("/backup/import", async (JsonObject body, ProviderBackupService backup, CancellationToken ct) =>
            Results.Ok(await backup.ImportAsync(body, ct)));
    }
}
