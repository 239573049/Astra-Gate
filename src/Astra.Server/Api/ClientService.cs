using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;
using Astra.Server.Hosting;

namespace Astra.Server.Api;



/// <summary>Client admin operations; file changes are serialized so two UI requests cannot overwrite each other.</summary>
public sealed class ClientService(
    AstraDatabase db, ISecretProtector secrets, ClientEnvironment env, AstraPaths paths, ServerOptions server,
    ClientInstallService installs, ClientRouting routing, IAgentPluginCatalog agents, SmartRouter smartRouter)
{
    /// <summary>The key in a plugin client's extras holding its plugin's descriptor, for restoring it offline.</summary>
    public const string PluginExtrasKey = "plugin";

    private volatile ClientAdapterRegistry _registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState);
    private (long Generation, string Remembered) _registryKey = (-1, "");
    private readonly ClientConfigApplier _applier = new(db.ClientConfigState, env, paths.ClientBackupsDir);
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>The last staleness check of each client (<see cref="IsOutdatedAsync"/>); emptied by every write (<see cref="MutateAsync"/>).</summary>
    private readonly ConcurrentDictionary<string, OutdatedCheck> _outdatedChecks = new(StringComparer.Ordinal);

    private sealed record OutdatedCheck(EnableContext Context, IReadOnlyList<FileProbe> Reads, bool Outdated);

    public async Task<IReadOnlyList<ClientInfoDto>> ListAsync(CancellationToken ct = default)
    {
        await RefreshRegistryAsync(ct);
        // Probes every client's version in parallel up front (cached afterwards), so InfoAsync below stays cheap.
        var install = await installs.DescribeAllAsync(ct);
        var result = new List<ClientInfoDto>();
        var defaultProvider = DefaultProvider(ct); // read once for the whole list, only if some client needs it
        foreach (var adapter in _registry.All) result.Add(await InfoAsync(adapter, ct, install.GetValueOrDefault(adapter.Kind), defaultProvider));
        return result;
    }

    /// <summary>
    /// Every client's name, enabled flag and primary binding, as <see cref="ListAsync"/> reports them, from the database
    /// alone: no config file is read and no drift, staleness or install check runs. The desktop tray polls this every 10 s;
    /// the full list costs tens of milliseconds and megabytes on a machine with large client configs.
    /// </summary>
    public async Task<IReadOnlyList<ClientSummaryDto>> SummaryAsync(CancellationToken ct = default)
    {
        await RefreshRegistryAsync(ct);
        var defaultProvider = DefaultProvider(ct);
        var result = new List<ClientSummaryDto>();
        foreach (var adapter in _registry.All)
        {
            var enabled = (await db.Clients.GetAsync(adapter.Kind, ct))?.Enabled ?? false;
            var bindings = await BindingsOrDefaultAsync(adapter.Kind, enabled, defaultProvider, ct);
            var primary = bindings.Count > 0 ? bindings[0] : null;
            result.Add(new ClientSummaryDto(adapter.Kind, DisplayName(adapter), enabled, primary?.ProviderId, primary?.AccountId));
        }
        return result;
    }

    private static string DisplayName(IClientAdapter adapter) => adapter is PluginClientAdapter p ? p.Agent.Name : NameOf(adapter.Kind);

    /// <summary>
    /// Sets the client's primary provider (priority 0), keeping its other bindings; the provider is dropped from a lower
    /// slot if it held one. accountId semantics (plan §5.4): null = keep the pin the provider already has for this
    /// client, "" = clear the pin (use the provider's default account), an id = pin it.
    /// </summary>
    public async Task<ClientInfoDto> SetBindingAsync(string kind, string providerId, string? accountId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var adapter = Require(kind);
            await RequireProviderAsync(providerId, ct);
            var bindings = await db.Clients.ListBindingsAsync(kind, ct);
            var pinned = accountId switch
            {
                null => bindings.FirstOrDefault(b => b.ProviderId == providerId)?.AccountId,
                "" => null,
                _ => await RequireAccountAsync(providerId, accountId, ct),
            };
            await db.Clients.ReplaceBindingsAsync(kind, WithPrimary(bindings, kind, providerId, pinned), ct);
            if (ClientKinds.HasModelList(kind)) await SyncModelListCoreAsync(kind, null, ct);
            return await InfoAsync(adapter, ct);
        }, ct);

    /// <summary>
    /// Replaces the client's provider list; the order is the routing order (a request goes to the first provider that
    /// serves its model, else the first — see <see cref="ClientRouting"/>). Providers must exist and be distinct; a disabled one may stay
    /// only if it is already bound. accountId: null = the provider's default account, an id = pin it.
    /// </summary>
    public async Task<ClientInfoDto> SetBindingsAsync(string kind, IReadOnlyList<ClientBindingDto> input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var adapter = Require(kind);
            if (input.Count == 0) throw new AdminApiException(400, "Choose at least one provider");
            if (input.Select(b => b.ProviderId).Distinct().Count() != input.Count) throw new AdminApiException(400, "Each provider can be bound only once");
            var current = (await db.Clients.ListBindingsAsync(kind, ct)).Select(b => b.ProviderId).ToHashSet();
            var next = new List<ClientBinding>();
            foreach (var b in input)
            {
                if (string.IsNullOrWhiteSpace(b.ProviderId)) throw new AdminApiException(400, "providerId is required");
                if (current.Contains(b.ProviderId))
                {
                    if (await db.Providers.GetAsync(b.ProviderId, ct) is null) throw new AdminApiException(404, "Provider not found");
                }
                else await RequireProviderAsync(b.ProviderId, ct);
                var pinned = string.IsNullOrEmpty(b.AccountId) ? null : await RequireAccountAsync(b.ProviderId, b.AccountId, ct);
                next.Add(new ClientBinding { ClientKind = kind, ProviderId = b.ProviderId, AccountId = pinned });
            }
            await db.Clients.ReplaceBindingsAsync(kind, next, ct);
            if (ClientKinds.HasModelList(kind)) await SyncModelListCoreAsync(kind, null, ct);
            return await InfoAsync(adapter, ct);
        }, ct);

    /// <summary>The provider a client nobody has configured yet defaults to: the first usable one, in the providers page's order.</summary>
    private Lazy<Task<string?>> DefaultProvider(CancellationToken ct) =>
        new(async () => (await db.Providers.ListAsync(ct)).FirstOrDefault(ClientRouting.IsUsable)?.Id);

    /// <summary>
    /// The client's bindings, with a default for one nobody has chosen a provider for yet: until it is enabled, a client
    /// with nothing stored is bound to <paramref name="defaultProvider"/>, so it is ready to enable the way cc-switch
    /// treats its first provider as current. Read side only — nothing is stored until the client is enabled or its list
    /// is edited, and an enabled client with no bindings stays unbound (the gateway refuses it, so the page must not
    /// claim a provider).
    /// </summary>
    private async Task<IReadOnlyList<ClientBinding>> BindingsOrDefaultAsync(string kind, bool enabled, Lazy<Task<string?>> defaultProvider, CancellationToken ct)
    {
        var bindings = await db.Clients.ListBindingsAsync(kind, ct);
        if (bindings.Count > 0 || enabled) return bindings;
        return await defaultProvider.Value is { } id ? [new ClientBinding { ClientKind = kind, ProviderId = id }] : bindings;
    }

    /// <summary>The bindings with <paramref name="providerId"/> as the primary: it replaces slot 0, later slots keep their order.</summary>
    private static List<ClientBinding> WithPrimary(IReadOnlyList<ClientBinding> bindings, string kind, string providerId, string? accountId) =>
        [
            new ClientBinding { ClientKind = kind, ProviderId = providerId, AccountId = accountId },
            .. bindings.Skip(1).Where(b => b.ProviderId != providerId),
        ];

    /// <summary>
    /// Plan §7.6: OpenCode (and the other <see cref="ClientKinds.WithModelList"/> clients)
    /// list the bound providers' models in their config. Keeps those lists current when a provider's models change
    /// (<paramref name="providerId"/> = the changed provider; null = whatever is bound). Never touches a config the user
    /// edited (drift). Returns true when any file was rewritten.
    /// </summary>
    public async Task<bool> SyncModelListsAsync(string? providerId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var changed = false;
            foreach (var kind in _registry.All.Select(a => a.Kind).Where(ClientKinds.HasModelList))
                changed |= await SyncModelListCoreAsync(kind, providerId, ct);
            return changed;
        }, ct);

    private async Task<bool> SyncModelListCoreAsync(string kind, string? providerId, CancellationToken ct)
    {
        var adapter = Require(kind);
        var record = await db.Clients.GetAsync(kind, ct);
        if (record is not { Enabled: true }) return false;
        var bindings = await db.Clients.ListBindingsAsync(kind, ct);
        if (bindings.Count == 0 || (providerId is not null && bindings.All(b => b.ProviderId != providerId))) return false;
        var targets = await routing.TargetsAsync(bindings, ct);
        if (targets.Count == 0) return false;
        try
        {
            var status = adapter.Inspect();
            if (!status.Enabled || status.DriftedKeys.Count > 0) return false;
            if (await ClientKeyAsync(record, ct) is not { } key) return false;
            var context = await ContextAsync(kind, targets, record, key, ct);
            var plan = adapter.PlanEnable(context); // rewrites the Astra provider as a whole so its recorded applied value stays exact
            if (plan.Diffs.Count == 0) return false;
            Apply(adapter, plan);
            record.ExtraJson = context.Extras?.ToJsonString();
            record.AppliedAt = DateTimeOffset.UtcNow;
            await db.Clients.UpsertAsync(record, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false; // the client page reports unreadable / drifted configs on its own
        }
    }

    public async Task<ClientPreviewDto> PreviewAsync(string kind, JsonObject input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var (adapter, record, context, _, _, warnings) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, context, input);
            return new ClientPreviewDto(plan.Changes.Select(c => new ConfigChangeDto(c.File,
                c.Format.ToString().ToLowerInvariant(), c.KeyPath, c.Before, c.After)).ToList(),
                plan.Diffs.Select(d => new FileDiffDto(d.File, d.UnifiedDiff)).ToList(), warnings);
        }, ct);

    public async Task<ClientInfoDto> EnableAsync(string kind, JsonObject input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var (adapter, record, context, providerId, tokenId, _) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, context, input); // parse/validate before writing
            Apply(adapter, plan);
            record.Enabled = true;
            record.TokenId = tokenId;
            record.SelectedModel = context.Model;
            record.ExtraJson = context.Extras?.ToJsonString();
            record.AppliedAt = DateTimeOffset.UtcNow;
            // Once files have been written, finish the metadata commit even if the caller disconnected.
            await db.Clients.UpsertAsync(record, CancellationToken.None);
            var bindings = await db.Clients.ListBindingsAsync(kind);
            if (bindings.Count == 0 || bindings[0].ProviderId != providerId)
                await db.Clients.ReplaceBindingsAsync(kind,
                    WithPrimary(bindings, kind, providerId, bindings.FirstOrDefault(b => b.ProviderId == providerId)?.AccountId));
            return await InfoAsync(adapter, CancellationToken.None);
        }, ct);

    public async Task<ClientDisableDto> DisableAsync(string kind, bool force, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var adapter = Require(kind);
            var plan = adapter.PlanDisable();
            var report = force ? _applier.ForceRestore(plan) : _applier.Disable(plan);
            if (await db.Clients.GetAsync(kind) is { } record)
            {
                record.Enabled = false;
                await db.Clients.UpsertAsync(record);
            }
            return new ClientDisableDto(
                report.Keys.Where(k => k.Outcome == RestoreKeyOutcome.Restored).Select(KeyLabel).Concat(report.DeletedFiles).Distinct().ToList(),
                report.Keys.Where(k => k.Outcome == RestoreKeyOutcome.Drifted).Select(KeyLabel).ToList(),
                await InfoAsync(adapter, CancellationToken.None));
        }, ct);

    /// <summary>
    /// Fixes the config files the client cannot read as they are (comments or trailing commas in a file it parses as
    /// strict JSON), through the applier so the usual backups are taken. Nothing to fix is not an error.
    /// </summary>
    public async Task<ClientInfoDto> RepairAsync(string kind, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var adapter = Require(kind);
            _applier.Repair(kind, adapter.Inspect().Repairs.Select(r => r.File));
            return await InfoAsync(adapter, CancellationToken.None);
        }, ct);

    public IReadOnlyList<ClientBackupDto> Backups(string kind)
    {
        Require(kind);
        return _applier.ListBackups(kind).OrderByDescending(b => b.IsFirstWrite).ThenByDescending(b => b.CreatedAt)
            .Select(b => new ClientBackupDto(b.BackupId, b.CreatedAt, b.Files.Select(f => f.OriginalPath).ToList(), b.IsFirstWrite)).ToList();
    }

    public async Task<ClientInfoDto> RestoreBackupAsync(string kind, string backupId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var adapter = Require(kind);
            // Only allow a real, listed backup id — never let the URL turn into a filesystem path.
            if (!_applier.ListBackups(kind).Any(b => b.BackupId == backupId)) throw new AdminApiException(404, "Backup not found");
            _applier.RestoreFromBackup(kind, backupId);
            if (await db.Clients.GetAsync(kind) is { } record)
            {
                var inspected = adapter.Inspect();
                record.Enabled = record.Enabled && inspected.Enabled && inspected.DriftedKeys.Count == 0;
                await db.Clients.UpsertAsync(record);
            }
            return await InfoAsync(adapter, CancellationToken.None);
        }, ct);

    /// <summary>
    /// Rewrites every enabled client that uses the token (after its key was reset, or after its clients were moved to
    /// it), through the applier so the restore invariants hold. Drifted or unreadable configs are never touched and are
    /// reported as skipped; configs that already hold the current values are left as they are.
    /// </summary>
    public async Task<TokenRewriteResult> RewriteForTokenAsync(string tokenId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var rewritten = new List<string>();
            var skipped = new List<string>();
            foreach (var record in await db.Clients.ListByTokenAsync(tokenId, tokenId == TokenIds.Default, ct))
            {
                if (!record.Enabled || _registry.Get(record.Kind) is not { } adapter) continue;
                try
                {
                    var status = adapter.Inspect();
                    if (!status.Enabled || status.DriftedKeys.Count > 0 || await ClientKeyAsync(record, ct) is not { } key)
                    {
                        skipped.Add(record.Kind);
                        continue;
                    }
                    // A client without a usable provider keeps the stored model list instead of rebuilding it.
                    var targets = await routing.TargetsAsync(await db.Clients.ListBindingsAsync(record.Kind, ct), ct);
                    var context = await ContextAsync(record.Kind, targets, record, key, ct);
                    var plan = adapter.PlanEnable(context);
                    if (plan.Diffs.Count == 0) continue;
                    Apply(adapter, plan);
                    record.ExtraJson = context.Extras?.ToJsonString();
                    record.AppliedAt = DateTimeOffset.UtcNow;
                    await db.Clients.UpsertAsync(record, CancellationToken.None);
                    rewritten.Add(record.Kind);
                }
                catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException
                                               or System.Security.Cryptography.CryptographicException)
                {
                    skipped.Add(record.Kind);
                }
            }
            return new TokenRewriteResult(rewritten, skipped);
        }, ct);

    /// <summary>
    /// Uninstalls a client (an install job with action "uninstall"). A client Astra has taken over gets its original config
    /// back first, through the regular disable path (so the restore invariants hold: drifted values are left as the user
    /// changed them) — a removed client must not leave Astra's settings behind, and once its files are gone there may be
    /// nothing left that would let the user restore them. The request is checked before anything is restored, so a refused
    /// uninstall changes nothing; a failed restore stops it before the command runs.
    /// </summary>
    public async Task<ClientInstallJobDto> UninstallAsync(string kind, bool elevated, CancellationToken ct)
    {
        installs.EnsureCanStart(kind, "uninstall", elevated);
        var prelude = new List<string>();
        if (await IsEnabledAsync(kind, ct))
        {
            var report = await DisableAsync(kind, false, ct);
            prelude.Add($"已恢复 {report.Client.Name} 的原始配置，Astra 不再接管它。");
            if (report.Drifted.Count > 0) prelude.Add($"这些配置项已被手动修改，保持原样：{string.Join(", ", report.Drifted)}");
            prelude.Add("");
        }
        return installs.Start(kind, "uninstall", elevated, prelude);
    }

    /// <summary>Whether the client is enabled in Astra.</summary>
    public async Task<bool> IsEnabledAsync(string kind, CancellationToken ct) => (await db.Clients.GetAsync(kind, ct))?.Enabled == true;

    /// <summary>
    /// The model ids the client can use: enabled models of its usable bound providers, each id once, and smart routing's
    /// virtual model while it is on.
    /// </summary>
    public async Task<IReadOnlyList<string>> ModelsAsync(string kind, CancellationToken ct)
    {
        await RefreshRegistryAsync(ct);
        Require(kind);
        var targets = await routing.TargetsAsync(await BindingsOrDefaultAsync(kind, await IsEnabledAsync(kind, ct), DefaultProvider(ct), ct), ct);
        var models = (await routing.ListModelsAsync(targets, ct)).Select(m => m.Id).ToList();
        if (smartRouter.AdvertisedModel is { } virtualModel && !models.Contains(virtualModel)) models.Add(virtualModel);
        return models;
    }

    private async Task<(IClientAdapter Adapter, ClientRecord Record, EnableContext Context, string ProviderId, string TokenId, List<string> Warnings)>
        PrepareAsync(string kind, JsonObject input, CancellationToken ct)
    {
        var adapter = Require(kind);
        if (adapter.Availability != ClientAvailability.Available) throw new AdminApiException(409, adapter.UnavailableReason ?? "Client not supported yet");
        var record = await db.Clients.GetAsync(kind, ct) ?? new ClientRecord { Kind = kind };
        var bindings = await BindingsOrDefaultAsync(kind, record.Enabled, DefaultProvider(ct), ct);
        var providerId = input["providerId"]?.GetValue<string>() ?? bindings.FirstOrDefault()?.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId)) throw new AdminApiException(400, "Choose a provider first");
        await RequireProviderAsync(providerId, ct);
        var next = Json.Deserialize<ClientRecord>(Json.Serialize(record))!;
        if (input.ContainsKey("model")) next.SelectedModel = Trim(input["model"]?.GetValue<string>());
        if (input.ContainsKey("extras"))
        {
            if (input["extras"] is not null and not JsonObject) throw new AdminApiException(400, "extras must be an object");
            if (input["extras"]?[ClientOptions.ExtrasKey] is { } options)
            {
                if (options is not JsonObject optionsObject) throw new AdminApiException(400, "extras.options must be an object");
                if (ClientOptions.Validate(kind, optionsObject) is { } invalid) throw new AdminApiException(400, invalid);
            }
            if (input["extras"]?[ClientProtocols.ExtrasKey] is { } overrides)
            {
                if (overrides is not JsonObject overridesObject) throw new AdminApiException(400, "extras.protocolOverrides must be an object");
                if (ClientProtocols.Validate(kind, overridesObject) is { } invalid) throw new AdminApiException(400, invalid);
            }
            next.ExtraJson = input["extras"]?.ToJsonString();
        }
        // Plan §7.5: the gateway maps Claude Desktop's role ids, so at least one role must point at a model.
        if (kind == ClientKinds.ClaudeDesktop && ClaudeDesktopRoles.Parse(next.ExtraJson).Count == 0)
            throw new AdminApiException(400, "Claude Desktop needs at least one role mapping (sonnet / opus / haiku → a provider model)");
        // Tokens: the chosen token, else the one this client already uses, else the default token.
        var tokenId = Trim(input["tokenId"]?.GetValue<string>()) ?? record.TokenId ?? TokenIds.Default;
        var token = await db.Tokens.GetAsync(tokenId, ct) ?? throw new AdminApiException(404, "Token not found");
        if (!token.Enabled) throw new AdminApiException(409, "Token is disabled");
        if (token.KeyEnc is null) throw new AdminApiException(409, "Token has no key yet");
        var key = GatewayTokens.ForClient(secrets.Unprotect(token.KeyEnc), kind);
        // The providers as they will be bound after enabling: the chosen one first, then the client's others.
        var targets = await routing.TargetsAsync(
            WithPrimary(bindings, kind, providerId, bindings.FirstOrDefault(b => b.ProviderId == providerId)?.AccountId), ct);
        var context = await ContextAsync(kind, targets, next, key, ct);
        var warnings = adapter.Inspect().Warnings.ToList();
        return (adapter, record, context, providerId, token.Id, warnings);
    }

    private async Task<EnableContext> ContextAsync(string kind, IReadOnlyList<RouteTarget> targets, ClientRecord record, string key, CancellationToken ct)
    {
        var extras = Json.Deserialize<JsonObject>(record.ExtraJson) ?? new JsonObject();
        // A plugin's client keeps its plugin's descriptor, so it can be restored once the plugin is gone (restore-all).
        if (_registry.Get(kind) is PluginClientAdapter plugin)
            extras[PluginExtrasKey] = JsonNode.Parse(Json.Serialize(plugin.Agent));
        if (ClientKinds.HasModelList(kind) && targets.Count > 0)
        {
            // Every bound provider's models and mapped ids, each id once (ClientRouting): the gateway routes each to its
            // provider. A mapped id carries its target model's capabilities under its own name.
            var list = new JsonObject();
            foreach (var (id, target, pm, effective) in await routing.ListModelsAsync(targets, ct))
            {
                var entry = new JsonObject { ["id"] = id, ["name"] = id == pm.ModelId ? effective.DisplayName : id };
                // Capability hints for clients that declare per-model limits (VS Code Copilot); omitted when unknown.
                if (effective.ContextWindow is { } context) entry["contextWindow"] = context;
                if (effective.MaxOutputTokens is { } maxOut) entry["maxOutputTokens"] = maxOut;
                if (effective.Capabilities.Vision is { } vision) entry["vision"] = vision;
                if (effective.Capabilities.Reasoning is { } reasoning) entry["reasoning"] = reasoning;
                if (effective.Capabilities.Pdf is { } pdf) entry["pdf"] = pdf;
                // The protocols this model passes through in untranslated, preferred first; clients that can choose a
                // protocol per model use the first one they speak (ClientProtocols).
                var protocols = GatewayRouter.PassthroughProtocols(target.Provider, pm.UpstreamProtocols);
                if (protocols.Count > 0) entry["protocols"] = new JsonArray([.. protocols.Select(p => (JsonNode)p.ToId())]);
                list[id] = entry;
            }
            // Smart routing's virtual model (plan §5.8), while it is on: the client offers it like any other model.
            if (smartRouter.AdvertisedModel is { } virtualModel && !list.ContainsKey(virtualModel))
                list[virtualModel] = new JsonObject { ["id"] = virtualModel, ["name"] = "Astra Auto" };
            extras["models"] = list;
        }
        // A protocol the user changed in the client's own UI since Astra last wrote it is their choice: it joins the
        // client's overrides (persisted with the extras), so the rewrite keeps it instead of putting the native one back.
        if (_registry.Get(kind) is IClientProtocolChoices choices && choices.ChangedProtocolChoices() is { Count: > 0 } changed)
        {
            if (extras[ClientProtocols.ExtrasKey] is not JsonObject overrides)
                extras[ClientProtocols.ExtrasKey] = overrides = new JsonObject();
            foreach (var (model, protocol) in changed) overrides[model] = protocol.ToId();
        }
        // Claude Code's 1M option: models known to have a smaller window must not get the [1m] suffix.
        HashSet<string>? without1M = null;
        if (kind == ClientKinds.ClaudeCode && ClientOptions.GetBool(extras, ClientOptions.Context1M))
        {
            without1M = [];
            foreach (var (id, _, _, effective) in await routing.ListModelsAsync(targets, ct))
                if (effective.ContextWindow is { } window && window < 1_000_000) without1M.Add(id);
        }
        return new EnableContext
        {
            GatewayBaseUrl = server.GatewayBaseUrl, LocalKey = key, Model = record.SelectedModel, Extras = extras,
            ModelsWithoutContext1M = without1M,
        };
    }

    private ConfigChangePlan EnablePlan(IClientAdapter adapter, EnableContext context, JsonObject input)
    {
        var plan = adapter.PlanEnable(context);
        if (!input.ContainsKey("model") && !input.ContainsKey("extras")) return plan;
        // Clearing a model selection restores what Astra had written for it (and whatever was there before); a
        // key the client owns that never held one of our values is left alone.
        var old = adapter.PlanDisable();
        var candidates = new List<string> { adapter is PluginClientAdapter plugin ? plugin.ModelKeyPath : ModelKeyOf(adapter.Kind) };
        // Claude Code's optional tiers only come back through PlanDisable (PlanEnable writes just the chosen ones).
        if (adapter.Kind == ClientKinds.ClaudeCode) candidates.AddRange(ClaudeCodeModels.Slots.Select(slot => $"env.{slot}"));
        candidates.AddRange(ClientOptions.ConfigKeyPaths(adapter.Kind));
        var written = plan.Changes.Where(c => c.After is not null).Select(c => (c.File, c.KeyPath)).ToHashSet();
        var changes = plan.Changes.ToList();
        foreach (var change in old.Changes.Where(c => candidates.Contains(c.KeyPath) && !written.Contains((c.File, c.KeyPath))))
        {
            if (db.ClientConfigState.Get(adapter.Kind, change.File, change.KeyPath) is not { } state) continue;
            changes.Add(new ConfigChange(change.File, change.Format, change.KeyPath, change.Before,
                state.OriginalAbsent ? null : state.OriginalValueJson, change.Kind));
        }
        return _applier.Preview(adapter.Kind, changes);
    }

    /// <summary>The config key behind a client's "default model" choice, so unsetting it can be restored.</summary>
    private static string ModelKeyOf(string kind) => kind switch
    {
        ClientKinds.ClaudeCode => "env.ANTHROPIC_MODEL",
        ClientKinds.GeminiCli => "GEMINI_MODEL",
        ClientKinds.GrokBuild => "model.astra.model",
        ClientKinds.Pi or ClientKinds.MiniMaxCode => "defaultModel",
        ClientKinds.HermesAgent => "model.default",
        ClientKinds.Crush => "models.large",
        ClientKinds.QwenCode => "model.name",
        ClientKinds.KimiCode => "default_model",
        ClientKinds.Zed => "agent.default_model",
        ClientKinds.Omp => "modelRoles.default",
        ClientKinds.DeepSeekHarness => "[id=agent-default-model].config.model",
        _ => "model",
    };

    private async Task<ClientInfoDto> InfoAsync(IClientAdapter adapter, CancellationToken ct, ClientInstallDto? install = null, Lazy<Task<string?>>? defaultProvider = null)
    {
        install ??= installs.Describe(adapter.Kind);
        var record = await db.Clients.GetAsync(adapter.Kind, ct);
        var bindings = await BindingsOrDefaultAsync(adapter.Kind, record?.Enabled ?? false, defaultProvider ?? DefaultProvider(ct), ct);
        var warnings = new List<string>();
        ClientStatus? inspected = null;
        try { inspected = adapter.Inspect(); }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException)
        { warnings.Add("Cannot read client configuration: " + ex.Message); }
        if (inspected is not null) warnings.AddRange(inspected.Warnings);
        var enabled = record?.Enabled ?? false;
        var drifted = inspected is null || inspected.DriftedKeys.Count > 0 || (enabled && !inspected.Enabled);
        if (drifted) warnings.Add("Client configuration differs from the recorded Astra configuration. Review before enabling or restoring.");
        var configPaths = adapter.ConfigPaths();
        var detection = inspected?.Detection ?? adapter.Detect();
        var outdated = enabled && !drifted && await IsOutdatedAsync(adapter, record!, bindings, ct);
        var primary = bindings.Count > 0 ? bindings[0] : null;
        return new ClientInfoDto(adapter.Kind, DisplayName(adapter), ClientKinds.ProtocolOf(adapter.Kind),
            adapter.Availability == ClientAvailability.Available ? "available" : "coming_soon", adapter.UnavailableReason,
            adapter.Mode == ClientMode.Coexist ? "coexist" : "switch",
            new ClientDetectionDto(detection.Detected, configPaths.Any(File.Exists), detection.Version ?? install?.Version, configPaths),
            drifted ? "drifted" : enabled ? "enabled" : "disabled", enabled, primary?.ProviderId, primary?.AccountId,
            record?.SelectedModel,
            Json.Deserialize<JsonObject>(record?.ExtraJson) ?? new JsonObject(), record?.AppliedAt, warnings, true, outdated,
            record?.TokenId ?? TokenIds.Default, install,
            bindings.Select(b => new ClientBindingDto(b.ProviderId, b.AccountId)).ToList(),
            ClientOptions.For(adapter.Kind).ToList(),
            (inspected?.Repairs ?? []).Select(r => new ConfigRepairDto(r.File, r.Problem, r.Line)).ToList(),
            ClientProtocols.IsPerModel(adapter.Kind) ? [.. ClientProtocols.For(adapter.Kind)] : null);
    }

    /// <summary>
    /// Plan §7.1: what Astra would write now (current gateway address, key, model list) versus what the file holds.
    /// Returns the re-apply plan when an enabled client's Astra-owned values are stale (typically after the port
    /// changed), or null when they are current or cannot be determined.
    /// </summary>
    private async Task<ConfigChangePlan?> OutdatedPlanAsync(IClientAdapter adapter, ClientRecord record, IReadOnlyList<ClientBinding> bindings, CancellationToken ct)
    {
        var targets = await routing.TargetsAsync(bindings, ct);
        if (targets.Count == 0) return null;
        try
        {
            if (await ClientKeyAsync(record, ct) is not { } key) return null;
            var context = await ContextAsync(adapter.Kind, targets, record, key, ct);
            var plan = adapter.PlanEnable(context);
            return IsStale(plan) ? plan : null;
        }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whether a plan would change an Astra-owned value (or create an Astra-owned table).</summary>
    private static bool IsStale(ConfigChangePlan plan) => plan.Changes.Any(c => c.Kind == ConfigChangeKind.Table
        ? c.Before is null && c.After is not null
        : !ConfigValueCodec.EqualsValue(c.Format, c.Before, c.After));

    /// <summary>
    /// <see cref="OutdatedPlanAsync"/> for the status card: plans without diffs (<see cref="ClientAdapterBase.ChangesOnly"/>),
    /// and reuses this client's last answer while its context and everything the plan looked at are unchanged (same file
    /// bytes, same paths present). The tray polls the client list every 10 s, and planning a client with a large config —
    /// Codex's model catalog, a 4,000-line NextCoWork file — costs tens of megabytes each time. A plugin client is always
    /// planned: its connect hook decides what it writes.
    /// </summary>
    private async Task<bool> IsOutdatedAsync(IClientAdapter adapter, ClientRecord record, IReadOnlyList<ClientBinding> bindings, CancellationToken ct)
    {
        var targets = await routing.TargetsAsync(bindings, ct);
        if (targets.Count == 0) return false;
        try
        {
            if (await ClientKeyAsync(record, ct) is not { } key) return false;
            var context = await ContextAsync(adapter.Kind, targets, record, key, ct);
            var reuse = adapter is not PluginClientAdapter;
            if (reuse && _outdatedChecks.TryGetValue(adapter.Kind, out var last) && SameContext(last.Context, context)
                && ClientEnvironment.Unchanged(last.Reads))
                return last.Outdated;
            // Copied before planning: the next check compares against it, and an adapter may change the context it is given.
            var kept = new EnableContext
            {
                GatewayBaseUrl = context.GatewayBaseUrl, LocalKey = context.LocalKey, Model = context.Model,
                Extras = context.Extras?.DeepClone().AsObject(), ModelsWithoutContext1M = context.ModelsWithoutContext1M,
            };
            bool outdated;
            IReadOnlyList<FileProbe> reads;
            using (var tracking = ClientEnvironment.TrackReads())
            {
                outdated = IsStale(ClientAdapterBase.ChangesOnly(() => adapter.PlanEnable(context)));
                reads = tracking.Reads.Distinct().ToList();
            }
            if (reuse) _outdatedChecks[adapter.Kind] = new OutdatedCheck(kept, reads, outdated);
            return outdated;
        }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static bool SameContext(EnableContext a, EnableContext b) =>
        a.GatewayBaseUrl == b.GatewayBaseUrl && a.LocalKey == b.LocalKey && a.Model == b.Model
        && JsonNode.DeepEquals(a.Extras, b.Extras)
        && (a.ModelsWithoutContext1M is null
            ? b.ModelsWithoutContext1M is null
            : b.ModelsWithoutContext1M is not null && a.ModelsWithoutContext1M.SetEquals(b.ModelsWithoutContext1M));

    /// <summary>
    /// Rewrites every enabled client whose Astra-owned values are stale (e.g. Astra now listens on another port).
    /// Drifted clients are never touched. Returns the kinds that were rewritten.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReapplyOutdatedAsync(CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var updated = new List<string>();
            foreach (var adapter in _registry.All)
            {
                if (await db.Clients.GetAsync(adapter.Kind, ct) is not { Enabled: true } record) continue;
                var status = adapter.Inspect();
                if (!status.Enabled || status.DriftedKeys.Count > 0) continue;
                var bindings = await db.Clients.ListBindingsAsync(adapter.Kind, ct);
                if (await OutdatedPlanAsync(adapter, record, bindings, ct) is not { } plan) continue;
                Apply(adapter, plan);
                if (ClientKinds.HasModelList(adapter.Kind))
                {
                    // Keep the stored model list in step with what was just written.
                    var key = await ClientKeyAsync(record, ct) ?? throw new InvalidOperationException("Token key vanished during reapply");
                    var context = await ContextAsync(adapter.Kind, await routing.TargetsAsync(bindings, ct), record, key, ct);
                    record.ExtraJson = context.Extras?.ToJsonString();
                }
                record.AppliedAt = DateTimeOffset.UtcNow;
                await db.Clients.UpsertAsync(record, CancellationToken.None);
                updated.Add(adapter.Kind);
            }
            return (IReadOnlyList<string>)updated;
        }, ct);

    /// <summary>
    /// The key Astra writes into this client's configuration: its token (default token when unset) plus the client
    /// suffix. Null when the token is missing or has no key yet.
    /// </summary>
    private async Task<string?> ClientKeyAsync(ClientRecord record, CancellationToken ct)
    {
        var token = await db.Tokens.GetAsync(record.TokenId ?? TokenIds.Default, ct);
        return token?.KeyEnc is null ? null : GatewayTokens.ForClient(secrets.Unprotect(token.KeyEnc), record.Kind);
    }

    private IClientAdapter Require(string kind) => _registry.Get(kind) ?? throw new AdminApiException(404, "Unknown client kind");

    /// <summary>Applies a plan; a plugin client then puts back the keys its connect stopped returning.</summary>
    private void Apply(IClientAdapter adapter, ConfigChangePlan plan)
    {
        _applier.Apply(plan);
        if (adapter is PluginClientAdapter plugin && plugin.PlanRestoreStale() is { IsEmpty: false } stale) _applier.Disable(stale);
    }

    /// <summary>
    /// Rebuilds the adapter list when the clients plugins add changed: the agents loaded now, and the plugin clients
    /// enabled before whose plugin is gone (from the descriptor their row keeps), which can still be disabled.
    /// </summary>
    private async Task RefreshRegistryAsync(CancellationToken ct)
    {
        await agents.RefreshAsync(ct);
        var remembered = new List<AgentPluginDescriptor>();
        foreach (var record in await db.Clients.ListAsync(ct))
        {
            if (!ClientKinds.IsPluginKind(record.Kind)) continue;
            if (Json.Deserialize<JsonObject>(record.ExtraJson)?[PluginExtrasKey] is JsonObject snapshot
                && Json.Deserialize<AgentPluginDescriptor>(snapshot.ToJsonString()) is { } descriptor)
                remembered.Add(descriptor);
        }
        var key = (agents.Generation, string.Join('|', remembered.Select(d => d.Id)));
        if (key == _registryKey) return;
        _registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState, agents, remembered);
        _registryKey = key;
    }

    internal async Task<Astra.Core.Models.Provider> RequireProviderAsync(string id, CancellationToken ct)
    {
        var provider = await db.Providers.GetAsync(id, ct) ?? throw new AdminApiException(404, "Provider not found");
        if (!provider.Enabled) throw new AdminApiException(409, "Provider is disabled");
        return provider;
    }

    /// <summary>Validates a pinned subscription account exists and belongs to the provider.</summary>
    internal async Task<string> RequireAccountAsync(string providerId, string accountId, CancellationToken ct)
    {
        var account = await db.Accounts.GetAsync(accountId, ct)
                      ?? throw new AdminApiException(400, "Subscription account not found");
        if (account.ProviderId != providerId)
            throw new AdminApiException(400, "Subscription account does not belong to this provider");
        return account.Id;
    }

    private async Task<T> MutateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try { return await action(); }
        finally
        {
            // A write can change what a plan reads besides files (the recorded state, the bindings): check afresh.
            _outdatedChecks.Clear();
            _writes.Release();
        }
    }

    private static string KeyLabel(RestoredKey key) => $"{key.FilePath}:{key.KeyPath}";
    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string NameOf(string kind) => kind switch
    {
        ClientKinds.Codex => "Codex", ClientKinds.ClaudeCode => "Claude Code", ClientKinds.GeminiCli => "Gemini CLI",
        ClientKinds.OpenCode => "OpenCode", ClientKinds.ClaudeDesktop => "Claude Desktop", ClientKinds.GrokBuild => "Grok Build",
        ClientKinds.Pi => "Pi", ClientKinds.HermesAgent => "Hermes Agent", ClientKinds.MiniMaxCode => "MiniMax Code",
        ClientKinds.CopilotCli => "Copilot CLI", ClientKinds.VsCodeCopilot => "VS Code Copilot",
        ClientKinds.Crush => "Crush", ClientKinds.QwenCode => "Qwen Code", ClientKinds.Droid => "Droid",
        ClientKinds.KimiCode => "Kimi Code", ClientKinds.Zed => "Zed", ClientKinds.VsCodeInsiders => "VS Code Insiders", ClientKinds.VsCodium => "VSCodium",
        ClientKinds.Omp => "omp (oh-my-pi)", ClientKinds.MiMoCode => "MiMo Code", ClientKinds.DeepSeekHarness => "DeepSeek Harness",
        ClientKinds.WorkBuddy => "WorkBuddy", ClientKinds.WorkBuddyAi => "WorkBuddy AI", ClientKinds.MuseCode => "Muse Code", ClientKinds.NextCoWork => "NextCoWork",
        ClientKinds.Paseo => "Paseo", ClientKinds.ZCode => "ZCode", ClientKinds.TKCode => "TKCode",
        _ => kind,
    };
}
