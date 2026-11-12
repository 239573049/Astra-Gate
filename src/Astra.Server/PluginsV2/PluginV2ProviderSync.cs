using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Data;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Runtime;
using Astra.Server.Api;

namespace Astra.Server.PluginsV2;

/// <summary>
/// The provider rows of v2 plugins (design/plugin-v2 08 §2): one row per declared provider, id <c>plugin2-&lt;plugin&gt;-&lt;provider&gt;</c>,
/// <c>AuthScheme = plugin</c> so the gateway sends through the plugin transport, <c>settings.plugin.v2</c> so that transport hands it
/// to the plugin's prepare. A provider that goes away keeps its row, switched off and marked absent (pools, bindings and the
/// request log still name it). Also the account store behind <c>providers.accounts.*</c>: provider accounts of kind plugin whose
/// <c>extra.plugin_key</c> is the plugin's key; the credential is the plugin's host-held secret, never on the row.
/// </summary>
public sealed class PluginV2ProviderSync(AstraDatabase db, PluginV2Host host, ILogger<PluginV2ProviderSync> logger) : IPluginProviderAccounts
{
    public const string IdPrefix = "plugin2-";
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The row id of provider <paramref name="providerId"/> of plugin <paramref name="pluginId"/>.</summary>
    public static string RowId(string pluginId, string providerId) => IdPrefix + pluginId.Replace('.', '-') + "-" + providerId;

    private static bool IsV2Row(Provider p) => p.Id.StartsWith(IdPrefix, StringComparison.Ordinal) && Gateway.Pipeline.GatewayPipeline.V2PluginOf(p) is not null;

    private static ApiProtocol Protocol(string p) => p switch
    {
        "responses" => ApiProtocol.OpenAIResponses,
        "anthropic" => ApiProtocol.Anthropic,
        "gemini" => ApiProtocol.Gemini,
        _ => ApiProtocol.OpenAIChat,
    };

