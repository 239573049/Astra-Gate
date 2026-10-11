using System.Text.Json.Nodes;

namespace Astra.Server.Api;

/// <summary>
/// Plugins (magpie-compatible: OpenCode provider plugins, gateway middleware, agents, pi extensions).
/// A plugin is named by its spec in the query string (?spec=), since specs have slashes and @ signs.
/// Installs, updates and removals run as jobs: the call returns the job, GET /jobs/{id} follows it.
/// </summary>
public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/plugins").AddEndpointFilter<AdminApiErrorFilter>();

        g.MapGet("", async (PluginService plugins, CancellationToken ct) => Results.Ok(await plugins.ListAsync(ct)));
        g.MapPost("", (PluginAddInput body, PluginService plugins) => Results.Ok(plugins.Add(body)));
        g.MapPatch("", async (string spec, JsonObject body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.PatchAsync(spec, body, ct)));
        g.MapDelete("", async (string spec, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.RemoveAsync(spec, ct)));
        g.MapGet("/jobs/{id}", (string id, PluginService plugins) => Results.Ok(plugins.Job(id)));
        g.MapPut("/order", async (PluginOrderInput body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.ReorderAsync(body.Specs ?? [], ct)));
        g.MapPost("/update", (PluginUpdateInput? body, PluginService plugins) => Results.Ok(plugins.Update(body?.Spec)));
        g.MapPost("/prefer", async (PluginPreferInput body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.PreferAsync(body, ct)));
        g.MapGet("/runtime", (PluginService plugins) => Results.Ok(plugins.Runtime()));
        g.MapPost("/runtime/install", (PluginService plugins) => Results.Ok(plugins.InstallRuntime()));
        g.MapGet("/export", async (PluginService plugins, CancellationToken ct) => Results.Ok(await plugins.ExportAsync(ct)));
        g.MapGet("/import-magpie", async (MagpieImportService magpie, CancellationToken ct) => Results.Ok(await magpie.PreviewAsync(ct)));
        g.MapPost("/import-magpie", (MagpieImportInput? body, MagpieImportService magpie) => Results.Ok(magpie.Import(body?.SignIns ?? false)));
        g.MapGet("/host", (PluginService plugins) => Results.Ok(plugins.HostStatus()));
        g.MapGet("/market", async (bool? refresh, Astra.Plugins.Market.PluginMarket market, CancellationToken ct) =>
            Results.Ok(await market.ListAsync(refresh ?? false, ct)));
        g.MapGet("/search", async (string q, Astra.Plugins.Market.PluginMarket market, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? ModelEndpoints.Bad("q is required") : Results.Ok(await market.SearchAsync(q, ct)));

        // Signing in to a plugin's provider ({id} is the provider row's id, "plugin-<id>").
        var p = app.MapGroup("/api/plugin-providers/{id}").AddEndpointFilter<AdminApiErrorFilter>();
        p.MapGet("/methods", async (string id, PluginSignInService signIn, CancellationToken ct) => Results.Ok(await signIn.MethodsAsync(id, ct)));
        p.MapPost("/prompt", async (string id, PluginPromptInput body, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.PromptAsync(id, body, ct)));
        p.MapPost("/validate", async (string id, PluginValidateInput body, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.ValidateAsync(id, body, ct)));
        p.MapPost("/sign-in", async (string id, PluginSignInInput body, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.StartAsync(id, body, ct)));
        p.MapGet("/sign-in/{flow}", (string id, string flow, PluginSignInService signIn) => Results.Ok(signIn.Status(flow)));
        p.MapPost("/sign-in/{flow}/code", async (string id, string flow, PluginCodeInput body, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.SubmitCodeAsync(flow, body.Code, ct)));
        p.MapPost("/api-key", async (string id, PluginApiKeyInput body, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.ApiKeyAsync(id, body, ct)));
        p.MapPost("/accounts/{accountId}/sign-out", async (string id, string accountId, PluginSignInService signIn, CancellationToken ct) =>
        {
            await signIn.SignOutAsync(id, accountId, ct);
            return Results.Ok(new RemovedDto(true));
        });
        p.MapGet("/accounts/{accountId}/usage", async (string id, string accountId, PluginSignInService signIn, CancellationToken ct) =>
            Results.Ok(await signIn.UsageAsync(id, accountId, ct)));
        p.MapGet("/checkin", async (string id, PluginCheckinService checkins, CancellationToken ct) => Results.Ok(await checkins.StateAsync(id, ct)));
        p.MapPut("/checkin", async (string id, PluginCheckinSwitch body, PluginCheckinService checkins, CancellationToken ct) =>
            Results.Ok(await checkins.SwitchAsync(id, body.On, ct)));
        p.MapPost("/checkin", async (string id, PluginCheckinService checkins, CancellationToken ct) => Results.Ok(await checkins.RunNowAsync(id, ct)));
        p.MapPost("/sync", async (PluginProviderSync sync, CancellationToken ct) =>
        {
            await sync.SyncAsync(ct);
            return Results.Ok(new RemovedDto(false));
        });
    }
}
