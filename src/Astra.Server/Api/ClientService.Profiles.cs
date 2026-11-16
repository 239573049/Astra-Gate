using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tokens;

namespace Astra.Server.Api;

/// <summary>
/// Profiles (<see cref="ClientProfile"/>) and the model shortlist, on top of the regular enable / disable / bindings paths:
/// a profile never writes a client file itself, every change goes through <see cref="EnableCoreAsync"/> /
/// <see cref="DisableCoreAsync"/>, so the applier's originals, backups and drift handling hold. Profile switching is the
/// idea of magpie's <c>save</c> / <c>use</c> (MIT); the snapshot, preview and drop rules are Astra's.
/// </summary>
public sealed partial class ClientService
{
    /// <summary>
    /// Sets the models the client should see (empty clears the shortlist). It is kept in the client's extras, so it is stored
    /// before the client is enabled too; an enabled client that lists its models in its config (<see cref="ClientKinds.HasModelList"/>)
    /// has that list rewritten, unless its config drifted.
    /// </summary>
    public async Task<ClientInfoDto> SetShortlistAsync(string kind, IReadOnlyList<string> models, CancellationToken ct) =>
        await MutateAsync(async () =>
        {
            await RefreshRegistryAsync(ct);
            var adapter = Require(kind);
            var ids = ModelShortlist.Normalize(models);
            var record = await db.Clients.GetAsync(kind, ct);
            if (record is null && ids.Count == 0) return await InfoAsync(adapter, ct);
            record ??= new ClientRecord { Kind = kind };
            var extras = Json.Deserialize<JsonObject>(record.ExtraJson) ?? new JsonObject();
            ModelShortlist.Set(extras, ids);
            record.ExtraJson = extras.Count == 0 ? null : extras.ToJsonString();
            await db.Clients.UpsertAsync(record, ct);
            if (ClientKinds.HasModelList(kind)) await SyncModelListCoreAsync(kind, null, ct);
            return await InfoAsync(adapter, ct);
        }, ct);

    // ---- profiles -------------------------------------------------------------------------------------------------

    /// <summary>The current state of every client Astra has a row or bindings for, as profile entries (database reads only).</summary>
    public async Task<List<ClientProfileEntry>> CaptureProfileAsync(CancellationToken ct)
    {
        await RefreshRegistryAsync(ct);
        var result = new List<ClientProfileEntry>();
        foreach (var adapter in _registry.All)
            if (await CaptureEntryAsync(adapter.Kind, ct) is { } entry) result.Add(entry);
        return result;
    }

    private async Task<ClientProfileEntry?> CaptureEntryAsync(string kind, CancellationToken ct)
    {
        var record = await db.Clients.GetAsync(kind, ct);
        var bindings = await db.Clients.ListBindingsAsync(kind, ct);
        if (record is null && bindings.Count == 0) return null;
        return new ClientProfileEntry
        {
            Kind = kind, Enabled = record?.Enabled ?? false, TokenId = record?.TokenId, Model = record?.SelectedModel,
            Bindings = [.. bindings.Select(b => new ClientProfileBinding { ProviderId = b.ProviderId, AccountId = b.AccountId })],
            Extras = SnapshotExtras(kind, record?.ExtraJson),
        };
    }

    /// <summary>
    /// The extras a profile keeps: without what every write regenerates (the plugin descriptor, and for a client that lists
    /// its models the <c>models</c> list — for Claude Code that key holds the user's slots, so it stays), and without an empty shortlist.
    /// </summary>
    private static JsonObject? SnapshotExtras(string kind, string? extraJson)
    {
        if (Json.Deserialize<JsonObject>(extraJson) is not { } extras) return null;
        extras.Remove(PluginExtrasKey);
        if (ClientKinds.HasModelList(kind)) extras.Remove(ClaudeCodeModels.ExtrasKey);
        if (extras[ModelShortlist.ExtrasKey] is JsonArray { Count: 0 }) extras.Remove(ModelShortlist.ExtrasKey);
        return extras.Count == 0 ? null : extras;
    }

    /// <summary>A profile entry as it would be applied here: providers, accounts and token that no longer exist taken out.</summary>
    private sealed record ProfileTarget(string Kind, IClientAdapter? Adapter, string? SkipReason, ClientProfileEntry Entry);

