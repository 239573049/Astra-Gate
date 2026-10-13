using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Data;
using Astra.Plugins;
using Astra.Plugins.Host;
using Astra.Plugins.Market;

namespace Astra.Server.Api;

/// <summary>
/// Mirrors the providers the plugin host serves into Astra's own (plan: .plan/plugins.md, phase 4): each plugin
/// provider is a provider row (<c>plugin-&lt;id&gt;</c>, auth scheme <see cref="AuthSchemes.Plugin"/>) whose
/// endpoints are <c>plugin://</c> addresses, its models the plugin's (each on the one wire API its AI SDK package
/// speaks), and its accounts the plugin's sign-ins (<c>extra.plugin_key</c> names each in plugin-auth.json; the
/// credential never leaves that file). The user's own settings on those rows — enabled, order, proxies, pins —
/// are kept; a provider whose plugin is gone is switched off, not deleted, so bindings and logs survive.
/// </summary>
public sealed class PluginProviderSync(AstraDatabase db, PluginProviders providers, ILogger<PluginProviderSync> logger)
{
    /// <summary>The prefix of a plugin provider's row id.</summary>
    public const string IdPrefix = "plugin-";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The row id of the plugin provider OpenCode calls <paramref name="pluginProviderId"/>.</summary>
    public static string RowId(string pluginProviderId) => IdPrefix + pluginProviderId;

