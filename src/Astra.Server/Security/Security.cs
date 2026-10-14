using System.Net;
using System.Security.Cryptography;
using System.Text;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Astra.Server.Hosting;
using Microsoft.AspNetCore.DataProtection;

namespace Astra.Server.Security;

/// <summary>PBKDF2 password hashing: "pbkdf2-sha256$iterations$salt$hash" (base64).</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Cookie session for the admin UI when listening on a non-loopback address.</summary>
public sealed class AdminSessions(IDataProtectionProvider dp)
{
    public const string CookieName = "astra_session";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly ITimeLimitedDataProtector _protector = dp.CreateProtector("Astra.AdminSession.v1").ToTimeLimitedDataProtector();

    public void SignIn(HttpContext ctx)
    {
        var token = _protector.Protect("admin", Lifetime);
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            MaxAge = Lifetime,
        });
    }

    public void SignOut(HttpContext ctx) => ctx.Response.Cookies.Delete(CookieName);

    public bool IsSignedIn(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var token)) return false;
        try
        {
            return _protector.Unprotect(token) == "admin";
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Request guard (plan §12):
/// - loopback mode: Host header must be 127.0.0.1 / localhost / [::1] (DNS-rebinding defense), or the public hostname of
///   the Cloudflare Tunnel Astra runs (<see cref="Tunnel.TunnelService"/>);
/// - every mutating /api call needs "X-Astra-Admin: 1" (forces a CORS preflight from foreign origins);
/// - non-loopback mode (e.g. 0.0.0.0 for the LAN): a request is from this machine only when its peer is loopback, its
///   Host is a loopback name (a rebound domain is not) and no proxy forwarded it; everything else is from the network
///   (<see cref="RequestOrigin.MarkNetwork"/>), and /api wants an admin session cookie from it (except health/version/auth).
/// Requests that came through Cloudflare (Cf-* headers, or the tunnel's hostname) are remote
/// (<see cref="RequestOrigin"/>): the gateway wants a token from them, /api wants a session too, and in loopback mode
/// they reach only the gateway unless the tunnel settings allow the admin UI. In loopback mode the connection is
/// local, so Cloudflare's own headers are trusted for the client address and scheme (rate limits, Secure cookies).
/// Gateway endpoints (/v1, /v1beta) authenticate with gateway tokens elsewhere; network requests always need one.
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, ServerOptions options, AdminSessions sessions,
    Tunnel.TunnelService tunnel, SettingsService settings)
{
    public const string AdminHeader = "X-Astra-Admin";
    public const string DesktopOrigin = "app://astra";

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]", "::1" };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        var tunnelHost = tunnel.PublicHost is { } h && string.Equals(host, h, StringComparison.OrdinalIgnoreCase);
        if (options.IsLoopback && !LoopbackHosts.Contains(host) && !tunnelHost)
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await ctx.Response.WriteAsync("Host not allowed");
            return;
        }

        var remote = tunnelHost || ViaCloudflare(ctx.Request);
        if (remote)
        {
            RequestOrigin.MarkRemote(ctx);
            if (options.IsLoopback) AdoptCloudflareHeaders(ctx);
        }
        else if (!options.IsLoopback && !FromThisMachine(ctx, host))
        {
            RequestOrigin.MarkNetwork(ctx);
        }

        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") && !HttpMethods.IsOptions(ctx.Request.Method))
        {
            // Remote access is gateway-only unless the admin UI is let through: the page itself can still learn that
            // (auth/status) and say so instead of failing every call.
            if (remote && options.IsLoopback && !settings.Current.Tunnel.AllowAdmin && !IsRemoteProbe(path))
            {
                await Problem(ctx, StatusCodes.Status403Forbidden, "Remote admin access is disabled");
                return;
            }

            var mutating = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method));
            if (mutating && ctx.Request.Headers[AdminHeader] != "1")
            {
                await Problem(ctx, StatusCodes.Status403Forbidden, $"Missing {AdminHeader} header");
                return;
            }

            var open = path.StartsWithSegments("/api/health") || path.StartsWithSegments("/api/version")
                       || path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/admin/shutdown")
                       || path.StartsWithSegments("/api/update/status");
            if (SessionRequired(ctx) && !open && !sessions.IsSignedIn(ctx))
            {
                await Problem(ctx, StatusCodes.Status401Unauthorized, "Login required");
                return;
            }
        }

        await next(ctx);
    }

    /// <summary>Whether /api wants a signed-in session from this request: whenever it is not from this machine.</summary>
    public static bool SessionRequired(HttpContext ctx) => !RequestOrigin.IsLocal(ctx);

    /// <summary>
    /// Headers a proxy adds. X-Original-For is what ASP.NET's own forwarded-headers handling
    /// (ASPNETCORE_FORWARDEDHEADERS_ENABLED) leaves after replacing the peer address with X-Forwarded-For's — which
    /// anyone can send, so a peer of 127.0.0.1 that came that way proves nothing.
    /// </summary>
    private static readonly string[] ProxyHeaders = ["X-Forwarded-For", "X-Forwarded-Host", "Forwarded", "X-Real-IP", "X-Original-For"];

    /// <summary>
    /// Off-loopback, what still counts as this machine: a loopback peer asking for a loopback Host, with no proxy
    /// headers. A page whose domain was rebound to 127.0.0.1 keeps its own Host, and a reverse proxy on this machine
    /// relays other machines' requests, so neither is trusted like a local caller.
    /// </summary>
    private static bool FromThisMachine(HttpContext ctx, string host)
    {
        if (ctx.Connection.RemoteIpAddress is not { } peer) return false;
        var headers = ctx.Request.Headers;
        return IPAddress.IsLoopback(peer) && LoopbackHosts.Contains(host) && !ProxyHeaders.Any(headers.ContainsKey);
    }

    /// <summary>What a remote browser may ask while remote admin is off: enough for the page to explain itself.</summary>
    private static bool IsRemoteProbe(PathString path) =>
        path.StartsWithSegments("/api/health") || path.StartsWithSegments("/api/version") || path.StartsWithSegments("/api/auth/status");

    /// <summary>Cloudflare adds these to every request it proxies; nothing local sends them.</summary>
    private static bool ViaCloudflare(HttpRequest request) =>
        request.Headers.ContainsKey("Cf-Connecting-IP") || request.Headers.ContainsKey("Cf-Ray");

    /// <summary>
    /// The visitor's address and scheme, from Cloudflare's headers: per-IP rate limits then count visitors rather than
    /// cloudflared, and the session cookie is Secure on an https edge. The shutdown endpoint, which only answers
    /// loopback callers, then turns remote callers away too.
    /// </summary>
    private static void AdoptCloudflareHeaders(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        ctx.Connection.RemoteIpAddress = IPAddress.TryParse(headers["Cf-Connecting-IP"].ToString(), out var ip) ? ip : IPAddress.None;
        if (headers["X-Forwarded-Proto"] == "https" || headers["Cf-Visitor"].ToString().Contains("\"https\"", StringComparison.Ordinal))
            ctx.Request.Scheme = "https";
    }

    private static Task Problem(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorOnlyDto(message), ApiJson.Info<ErrorOnlyDto>());
    }
}
