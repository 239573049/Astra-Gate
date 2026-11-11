using System.Text.Json.Nodes;
using Astra.Gateway.Http;

namespace Astra.Server.Api;

/// <summary>/api/backup/webdav: the cloud copy of the provider backup (<see cref="WebDavBackupService"/>). Everything but the status is for this machine only.</summary>
public static class WebDavBackupEndpoints
{
    public static void MapWebDavBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup/webdav").AddEndpointFilter<AdminApiErrorFilter>();

        group.MapGet("", async (WebDavBackupService dav, CancellationToken ct) => Results.Ok(await dav.StatusAsync(ct)));
        group.MapPut("", async (HttpContext http, JsonObject body, WebDavBackupService dav, CancellationToken ct) =>
        {
            RequireLocal(http);
            return Results.Ok(await dav.SaveAsync(body, ct));
        });
        group.MapDelete("", async (HttpContext http, WebDavBackupService dav, CancellationToken ct) =>
        {
            RequireLocal(http);
            await dav.ForgetAsync(ct);
            return Results.Ok(await dav.StatusAsync(ct));
        });
        group.MapPost("/test", async (HttpContext http, WebDavBackupService dav, CancellationToken ct) =>
        {
            RequireLocal(http);
            return Results.Ok(await dav.TestAsync(ct));
        });
        group.MapPost("/upload", async (HttpContext http, JsonObject body, WebDavBackupService dav, CancellationToken ct) =>
        {
            RequireLocal(http);
            return Results.Ok(await dav.UploadAsync(body, ct));
        });
        group.MapPost("/restore", async (HttpContext http, WebDavBackupService dav, CancellationToken ct) =>
        {
            RequireLocal(http);
            return Results.Ok(await dav.LatestAsync(ct));
        });
    }

    private static void RequireLocal(HttpContext http)
    {
        if (!RequestOrigin.IsLocal(http)) throw new AdminApiException(403, "The WebDAV backup can only be set up and used on this machine");
    }
}
