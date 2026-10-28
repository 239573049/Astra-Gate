using Astra.Gateway.Http;
using Astra.Server.Autostart;

namespace Astra.Server.Api;

/// <summary>/api/server/autostart: starting this server when the user logs in (<see cref="AutostartService"/>).</summary>
public static class AutostartEndpoints
{


    public static void MapAutostartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/server/autostart").AddEndpointFilter<AdminApiErrorFilter>();

        group.MapGet("", async (HttpContext http, AutostartService autostart, CancellationToken ct) =>
            Results.Ok(ToDto(await autostart.StatusAsync(ct), http)));

        // Registering a program to run at login is for this machine only — never through a tunnel or from the LAN.
        group.MapPut("", async (AutostartRequest body, HttpContext http, AutostartService autostart, CancellationToken ct) =>
        {
            if (!RequestOrigin.IsLocal(http)) throw new AdminApiException(403, "Autostart can only be changed on this machine");
            if (autostart.Unsupported is { } why) throw new AdminApiException(409, $"Autostart is not supported here ({why})");
            try
            {
                return Results.Ok(ToDto(await autostart.SetAsync(body.Enabled, ct), http));
            }
            catch (AutostartException ex)
            {
                throw new AdminApiException(500, ex.Message);
            }
        });
    }

    private static AutostartDto ToDto(AutostartState s, HttpContext http) =>
        new(s.Supported, s.Enabled, s.Mechanism, s.Unsupported, s.Location, s.Program, RequestOrigin.IsLocal(http));
}
