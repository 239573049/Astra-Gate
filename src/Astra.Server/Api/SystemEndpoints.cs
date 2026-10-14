using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

    /// <summary>
    /// The listen address. <paramref name="Host"/> is what this process is bound to and <paramref name="ConfiguredHost"/>
    /// what config.json asks for at the next start (they differ until a restart); with <paramref name="HostFromEnvironment"/>
    /// ASTRA_HOST decides and config.json does not apply. <paramref name="NetworkUrls"/> are the base URLs other machines
    /// can use while listening beyond loopback (one per network interface for a wildcard host).
    /// </summary>
    public sealed record ListenDto(string Host, int Port, string ConfiguredHost, bool HostFromEnvironment, bool PasswordSet, List<string> NetworkUrls);

    public sealed record ListenRequest(string Host);

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
            var required = SecurityMiddleware.SessionRequired(ctx);
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
        // tunnel or from the network. It goes to config.json; a password from ASTRA_ADMIN_PASSWORD still wins on the next start.
        app.MapPost("/api/auth/password", (SetPasswordRequest body, HttpContext ctx, ServerOptions options, AstraPaths paths) =>
        {
            if (!RequestOrigin.IsLocal(ctx)) return ApiJson.Result(new ErrorOnlyDto("The admin password can only be set on this machine"), StatusCodes.Status403Forbidden);
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

        app.MapGet("/api/server/listen", (ServerOptions options, AstraPaths paths) => Results.Ok(Listen(options, paths)));

        // Changes config.json's host — what the next start binds (the CLI, the desktop app and autostart all read it from
        // there). Beyond loopback the server refuses to start without an admin password, so this refuses to save it too.
        app.MapPut("/api/server/listen", (ListenRequest body, HttpContext ctx, ServerOptions options, AstraPaths paths) =>
        {
            if (!RequestOrigin.IsLocal(ctx))
                return ApiJson.Result(new ErrorOnlyDto("The listen address can only be changed on this machine"), StatusCodes.Status403Forbidden);
            if (options.HostFromEnvironment)
                return ApiJson.Result(new ErrorOnlyDto("ASTRA_HOST sets the listen address; change it there"), StatusCodes.Status409Conflict);
            var host = body.Host?.Trim() ?? "";
            if (!ServerOptions.IsValidHost(host))
                return ApiJson.Result(new ErrorOnlyDto("Host must be an IP address, localhost or *"), StatusCodes.Status400BadRequest);
            // Reload the file rather than saving the running options: flags and the environment must not end up in it.
            var stored = ServerOptions.Load(paths);
            stored.Host = host;
            if (!stored.IsLoopback && string.IsNullOrEmpty(options.AdminPasswordHash))
                return ApiJson.Result(new ErrorOnlyDto("Set an admin password before listening beyond this machine"), StatusCodes.Status409Conflict);
            stored.Save(paths);
            return Results.Ok(Listen(options, paths));
        });
    }

    private static ListenDto Listen(ServerOptions options, AstraPaths paths) => new(
        options.Host, options.Port, options.HostFromEnvironment ? options.Host : ServerOptions.Load(paths).Host,
        options.HostFromEnvironment, !string.IsNullOrEmpty(options.AdminPasswordHash), NetworkUrls(options));

    /// <summary>Where other machines reach this server: its own address, or every up interface's IPv4 address for a wildcard host.</summary>
    private static List<string> NetworkUrls(ServerOptions options)
    {
        if (options.IsLoopback) return [];
        if (!options.IsWildcard) return [options.LocalUrl];
        var urls = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    // Skip link-local 169.254/16: a self-assigned address nobody else on the LAN routes to.
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.GetAddressBytes() is [169, 254, ..]) continue;
                    var url = $"http://{ip}:{options.Port}";
                    if (!urls.Contains(url)) urls.Add(url);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return urls;
    }
}
