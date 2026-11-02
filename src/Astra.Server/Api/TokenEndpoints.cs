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
        group.MapGet("/{id}", async (string id, TokenService tokens, CancellationToken ct) => Results.Ok(await tokens.GetAsync(id, ct)));
        group.MapPost("", async (JsonObject body, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.CreateAsync(body, ct)));
        group.MapPatch("/{id}", async (string id, JsonObject body, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.UpdateAsync(id, body, ct)));
        group.MapPost("/{id}/reset", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.ResetAsync(id, ct)));
        // POST (not GET) so revealing the plaintext requires the X-Astra-Admin header like every mutation.
        group.MapPost("/{id}/reveal", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.RevealAsync(id, ct)));
        // One-time join links (the desktop's "connect to a unified gateway"): the link is returned once and holds no key.
        group.MapPost("/{id}/invites", async (string id, HttpRequest request, TokenInviteService invites, CancellationToken ct) =>
        {
            // The body is optional ({"ttlMinutes": n}); no body = the default lifetime.
            var body = request.ContentLength != 0 && request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject : null;
            return Results.Ok(await invites.CreateAsync(id, body?["ttlMinutes"] is JsonValue v && v.TryGetValue<int>(out var ttl) ? ttl : null, ct));
        });
        group.MapGet("/{id}/invites", async (string id, TokenInviteService invites, CancellationToken ct) =>
            Results.Ok(await invites.ListAsync(id, ct)));
        group.MapDelete("/{id}/invites/{inviteId}", async (string id, string inviteId, TokenInviteService invites, CancellationToken ct) =>
        {
            await invites.RevokeAsync(id, inviteId, ct);
            return Results.NoContent();
        });
        group.MapDelete("/{id}", async (string id, TokenService tokens, CancellationToken ct) =>
            Results.Ok(await tokens.DeleteAsync(id, ct)));
    }
}
