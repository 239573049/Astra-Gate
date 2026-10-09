using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

public sealed record ClientDetectionDto(bool Installed, bool ConfigExists, string? Version, IReadOnlyList<string> ConfigPaths);
/// <summary>One provider bound to a client; the list order is the routing order (see <see cref="ClientRouting"/>).</summary>
public sealed record ClientBindingDto(string ProviderId, string? AccountId);
/// <summary>ProviderId / AccountId describe the primary binding (first of <see cref="Bindings"/>).</summary>
public sealed record ClientInfoDto(
    string Kind, string Name, ApiProtocol Protocol, string Availability, string? AvailabilityReason, string Mode,
    ClientDetectionDto Detection, string Status, bool Enabled, string? ProviderId, string? AccountId, string? SelectedModel,
    JsonObject Extras, DateTimeOffset? AppliedAt, IReadOnlyList<string> Warnings, bool RequiresRestart,
    bool ConfigOutdated = false, string? TokenId = null, ClientInstallDto? Install = null, IReadOnlyList<ClientBindingDto>? Bindings = null);
public sealed record ClientPreviewDto(IReadOnlyList<ConfigChangeDto> Changes, IReadOnlyList<FileDiff> Diffs, IReadOnlyList<string> Warnings);
public sealed record ConfigChangeDto(string File, string Format, string KeyPath, string? Before, string? After);
public sealed record ClientDisableDto(IReadOnlyList<string> Restored, IReadOnlyList<string> Drifted, ClientInfoDto Client);
public sealed record ClientBackupDto(string Id, DateTimeOffset CreatedAt, IReadOnlyList<string> Files, bool FirstWrite);

/// <summary>Outcome of rewriting the clients that use one token: rewritten kinds and kinds left alone (drifted / unreadable).</summary>
public sealed record TokenRewriteResult(IReadOnlyList<string> Rewritten, IReadOnlyList<string> Skipped);

