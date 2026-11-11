using Astra.Gateway.Http;

namespace Astra.Server.Api;

public static class LibraryEndpoints
{
    /// <summary>
    /// The resource library (<c>.plan/resources-library.md</c> §10). Installing a Skill (a path on this machine or a repository, and the
    /// scripts that may be in it) is local-only; looking, migrating, switching a client and giving Skills back work from any admin session.
    /// </summary>
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/library/skills").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("", async (HttpContext ctx, LibraryService lib, CancellationToken ct) =>
            Results.Ok(await lib.StateAsync(RequestOrigin.IsLocal(ctx), ct)));
        group.MapPost("/migrate/preview", async (SkillMigratePreviewRequestDto body, LibraryService lib, CancellationToken ct) =>
            Results.Ok(await lib.MigratePreviewAsync(body.Names, ct)));
        group.MapPost("/migrate", async (SkillMigrateRequestDto body, LibraryService lib, CancellationToken ct) =>
            Results.Ok(await lib.MigrateAsync(body, ct)));
        group.MapPut("/{name}/clients/{client}", async (string name, string client, SkillSwitchDto body, LibraryService lib, CancellationToken ct) =>
            Results.Ok(await lib.SwitchAsync(name, client, body, ct)));
        group.MapPost("/probe", async (HttpContext ctx, SkillProbeRequestDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await lib.ProbeAsync(body, ct));
        });
        group.MapPost("/install", async (HttpContext ctx, SkillInstallRequestDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await lib.InstallAsync(body, ct));
        });
        group.MapPost("/return-all", async (LibraryService lib, CancellationToken ct) => Results.Ok(await lib.ReturnAsync(null, ct)));
        group.MapPost("/{name}/return", async (string name, LibraryService lib, CancellationToken ct) => Results.Ok(await lib.ReturnAsync(name, ct)));
        group.MapPost("/{name}/take-edits", async (string name, LibraryService lib, CancellationToken ct) =>
        {
            await lib.TakeEditsAsync(name, ct);
            return Results.NoContent();
        });
        group.MapPost("/{name}/overwrite-copy", async (string name, LibraryService lib, CancellationToken ct) =>
        {
            await lib.OverwriteCopyAsync(name, ct);
            return Results.NoContent();
        });
        group.MapDelete("/{name}", async (string name, LibraryService lib, CancellationToken ct) =>
        {
            await lib.DeleteAsync(name, ct);
            return Results.NoContent();
        });
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "Skills can only be installed on this machine");
    }
}
