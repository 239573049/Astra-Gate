using Astra.Core;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>Update feed status and manual checks (plan §P1.4). Applies take place out of process (CLI / desktop).</summary>
public static class UpdateEndpoints
{
    /// <summary>Payload of /api/update/status and /api/update/check (the UI caches it under one key).</summary>
    public sealed record UpdateStatusDto(
        string Current, string? Available, DateTimeOffset? LastCheckAt, string? Notes, string? Error,
        string Channel, bool AutoCheck, bool FeedConfigured);

    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, same tier as /api/version: no secrets, safe without an admin session.
        app.MapGet("/api/update/status", async (UpdateCheckService checks, SettingsService settings, AstraDatabase db, CancellationToken ct) =>
        {
            var state = await db.Settings.GetAsync<UpdateCheckState>(UpdateCheckService.StateKey, ct) ?? new UpdateCheckState();
            return Results.Ok(Status(state, checks, settings));
        });

        var g = app.MapGroup("/api/update").AddEndpointFilter<AdminApiErrorFilter>();

        // Same shape as /status: the web UI stores the result in the status query cache.
        g.MapPost("/check", async (UpdateCheckService checks, SettingsService settings, CancellationToken ct) =>
            Results.Ok(Status(await checks.CheckAsync(ct), checks, settings)));
    }

    private static UpdateStatusDto Status(UpdateCheckState state, UpdateCheckService checks, SettingsService settings)
    {
        var s = settings.Current;
        return new UpdateStatusDto(
            ServerOptions.Version, state.AvailableVersion, state.LastCheckAt, state.Notes, state.Error,
            s.UpdateChannel, s.UpdateAutoCheck, checks.EffectiveFeedUrl is not null);
    }
}
