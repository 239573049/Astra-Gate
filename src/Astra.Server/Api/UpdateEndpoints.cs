using Astra.Core;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>
/// Update feed status and manual checks (plan §P1.4). Applies take place out of process: the CLI / desktop swap the binary,
/// and a Docker deployment asks its container updater (<see cref="ContainerUpdater"/>, <c>POST /api/update/apply</c>).
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, same tier as /api/version: no secrets, safe without an admin session.
        app.MapGet("/api/update/status", async (UpdateCheckService checks, ContainerUpdater updater, SettingsService settings, AstraDatabase db, CancellationToken ct) =>
        {
            var state = await db.Settings.GetAsync<UpdateCheckState>(UpdateCheckService.StateKey, ct) ?? new UpdateCheckState();
            return Results.Ok(Status(state, checks, updater, settings));
        });

        var g = app.MapGroup("/api/update").AddEndpointFilter<AdminApiErrorFilter>();

        // Same shape as /status: the web UI stores the result in the status query cache.
        g.MapPost("/check", async (UpdateCheckService checks, ContainerUpdater updater, SettingsService settings, CancellationToken ct) =>
            Results.Ok(Status(await checks.CheckAsync(ct), checks, updater, settings)));

        // Docker: ask the container updater to pull the image and recreate this container now. The answer comes before the
        // restart; a refusal or an unreachable updater is the status's applyError, not an HTTP error.
        g.MapPost("/apply", async (UpdateCheckService checks, ContainerUpdater updater, SettingsService settings, CancellationToken ct) =>
        {
            if (!updater.Configured)
                throw new AdminApiException(409, "No container updater is configured (ASTRA_UPDATER_URL); update through the CLI or the desktop app");
            return Results.Ok(Status(await updater.RequestAsync(ct), checks, updater, settings));
        });
    }

    private static UpdateStatusDto Status(UpdateCheckState state, UpdateCheckService checks, ContainerUpdater updater, SettingsService settings)
    {
        var s = settings.Current;
        return new UpdateStatusDto(
            ServerOptions.Version, state.AvailableVersion, state.LastCheckAt, state.Notes, state.Error,
            UpdateChannels.Normalize(s.UpdateChannel), s.UpdateAutoCheck, checks.EffectiveFeedUrl is not null,
            updater.Configured, s.UpdateAutoApply, state.ApplyVersion, state.ApplyRequestedAt, state.ApplyError);
    }
}