    /// <summary>Reads the host's providers and brings the rows in line. A host that can't be reached changes nothing.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            List<PluginProviderInfo> listed;
            try
            {
                // no plugin runs in the host (the last provider plugin was removed or switched off): nothing is
                // listed, and the host isn't started just to say so
                listed = await providers.Host.HasPluginsAsync(ct)
                    ? await providers.ListAsync(await ProxiesAsync(ct), force: true, ct: ct)
                    : [];
            }
            catch (PluginHostException e)
            {
                logger.LogWarning("Plugin providers not synced: {Error}", e.Message);
                return;
            }
            var rows = (await db.Providers.ListAsync(ct)).Where(p => p.AuthScheme == AuthSchemes.Plugin).ToList();
            foreach (var pp in listed) await SyncProviderAsync(pp, rows.FirstOrDefault(r => r.Id == RowId(pp.Id)), ct);
            foreach (var gone in rows.Where(r => listed.All(pp => RowId(pp.Id) != r.Id) && r.Enabled))
            {
                // its plugin was removed or switched off: kept, but out of service until it is back
                gone.Enabled = false;
                gone.Settings["plugin"]!.AsObject()["absent"] = true;
                await db.Providers.UpdateAsync(gone, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// What a reply said of an account's sign-in: "expired" marks it, "renewed" clears the mark, "kept" leaves
    /// it; without a word a 401 marks it and a success clears it (magpie's notePluginSaid).
    /// </summary>
    public async Task NoteSignInAsync(string pluginProviderId, string key, string said, int status)
    {
        var expired = said switch
        {
            "expired" => true,
            "renewed" => false,
            "kept" => (bool?)null,
            _ => status == 401 ? true : status is >= 200 and < 300 ? false : null,
        };
        if (expired is null) return;
        var account = (await db.Accounts.ListAsync(RowId(pluginProviderId))).FirstOrDefault(a => PluginKeyOf(a) == key);
        if (account is null) return;
        var status2 = expired.Value ? AccountStatus.Expired : AccountStatus.Active;
        if (account.Status != status2) await db.Accounts.SetStatusAsync(account.Id, status2);
    }

    private async Task SyncProviderAsync(PluginProviderInfo pp, Provider? row, CancellationToken ct)
    {
        var protocols = pp.Models.Select(m => PluginProviders.ProtocolOf(pp.Id, m)).Distinct().ToList();
        if (protocols.Count == 0) protocols.Add(Core.ApiProtocol.OpenAIChat);
        var isNew = row is null;
        row ??= new Provider { Id = RowId(pp.Id), Enabled = true, CreatedAt = DateTimeOffset.UtcNow };
        row.Name = pp.Name;
        row.Icon = SafeIcon(pp.Icon) ?? PluginMarket.BuiltInIcon(pp.Id, pp.Spec) ?? row.Icon;
        row.Category = "plugin";
        row.AuthScheme = AuthSchemes.Plugin;
        row.AdapterId = "plugin:" + pp.Id;
        row.Endpoints = protocols.Select(p => new ProviderEndpoint { Protocol = p, BaseUrl = PluginFetchHandler.BaseFor(pp.Id, p) }).ToList();
        row.PreferredUpstreamProtocols = protocols;
        var settings = row.Settings["plugin"] as JsonObject ?? new JsonObject();
        if (settings["absent"] is not null)
        {
            // back after its plugin was gone: in service again
            settings.Remove("absent");
            row.Enabled = true;
        }
        settings["spec"] = pp.Spec;
        settings["provider"] = pp.Id;
        settings["usage"] = pp.Usage;
        settings["checkin"] = pp.Checkin;
        row.Settings["plugin"] = settings;

        if (isNew) await db.Providers.InsertWithModelsAsync(row, [], ct);
        else await db.Providers.UpdateAsync(row, ct);
        await SyncModelsAsync(pp, row, ct);
        await SyncAccountsAsync(pp, row, ct);
    }

    private async Task SyncModelsAsync(PluginProviderInfo pp, Provider row, CancellationToken ct)
    {
        var existing = (await db.Providers.ListModelsAsync(row.Id, ct)).ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var order = existing.Values.Select(m => m.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        foreach (var m in pp.Models)
        {
            var overrides = new ModelOverrides
            {
                DisplayName = m.Name,
                ContextWindow = m.ContextWindow > 0 ? m.ContextWindow : null,
                MaxOutputTokens = m.Output > 0 ? m.Output : null,
                Capabilities = new ModelCapabilities { Vision = m.Image ? true : null, Reasoning = m.Reasoning ? true : null },
            };
            List<Core.ApiProtocol> protocols = [PluginProviders.ProtocolOf(pp.Id, m)];
            if (existing.Remove(m.Id, out var current))
            {
                current.Overrides = overrides;
                current.UpstreamProtocols = protocols;
                await db.Providers.UpdateModelAsync(current, ct);
                await db.Providers.UpdateModelProtocolsAsync(current.Id, protocols, ct);
                continue;
            }
            await db.Providers.InsertModelAsync(new ProviderModel
            {
                ProviderId = row.Id,
                ModelId = m.Id,
                SystemModelId = ModelIdMatcher.Match(m.UpstreamId, candidates, null),
                Overrides = overrides,
                // a model that answers decisions rather than a conversation isn't offered to agents
                Enabled = !m.Decides,
                SortOrder = order++,
                UpstreamProtocols = protocols,
            }, ct);
        }
        // the plugin no longer lists them
        foreach (var stale in existing.Values) await db.Providers.DeleteModelAsync(stale.Id, ct);
    }

    private async Task SyncAccountsAsync(PluginProviderInfo pp, Provider row, CancellationToken ct)
    {
        var accounts = await db.Accounts.ListAsync(row.Id, ct);
        foreach (var a in pp.Accounts)
        {
            var name = a.AccountId ?? (a.Hint is { } hint ? $"{pp.Name} …{hint}" : pp.Name);
            if (accounts.FirstOrDefault(x => PluginKeyOf(x) == a.Key) is { } known)
            {
                if (known.DisplayName != name) await db.Accounts.SetDisplayNameAsync(known.Id, name, ct);
                continue;
            }
            await db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = Ulid.NewUlid(),
                ProviderId = row.Id,
                DisplayName = name,
                AccountEmail = a.AccountId is { } id && id.Contains('@') ? id : null,
                Status = AccountStatus.Active,
                Kind = AccountKinds.Plugin,
                Source = AccountSources.Plugin,
                Extra = new JsonObject { ["plugin_key"] = a.Key, ["plugin_type"] = a.Type },
            }, ct);
        }
        // signed out in the plugin (here, or in another app sharing plugin-auth.json)
        foreach (var gone in accounts.Where(x => PluginKeyOf(x) is { } key && pp.Accounts.All(a => a.Key != key)))
            await db.Accounts.DeleteAsync(gone.Id, ct);
    }

    // Each plugin provider's and account's own proxy, for its model listing: {id: {"": p, key: p}}.
    private async Task<JsonObject> ProxiesAsync(CancellationToken ct)
    {
        var proxies = new JsonObject();
        foreach (var row in (await db.Providers.ListAsync(ct)).Where(p => p.AuthScheme == AuthSchemes.Plugin))
        {
            var id = Gateway.Pipeline.GatewayPipeline.PluginProviderOf(row);
            var entry = new JsonObject();
            if (row.HttpProxy is { Length: > 0 } p) entry[""] = p;
            foreach (var a in await db.Accounts.ListAsync(row.Id, ct))
                if (a.HttpProxy is { Length: > 0 } ap && PluginKeyOf(a) is { } key) entry[key] = ap;
            if (entry.Count > 0) proxies[id] = entry;
        }
        return proxies;
    }

    public static string? PluginKeyOf(ProviderAccount account) => Gateway.Pipeline.UpstreamAuthResolver.PluginKeyOf(account);

    private static string? SafeIcon(string? icon) =>
        icon is not null && (icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             || (icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !icon.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase) && icon.Length <= 64 * 1024))
            ? icon
            : null;
}

/// <summary>
/// Keeps the plugin providers in sync: at start, whenever the plugin list changes (installed, updated, switched
/// on or off, removed), whenever a sign-in changes, and on each reply's word on a sign-in.
/// </summary>
public sealed class PluginProviderWorker(PluginProviderSync sync, PluginProviders providers, PluginCatalog catalog, PluginFetchHandler transport, ILogger<PluginProviderWorker> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        providers.Host.Changed += Wake;
        catalog.Changed += OnPluginsChanged;
        transport.SignIn += OnSignIn;
        try
        {
            // the first sync starts the host only when some plugin runs in it
            if (await providers.Host.HasPluginsAsync(stoppingToken)) Wake();
            while (!stoppingToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(stoppingToken);
                // a burst of sign-in events is one sync
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                try
                {
                    await sync.SyncAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Plugin provider sync failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            providers.Host.Changed -= Wake;
            catalog.Changed -= OnPluginsChanged;
            transport.SignIn -= OnSignIn;
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void OnPluginsChanged(long generation) => Wake();

    private void OnSignIn(string provider, string key, string said, int status) =>
        _ = sync.NoteSignInAsync(provider, key, said, status).ContinueWith(
            t => logger.LogWarning(t.Exception, "Noting a plugin sign-in failed"), TaskContinuationOptions.OnlyOnFaulted);
}
