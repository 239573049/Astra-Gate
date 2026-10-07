using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

public sealed record ClientDetectionDto(bool Installed, bool ConfigExists, string? Version, IReadOnlyList<string> ConfigPaths);
public sealed record ClientInfoDto(
    string Kind, string Name, ApiProtocol Protocol, string Availability, string? AvailabilityReason, string Mode,
    ClientDetectionDto Detection, string Status, bool Enabled, string? ProviderId, string? AccountId, string? SelectedModel,
    JsonObject Extras, DateTimeOffset? AppliedAt, IReadOnlyList<string> Warnings, bool RequiresRestart,
    bool ConfigOutdated = false);
public sealed record ClientPreviewDto(IReadOnlyList<ConfigChangeDto> Changes, IReadOnlyList<FileDiff> Diffs, IReadOnlyList<string> Warnings);
public sealed record ConfigChangeDto(string File, string Format, string KeyPath, string? Before, string? After);
public sealed record ClientDisableDto(IReadOnlyList<string> Restored, IReadOnlyList<string> Drifted, ClientInfoDto Client);
public sealed record ClientBackupDto(string Id, DateTimeOffset CreatedAt, IReadOnlyList<string> Files, bool FirstWrite);

/// <summary>Client admin operations; file changes are serialized so two UI requests cannot overwrite each other.</summary>
public sealed class ClientService(
    AstraDatabase db, ISecretProtector secrets, ClientEnvironment env, AstraPaths paths, ServerOptions server,
    EffectiveModelResolver models)
{
    private readonly ClientAdapterRegistry _registry = ClientAdapterRegistry.CreateDefault(env, db.ClientConfigState);
    private readonly ClientConfigApplier _applier = new(db.ClientConfigState, env, paths.ClientBackupsDir);
    private readonly SemaphoreSlim _writes = new(1, 1);

    public async Task<IReadOnlyList<ClientInfoDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<ClientInfoDto>();
        foreach (var adapter in _registry.All) result.Add(await InfoAsync(adapter, ct));
        return result;
    }

    /// <summary>
    /// Sets the provider a client uses. accountId semantics (plan §5.4): null = keep the current pin when
    /// the provider is unchanged, "" = clear the pin (use the provider's default account), an id = pin it.
    /// </summary>
    public async Task<ClientInfoDto> SetBindingAsync(string kind, string providerId, string? accountId, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var adapter = Require(kind);
            await RequireProviderAsync(providerId, ct);
            var previous = await db.Clients.GetBindingAsync(kind, ct);
            var pinned = accountId switch
            {
                null => previous?.ProviderId == providerId ? previous.AccountId : null,
                "" => null,
                _ => await RequireAccountAsync(providerId, accountId, ct),
            };
            await db.Clients.SetBindingAsync(new ClientBinding
            {
                ClientKind = kind,
                ProviderId = providerId,
                AccountId = pinned,
            }, ct);
            if (ClientKinds.WithModelList.Contains(kind)) await SyncModelListCoreAsync(kind, null, ct);
            return await InfoAsync(adapter, ct);
        }, ct);

    /// <summary>
    /// Plan §7.6: OpenCode (and the other <see cref="ClientKinds.WithModelList"/> clients: Pi, MiniMax Code, Copilot CLI)
    /// list the bound provider's models in their config. Keeps those lists current when the provider's models change
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
        if (record is not { Enabled: true, LocalKeyEnc: not null }) return false;
        var binding = await db.Clients.GetBindingAsync(kind, ct);
        if (binding is null || (providerId is not null && binding.ProviderId != providerId)) return false;
        if (await db.Providers.GetAsync(binding.ProviderId, ct) is not { Enabled: true }) return false;
        try
        {
            var status = adapter.Inspect();
            if (!status.Enabled || status.DriftedKeys.Count > 0) return false;
            var context = await ContextAsync(kind, binding.ProviderId, record, secrets.Unprotect(record.LocalKeyEnc), ct);
            var plan = adapter.PlanEnable(context); // rewrites the Astra provider as a whole so its recorded applied value stays exact
            if (plan.Diffs.Count == 0) return false;
            _applier.Apply(plan);
            record.ExtraJson = context.Extras?.ToJsonString();
            record.AppliedAt = DateTimeOffset.UtcNow;
            await db.Clients.UpsertAsync(record, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is EditorException or IOException or UnauthorizedAccessException)
        {
            return false; // the client page reports unreadable / drifted configs on its own
        }
    }

    public async Task<ClientPreviewDto> PreviewAsync(string kind, JsonObject input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var (adapter, record, context, _, warnings) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, record, context, input);
            return new ClientPreviewDto(plan.Changes.Select(c => new ConfigChangeDto(c.File,
                c.Format.ToString().ToLowerInvariant(), c.KeyPath, c.Before, c.After)).ToList(), plan.Diffs, warnings);
        }, ct);

    public async Task<ClientInfoDto> EnableAsync(string kind, JsonObject input, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var (adapter, record, context, providerId, _) = await PrepareAsync(kind, input, ct);
            var plan = EnablePlan(adapter, record, context, input); // parse/validate before persisting or writing
            if (record.LocalKeyEnc is null)
            {
                record.LocalKeyEnc = secrets.Protect(context.LocalKey);
                record.LocalKeyHash = LocalKeys.Hash(context.LocalKey);
                record.LocalKeyPrefix = LocalKeys.PrefixOf(context.LocalKey);
                // Persist the new key before touching a file. If IO fails the row remains disabled and reusable.
                await db.Clients.UpsertAsync(record, ct);
            }
            _applier.Apply(plan);
            record.Enabled = true;
            record.SelectedModel = context.Model;
            record.ExtraJson = context.Extras?.ToJsonString();
            record.AppliedAt = DateTimeOffset.UtcNow;
            // Once files have been written, finish the metadata commit even if the caller disconnected.
            await db.Clients.UpsertAsync(record, CancellationToken.None);
            await db.Clients.SetBindingAsync(new ClientBinding { ClientKind = kind, ProviderId = providerId,
                AccountId = (await db.Clients.GetBindingAsync(kind)) is { } old && old.ProviderId == providerId ? old.AccountId : null });
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

    public async Task<ClientInfoDto> RotateKeyAsync(string kind, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            var adapter = Require(kind);
            var record = await db.Clients.GetAsync(kind, ct) ?? throw new AdminApiException(409, "Client is not configured");
            var binding = await db.Clients.GetBindingAsync(kind, ct);
            if (record.Enabled && adapter.Inspect().DriftedKeys.Count > 0)
                throw new AdminApiException(409, "Client configuration was modified; review and re-enable before rotating its key");
            var key = LocalKeys.Rotate(record, secrets);
            if (record.Enabled)
            {
                var context = await ContextAsync(kind, binding?.ProviderId, record, key, ct);
                _applier.Apply(adapter.PlanEnable(context));
                record.AppliedAt = DateTimeOffset.UtcNow;
            }
            await db.Clients.UpsertAsync(record);
            return await InfoAsync(adapter, CancellationToken.None);
        }, ct);

    public async Task<IReadOnlyList<string>> ModelsAsync(string kind, CancellationToken ct)
    {
        Require(kind);
        var binding = await db.Clients.GetBindingAsync(kind, ct);
        if (binding is null || await db.Providers.GetAsync(binding.ProviderId, ct) is not { Enabled: true } provider) return [];
        var result = new List<string>();
        foreach (var pm in await db.Providers.ListModelsAsync(provider.Id, ct))
            if ((await models.ResolveAsync(provider, pm, ct)).Enabled) result.Add(pm.ModelId);
        return result;
    }

    private async Task<(IClientAdapter Adapter, ClientRecord Record, EnableContext Context, string ProviderId, List<string> Warnings)>
        PrepareAsync(string kind, JsonObject input, CancellationToken ct)
    {
        var adapter = Require(kind);
        if (adapter.Availability != ClientAvailability.Available) throw new AdminApiException(409, adapter.UnavailableReason ?? "Client not supported yet");
        var record = await db.Clients.GetAsync(kind, ct) ?? new ClientRecord { Kind = kind };
        var binding = await db.Clients.GetBindingAsync(kind, ct);
        var providerId = input["providerId"]?.GetValue<string>() ?? binding?.ProviderId;
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
        var key = record.LocalKeyEnc is null ? LocalKeys.Generate(kind) : secrets.Unprotect(record.LocalKeyEnc);
        var context = await ContextAsync(kind, providerId, next, key, ct);
        var warnings = adapter.Inspect().Warnings.ToList();
        if (record.LocalKeyEnc is null) warnings.Add("The new local key shown in this preview is an example; confirmation generates the actual key.");
        return (adapter, record, context, providerId, warnings);
    }

    private async Task<EnableContext> ContextAsync(string kind, string? providerId, ClientRecord record, string key, CancellationToken ct)
    {
        var extras = Json.Deserialize<JsonObject>(record.ExtraJson) ?? new JsonObject();
        if (ClientKinds.WithModelList.Contains(kind) && providerId is not null)
        {
            var provider = await RequireProviderAsync(providerId, ct);
            var list = new JsonObject();
            foreach (var pm in await db.Providers.ListModelsAsync(provider.Id, ct))
            {
                var effective = await models.ResolveAsync(provider, pm, ct);
                if (effective.Enabled) list[pm.ModelId] = new JsonObject { ["id"] = pm.ModelId, ["name"] = effective.DisplayName };
            }
            extras["models"] = list;
        }
        return new EnableContext { GatewayBaseUrl = server.GatewayBaseUrl, LocalKey = key, Model = record.SelectedModel, Extras = extras };
    }

    private ConfigChangePlan EnablePlan(IClientAdapter adapter, ClientRecord record, EnableContext context, JsonObject input)
    {
        var plan = adapter.PlanEnable(context);
        if (!input.ContainsKey("model") || context.Model is not null || record.SelectedModel is null) return plan;
        // Unsetting a model restores only the model selection Astra owned, not an unrelated user setting.
        var modelKey = adapter.Kind switch
        {
            ClientKinds.ClaudeCode => "env.ANTHROPIC_MODEL",
            ClientKinds.GeminiCli => "GEMINI_MODEL",
            ClientKinds.GrokBuild => "model.astra.model",
            ClientKinds.Pi or ClientKinds.MiniMaxCode => "defaultModel",
            ClientKinds.HermesAgent => "model.default",
            _ => "model",
        };
        var changes = plan.Changes.ToList();
        foreach (var old in adapter.PlanDisable().Changes.Where(c => c.KeyPath == modelKey))
        {
            var state = db.ClientConfigState.Get(adapter.Kind, old.File, old.KeyPath);
            if (state is null || changes.Any(c => c.File == old.File && c.KeyPath == old.KeyPath)) continue;
            changes.Add(new ConfigChange(old.File, old.Format, old.KeyPath, old.Before,
                state.OriginalAbsent ? null : state.OriginalValueJson, old.Kind));
        }
        return _applier.Preview(adapter.Kind, changes);
    }

    private async Task<ClientInfoDto> InfoAsync(IClientAdapter adapter, CancellationToken ct)
    {
        var record = await db.Clients.GetAsync(adapter.Kind, ct);
        var binding = await db.Clients.GetBindingAsync(adapter.Kind, ct);
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
        var outdated = enabled && !drifted && await OutdatedPlanAsync(adapter, record!, binding, ct) is not null;
        return new ClientInfoDto(adapter.Kind, NameOf(adapter.Kind), ClientKinds.ProtocolOf(adapter.Kind),
            adapter.Availability == ClientAvailability.Available ? "available" : "coming_soon", adapter.UnavailableReason,
            adapter.Mode == ClientMode.Coexist ? "coexist" : "switch",
            new ClientDetectionDto(detection.Detected, configPaths.Any(File.Exists), detection.Version, configPaths),
            drifted ? "drifted" : enabled ? "enabled" : "disabled", enabled, binding?.ProviderId, binding?.AccountId,
            record?.SelectedModel,
            Json.Deserialize<JsonObject>(record?.ExtraJson) ?? new JsonObject(), record?.AppliedAt, warnings, true, outdated);
    }

    /// <summary>
    /// Plan §7.1: what Astra would write now (current gateway address, key, model list) versus what the file holds.
    /// Returns the re-apply plan when an enabled client's Astra-owned values are stale (typically after the port
    /// changed), or null when they are current or cannot be determined.
    /// </summary>
    private async Task<ConfigChangePlan?> OutdatedPlanAsync(IClientAdapter adapter, ClientRecord record, ClientBinding? binding, CancellationToken ct)
    {
        if (record.LocalKeyEnc is null || binding is null) return null;
        if (await db.Providers.GetAsync(binding.ProviderId, ct) is not { Enabled: true }) return null;
        try
        {
            var context = await ContextAsync(adapter.Kind, binding.ProviderId, record, secrets.Unprotect(record.LocalKeyEnc), ct);
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
                var binding = await db.Clients.GetBindingAsync(adapter.Kind, ct);
                if (await OutdatedPlanAsync(adapter, record, binding, ct) is not { } plan) continue;
                _applier.Apply(plan);
                if (ClientKinds.WithModelList.Contains(adapter.Kind))
                {
                    // Keep the stored model list in step with what was just written.
                    var context = await ContextAsync(adapter.Kind, binding!.ProviderId, record, secrets.Unprotect(record.LocalKeyEnc!), ct);
                    record.ExtraJson = context.Extras?.ToJsonString();
                }
                record.AppliedAt = DateTimeOffset.UtcNow;
                await db.Clients.UpsertAsync(record, CancellationToken.None);
                updated.Add(adapter.Kind);
            }
            return (IReadOnlyList<string>)updated;
        }, ct);

    private IClientAdapter Require(string kind) => _registry.Get(kind) ?? throw new AdminApiException(404, "Unknown client kind");

    private async Task<Astra.Core.Models.Provider> RequireProviderAsync(string id, CancellationToken ct)
    {
        var provider = await db.Providers.GetAsync(id, ct) ?? throw new AdminApiException(404, "Provider not found");
        if (!provider.Enabled) throw new AdminApiException(409, "Provider is disabled");
        return provider;
    }

    /// <summary>Validates a pinned subscription account exists and belongs to the provider.</summary>
    private async Task<string> RequireAccountAsync(string providerId, string accountId, CancellationToken ct)
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
        ClientKinds.CopilotCli => "Copilot CLI",
        _ => kind,
    };
}
