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
        group.MapPut("/{name}/projects", async (HttpContext ctx, string name, SkillProjectSwitchDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx, "Skills can only be placed into a project on this machine");
            return Results.Ok(await lib.SwitchProjectAsync(name, body, ct));
        });
        group.MapGet("/updates", async (HttpContext ctx, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx, "Checking for updates runs git on this machine");
            return Results.Ok(await lib.CheckUpdatesAsync(ct));
        });
        group.MapPost("/{name}/update", async (HttpContext ctx, string name, bool? force, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx, "Skills can only be updated on this machine");
            return Results.Ok(await lib.UpdateSkillAsync(name, force == true, ct));
        });
        group.MapGet("/router", async (LibraryService lib, CancellationToken ct) => Results.Ok(await lib.RouterAsync(ct)));
        group.MapPut("/router/{client}", async (HttpContext ctx, string client, SkillRouterSwitchDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx, "The skill router can only be changed on this machine");
            return Results.Ok(await lib.SetRouterAsync(client, body.On, ct));
        });
        group.MapGet("/market", async (string? q, LibraryService lib, CancellationToken ct) => Results.Ok(await lib.MarketSearchAsync(q, ct)));
        group.MapGet("/market/source", async (LibraryService lib, CancellationToken ct) => Results.Ok(await lib.MarketSettingsAsync(ct)));
        group.MapPut("/market/source", async (HttpContext ctx, SkillMarketSettingsInputDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx, "The registry address can only be changed on this machine");
            return Results.Ok(await lib.SetMarketSettingsAsync(body, ct));
        });
        group.MapPost("/market/install", async (HttpContext ctx, SkillMarketInstallDto body, LibraryService lib, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await lib.MarketInstallAsync(body, ct));
        });
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

    private static void RequireLocal(HttpContext ctx, string message = "Skills can only be installed on this machine")
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, message);
    }
}
