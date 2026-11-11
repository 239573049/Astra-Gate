using System.Net;
using System.Text.Json.Nodes;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>/api/mcp: the MCP gateway's administration (plan mcp-gateway, GitHub issue #29). The data plane is <c>/mcp</c> (<see cref="McpGatewayEndpoints"/>).</summary>
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcp").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("/settings", (McpService mcp) => Results.Ok(mcp.GetSettings()));
        group.MapPut("/settings", async (JsonObject body, McpService mcp, CancellationToken ct) => Results.Ok(await mcp.UpdateSettingsAsync(body, ct)));
        group.MapGet("/servers", async (McpService mcp, CancellationToken ct) => Results.Ok(await mcp.ListAsync(ct)));
        group.MapGet("/servers/{id}", async (string id, McpService mcp, CancellationToken ct) => Results.Ok(await mcp.GetAsync(id, ct)));
        // Whoever creates, changes or tests a server makes Astra connect to an address of their choosing: this machine only.
        group.MapPost("/servers", async (HttpContext ctx, JsonObject body, McpService mcp, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await mcp.CreateAsync(body, ct));
        });
        group.MapPatch("/servers/{id}", async (HttpContext ctx, string id, JsonObject body, McpService mcp, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await mcp.UpdateAsync(id, body, ct));
        });
        group.MapDelete("/servers/{id}", async (string id, McpService mcp, CancellationToken ct) =>
        {
            await mcp.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        group.MapPost("/servers/{id}/test", async (HttpContext ctx, string id, McpService mcp, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await mcp.TestAsync(id, ct));
        });
        group.MapPost("/servers/{id}/oauth/start", async (HttpContext ctx, string id, McpService mcp, McpOAuthService oauth, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await oauth.StartAsync(await mcp.RequireAsync(id, ct), ct));
        });
        group.MapDelete("/servers/{id}/oauth", async (string id, McpService mcp, CancellationToken ct) => Results.Ok(await mcp.ForgetOAuthAsync(id, ct)));
        group.MapGet("/calls", async (int? limit, string? tokenId, string? serverId, McpService mcp, CancellationToken ct) =>
            Results.Ok(await mcp.CallsAsync(limit, tokenId, serverId, ct)));

        // Where the browser lands after authorizing: a GET navigation (no admin header), so it is outside the admin group. From another
        // machine it needs the admin session like every /api call (the security middleware); the single-use state does the rest.
        app.MapGet(Astra.Gateway.Mcp.McpRuntime.OAuthCallbackPath, async (HttpContext ctx, McpOAuthService oauth, IGatewaySettings settings) =>
        {
            var q = ctx.Request.Query;
            var (found, problem) = await oauth.CompleteAsync(q["state"], q["code"], q["iss"], q["error"]);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            var english = settings.Current.Locale == "en";
            var (status, title, text) = !found
                ? (StatusCodes.Status400BadRequest, english ? "This link has expired" : "此链接已失效", english ? "Start the authorization again in Astra." : "请回到 Astra 重新发起授权。")
                : problem is null
                    ? (StatusCodes.Status200OK, english ? "Authorized" : "授权成功", english ? "You can close this page and go back to Astra." : "可以关闭此页面，回到 Astra。")
                    : (StatusCodes.Status400BadRequest, english ? "Authorization failed" : "授权失败", WebUtility.HtmlEncode(problem));
            var page = "<!doctype html><html lang=\"" + (english ? "en" : "zh") + "\"><meta charset=\"utf-8\">"
                       + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + title + "</title>"
                       + "<style>" + PageStyle + "</style><h1>" + title + "</h1><p>" + text + "</p>";
            return Results.Content(page, "text/html; charset=utf-8", statusCode: status);
        }).ExcludeFromDescription();
    }

    private const string PageStyle =
        "body{font:16px system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem;color:#222}h1{font-size:1.4rem}"
        + "@media(prefers-color-scheme:dark){body{background:#161616;color:#eee}}";

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "MCP servers can only be added, changed or tested on this machine");
    }
}
