using Astra.Gateway.Http;
using Astra.Server.Autostart;

namespace Astra.Server.Api;

/// <summary>/api/server/autostart: starting this server when the user logs in (<see cref="AutostartService"/>).</summary>
public static class AutostartEndpoints
{
    /// <param name="Supported">False in a container, on Linux without systemd, or where Astra has no login entry.</param>
    /// <param name="Enabled">An entry starts the server at login (written here or by <c>astra autostart enable</c>).</param>
    /// <param name="Mechanism">launchd | systemd | registry | none.</param>
    /// <param name="Unsupported">container | no-systemd | platform; null when supported.</param>
    /// <param name="Location">Where the entry lives (plist, unit file, Run key value, or the CLI's old scheduled task).</param>
    /// <param name="Program">The program the entry starts, when it can be read back.</param>
    /// <param name="CanChange">The caller is on this machine: only it may add or remove the entry.</param>
    public sealed record AutostartDto(
        bool Supported, bool Enabled, string Mechanism, string? Unsupported, string? Location, string? Program, bool CanChange);

    public sealed record AutostartRequest(bool Enabled);

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
