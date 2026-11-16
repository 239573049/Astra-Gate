using Astra.Api.Contracts;
using Astra.Gateway.Http;

namespace Astra.Server.Api;

/// <summary>
/// <c>/api/claude-bridge</c> (linked update 36): the Claude bridge — what <c>claude</c> program Astra found, its settings and runs, who is signed in to
/// it, the accounts that are answered through it — and <c>/api/warmup</c>, the 5-hour window warm-up. Whatever names a program or a directory for
/// Astra to use answers only callers on this machine (like the tunnel's <c>cloudflaredPath</c>).
/// </summary>
public static class ClaudeBridgeEndpoints
{
    public static void MapClaudeBridgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/claude-bridge").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("", async (ClaudeBridgeAdmin admin, CancellationToken ct) => Results.Ok(await admin.StatusAsync(ct)));
        group.MapPut("/settings", async (HttpContext ctx, ClaudeBridgeSettingsPatch patch, ClaudeBridgeAdmin admin, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await admin.UpdateSettingsAsync(patch, ct));
        });
        group.MapGet("/signin", async (string? configDir, ClaudeBridgeAdmin admin, CancellationToken ct) => Results.Ok(await admin.SignInAsync(null, configDir, ct)));
        group.MapGet("/accounts/{id}/signin", async (string id, ClaudeBridgeAdmin admin, CancellationToken ct) => Results.Ok(await admin.SignInAsync(id, null, ct)));
        group.MapPost("/accounts", async (HttpContext ctx, ClaudeBridgeAccountRequest request, ClaudeBridgeAdmin admin, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await admin.AddAccountAsync(request, ct));
        });
        group.MapPatch("/accounts/{id}", async (HttpContext ctx, string id, ClaudeBridgeAccountPatch patch, ClaudeBridgeAdmin admin, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await admin.PatchAccountAsync(id, patch, ct));
        });
        group.MapPost("/accounts/{id}/login", async (HttpContext ctx, string id, ClaudeBridgeAdmin admin, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await admin.StartLoginAsync(id, ct));
        });
        group.MapGet("/accounts/{id}/login", (string id, ClaudeBridgeAdmin admin) => Results.Ok(admin.LoginState(id)));

        var warm = app.MapGroup("/api/warmup").AddEndpointFilter<AdminApiErrorFilter>();
        warm.MapGet("", (WarmupService warmup) => Results.Ok(warmup.GetSettings()));
        warm.MapPut("", async (WarmupSettingsPatch patch, WarmupService warmup, CancellationToken ct) => Results.Ok(await warmup.UpdateSettingsAsync(patch, ct)));
        warm.MapGet("/accounts", async (WarmupService warmup, CancellationToken ct) => Results.Ok(await warmup.ListAsync(ct)));
        warm.MapPatch("/accounts/{id}", async (string id, WarmupAccountPatch patch, WarmupService warmup, CancellationToken ct) => Results.Ok(await warmup.PatchAccountAsync(id, patch, ct)));
        warm.MapPost("/accounts/{id}/run", async (string id, WarmupService warmup, CancellationToken ct) => Results.Ok(await warmup.WarmNowAsync(id, ct)));
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx))
            throw new AdminApiException(403, "The claude program, its sign-in directory and its accounts can only be set on the machine running Astra");
    }
}
