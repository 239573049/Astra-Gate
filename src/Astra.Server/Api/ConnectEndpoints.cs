namespace Astra.Server.Api;

/// <summary>/api/connect: connect this machine to a unified gateway (a company's Astra server) and send clients through it (<see cref="ConnectService"/>).</summary>
public static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connect").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("/status", async (ConnectService connect, CancellationToken ct) => Results.Ok(await connect.StatusAsync(ct)));
        group.MapPost("/join", async (Astra.Api.Contracts.ConnectJoinRequest body, ConnectService connect, CancellationToken ct) =>
            Results.Ok(await connect.JoinAsync(body, ct)));
        group.MapPost("/apply", async (Astra.Api.Contracts.ConnectApplyRequest body, ConnectService connect, CancellationToken ct) =>
            Results.Ok(await connect.ApplyAsync(body, ct)));
        group.MapPost("/leave", async (ConnectService connect, CancellationToken ct) => Results.Ok(await connect.LeaveAsync(ct)));
    }
}
