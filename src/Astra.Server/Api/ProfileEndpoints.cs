namespace Astra.Server.Api;

/// <summary>Profiles: named snapshots of the whole client side (<see cref="ProfileService"/>). <c>{id}</c> also accepts a profile's name.</summary>
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("", async (ProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.ListAsync(ct)));
        group.MapGet("/{id}", async (string id, ProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.GetAsync(id, ct)));
        // Saves the clients' current state as a new profile.
        group.MapPost("", async (ProfileInput body, ProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.SaveAsync(body.Name, ct)));
        // Renames and / or overwrites with the current state.
        group.MapPut("/{id}", async (string id, ProfileInput body, ProfileService profiles, CancellationToken ct) =>
            Results.Ok(await profiles.UpdateAsync(id, body, ct)));
        group.MapDelete("/{id}", async (string id, ProfileService profiles, CancellationToken ct) =>
        {
            await profiles.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        group.MapGet("/{id}/preview", async (string id, ProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.PreviewAsync(id, ct)));
        group.MapPost("/{id}/apply", async (string id, ProfileService profiles, CancellationToken ct) => Results.Ok(await profiles.ApplyAsync(id, ct)));
    }
}
