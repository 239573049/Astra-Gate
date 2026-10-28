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
        g.MapPost("", (PluginAddInput body, PluginService plugins) => Results.Ok(plugins.Add(body).ToDto()));
        g.MapPatch("", async (string spec, JsonObject body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.PatchAsync(spec, body, ct)));
        g.MapDelete("", async (string spec, PluginService plugins, CancellationToken ct) =>
            Results.Ok((await plugins.RemoveAsync(spec, ct)).ToDto()));
        g.MapGet("/jobs/{id}", (string id, PluginService plugins) => Results.Ok(plugins.Job(id).ToDto()));
        g.MapPut("/order", async (PluginOrderInput body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.ReorderAsync(body.Specs ?? [], ct)));
        g.MapPost("/update", (PluginUpdateInput? body, PluginService plugins) => Results.Ok(plugins.Update(body?.Spec).ToDto()));
        g.MapPost("/prefer", async (PluginPreferInput body, PluginService plugins, CancellationToken ct) =>
            Results.Ok(await plugins.PreferAsync(body, ct)));
        g.MapGet("/runtime", (PluginService plugins) => Results.Ok(plugins.Runtime().ToDto()));
        g.MapPost("/runtime/install", (PluginService plugins) => Results.Ok(plugins.InstallRuntime().ToDto()));
        g.MapGet("/export", async (PluginService plugins, CancellationToken ct) => Results.Ok(await plugins.ExportAsync(ct)));
        g.MapGet("/import-magpie", async (MagpieImportService magpie, CancellationToken ct) => Results.Ok(await magpie.PreviewAsync(ct)));
        g.MapPost("/import-magpie", (MagpieImportInput? body, MagpieImportService magpie) => Results.Ok(magpie.Import(body?.SignIns ?? false).ToDto()));
        g.MapGet("/host", (PluginService plugins) => Results.Ok(plugins.HostStatus().ToDto()));
        g.MapGet("/market", async (bool? refresh, Astra.Plugins.Market.PluginMarket market, CancellationToken ct) =>
            Results.Ok((await market.ListAsync(refresh ?? false, ct)).Select(l => l.ToDto()).ToList()));
        g.MapGet("/search", async (string q, Astra.Plugins.Market.PluginMarket market, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? ModelEndpoints.Bad("q is required") : Results.Ok((await market.SearchAsync(q, ct)).Select(r => r.ToDto()).ToList()));

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

/// <summary>
/// Maps the plugin runtime's records (Astra.Plugins) to the mirror DTOs in Astra.Api.Contracts, which the Avalonia client shares: same
/// fields in the same order, so the JSON is what the Astra.Plugins types produced (plan avalonia-client §19).
/// </summary>
internal static class PluginWire
{
    public static PluginJobDto ToDto(this Astra.Plugins.Packages.PluginJob j) =>
        new(j.Id, j.Action, j.Target, j.State, j.StartedAt, j.FinishedAt, j.Log, j.Error, j.Result);

    public static BunStatusDto ToDto(this Astra.Plugins.Runtime.BunStatus b) => new(b.Path, b.Version, b.Downloading, b.Error, b.Target);

    public static PluginHostStatusDto ToDto(this Astra.Plugins.Host.PluginHostStatus h) =>
        new(h.Running, h.Plugins.Select(p => new PluginHostLoadedDto(p.Spec, p.Error, p.Provides, p.ServedBy)).ToList(), h.StartError, h.GoneReason,
            h.Recent.Select(m => new PluginHostMessageDto(m.At, m.Variant, m.Title, m.Message)).ToList());

    public static MarketListingDto ToDto(this Astra.Plugins.Market.MarketListing l) =>
        new(l.Package, l.Name, l.Icon, l.Kind, l.Providers, l.Replaces, l.Summary, l.Latest, l.Description, l.License, l.Spec, l.Origin, l.Author,
            l.Homepage, l.Repository, l.Tags, l.MinAstraVersion,
            l.Tarballs.Select(t => new MarketTarballDto(t.Version, t.Url, t.Sha256, t.Sha512, t.Size)).ToList());

    public static MarketSearchResultDto ToDto(this Astra.Plugins.Market.MarketSearchResult r) =>
        new(r.Spec, r.Name, r.Description, r.Version, r.Source, r.Kind, r.Stars);
}
