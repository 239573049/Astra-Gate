using System.Net;
using System.Security.Cryptography;
using System.Text;
using Astra.Server.Hosting;
using Astra.Server.Security;

namespace Astra.Server.Api;

public static class SystemEndpoints
{
    public const string RuntimeTokenHeader = "X-Astra-Runtime-Token";

    public sealed record LoginRequest(string Password);

    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new StatusDto("ok")));

        app.MapGet("/api/version", () => Results.Ok(new VersionDto(ServerOptions.Version, ServerOptions.ApiVersion)));

        app.MapPost("/api/admin/shutdown", (HttpContext ctx, RuntimeFile runtime, IHostApplicationLifetime lifetime) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote is not null && !IPAddress.IsLoopback(remote)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var expected = runtime.Current?.RuntimeToken;
            var provided = ctx.Request.Headers[RuntimeTokenHeader].ToString();
            if (expected is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            ctx.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Ok(new StatusDto("stopping"));
        });

        app.MapGet("/api/auth/status", (HttpContext ctx, ServerOptions options, AdminSessions sessions) => Results.Ok(new AuthStatusDto(
            Required: !options.IsLoopback,
            SignedIn: options.IsLoopback || sessions.IsSignedIn(ctx))));

        app.MapPost("/api/auth/login", (LoginRequest body, HttpContext ctx, ServerOptions options, AdminSessions sessions) =>
        {
            if (!PasswordHasher.Verify(body.Password, options.AdminPasswordHash))
                return ApiJson.Result(new ErrorOnlyDto("Invalid password"), StatusCodes.Status401Unauthorized);
            sessions.SignIn(ctx);
            return Results.Ok(new SignedInDto(true));
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx, AdminSessions sessions) =>
        {
            sessions.SignOut(ctx);
            return Results.Ok(new SignedInDto(false));
        });
    }
}
