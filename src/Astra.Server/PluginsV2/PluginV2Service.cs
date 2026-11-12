using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Astra.Plugins;
using Astra.Plugins.Market;
using Astra.Plugins.Packages;
using Astra.Plugins.Store;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Runtime;
using Astra.Server.Api;

namespace Astra.Server.PluginsV2;

/// <summary>
/// The admin side of v2 plugins (design/plugin-v2): listing them with what they contribute, granting permissions,
/// settings, commands, assets, runtime permission requests, and the v1 → v2 update reminder and upgrade.
/// </summary>
public sealed class PluginV2Service(
    PluginCatalog catalog, PluginV2Host host, PluginV2HostServices services, PluginInstaller installer, PluginJobs jobs, PluginMarket market,
    Astra.Data.AstraDatabase db, PluginV2ProviderSync providerSync, Astra.Core.AstraPaths paths)
{
    /// <summary>Asset types a plugin may serve to the shells (icons and images only — never a script or a page).</summary>
    private static readonly Dictionary<string, string> AssetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp", [".gif"] = "image/gif", [".ico"] = "image/x-icon",
    };

    public async Task<PluginV2ListDto> ListAsync(string locale, CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var running = (await host.ListAsync(ct)).ToDictionary(p => p.Spec);
        var problems = await host.ProblemsAsync(ct);
        var rows = new List<PluginV2Dto>();
        foreach (var installed in snapshot.Plugins.Where(p => p.Manifest.IsV2))
            rows.Add(await DtoAsync(installed, running.GetValueOrDefault(installed.Entry.Spec), problems.GetValueOrDefault(installed.Entry.Spec), locale, ct));
        return new PluginV2ListDto(rows, Contributions(running.Values, locale),
            [.. host.PendingRequests.Select(r => new PluginPermissionRequestDto(r.Id, r.PluginId, r.PluginName, [.. r.Permissions], r.At))]);
    }

    public async Task<PluginV2DetailDto> DetailAsync(string id, string locale, CancellationToken ct)
    {
        var (installed, instance) = await FindAsync(id, ct);
        var problems = await host.ProblemsAsync(ct);
        var dto = await DtoAsync(installed, instance, problems.GetValueOrDefault(installed.Entry.Spec), locale, ct);
        var activity = await services.Data.ActivityAsync(id, 200, ct);
        return new PluginV2DetailDto(dto,
            [.. activity.Select(a => new PluginV2ActivityDto(a.AtUtc, a.Call, a.Target, a.Outcome))],
            [.. (instance?.Lines ?? []).Select(l => new PluginV2LogDto(l.At, l.Level, l.Text))]);
    }

    /// <summary>
    /// Grants permissions the user confirmed: any of the manifest's (install, or an update that asks for more) and its
    /// optional ones. A permission the manifest doesn't name is refused. The plugin starts once all of its own are granted.
    /// </summary>
    public async Task<PluginV2ListDto> GrantAsync(string id, PluginV2GrantInput body, string locale, CancellationToken ct)
    {
        var (installed, _) = await FindAsync(id, ct);
        var manifest = installed.Manifest.V2 ?? throw new AdminApiException(409, installed.Manifest.V2Error ?? "not a usable v2 plugin");
        var asked = body.Permissions ?? [];
        var allowed = manifest.Permissions.Concat(manifest.OptionalPermissions).ToHashSet();
        if (asked.FirstOrDefault(p => !allowed.Contains(p)) is { } bad) throw new AdminApiException(400, $"{id} doesn't ask for \"{bad}\"");
        var granted = (await services.Data.GrantsAsync(id, ct)).Concat(asked).Distinct().ToList();
        await services.Data.SetGrantsAsync(id, granted, ct);
        await services.Data.AddActivityAsync(new PluginActivity(id, DateTimeOffset.UtcNow, "grant", string.Join(" ", asked), "granted"), ct);
        catalog.Invalidate();
        return await ListAsync(locale, ct);
    }

    /// <summary>Takes back permissions (the plugin stops when one of its own is taken).</summary>
    public async Task<PluginV2ListDto> RevokeAsync(string id, PluginV2GrantInput body, string locale, CancellationToken ct)
    {
        await FindAsync(id, ct);
        var take = (body.Permissions ?? []).ToHashSet();
        var granted = (await services.Data.GrantsAsync(id, ct)).Where(p => !take.Contains(p)).ToList();
        await services.Data.SetGrantsAsync(id, granted, ct);
        await services.Data.AddActivityAsync(new PluginActivity(id, DateTimeOffset.UtcNow, "revoke", string.Join(" ", take), "revoked"), ct);
        catalog.Invalidate();
        return await ListAsync(locale, ct);
    }

    /// <summary>Saves settings: values checked against the plugin's configuration; secret ones go to its secrets.</summary>
    public async Task<PluginV2DetailDto> SettingsAsync(string id, PluginV2SettingsInput body, string locale, CancellationToken ct)
    {
        var (installed, _) = await FindAsync(id, ct);
        var manifest = installed.Manifest.V2 ?? throw new AdminApiException(409, "not a usable v2 plugin");
        var schema = manifest.Configuration;
        var current = installed.Entry.OptionsJson is { Length: > 0 } json ? JsonNode.Parse(json) as JsonObject ?? [] : [];
        if (body.Values is { } values)
            foreach (var (key, value) in values)
            {
                var prop = schema?.Properties.FirstOrDefault(p => p.Name == key) ?? throw new AdminApiException(400, $"{key} is not a setting of {id}");
                if (prop.Secret) throw new AdminApiException(400, $"{key} is a secret setting: send it in secrets");
                if (value is null) current.Remove(key);
                else
                {
                    if (Problem(prop, value) is { } why) throw new AdminApiException(400, $"{key}: {why}");
                    current[key] = value.DeepClone();
                }
            }
        if (body.Secrets is { } secrets)
            foreach (var (key, value) in secrets)
            {
                var prop = schema?.Properties.FirstOrDefault(p => p.Name == key && p.Secret) ?? throw new AdminApiException(400, $"{key} is not a secret setting of {id}");
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
                    await services.Data.SetSecretAsync(id, prop.Name, services.Protect(text), ct);
                else await services.Data.DeleteSecretAsync(id, prop.Name, ct);
            }
        await catalog.Store.SetOptionsAsync(installed.Entry.Spec, current.Count == 0 ? null : current.ToJsonString(), ct);
        catalog.Invalidate();
        await host.RefreshAsync(ct);
        return await DetailAsync(id, locale, ct);
    }

    private static string? Problem(ConfigurationProperty p, JsonNode value)
    {
        var kind = value.GetValueKind();
        switch (p.Type)
        {
            case "string":
                if (kind != JsonValueKind.String) return "expected text";
                var s = value.GetValue<string>();
                if (p.Enum is { } e && !e.Contains(s)) return "expected one of " + string.Join(", ", e);
                if (p.Pattern is { } pattern && !System.Text.RegularExpressions.Regex.IsMatch(s, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50)))
                    return "doesn't match the expected form";
                return null;
            case "number":
            case "integer":
                if (kind != JsonValueKind.Number) return "expected a number";
                var d = value.GetValue<double>();
                if (p.Type == "integer" && d != Math.Floor(d)) return "expected a whole number";
                if (p.Minimum is { } min && d < min) return $"at least {min}";
                if (p.Maximum is { } max && d > max) return $"at most {max}";
                return null;
            case "boolean":
                return kind is JsonValueKind.True or JsonValueKind.False ? null : "expected true or false";
            case "array":
                if (value is not JsonArray a) return "expected a list";
                if (p.ItemsType == "string" && a.Any(x => x?.GetValueKind() != JsonValueKind.String)) return "expected a list of text";
                return null;
            case "object":
                return value is JsonObject ? null : "expected an object";
        }
        return null;
    }

    public async Task<JsonNode?> CommandAsync(string id, string command, JsonNode? args, CancellationToken ct)
    {
        try
        {
            return await host.ExecuteCommandAsync(id + ":" + command, args, null, ct);
        }
        catch (PluginCallException e)
        {
            throw new AdminApiException(e.Code switch { "not_found" => 404, "permission_denied" => 403, "timeout" => 504, _ => 422 }, e.Message);
        }
    }

    /// <summary>A file of the plugin the shells may show (an icon, an image): its path and type; images only.</summary>
    public async Task<(string Path, string Type)> AssetAsync(string id, string path, CancellationToken ct)
    {
        var (installed, _) = await FindAsync(id, ct);
        var root = installed.Manifest.V2?.Root ?? installed.Manifest.ModuleRoot;
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var rel = Path.GetRelativePath(root, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || !File.Exists(full))
            throw new AdminApiException(404, "no such asset");
        if ((File.ResolveLinkTarget(full, true)?.FullName ?? full) is var target && Path.GetRelativePath(root, target).StartsWith("..", StringComparison.Ordinal))
            throw new AdminApiException(404, "no such asset");
        if (!AssetTypes.TryGetValue(Path.GetExtension(full), out var type)) throw new AdminApiException(415, "only images are served");
        if (new FileInfo(full).Length > 5 * 1024 * 1024) throw new AdminApiException(413, "asset too large");
        return (full, type);
    }

    public bool AnswerRequest(string requestId, bool allow) =>
        host.Answer(requestId, allow) ? true : throw new AdminApiException(404, "no such request (it was answered or timed out)");

    public List<PluginV2NotificationDto> Notifications(long after) =>
        [.. services.Notifications(after).Select(n => new PluginV2NotificationDto(n.Id, n.Plugin, n.Title, n.Body, n.Tone, n.At))];

    // ---- the v2 update reminder (design/plugin-v2 07 §3)

    /// <summary>
    /// The installed v1 plugins the market has a v2 successor for: a v2 entry whose <c>replaces</c> names the v1 plugin's
    /// package or spec, that runs on this Astra, isn't installed already, and whose version the user didn't dismiss.
    /// </summary>
    public async Task<List<PluginSuccessorDto>> SuccessorsAsync(string locale, CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        IReadOnlyList<MarketListing> listings;
        try
        {
            listings = await market.ListAsync(false, ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
        var v2Installed = snapshot.Plugins.Where(p => p.Manifest.V2 is not null).Select(p => p.Manifest.V2!.Id).ToHashSet();
        var result = new List<PluginSuccessorDto>();
        foreach (var p in snapshot.Plugins.Where(p => !p.Manifest.IsV2))
        {
            var name = PluginSpec.Name(p.Entry.Spec, catalog.PackagesDir);
            var listing = Successor(listings, p.Entry.Spec, name, p.Entry.Name);
            if (listing is null || v2Installed.Contains(listing.Package) || listing.Tarballs.Count == 0) continue;
            if (!PluginMarket.MeetsMinVersion(listing.MinAstraVersion, Hosting.ServerOptions.Version)) continue;
            var version = listing.Tarballs[0].Version;
            if (await services.Data.DismissedSuccessorAsync(p.Entry.Spec, ct) == version) continue;
            var permissions = listing.Permissions ?? [];
            result.Add(new PluginSuccessorDto(p.Entry.Spec, PluginSpec.ShortName(p.Entry.Name), listing.Package, listing.Package, version,
                listing.Name, Lang(listing.Notes, locale), [.. permissions], [.. permissions.Where(PluginPermissions.IsSensitive)]));
        }
        return result;
    }

    private static MarketListing? Successor(IReadOnlyList<MarketListing> listings, string spec, string name, string entryName)
    {
        var bare = spec.LastIndexOf('@') > 0 && !spec.StartsWith('/') ? spec[..spec.LastIndexOf('@')] : spec;
        return listings.FirstOrDefault(l => l.ApiVersion == 2 && l.ReplacesV1 is { } r
            && (r.Contains(spec) || r.Contains(bare) || r.Contains(name) || r.Contains(entryName)));
    }

    public async Task DismissAsync(PluginSuccessorDismissInput body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body.Spec) || string.IsNullOrEmpty(body.Version)) throw new AdminApiException(400, "spec and version are required");
        await services.Data.SetDismissedSuccessorAsync(body.Spec, body.Version, ct);
    }

    /// <summary>
    /// Upgrades a v1 plugin to its v2 successor, as a job (design/plugin-v2 07 §3.3): install the v2 plugin, grant what
    /// the user confirmed, give it the v1 plugin's place, settings and switch, activate it (its activate sees
    /// <c>ctx.upgrade</c> and maps the old options). Only then is the v1 plugin removed; if anything fails the v2 plugin is
    /// removed again and the v1 plugin stays as it was.
    /// </summary>
    public PluginJob Upgrade(PluginUpgradeInput body, CancellationToken requestCt)
    {
        var spec = body.Spec ?? throw new AdminApiException(400, "spec is required");
        if (catalog.Off) throw new AdminApiException(409, "Plugins are switched off on this server (ASTRA_PLUGINS=off)");
        return jobs.Start("upgrade", spec, async (log, ct) =>
        {
            var snapshot = await catalog.SnapshotAsync(ct);
            var v1 = snapshot.Plugins.FirstOrDefault(p => p.Entry.Spec == spec && !p.Manifest.IsV2)
                     ?? throw new InvalidOperationException($"no v1 plugin {spec}");
            var listings = await market.ListAsync(false, ct);
            var listing = Successor(listings, spec, PluginSpec.Name(spec, catalog.PackagesDir), v1.Entry.Name)
                          ?? throw new InvalidOperationException($"the market has no v2 version of {v1.Entry.Name}");
            if (listing.Tarballs.Count == 0) throw new InvalidOperationException($"the market hosts no tarball of {listing.Package}");
            log($"installing {listing.Package} {listing.Tarballs[0].Version}");
            var entry = await installer.AddHostedAsync(listing.Package, listing.Tarballs[0], false, log, ct);
            var installed = (await catalog.SnapshotAsync(ct)).Plugins.First(p => p.Entry.Spec == entry.Spec);
            try
            {
                var manifest = installed.Manifest.V2 ?? throw new InvalidOperationException(installed.Manifest.V2Error ?? $"{listing.Package} is not a v2 plugin");
                var grant = body.Grant ?? [];
                var missing = manifest.Permissions.Where(p => !grant.Contains(p)).ToList();
                if (missing.Count > 0) throw new InvalidOperationException("not granted: " + string.Join(", ", missing));
                await services.Data.SetGrantsAsync(manifest.Id, [.. manifest.Permissions], ct);
                // its place in the chain and its switch
                var order = (await catalog.Store.ListAsync(ct)).Select(p => p.Spec).ToList();
                order.Remove(entry.Spec);
                order.Insert(Math.Max(0, order.IndexOf(spec)), entry.Spec);
                await catalog.Store.ReorderAsync(order, ct);
                await catalog.Store.SetEnabledAsync(entry.Spec, v1.Entry.Enabled, ct);
                var from = new JsonObject
                {
                    ["apiVersion"] = 1,
                    ["package"] = v1.Entry.Name,
                    ["spec"] = spec,
                    ["version"] = v1.Manifest.Version ?? v1.Entry.InstalledVersion,
                    ["options"] = v1.Entry.OptionsJson is { Length: > 0 } o ? JsonNode.Parse(o) : null,
                };
                // 08 §6: with legacy.auth:import, that v1 plugin's own sign-ins (its providers' plugin-auth.json entries), once
                if (manifest.Permissions.Contains(PluginPermissions.LegacyAuthImport) && await LegacyAuthAsync(spec, ct) is { Count: > 0 } auth)
                {
                    from["auth"] = auth;
                    log($"handing {auth.Count} sign-in(s) of {v1.Entry.Name} to the new version");
                }
                host.SetUpgrade(manifest.Id, from);
                catalog.Invalidate();
                await host.RefreshAsync(ct);
                if (v1.Entry.Enabled)
                {
                    log("activating the new version");
                    await host.ActivateAsync(manifest.Id, ct);
                }
                // 08 §6: a v1 provider plugin's rows hand their client bindings and token pools to the v2 rows of the same provider
                var moved = new JsonArray();
                if (manifest.Providers.Count > 0)
                {
                    await providerSync.SyncAsync(ct);
                    foreach (var pp in manifest.Providers)
                    {
                        var oldRow = Astra.Server.Api.PluginProviderSync.RowId(pp.Id);
                        var newRow = PluginV2ProviderSync.RowId(manifest.Id, pp.Id);
                        if (await db.Providers.GetAsync(oldRow, ct) is null || await db.Providers.GetAsync(newRow, ct) is null) continue;
                        var n = await RepointAsync(oldRow, newRow, ct);
                        moved.Add((JsonNode)new JsonArray(oldRow, newRow));
                        log($"{oldRow} → {newRow}: {n} binding(s) and pool(s) moved");
                    }
                }
                // what "back to v1" needs, kept 30 days
                await services.Data.SetAsync("astra.upgrades", manifest.Id, new JsonObject
                {
                    ["spec"] = spec, ["options"] = v1.Entry.OptionsJson, ["enabled"] = v1.Entry.Enabled,
                    ["until"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O"), ["moved"] = moved,
                }.ToJsonString(), ct);
                log($"removing {v1.Entry.Name}");
                await installer.RemoveAsync(spec, log, ct);
                return entry.Spec;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log($"upgrade failed, keeping {v1.Entry.Name}: {e.Message}");
                await installer.RemoveAsync(entry.Spec, log, CancellationToken.None);
                catalog.Invalidate();
                throw;
            }
        });
    }

    /// <summary>
    /// Moves every client binding and token (direct provider and pool member) from provider row <paramref name="from"/> to
    /// <paramref name="to"/>. A pinned account doesn't move (the rows' accounts differ): the pin is dropped. Returns how many moved.
    /// </summary>
    internal async Task<int> RepointAsync(string from, string to, CancellationToken ct)
    {
        var n = 0;
        foreach (var kind in (await db.Clients.ListBindingsByProviderAsync(from, ct)).Select(b => b.ClientKind).Distinct().ToList())
        {
            var bindings = (await db.Clients.ListBindingsAsync(kind, ct)).Select(b => b.Clone()).ToList();
            foreach (var b in bindings.Where(b => b.ProviderId == from))
            {
                b.ProviderId = to;
                b.AccountId = null;
                n++;
            }
            // one binding per provider: a client already bound to the new row keeps only its first
            await db.Clients.ReplaceBindingsAsync(kind, bindings.DistinctBy(b => b.ProviderId).ToList(), ct);
        }
        foreach (var token in await db.Tokens.ListAsync(ct))
        {
            var changed = false;
            if (token.ProviderId == from)
            {
                token.ProviderId = to;
                token.AccountId = null;
                changed = true;
            }
            if (Astra.Core.Tokens.TokenPool.Parse(token.PoolJson) is { } pool && pool.Members.Any(m => m.ProviderId == from))
            {
                foreach (var m in pool.Members.Where(m => m.ProviderId == from))
                {
                    m.ProviderId = to;
                    m.AccountId = null;
                }
                pool.Members = pool.Members.DistinctBy(m => m.ProviderId).ToList();
                token.PoolJson = Astra.Core.Json.Serialize(pool);
                changed = true;
            }
            if (changed)
            {
                await db.Tokens.UpdateAsync(token, ct);
                n++;
            }
        }
        return n;
    }

    // The plugin-auth.json entries of the providers a v1 plugin serves (their rows carry settings.plugin.spec): key → entry.
    private async Task<JsonObject?> LegacyAuthAsync(string v1Spec, CancellationToken ct)
    {
        if (!File.Exists(paths.PluginAuthFile)) return null;
        var ids = (await db.Providers.ListAsync(ct))
            .Where(p => p.Settings["plugin"]?["spec"]?.GetValue<string>() == v1Spec && Gateway.Pipeline.GatewayPipeline.V2PluginOf(p) is null)
            .Select(Gateway.Pipeline.GatewayPipeline.PluginProviderOf).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return null;
        JsonObject all;
        try
        {
            all = JsonNode.Parse(await File.ReadAllTextAsync(paths.PluginAuthFile, ct)) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
        var mine = new JsonObject();
        foreach (var (key, entry) in all)
            if (ids.Contains(key.Split('#')[0])) mine[key] = entry?.DeepClone();
        return mine;
    }

    /// <summary>Goes back to the v1 plugin a v2 one replaced (within 30 days of the upgrade).</summary>
    public PluginJob Rollback(string id)
    {
        return jobs.Start("rollback", id, async (log, ct) =>
        {
            var saved = await services.Data.GetAsync("astra.upgrades", id, ct) ?? throw new InvalidOperationException($"{id} wasn't upgraded from a v1 plugin here");
            var o = JsonNode.Parse(saved)!.AsObject();
            if (DateTimeOffset.TryParse(o["until"]?.GetValue<string>(), out var until) && until < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("the upgrade is older than 30 days");
            var spec = o["spec"]!.GetValue<string>();
            var (installed, _) = await FindAsync(id, ct);
            var entry = await installer.AddAsync(spec, null, log, ct);
            if (o["options"]?.GetValue<string>() is { } options) await catalog.Store.SetOptionsAsync(entry.Spec, options, ct);
            await catalog.Store.SetEnabledAsync(entry.Spec, o["enabled"]?.GetValue<bool>() ?? true, ct);
            // bindings and pools that moved to the v2 rows go back
            foreach (var pair in (o["moved"] as JsonArray ?? []).OfType<JsonArray>())
                if (pair.Count == 2) await RepointAsync(pair[1]!.GetValue<string>(), pair[0]!.GetValue<string>(), ct);
            await installer.RemoveAsync(installed.Entry.Spec, log, ct);
            await services.Data.DeleteAsync("astra.upgrades", id, ct);
            catalog.Invalidate();
            return entry.Spec;
        });
    }

    // ---- shaping

    /// <summary>
    /// Runs a dev plugin's tests in the sandbox (astra plugin test, design/plugin-v2 06 §4): an isolated host over a throwaway
    /// store, so nothing the tests do touches the running plugin. Only a plugin mounted from a folder.
    /// </summary>
    public async Task<List<PluginV2TestResultDto>> TestAsync(string id, CancellationToken ct)
    {
        var (installed, _) = await FindAsync(id, ct);
        if (installed.Entry.Source != PluginSources.Path)
            throw new AdminApiException(400, $"{id} isn't mounted from a folder: tests run for a dev plugin (astra plugin dev)");
        try
        {
            var results = await Astra.Plugins.V2.PluginV2TestRunner.RunAsync(installed.Entry.Spec, services.HostVersion, ct);
            return [.. results.Select(r => new PluginV2TestResultDto(r.File, r.Name, r.Status, r.Ms, r.Error))];
        }
        catch (Astra.Plugins.V2.PluginManifestException e)
        {
            throw new AdminApiException(400, e.Message);
        }
    }

    private async Task<(InstalledPlugin Installed, PluginV2Instance? Instance)> FindAsync(string id, CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var installed = snapshot.Plugins.FirstOrDefault(p => p.Manifest.V2?.Id == id)
                        ?? snapshot.Plugins.FirstOrDefault(p => p.Manifest.IsV2 && p.Entry.Name == id)
                        ?? throw new AdminApiException(404, $"no v2 plugin {id}");
        return (installed, await host.FindAsync(id, ct));
    }

    private async Task<PluginV2Dto> DtoAsync(InstalledPlugin installed, PluginV2Instance? instance, string? problem, string locale, CancellationToken ct)
    {
        var m = installed.Manifest.V2;
        var id = m?.Id ?? installed.Entry.Name;
        var granted = (await services.Data.GrantsAsync(id, ct)).ToList();
        var permissions = m?.Permissions.ToList() ?? [];
        var settings = instance?.Config() ?? new JsonObject();
        if (instance is null && m is not null && installed.Entry.OptionsJson is { Length: > 0 } json && JsonNode.Parse(json) is JsonObject o)
            foreach (var (k, v) in o)
                if (m.Configuration?.Properties.FirstOrDefault(p => p.Name == k) is not { Secret: true }) settings[k] = v?.DeepClone();
        return new PluginV2Dto(
            id, installed.Entry.Spec, m?.Version ?? installed.Entry.InstalledVersion ?? "", m?.DisplayName(locale) ?? installed.Entry.Name,
            m is null ? null : LocalText.Resolve(m.Description, locale), Icon(m), m?.PublisherName, m?.Homepage, m?.License,
            permissions, m?.OptionalPermissions.ToList() ?? [], granted, [.. permissions.Where(p => !granted.Contains(p))],
            [.. permissions.Where(PluginPermissions.IsSensitive)],
            installed.Entry.Enabled, instance?.Running ?? false, instance?.Healthy ?? true,
            problem ?? installed.Manifest.V2Error ?? (installed.Entry.Enabled ? null : null), instance?.LastError,
            m is null ? Empty : Contributions(instance is null ? [] : [instance], locale, m),
            m?.Configuration is { } c ? Schema(c, locale) : null, settings,
            [.. await services.Data.SecretNamesAsync(id, ct)], new Dictionary<string, long>(host.Gateway.Counters(id)),
            [.. m?.Warnings ?? []], installed.Entry.Source == PluginSources.Path, [.. m?.Replaces ?? []]);
    }

    private static readonly PluginV2ContributionsDto Empty = new([], [], [], [], [], [], [], [], [], []);

    // A manifest icon: a Lucide name, a data: image, or a file inside the plugin (served from /assets).
    private static string? Icon(PluginV2Manifest? m)
    {
        if (m?.Icon is not { } icon) return null;
        if (icon.StartsWith("data:image/", StringComparison.Ordinal) && icon.Length < 200_000) return icon;
        if (icon.Contains('.') && !icon.Contains("..")) return $"/api/plugins/v2/{Uri.EscapeDataString(m.Id)}/assets/{icon.TrimStart('/')}";
        return icon.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? icon : null;
    }

    private static PluginV2ContributionsDto Contributions(IEnumerable<PluginV2Instance> plugins, string locale, PluginV2Manifest? only = null)
    {
        var pages = new List<PluginV2PageDto>();
        var slots = new List<PluginV2SlotDto>();
        var menus = new List<PluginV2MenuDto>();
        var commands = new List<PluginV2CommandDto>();
        var settings = new List<PluginV2SettingsDto>();
        var hooks = new List<string>();
        var jobsList = new List<string>();
        var flows = new List<PluginV2FlowDto>();
        var providers = new List<PluginV2ProviderDto>();
        var clients = new List<string>();
        var manifests = only is not null ? [only] : plugins.Where(p => p.Healthy).OrderBy(p => p.SortOrder).Select(p => p.Manifest);
        foreach (var m in manifests)
        {
            pages.AddRange(m.Pages.Select(p => new PluginV2PageDto(m.Id, p.Id, LocalText.Resolve(p.Title, locale) ?? m.DisplayName(locale), p.Icon, p.Nav,
                [.. p.Routes.Select(r => new PluginV2RouteDto(r.Path, r.View))])));
            slots.AddRange(m.Slots.Select(s => new PluginV2SlotDto(m.Id, s.Slot, s.View, LocalText.Resolve(s.Title, locale))));
            menus.AddRange(m.Menus.Select(x => new PluginV2MenuDto(m.Id, x.Menu, x.Command,
                LocalText.Resolve(x.Title, locale) ?? LocalText.Resolve(m.Commands.FirstOrDefault(c => c.Id == x.Command)?.Title, locale) ?? x.Command,
                x.Icon ?? m.Commands.FirstOrDefault(c => c.Id == x.Command)?.Icon)));
            commands.AddRange(m.Commands.Select(c => new PluginV2CommandDto(m.Id, c.Id, LocalText.Resolve(c.Title, locale) ?? c.Id, c.Icon, c.Keybinding)));
            if (m.SettingsView is { } view) settings.Add(new PluginV2SettingsDto(m.Id, view));
            hooks.AddRange(m.Hooks.Select(h => $"{m.Id}/{h.Id}: {string.Join(", ", h.On)}"));
            jobsList.AddRange(m.Jobs.Select(j => $"{m.Id}/{j.Id}: {(j.Every is { } e ? "every " + e : j.Cron)}"));
            flows.AddRange(m.Flows.Select(f => new PluginV2FlowDto(m.Id, f.Id, f.View, LocalText.Resolve(f.Title, locale) ?? f.Id)));
            providers.AddRange(m.Providers.Select(p => new PluginV2ProviderDto(m.Id, p.Id, LocalText.Resolve(p.Name, locale) ?? p.Id,
                PluginV2ProviderSync.RowId(m.Id, p.Id), p.SignInFlow)));
            clients.AddRange(m.Clients.Select(c => Astra.Core.Clients.ClientKinds.PluginPrefix + c.Id));
        }
        return new PluginV2ContributionsDto(pages, slots, menus, commands, settings, hooks, jobsList, flows, providers, clients);
    }

    // The configuration schema with its titles in the caller's language (what the settings form renders).
    private static JsonObject Schema(ConfigurationSchema c, string locale)
    {
        var props = new JsonArray();
        foreach (var p in c.Properties)
        {
            var o = new JsonObject
            {
                ["name"] = p.Name, ["type"] = p.Type, ["title"] = LocalText.Resolve(p.Title, locale) ?? p.Name,
                ["description"] = LocalText.Resolve(p.Description, locale), ["default"] = p.Default?.DeepClone(),
                ["secret"] = p.Secret, ["widget"] = p.Widget, ["unit"] = p.Unit, ["minimum"] = p.Minimum, ["maximum"] = p.Maximum,
                ["pattern"] = p.Pattern, ["itemsType"] = p.ItemsType, ["required"] = p.Required,
            };
            if (p.Enum is { } e)
            {
                o["enum"] = new JsonArray([.. e.Select(x => (JsonNode?)x)]);
                o["enumTitles"] = new JsonArray([.. e.Select(x => (JsonNode?)(LocalText.Resolve(p.EnumTitles?[x], locale) ?? x))]);
            }
            props.Add((JsonNode)o);
        }
        return new JsonObject
        {
            ["properties"] = props,
            ["groups"] = new JsonArray([.. c.Groups.Select(g => (JsonNode?)new JsonObject
            {
                ["title"] = LocalText.Resolve(g.Title, locale), ["properties"] = new JsonArray([.. g.Properties.Select(x => (JsonNode?)x)]),
            })]),
        };
    }

    private static string? Lang(Dictionary<string, string>? texts, string locale)
    {
        if (texts is null || texts.Count == 0) return null;
        var lang = locale.Split('-', '_')[0];
        return texts.GetValueOrDefault(locale) ?? texts.GetValueOrDefault(lang) ?? texts.GetValueOrDefault("en") ?? texts.Values.First();
    }
}
