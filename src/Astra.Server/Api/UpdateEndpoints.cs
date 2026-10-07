using Astra.Core;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>Update feed status and manual checks (plan §P1.4). Applies take place out of process (CLI / desktop).</summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, same tier as /api/version: no secrets, safe without an admin session.
        app.MapGet("/api/update/status", async (UpdateCheckService checks, SettingsService settings, AstraDatabase db, CancellationToken ct) =>
        {
            var state = await db.Settings.GetAsync<UpdateCheckState>(UpdateCheckService.StateKey, ct) ?? new UpdateCheckState();
            var s = settings.Current;
            return Results.Ok(new
            {
                current = ServerOptions.Version,
                available = state.AvailableVersion,
                lastCheckAt = state.LastCheckAt,
                notes = state.Notes,
                error = state.Error,
                channel = s.UpdateChannel,
                autoCheck = s.UpdateAutoCheck,
                feedConfigured = checks.EffectiveFeedUrl is not null,
            });
        });

        var g = app.MapGroup("/api/update").AddEndpointFilter<AdminApiErrorFilter>();

        g.MapPost("/check", async (UpdateCheckService checks, CancellationToken ct) => Results.Ok(await checks.CheckAsync(ct)));
    }
}