    private async Task<ProfileTarget> ResolveProfileEntryAsync(ClientProfileEntry source, List<ProfileDropDto> dropped, CancellationToken ct)
    {
        var entry = new ClientProfileEntry
        {
            Kind = source.Kind, Enabled = source.Enabled, TokenId = source.TokenId, Model = source.Model,
            Extras = source.Extras?.DeepClone().AsObject(),
        };
        if (_registry.Get(source.Kind) is not { } adapter) return new ProfileTarget(source.Kind, null, "unknown-client", entry);
        if (adapter.Availability != ClientAvailability.Available) return new ProfileTarget(source.Kind, adapter, "unavailable", entry);
        if (!adapter.Detect().Detected) return new ProfileTarget(source.Kind, adapter, "not-installed", entry);
        foreach (var b in source.Bindings)
        {
            var provider = await db.Providers.GetAsync(b.ProviderId, ct);
            if (provider is null || !provider.Enabled)
            {
                dropped.Add(new ProfileDropDto(source.Kind, "provider", b.ProviderId, provider is null ? "missing" : "disabled"));
                continue;
            }
            var account = b.AccountId;
            if (!string.IsNullOrEmpty(account) && (await db.Accounts.GetAsync(account, ct) is not { } found || found.ProviderId != b.ProviderId))
            {
                dropped.Add(new ProfileDropDto(source.Kind, "account", account, "missing"));
                account = null;
            }
            if (entry.Bindings.All(x => x.ProviderId != b.ProviderId))
                entry.Bindings.Add(new ClientProfileBinding { ProviderId = b.ProviderId, AccountId = string.IsNullOrEmpty(account) ? null : account });
        }
        if (source.TokenId is { } tokenId && !(await db.Tokens.GetAsync(tokenId, ct) is { Enabled: true, KeyEnc: not null }))
        {
            dropped.Add(new ProfileDropDto(source.Kind, "token", tokenId, "missing"));
            entry.TokenId = null;
        }
        return new ProfileTarget(source.Kind, adapter, null, entry);
    }

    /// <summary>Whether the client's stored state already is <paramref name="target"/> (enabled flag, providers and pins, model, token, extras).</summary>
    private static bool SameState(ClientProfileEntry? current, ClientProfileEntry target)
    {
        if (current is null)
            return !target.Enabled && target.Bindings.Count == 0 && target.Model is null && target.Extras is null;
        return current.Enabled == target.Enabled && current.Model == target.Model
            && (target.TokenId is null || (current.TokenId ?? TokenIds.Default) == target.TokenId)
            && current.Bindings.Select(b => (b.ProviderId, b.AccountId ?? "")).SequenceEqual(target.Bindings.Select(b => (b.ProviderId, b.AccountId ?? "")))
            && JsonNode.DeepEquals(current.Extras ?? new JsonObject(), target.Extras ?? new JsonObject());
    }

    /// <summary>Whether the clients a profile covers still match it (the ones it cannot apply — not installed, gone — do not count).</summary>
    public async Task<bool> ProfileDriftedAsync(ClientProfile profile, CancellationToken ct)
    {
        await RefreshRegistryAsync(ct);
        foreach (var entry in profile.Clients)
        {
            var target = await ResolveProfileEntryAsync(entry, [], ct);
            if (target.SkipReason is not null) continue;
            if (target.Entry.Enabled && target.Entry.Bindings.Count == 0) continue; // cannot be enabled any more
            if (!SameState(await CaptureEntryAsync(entry.Kind, ct), target.Entry)) return true;
        }
        return false;
    }

    /// <summary>What applying the profile would do, client by client, without changing anything.</summary>
    public async Task<ProfilePlanDto> PreviewProfileAsync(ClientProfile profile, ProfileDto summary, CancellationToken ct) =>
        await MutateAsync(() => RunProfileAsync(profile, summary, dryRun: true, ct), ct);

    /// <summary>Applies the profile: each client is switched through the regular enable / disable path; one that cannot be is reported and skipped.</summary>
    public async Task<ProfilePlanDto> ApplyProfileAsync(ClientProfile profile, ProfileDto summary, CancellationToken ct) =>
        await MutateAsync(() => RunProfileAsync(profile, summary, dryRun: false, ct), ct);

    private async Task<ProfilePlanDto> RunProfileAsync(ClientProfile profile, ProfileDto summary, bool dryRun, CancellationToken ct)
    {
        await RefreshRegistryAsync(ct);
        var steps = new List<ProfileStepDto>();
        var dropped = new List<ProfileDropDto>();
        foreach (var source in profile.Clients)
        {
            var target = await ResolveProfileEntryAsync(source, dropped, ct);
            var name = target.Adapter is null ? source.Kind : DisplayName(target.Adapter);
            try
            {
                steps.Add(await RunProfileEntryAsync(target, name, dryRun, ct));
            }
            catch (Exception ex) when (ex is AdminApiException or EditorException or IOException or UnauthorizedAccessException
                                           or System.Security.Cryptography.CryptographicException)
            {
                steps.Add(new ProfileStepDto(source.Kind, name, "failed", ex.Message, [], [], []));
            }
        }
        return new ProfilePlanDto(summary, steps, dropped);
    }

