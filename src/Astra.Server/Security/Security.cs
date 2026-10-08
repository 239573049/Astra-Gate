using System.Security.Cryptography;
using System.Text;
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
/// - loopback mode: Host header must be 127.0.0.1 / localhost / [::1] (DNS-rebinding defense);
/// - every mutating /api call needs "X-Astra-Admin: 1" (forces a CORS preflight from foreign origins);
/// - non-loopback mode: /api requires an admin session cookie (except health/version/auth).
/// Gateway endpoints (/v1, /v1beta) authenticate with gateway tokens elsewhere.
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, ServerOptions options, AdminSessions sessions)
{
    public const string AdminHeader = "X-Astra-Admin";
    public const string DesktopOrigin = "app://astra";

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]", "::1" };

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (options.IsLoopback && !LoopbackHosts.Contains(ctx.Request.Host.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await ctx.Response.WriteAsync("Host not allowed");
            return;
        }

        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") && !HttpMethods.IsOptions(ctx.Request.Method))
        {
            var mutating = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method));
            if (mutating && ctx.Request.Headers[AdminHeader] != "1")
            {
                await Problem(ctx, StatusCodes.Status403Forbidden, $"Missing {AdminHeader} header");
                return;
            }

            var open = path.StartsWithSegments("/api/health") || path.StartsWithSegments("/api/version")
                       || path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/admin/shutdown")
                       || path.StartsWithSegments("/api/update/status");
            if (!options.IsLoopback && !open && !sessions.IsSignedIn(ctx))
            {
                await Problem(ctx, StatusCodes.Status401Unauthorized, "Login required");
                return;
            }
        }

        await next(ctx);
    }

    private static Task Problem(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorOnlyDto(message), ApiJson.Info<ErrorOnlyDto>());
    }
}
