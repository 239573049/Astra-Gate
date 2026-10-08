using System.Text.Json.Nodes;

namespace Astra.Server.Api;

/// <summary>/api/tokens: gateway tokens with today's and lifetime usage (plan: tokens).</summary>
public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tokens").AddEndpointFilter<AdminApiErrorFilter>();
        // Materialized to List<TokenDto>: an interface-typed root makes the serializer probe the
        // interface graph for metadata (see ServerJsonContext).
        group.MapGet("", async (TokenService tokens, CancellationToken ct) => Results.Ok((await tokens.ListAsync(ct)).ToList()));
        group.MapPost("", async (JsonObject body, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.CreateAsync(body, ct)));
        group.MapPatch("/{id}", async (string id, JsonObject body, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.UpdateAsync(id, body, ct)));
        group.MapPost("/{id}/reset", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.ResetAsync(id, ct)));
        // POST (not GET) so revealing the plaintext requires the X-Astra-Admin header like every mutation.
        group.MapPost("/{id}/reveal", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.RevealAsync(id, ct)));
        group.MapDelete("/{id}", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.DeleteAsync(id, ct)));
    }
}