    private async Task<ProfileStepDto> RunProfileEntryAsync(ProfileTarget target, string name, bool dryRun, CancellationToken ct)
    {
        var kind = target.Kind;
        if (target.SkipReason is { } skip) return Step(kind, name, "skipped", skip);
        var adapter = target.Adapter!;
        var wanted = target.Entry;
        var current = await CaptureEntryAsync(kind, ct);
        var wasEnabled = current?.Enabled ?? false;
        var wantsEnabled = wanted.Enabled;
        if (wantsEnabled && wanted.Bindings.Count == 0)
        {
            // Every provider the profile named is gone: the client cannot stay on Astra, as the gateway would refuse it.
            if (!wasEnabled) return Step(kind, name, "skipped", "no-provider");
            if (dryRun) return Step(kind, name, "disable", "no-provider");
            var report = await DisableCoreAsync(kind, false, ct);
            return Step(kind, name, "disable", "no-provider", notes: DriftNotes(report));
        }
        if (SameState(current, wanted)) return Step(kind, name, "unchanged");

        var bindings = wanted.Bindings.Select(b => new ClientBinding { ClientKind = kind, ProviderId = b.ProviderId, AccountId = b.AccountId }).ToList();
        if (!wantsEnabled)
        {
            if (dryRun) return Step(kind, name, wasEnabled ? "disable" : "store");
            List<string> notes = [];
            if (wasEnabled) notes.AddRange(DriftNotes(await DisableCoreAsync(kind, false, ct)));
            await StoreProfileStateAsync(kind, wanted, bindings, ct);
            return Step(kind, name, wasEnabled ? "disable" : "store", notes: notes);
        }

        var input = EnableInput(wanted);
        var action = wasEnabled ? "update" : "enable";
        if (dryRun)
        {
            var (_, _, context, _, _, warnings) = await PrepareAsync(kind, input, ct, bindings);
            var plan = EnablePlan(adapter, context, input);
            return new ProfileStepDto(kind, name, action, null,
                plan.Changes.Select(c => new ConfigChangeDto(c.File, c.Format.ToString().ToLowerInvariant(), c.KeyPath, c.Before, c.After)).ToList(),
                plan.Diffs.Select(d => new FileDiffDto(d.File, d.UnifiedDiff)).ToList(), warnings);
        }
        // The providers first (enable keeps the list and makes its first entry the primary), put back if the enable is refused.
        var before = await db.Clients.ListBindingsAsync(kind, ct);
        await db.Clients.ReplaceBindingsAsync(kind, bindings, ct);
        try { await EnableCoreAsync(kind, input, ct); }
        catch
        {
            await db.Clients.ReplaceBindingsAsync(kind, [.. before], CancellationToken.None);
            throw;
        }
        return Step(kind, name, action);
    }

    /// <summary>The enable request that sets a client to <paramref name="e"/>; the shortlist is always sent so an old one is not carried over.</summary>
    private static JsonObject EnableInput(ClientProfileEntry e)
    {
        var extras = e.Extras?.DeepClone().AsObject() ?? new JsonObject();
        if (!extras.ContainsKey(ModelShortlist.ExtrasKey)) extras[ModelShortlist.ExtrasKey] = new JsonArray();
        var input = new JsonObject { ["providerId"] = e.Bindings[0].ProviderId, ["model"] = e.Model, ["extras"] = extras };
        if (e.TokenId is not null) input["tokenId"] = e.TokenId;
        return input;
    }

    /// <summary>Records what a client that stays off should hold: its providers, model, token and extras (nothing is written to its files).</summary>
    private async Task StoreProfileStateAsync(string kind, ClientProfileEntry e, List<ClientBinding> bindings, CancellationToken ct)
    {
        var record = await db.Clients.GetAsync(kind, ct);
        if (record is not null || e.Model is not null || e.Extras is not null || e.TokenId is not null)
        {
            record ??= new ClientRecord { Kind = kind };
            record.SelectedModel = e.Model;
            if (e.TokenId is not null) record.TokenId = e.TokenId;
            record.ExtraJson = e.Extras?.ToJsonString();
            record.Enabled = false;
            await db.Clients.UpsertAsync(record, ct);
        }
        await db.Clients.ReplaceBindingsAsync(kind, bindings, ct);
    }

    private static List<string> DriftNotes(ClientDisableDto report) =>
        report.Drifted.Select(k => $"drifted:{k}").ToList();

    private static ProfileStepDto Step(string kind, string name, string action, string? reason = null, List<string>? notes = null) =>
        new(kind, name, action, reason, [], [], notes ?? []);

    /// <summary>The display name of a client kind (for profile listings of clients that may not be installed).</summary>
    public string ClientNameOf(string kind) => _registry.Get(kind) is { } adapter ? DisplayName(adapter) : NameOf(kind);
}
