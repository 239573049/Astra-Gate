using Astra.Gateway.Http;

namespace Astra.Server.Api;

public static class LocalMcpEndpoints
{
    /// <summary>
    /// MCP servers (stdio or remote) written into clients, and the import of what clients already have (issue #34). Reading works from any admin session (no environment values are
    /// returned); creating, changing or deleting writes client files and stores secrets, so only from this machine.
    /// </summary>
    public static void MapLocalMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcp/local").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("", async (LocalMcpService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        group.MapGet("/import", async (LocalMcpService svc, CancellationToken ct) => Results.Ok(await svc.CandidatesAsync(ct)));
        group.MapPost("/import", async (HttpContext ctx, LocalMcpImportDto body, LocalMcpService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await svc.ImportAsync(body, ct));
        });
        group.MapPost("", async (HttpContext ctx, LocalMcpServerInputDto body, LocalMcpService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await svc.CreateAsync(body, ct));
        });
        group.MapPut("/{id}", async (HttpContext ctx, string id, bool? force, LocalMcpServerInputDto body, LocalMcpService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await svc.UpdateAsync(id, body, force == true, ct));
        });
        group.MapPost("/{id}/apply", async (HttpContext ctx, string id, bool? force, LocalMcpService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await svc.ApplyAsync(id, force == true, ct));
        });
        group.MapDelete("/{id}", async (HttpContext ctx, string id, bool? force, LocalMcpService svc, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            await svc.DeleteAsync(id, force == true, ct);
            return Results.NoContent();
        });
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "Local MCP servers can only be changed on this machine");
    }
}
