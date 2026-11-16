using Astra.Gateway.Http;

namespace Astra.Server.Api;

public static class ResourceLibraryEndpoints
{
    /// <summary>
    /// The rest of the resource library: <c>/api/library/{instructions|agents|commands|hooks|env}</c> and <c>/api/library/projects</c>. Reading works from any
    /// admin session; every write puts files into client directories (or settings), so it is local-origin only.
    /// </summary>
    public static void MapResourceLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var segment in new[] { "instructions", "agents", "commands", "hooks", "env" })
        {
            var kind = ResourceLibraryService.KindOf(segment)!;
            var group = app.MapGroup("/api/library/" + segment).AddEndpointFilter<AdminApiErrorFilter>();
            group.MapGet("", async (HttpContext ctx, ResourceLibraryService svc, CancellationToken ct) =>
                Results.Ok(await svc.StateAsync(kind, RequestOrigin.IsLocal(ctx), ct)));
            group.MapPost("", async (HttpContext ctx, ResourceInputDto body, bool? force, ResourceLibraryService svc, CancellationToken ct) =>
            {
                RequireLocal(ctx);
                return Results.Ok(await svc.CreateAsync(kind, body, force == true, ct));
            });
            group.MapPut("/{id}", async (HttpContext ctx, string id, ResourceInputDto body, bool? force, ResourceLibraryService svc, CancellationToken ct) =>
            {
                RequireLocal(ctx);
                return Results.Ok(await svc.UpdateAsync(kind, id, body, force == true, ct));
            });
            group.MapPost("/{id}/apply", async (HttpContext ctx, string id, bool? force, ResourceLibraryService svc, CancellationToken ct) =>
            {
                RequireLocal(ctx);
                return Results.Ok(await svc.ApplyAsync(kind, id, force == true, ct));
            });
            group.MapDelete("/{id}", async (HttpContext ctx, string id, bool? force, ResourceLibraryService svc, CancellationToken ct) =>
            {
                RequireLocal(ctx);
                await svc.DeleteAsync(kind, id, force == true, ct);
                return Results.NoContent();
            });
        }

        var projects = app.MapGroup("/api/library/projects").AddEndpointFilter<AdminApiErrorFilter>();
        projects.MapGet("", async (ResourceLibraryService svc, CancellationToken ct) => Results.Ok(await svc.ProjectsAsync(ct)));
        projects.MapPost("", async (HttpContext ctx, ProjectInputDto body, ResourceLibraryService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await svc.AddProjectAsync(body, ct));
        });
        projects.MapDelete("", async (HttpContext ctx, string path, ResourceLibraryService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            await svc.RemoveProjectAsync(path, ct);
            return Results.NoContent();
        });
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "The library writes files into client directories, so it can only be changed on this machine");
    }
}