/// <summary>Client admin operations; file changes are serialized so two UI requests cannot overwrite each other.</summary>
public sealed class ClientService(
    AstraDatabase db, ISecretProtector secrets, ClientEnvironment env, AstraPaths paths, ServerOptions server,
    ClientInstallService installs, ClientRouting routing)
{
    private readonly ClientAdapterRegistry _registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState);
    private readonly ClientConfigApplier _applier = new(db.ClientConfigState, env, paths.ClientBackupsDir);
    private readonly SemaphoreSlim _writes = new(1, 1);

    public async Task<IReadOnlyList<ClientInfoDto>> ListAsync(CancellationToken ct = default)
    {
        // Probes every client's version in parallel up front (cached afterwards), so InfoAsync below stays cheap.
        var install = await installs.DescribeAllAsync(ct);
        var result = new List<ClientInfoDto>();
        foreach (var adapter in _registry.All) result.Add(await InfoAsync(adapter, ct, install.GetValueOrDefault(adapter.Kind)));
        return result;
    }

    /// <summary>
    /// Sets the client's primary provider (priority 0), keeping its other bindings; the provider is dropped from a lower
    /// slot if it held one. accountId semantics (plan §5.4): null = keep the pin the provider already has for this
    /// client, "" = clear the pin (use the provider's default account), an id = pin it.
    /// </summary>
    public async Task<ClientInfoDto> SetBindingAsync(string kind, string providerId, string? accountId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
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
            if (ClientKinds.WithModelList.Contains(kind)) await SyncModelListCoreAsync(kind, null, ct);
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
            if (ClientKinds.WithModelList.Contains(kind)) await SyncModelListCoreAsync(kind, null, ct);
            return await InfoAsync(adapter, ct);
        }, ct);

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
            var changed = false;
            foreach (var kind in ClientKinds.WithModelList) changed |= await SyncModelListCoreAsync(kind, providerId, ct);
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
            _applier.Apply(plan);
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
            var (adapter, record, context, _, _, warnings) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, context, input);
            return new ClientPreviewDto(plan.Changes.Select(c => new ConfigChangeDto(c.File,
                c.Format.ToString().ToLowerInvariant(), c.KeyPath, c.Before, c.After)).ToList(), plan.Diffs, warnings);
        }, ct);

    public async Task<ClientInfoDto> EnableAsync(string kind, JsonObject input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var (adapter, record, context, providerId, tokenId, _) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, context, input); // parse/validate before writing
            _applier.Apply(plan);
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
                    _applier.Apply(plan);
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

    /// <summary>The model ids the client can use: enabled models of its usable bound providers, each id once.</summary>
    public async Task<IReadOnlyList<string>> ModelsAsync(string kind, CancellationToken ct)
    {
        Require(kind);
        var targets = await routing.TargetsAsync(await db.Clients.ListBindingsAsync(kind, ct), ct);
        return (await routing.ListModelsAsync(targets, ct)).Select(m => m.Id).ToList();
    }

    private async Task<(IClientAdapter Adapter, ClientRecord Record, EnableContext Context, string ProviderId, string TokenId, List<string> Warnings)>
        PrepareAsync(string kind, JsonObject input, CancellationToken ct)
    {
        var adapter = Require(kind);
        if (adapter.Availability != ClientAvailability.Available) throw new AdminApiException(409, adapter.UnavailableReason ?? "Client not supported yet");
        var record = await db.Clients.GetAsync(kind, ct) ?? new ClientRecord { Kind = kind };
        var bindings = await db.Clients.ListBindingsAsync(kind, ct);
        var providerId = input["providerId"]?.GetValue<string>() ?? bindings.FirstOrDefault()?.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId)) throw new AdminApiException(400, "Choose a provider first");
        await RequireProviderAsync(providerId, ct);
        var next = Json.Deserialize<ClientRecord>(Json.Serialize(record))!;
        if (input.ContainsKey("model")) next.SelectedModel = Trim(input["model"]?.GetValue<string>());
        if (input.ContainsKey("extras"))
        {
            if (input["extras"] is not null and not JsonObject) throw new AdminApiException(400, "extras must be an object");
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
        if (ClientKinds.WithModelList.Contains(kind) && targets.Count > 0)
        {
            // Every bound provider's models and mapped ids, each id once (ClientRouting): the gateway routes each to its
            // provider. A mapped id carries its target model's capabilities under its own name.
            var list = new JsonObject();
            foreach (var (id, _, pm, effective) in await routing.ListModelsAsync(targets, ct))
            {
                var entry = new JsonObject { ["id"] = id, ["name"] = id == pm.ModelId ? effective.DisplayName : id };
                // Capability hints for clients that declare per-model limits (VS Code Copilot); omitted when unknown.
                if (effective.ContextWindow is { } context) entry["contextWindow"] = context;
                if (effective.MaxOutputTokens is { } maxOut) entry["maxOutputTokens"] = maxOut;
                if (effective.Capabilities.Vision is { } vision) entry["vision"] = vision;
                if (effective.Capabilities.Reasoning is { } reasoning) entry["reasoning"] = reasoning;
                list[id] = entry;
            }
            extras["models"] = list;
        }
        return new EnableContext { GatewayBaseUrl = server.GatewayBaseUrl, LocalKey = key, Model = record.SelectedModel, Extras = extras };
    }

    private ConfigChangePlan EnablePlan(IClientAdapter adapter, EnableContext context, JsonObject input)
    {
        var plan = adapter.PlanEnable(context);
        if (!input.ContainsKey("model") && !input.ContainsKey("extras")) return plan;
        // Clearing a model selection restores what Astra had written for it (and whatever was there before); a
        // key the client owns that never held one of our values is left alone.
        var old = adapter.PlanDisable();
        var candidates = new List<string> { ModelKeyOf(adapter.Kind) };
        // Claude Code's optional tiers only come back through PlanDisable (PlanEnable writes just the chosen ones).
        if (adapter.Kind == ClientKinds.ClaudeCode) candidates.AddRange(ClaudeCodeModels.Slots.Select(slot => $"env.{slot}"));
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

    private async Task<ClientInfoDto> InfoAsync(IClientAdapter adapter, CancellationToken ct, ClientInstallDto? install = null)
    {
        install ??= installs.Describe(adapter.Kind);
        var record = await db.Clients.GetAsync(adapter.Kind, ct);
        var bindings = await db.Clients.ListBindingsAsync(adapter.Kind, ct);
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
        var outdated = enabled && !drifted && await OutdatedPlanAsync(adapter, record!, bindings, ct) is not null;
        var primary = bindings.Count > 0 ? bindings[0] : null;
        return new ClientInfoDto(adapter.Kind, NameOf(adapter.Kind), ClientKinds.ProtocolOf(adapter.Kind),
            adapter.Availability == ClientAvailability.Available ? "available" : "coming_soon", adapter.UnavailableReason,
            adapter.Mode == ClientMode.Coexist ? "coexist" : "switch",
            new ClientDetectionDto(detection.Detected, configPaths.Any(File.Exists), detection.Version ?? install?.Version, configPaths),
            drifted ? "drifted" : enabled ? "enabled" : "disabled", enabled, primary?.ProviderId, primary?.AccountId,
            record?.SelectedModel,
            Json.Deserialize<JsonObject>(record?.ExtraJson) ?? new JsonObject(), record?.AppliedAt, warnings, true, outdated,
            record?.TokenId ?? TokenIds.Default, install,
            bindings.Select(b => new ClientBindingDto(b.ProviderId, b.AccountId)).ToList());
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
            var stale = plan.Changes.Any(c => c.Kind == ConfigChangeKind.Table
                ? c.Before is null && c.After is not null
                : !ConfigValueCodec.EqualsValue(c.Format, c.Before, c.After));
            return stale ? plan : null;
        }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites every enabled client whose Astra-owned values are stale (e.g. Astra now listens on another port).
    /// Drifted clients are never touched. Returns the kinds that were rewritten.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReapplyOutdatedAsync(CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var updated = new List<string>();
            foreach (var adapter in _registry.All)
            {
                if (await db.Clients.GetAsync(adapter.Kind, ct) is not { Enabled: true } record) continue;
                var status = adapter.Inspect();
                if (!status.Enabled || status.DriftedKeys.Count > 0) continue;
                var bindings = await db.Clients.ListBindingsAsync(adapter.Kind, ct);
                if (await OutdatedPlanAsync(adapter, record, bindings, ct) is not { } plan) continue;
                _applier.Apply(plan);
                if (ClientKinds.WithModelList.Contains(adapter.Kind))
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
        finally { _writes.Release(); }
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
        ClientKinds.WorkBuddy => "WorkBuddy", ClientKinds.MuseCode => "Muse Code", ClientKinds.NextCoWork => "NextCoWork",
        _ => kind,
    };
}
