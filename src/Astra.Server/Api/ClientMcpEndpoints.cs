using Astra.Gateway.Http;

namespace Astra.Server.Api;

public static class ClientMcpEndpoints
{
    /// <summary>
    /// The MCP gateway entry in Claude Code and Codex (issue #34). Reading the state works from any admin session; writing a
    /// client's file, and the token key that goes into it, only from this machine.
    /// </summary>
    public static void MapClientMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clients").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("/mcp", async (ClientMcpService mcp, CancellationToken ct) => Results.Ok(await mcp.ListAsync(ct)));
        group.MapGet("/{kind}/mcp", async (string kind, ClientMcpService mcp, CancellationToken ct) => Results.Ok(await mcp.StatusAsync(kind, ct)));
        group.MapPost("/{kind}/mcp", async (HttpContext ctx, string kind, ClientMcpApplyDto body, ClientMcpService mcp, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await mcp.ApplyAsync(kind, body, ct));
        });
        group.MapDelete("/{kind}/mcp", async (HttpContext ctx, string kind, bool? force, ClientMcpService mcp, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await mcp.RemoveAsync(kind, force == true, ct));
        });
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "The MCP entry in a client can only be written or removed on this machine");
    }
}
