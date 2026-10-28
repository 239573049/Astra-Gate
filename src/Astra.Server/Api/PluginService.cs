using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Astra.Plugins;
using Astra.Plugins.Middleware;
using Astra.Plugins.Packages;
using Astra.Plugins.Runtime;
using Astra.Plugins.Store;

namespace Astra.Server.Api;








/// <summary>
/// The plugins the admin API manages: listing, installing (as jobs the page follows), switching on and
/// off, options, order, provider picks, and Bun. Every change moves the catalog's generation, which is
/// what the middleware, agents and plugin host reload on.
/// </summary>
public sealed class PluginService(
    PluginCatalog catalog, PluginInstaller installer, PluginJobs jobs, BunRuntime bun, MiddlewareEngine middleware,
    IAgentPluginCatalog agents, ClientService clients, Astra.Plugins.Host.PluginHost host, Astra.Plugins.Market.PluginMarket market)
{
    /// <summary>The named HttpClient for npm and Bun downloads.</summary>
    public const string HttpClientName = "plugins";

    public async Task<PluginsDto> ListAsync(CancellationToken ct = default)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var middlewareStates = await middleware.StatesAsync(ct);
        await agents.RefreshAsync(ct);
        var loadedAgents = agents.List();
        var rows = snapshot.Plugins.Select(p => new PluginDto(
            p.Entry.Spec, p.Entry.Name, PluginSpec.ShortName(p.Entry.Name), p.Manifest.Version ?? p.Entry.InstalledVersion,
            p.Entry.Enabled, [.. p.Kinds], p.Entry.Source, ParseOptions(p.Entry.OptionsJson), p.Manifest.OptionsExample,
            SafeIcon(p.Manifest.Icon), PluginSpec.IsPath(p.Entry.Spec),
            Exists(p.Target) ? p.Manifest.NotPluginReason : $"{p.Target} is missing: install it again",
            Dto(middlewareStates.GetValueOrDefault(p.Entry.Spec)), AgentOf(p, loadedAgents))).ToList();
        return new PluginsDto(rows, snapshot.Preferences.ToDictionary(), Dto(bun.Status), catalog.Off);
    }

    private static MiddlewareStateDto? Dto(MiddlewareState? m) =>
        m is null ? null : new MiddlewareStateDto(m.Hooks, m.Events, m.Error, m.Calls, m.AvgMicros, m.Failures, m.LastError);

    private static BunStatusDto Dto(BunStatus b) => new(b.Path, b.Version, b.Downloading, b.Error, b.Target);

    /// <summary>
    /// Starts installing <c>spec</c>. A plain package name (optionally <c>@version</c>) asked for by the market — or by no
    /// one in particular, as the CLI does — is looked up in the plugin market first: one the market hosts is installed
    /// from the market; one it lists with a git spec from that; anything else (or a market that can't be reached) from
    /// npm. The tarball's address and hash always come from the market's registry, never from the caller.
    /// </summary>
    public PluginJob Add(PluginAddInput body)
    {
        var spec = body.Spec?.Trim();
        if (string.IsNullOrEmpty(spec)) throw new AdminApiException(400, "spec is required");
        RequireOn();
        return jobs.Start("add", spec, async (log, ct) =>
        {
            if (body.Source is null or PluginSources.Market && IsPlainPackage(spec, out var name, out var version))
            {
                var listing = await FindListingAsync(name, ct);
                if (listing is not null)
                {
                    if (!Astra.Plugins.Market.PluginMarket.MeetsMinVersion(listing.MinAstraVersion, Astra.Server.Hosting.ServerOptions.Version))
                        throw new InvalidOperationException($"{name} needs Astra {listing.MinAstraVersion} or newer; this is {Astra.Server.Hosting.ServerOptions.Version}");
                    if (listing.Tarballs.Count > 0)
                    {
                        var tarball = version is null ? listing.Tarballs[0] : listing.Tarballs.FirstOrDefault(t => t.Version == version)
                            ?? throw new InvalidOperationException($"the plugin market hosts no version {version} of {name}");
                        return (await installer.AddHostedAsync(name, tarball, version is not null, log, ct)).Spec;
                    }
                    if (listing.Spec is { } gitSpec && version is null)
                        return (await installer.AddAsync(gitSpec, body.Source ?? PluginSources.Market, log, ct)).Spec;
                }
            }
            return (await installer.AddAsync(spec, body.Source, log, ct)).Spec;
        });
    }

    // "name", "name@1.2.3" or "name@latest" — not a path, a git repository or anything else bun resolves by itself.
    private static bool IsPlainPackage(string spec, out string name, out string? version)
    {
        name = spec;
        version = null;
        if (PluginSpec.IsPath(spec) || PluginSpec.IsGit(spec)) return false;
        var at = spec.LastIndexOf('@');
        if (at > 0)
        {
            name = spec[..at];
            version = spec[(at + 1)..] is { Length: > 0 } v && v != "latest" ? v : null;
        }
        return PluginSpec.IsPackageName(name);
    }

    // The market's listing of a package; a market that can't answer is no listing (npm is asked instead).
    private async Task<Astra.Plugins.Market.MarketListing?> FindListingAsync(string name, CancellationToken ct)
    {
        try
        {
            return await market.FindAsync(name, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public PluginJob Update(string? spec)
    {
        RequireOn();
        return jobs.Start("update", spec ?? "*", async (log, ct) =>
        {
            await installer.UpdateAsync(spec, log, ct);
            return spec;
        });
    }

    private void RequireOn()
    {
        if (catalog.Off) throw new AdminApiException(409, "Plugins are switched off on this server (ASTRA_PLUGINS=off)");
    }

    public async Task<PluginJob> RemoveAsync(string spec, CancellationToken ct)
    {
        await RequireAsync(spec, ct);
        // A client the plugin added gets its config back first: once the plugin is gone, its row can still be
        // restored, but a user removing the plugin expects the client to be back as it was.
        await agents.RefreshAsync(ct);
        foreach (var agent in agents.List().Where(a => a.Spec == spec))
            if (await clients.IsEnabledAsync(ClientKinds.PluginPrefix + agent.Id, ct))
                await clients.DisableAsync(ClientKinds.PluginPrefix + agent.Id, false, ct);
        return jobs.Start("remove", spec, async (log, c) =>
        {
            await installer.RemoveAsync(spec, log, c);
            return spec;
        });
    }

    public PluginJob Job(string id) => jobs.Get(id) ?? throw new AdminApiException(404, $"no plugin job {id}");

    /// <summary>PATCH: <c>enabled</c> switches it on or off; <c>options</c> (an object, or null to clear) sets its options.</summary>
    public async Task<PluginsDto> PatchAsync(string spec, JsonObject body, CancellationToken ct)
    {
        await RequireAsync(spec, ct);
        if (body.TryGetPropertyValue("enabled", out var enabled))
        {
            if (enabled is not JsonValue v || !v.TryGetValue<bool>(out var on)) throw new AdminApiException(400, "enabled must be true or false");
            if (on) RequireOn();
            await catalog.Store.SetEnabledAsync(spec, on, ct);
        }
        if (body.TryGetPropertyValue("options", out var options))
        {
            if (options is not null and not JsonObject) throw new AdminApiException(400, "options must be an object, or null to clear them");
            var json = options is JsonObject { Count: > 0 } o ? o.ToJsonString() : null;
            await catalog.Store.SetOptionsAsync(spec, json, ct);
        }
        catalog.Invalidate();
        return await ListAsync(ct);
    }

    public async Task<PluginsDto> ReorderAsync(List<string> specs, CancellationToken ct)
    {
        await catalog.Store.ReorderAsync(specs, ct);
        catalog.Invalidate();
        return await ListAsync(ct);
    }

    public async Task<PluginsDto> PreferAsync(PluginPreferInput body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Spec) || string.IsNullOrWhiteSpace(body.Provider))
            throw new AdminApiException(400, "spec and provider are required");
        await RequireAsync(body.Spec, ct);
        await catalog.Store.SetPreferenceAsync(body.Provider.Trim(), body.Spec, ct);
        catalog.Invalidate();
        return await ListAsync(ct);
    }

    public BunStatus Runtime() => bun.Status;

    /// <summary>The plugin host: running or not, the plugins it loaded (and why some didn't), what it last failed on.</summary>
    public Astra.Plugins.Host.PluginHostStatus HostStatus() => host.Status;

    public PluginJob InstallRuntime() =>
        jobs.Start("runtime", "bun", async (log, ct) =>
        {
            log($"downloading Bun {BunRuntime.Version} ({BunRuntime.Target(out _) ?? "no build for this machine"})");
            var path = await bun.EnsureAsync(ct);
            log($"Bun is at {path}");
            return path;
        });

    /// <summary>The list as magpie's plugins.json: {plugins:[{spec, off?, options?}], prefer?}.</summary>
    public async Task<JsonObject> ExportAsync(CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var plugins = new JsonArray();
        foreach (var p in snapshot.Plugins)
        {
            var e = new JsonObject { ["spec"] = p.Entry.Spec };
            if (!p.Entry.Enabled) e["off"] = true;
            if (ParseOptions(p.Entry.OptionsJson) is { } o) e["options"] = o;
            plugins.Add((JsonNode)e);
        }
        var doc = new JsonObject { ["plugins"] = plugins };
        if (snapshot.Preferences.Count > 0)
        {
            var prefer = new JsonObject();
            foreach (var (id, spec) in snapshot.Preferences) prefer[id] = spec;
            doc["prefer"] = prefer;
        }
        return doc;
    }

    private PluginAgentDto? AgentOf(InstalledPlugin p, IReadOnlyList<AgentPluginDescriptor> loaded)
    {
        if (p.Manifest.AgentFile is null) return null;
        if (loaded.FirstOrDefault(a => a.Spec == p.Entry.Spec) is { } agent)
            return new PluginAgentDto(agent.Id, ClientKinds.PluginPrefix + agent.Id, agent.Name, null);
        return new PluginAgentDto(null, null, null, agents.Errors.GetValueOrDefault(p.Entry.Spec) ?? (p.Entry.Enabled ? null : "switched off"));
    }

    private async Task RequireAsync(string spec, CancellationToken ct)
    {
        if (await catalog.Store.GetAsync(spec, ct) is null) throw new AdminApiException(404, $"no plugin {spec}");
    }

    private static bool Exists(string target) => File.Exists(target) || Directory.Exists(target);

    private static JsonObject? ParseOptions(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An icon name, or an https: / small data:image URL — never anything a page could run.
    private static string? SafeIcon(string? icon) => icon switch
    {
        null => null,
        _ when icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => icon,
        _ when icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && icon.Length <= 64 * 1024
               && !icon.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase) => icon,
        _ when icon.Length <= 64 && icon.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') => icon,
        _ => null,
    };
}
