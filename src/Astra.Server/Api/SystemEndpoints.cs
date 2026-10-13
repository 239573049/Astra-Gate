using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Astra.Core;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.RateLimiting;
using Astra.Server.Hosting;
using Astra.Server.Security;

namespace Astra.Server.Api;

public static class SystemEndpoints
{
    public const string RuntimeTokenHeader = "X-Astra-Runtime-Token";

    public sealed record LoginRequest(string Password);

    public sealed record SetPasswordRequest(string Password);

    /// <summary>Rate-limit policy (per client IP; tunnel visitors by Cloudflare's address) of the login endpoint.</summary>
    public const string LoginRateLimitPolicy = "astra-login";

    public static void AddLoginRateLimit(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(LoginRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    }

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

        app.MapGet("/api/auth/status", (HttpContext ctx, ServerOptions options, AdminSessions sessions, SettingsService settings) =>
        {
            var required = SecurityMiddleware.SessionRequired(ctx, options);
            var remote = RequestOrigin.IsRemote(ctx);
            return Results.Ok(new AuthStatusDto(
                Required: required,
                SignedIn: !required || sessions.IsSignedIn(ctx),
                Remote: remote,
                AdminBlocked: remote && options.IsLoopback && !settings.Current.Tunnel.AllowAdmin));
        });

        app.MapPost("/api/auth/login", (LoginRequest body, HttpContext ctx, ServerOptions options, AdminSessions sessions) =>
        {
            if (!PasswordHasher.Verify(body.Password, options.AdminPasswordHash))
                return ApiJson.Result(new ErrorOnlyDto("Invalid password"), StatusCodes.Status401Unauthorized);
            sessions.SignIn(ctx);
            return Results.Ok(new SignedInDto(true));
        }).RequireRateLimiting(LoginRateLimitPolicy);

        // Sets the admin password (what `astra-server set-password` does) from this machine only — never through a
        // tunnel. It goes to config.json; a password from ASTRA_ADMIN_PASSWORD still wins on the next start.
        app.MapPost("/api/auth/password", (SetPasswordRequest body, HttpContext ctx, ServerOptions options, AstraPaths paths) =>
        {
            if (RequestOrigin.IsRemote(ctx)) return ApiJson.Result(new ErrorOnlyDto("The admin password can only be set on this machine"), StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(body.Password) || body.Password.Length < 8)
                return ApiJson.Result(new ErrorOnlyDto("Password must be at least 8 characters."), StatusCodes.Status400BadRequest);
            var hash = PasswordHasher.Hash(body.Password);
            // Reload the file rather than saving the running options: flags and the environment must not end up in it.
            var stored = ServerOptions.Load(paths);
            stored.AdminPasswordHash = hash;
            stored.Save(paths);
            options.AdminPasswordHash = hash;
            return Results.Ok(new StatusDto("ok"));
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx, AdminSessions sessions) =>
        {
            sessions.SignOut(ctx);
            return Results.Ok(new SignedInDto(false));
        });
    }
}