    /// <summary>Brings the rows in line with the running v2 plugins' providers.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var rows = (await db.Providers.ListAsync(ct)).Where(IsV2Row).ToList();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in await host.ListAsync(ct))
                foreach (var pp in p.Manifest.Providers)
                {
                    var id = RowId(p.Id, pp.Id);
                    listed.Add(id);
                    try
                    {
                        await SyncProviderAsync(p, pp, rows.FirstOrDefault(r => r.Id == id), ct);
                    }
                    catch (Exception e) when (e is PluginCallException or InvalidOperationException)
                    {
                        logger.LogWarning("v2 provider {Id} not synced: {Error}", id, e.Message);
                    }
                }
            foreach (var gone in rows.Where(r => !listed.Contains(r.Id) && r.Enabled))
            {
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

    private async Task SyncProviderAsync(PluginV2Instance p, ProviderContribution pp, Provider? row, CancellationToken ct)
    {
        var models = pp.Models.ToList();
        // the code's models() (optional) adds to or replaces the manifest's list
        if (await host.ProviderCallAsync(p.Id, pp.Id, "models", null, null, ct) is JsonArray fromCode)
            foreach (var m in fromCode.OfType<JsonObject>())
                if (m["id"]?.GetValue<string>() is { } mid)
                {
                    models.RemoveAll(x => x.Id == mid);
                    models.Add(new ProviderModelContribution(mid, m["name"]?.DeepClone(), m["protocol"]?.GetValue<string>() ?? pp.Protocols[0],
                        m["context"] is JsonValue c && c.TryGetValue<long>(out var cl) ? cl : null, m["output"] is JsonValue o && o.TryGetValue<long>(out var ol) ? ol : null,
                        m["vision"]?.GetValue<bool>() == true, m["reasoning"]?.GetValue<bool>() == true));
                }
        var protocols = pp.Protocols.Select(Protocol).Distinct().ToList();
        var isNew = row is null;
        row ??= new Provider { Id = RowId(p.Id, pp.Id), Enabled = true, CreatedAt = DateTimeOffset.UtcNow };
        row.Name = LocalText.Resolve(pp.Name, "en") ?? pp.Id;
        row.Icon = PluginProviderSync.SafeIcon(pp.Icon) ?? pp.Icon ?? row.Icon;
        row.Category = "plugin";
        row.AuthScheme = AuthSchemes.Plugin;
        row.AdapterId = "plugin2:" + p.Id + "/" + pp.Id;
        row.Endpoints = protocols.Select(x => new ProviderEndpoint { Protocol = x, BaseUrl = V2ProviderTransport.BaseFor(p.Id, pp.Id, x) }).ToList();
        row.PreferredUpstreamProtocols = protocols;
        var settings = row.Settings["plugin"] as JsonObject ?? [];
        if (settings["absent"] is not null)
        {
            settings.Remove("absent");
            row.Enabled = true;
        }
        settings["v2"] = true;
        settings["plugin"] = p.Id;
        settings["provider"] = pp.Id;
        settings["spec"] = p.Spec;
        settings["signIn"] = pp.SignInFlow;
        row.Settings["plugin"] = settings;
        if (isNew) await db.Providers.InsertWithModelsAsync(row, [], ct);
        else await db.Providers.UpdateAsync(row, ct);

        var existing = (await db.Providers.ListModelsAsync(row.Id, ct)).ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var order = existing.Values.Select(m => m.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        foreach (var m in models)
        {
            var overrides = new ModelOverrides
            {
                DisplayName = LocalText.Resolve(m.Name, "en"),
                ContextWindow = m.Context > 0 ? m.Context : null,
                MaxOutputTokens = m.Output > 0 ? m.Output : null,
                Capabilities = new ModelCapabilities { Vision = m.Vision ? true : null, Reasoning = m.Reasoning ? true : null },
            };
            List<ApiProtocol> mp = [Protocol(m.Protocol)];
            if (existing.Remove(m.Id, out var current))
            {
                current.Overrides = overrides;
                current.UpstreamProtocols = mp;
                await db.Providers.UpdateModelAsync(current, ct);
                await db.Providers.UpdateModelProtocolsAsync(current.Id, mp, ct);
                continue;
            }
            await db.Providers.InsertModelAsync(new ProviderModel
            {
                ProviderId = row.Id, ModelId = m.Id, SystemModelId = ModelIdMatcher.Match(m.Id, candidates, null), Overrides = overrides,
                Enabled = true, SortOrder = order++, UpstreamProtocols = mp,
            }, ct);
        }
        foreach (var stale in existing.Values) await db.Providers.DeleteModelAsync(stale.Id, ct);
    }

    // ---- IPluginProviderAccounts: the accounts a plugin keeps through providers.accounts.*

    private async Task<string> EnsureRowAsync(string pluginId, string providerId, CancellationToken ct)
    {
        var id = RowId(pluginId, providerId);
        if (await db.Providers.GetAsync(id, ct) is null) await SyncAsync(ct);
        return id;
    }

    private static JsonObject View(ProviderAccount a) => new()
    {
        ["key"] = a.Extra["plugin_key"]?.GetValue<string>(), ["displayName"] = a.DisplayName, ["email"] = a.AccountEmail, ["plan"] = a.Plan,
        ["status"] = a.Status, ["enabled"] = a.Enabled,
    };

    public async Task<JsonArray> ListAsync(string pluginId, string providerId, CancellationToken ct)
    {
        var accounts = await db.Accounts.ListAsync(RowId(pluginId, providerId), ct);
        return new JsonArray([.. accounts.Where(a => PluginProviderSync.PluginKeyOf(a) is not null).Select(a => (JsonNode?)View(a))]);
    }

    public async Task SetAsync(string pluginId, string providerId, JsonObject account, CancellationToken ct)
    {
        var rowId = await EnsureRowAsync(pluginId, providerId, ct);
        var key = account["key"]!.GetValue<string>();
        var known = (await db.Accounts.ListAsync(rowId, ct)).FirstOrDefault(a => PluginProviderSync.PluginKeyOf(a) == key);
        string? Str(string k) => account[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (known is null)
        {
            await db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = Ulid.NewUlid(), ProviderId = rowId, DisplayName = Str("displayName") ?? key, AccountEmail = Str("email"), Plan = Str("plan"),
                Status = AccountStatus.Active, Kind = AccountKinds.Plugin, Source = AccountSources.Plugin,
                Extra = new JsonObject { ["plugin_key"] = key, ["v2"] = true },
            }, ct);
            return;
        }
        if (Str("displayName") is { } name) known.DisplayName = name;
        if (account.ContainsKey("email")) known.AccountEmail = Str("email");
        if (account.ContainsKey("plan")) known.Plan = Str("plan");
        if (Str("status") is "active" or "expired" or "revoked") known.Status = Str("status")!;
        await db.Accounts.UpdateAsync(known, ct);
    }

    public async Task<bool> RemoveAsync(string pluginId, string providerId, string key, CancellationToken ct)
    {
        var known = (await db.Accounts.ListAsync(RowId(pluginId, providerId), ct)).FirstOrDefault(a => PluginProviderSync.PluginKeyOf(a) == key);
        return known is not null && await db.Accounts.DeleteAsync(known.Id, ct);
    }

    /// <summary>A v2 provider's refresh had its say (the gateway after a 401): "expired" marks the account, "renewed" clears it.</summary>
    public async Task NoteSignInAsync(string rowId, string key, string said)
    {
        var status = said switch { "expired" => AccountStatus.Expired, "renewed" => AccountStatus.Active, _ => null };
        if (status is null) return;
        var account = (await db.Accounts.ListAsync(rowId)).FirstOrDefault(a => PluginProviderSync.PluginKeyOf(a) == key);
        if (account is not null && account.Status != status) await db.Accounts.SetStatusAsync(account.Id, status);
    }
}

/// <summary>Keeps the v2 provider rows current: at start and whenever a v2 plugin starts or stops (debounced).</summary>
public sealed class PluginV2ProviderWorker(PluginV2ProviderSync sync, PluginV2Host host, Astra.Plugins.Host.PluginFetchHandler transport,
    PluginV2HostServices services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // set here, not in the constructor: the sync needs the host, which needs the services
        services.ProviderAccounts = sync;
        var wake = new SemaphoreSlim(0);
        host.PluginChanged += _ => wake.Release();
        transport.V2SignInSaid += (row, key, said, status) => _ = sync.NoteSignInAsync(row, key, said);
        try
        {
            await host.EnsureAsync(stoppingToken);
            await sync.SyncAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await wake.WaitAsync(stoppingToken);
                await Task.Delay(500, stoppingToken);
                while (wake.CurrentCount > 0) await wake.WaitAsync(stoppingToken);
                await sync.SyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
