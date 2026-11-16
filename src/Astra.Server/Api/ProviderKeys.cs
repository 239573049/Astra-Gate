using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;

namespace Astra.Server.Api;

/// <summary>
/// A provider's API key list: several keys on one provider, stored as <see cref="AccountKinds.ApiKey"/> accounts so selection,
/// failover, balancing, cooldown and per-key usage reuse the subscription-account machinery (the key's weight, protocol and note
/// are <c>extra</c> entries, so there is no schema change). A provider that holds one key keeps it in
/// <see cref="Provider.ApiKeyEnc"/> as always; adding the second key moves the provider to the list
/// (<see cref="Provider.KeyPool"/>) and <see cref="Provider.ApiKeyEnc"/> then mirrors the first enabled key, so an older binary
/// still routes. Multi-key weighting and the cool-down-and-move-on rule follow magpie's account picker (MIT).
/// </summary>
public static class ProviderKeys
{
    /// <summary>Most keys one provider may hold.</summary>
    public const int MaxKeys = 100;

    private const int MaxNameLength = 80;
    private const int MaxNotesLength = 500;

    /// <summary>The provider's keys in failover order, <c>isCurrent</c> marking the one requests go to first.</summary>
    public static async Task<List<ProviderAccountDto>> ListAsync(AstraDatabase db, Provider p, CancellationToken ct)
    {
        var keys = await KeysOfAsync(db, p.Id, ct);
        var current = SubscriptionSupport.SelectAccount(keys, null, DateTimeOffset.UtcNow)?.Id;
        return keys.Select(k => SubscriptionEndpoints.ToDto(k, k.Id == current)).ToList();
    }

    private static async Task<List<ProviderAccount>> KeysOfAsync(AstraDatabase db, string providerId, CancellationToken ct) =>
        (await db.Accounts.ListAsync(providerId, ct)).Where(a => a.Kind == AccountKinds.ApiKey).ToList();

    /// <summary>Adds a key (first moving a single legacy key into the list), then returns the list.</summary>
    public static async Task<List<ProviderAccountDto>> AddAsync(
        AstraDatabase db, ISecretProtector secrets, Provider p, ProviderKeyAdd request, CancellationToken ct)
    {
        if (!AuthSchemes.IsStaticKey(p.AuthScheme))
            throw new AdminApiException(400, "Only a provider that authenticates with an API key can hold a key list");
        var key = CleanKey(request.Key) ?? throw new AdminApiException(400, "key is required");
        var existing = p.KeyPool ? await KeysOfAsync(db, p.Id, ct) : [];
        if (existing.Count >= MaxKeys) throw new AdminApiException(400, $"A provider holds at most {MaxKeys} keys");
        ProviderAccount? legacy = null;
        if (!p.KeyPool && p.ApiKeyEnc is not null)
        {
            legacy = NewAccount(p.Id, DefaultName(1), p.ApiKeyEnc, secrets.Unprotect(p.ApiKeyEnc), true);
            existing.Add(legacy);
        }
        if (existing.Any(k => k.AccessTokenEnc is not null && secrets.Unprotect(k.AccessTokenEnc) == key))
            throw new AdminApiException(409, "This key is already in the list");

        var account = NewAccount(p.Id, request.Name?.Trim() is { Length: > 0 } name ? Limit(name, MaxNameLength) : DefaultName(existing.Count + 1),
            secrets.Protect(key), key, request.Enabled ?? true);
        Apply(account, request.Protocol, request.Weight, request.HttpProxy, request.Notes, p);

        if (legacy is not null) await db.Accounts.InsertAsync(legacy, ct);
        await db.Accounts.InsertAsync(account, ct);
        p.Settings[Provider.KeyPoolKey] = true;
        // A list is for taking turns: unless the provider already says how its accounts switch, start with failover (in order).
        var section = p.Settings["subscription"] as JsonObject ?? new JsonObject();
        if (section["switch_mode"] is null) section["switch_mode"] = SwitchModes.Failover;
        p.Settings["subscription"] = section;
        await MirrorAsync(db, p, ct);
        return await ListAsync(db, p, ct);
    }

    /// <summary>Changes one key; omitted fields keep their value. Returns the list.</summary>
    public static async Task<List<ProviderAccountDto>> PatchAsync(
        AstraDatabase db, ISecretProtector secrets, Provider p, string keyId, ProviderKeyPatch patch, CancellationToken ct)
    {
        var account = await RequireKeyAsync(db, p, keyId, ct);
        if (patch.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new AdminApiException(400, "name must not be empty");
            account.DisplayName = Limit(name.Trim(), MaxNameLength);
        }
        if (patch.Key is { } rawKey)
        {
            var key = CleanKey(rawKey) ?? throw new AdminApiException(400, "key must not be empty");
            var others = (await KeysOfAsync(db, p.Id, ct)).Where(k => k.Id != account.Id);
            if (others.Any(k => k.AccessTokenEnc is not null && secrets.Unprotect(k.AccessTokenEnc) == key))
                throw new AdminApiException(409, "This key is already in the list");
            account.AccessTokenEnc = secrets.Protect(key);
            account.Extra[SubscriptionEndpoints.KeyMaskedExtra] = SubscriptionEndpoints.MaskToken(key);
            // A new secret starts clean: a rejection of the old one says nothing about it.
            await db.Accounts.SetCooldownAsync(account.Id, null, null, ct);
        }
        Apply(account, patch.Protocol, patch.Weight, patch.HttpProxy, patch.Notes, p);
        await db.Accounts.UpdateAsync(account, ct);
        if (patch.HttpProxy is not null) await db.Accounts.SetHttpProxyAsync(account.Id, account.HttpProxy, ct);
        if (patch.Enabled is { } enabled)
        {
            await db.Accounts.SetEnabledAsync(account.Id, enabled, ct);
            if (!enabled && account.IsCurrent) await db.Accounts.SetCurrentAsync(p.Id, null, ct);
        }
        await MirrorAsync(db, p, ct);
        return await ListAsync(db, p, ct);
    }

    /// <summary>Removes one key; the last one takes the provider back to the single-key form (without a key). Returns the list.</summary>
    public static async Task<List<ProviderAccountDto>> RemoveAsync(AstraDatabase db, Provider p, string keyId, CancellationToken ct)
    {
        var account = await RequireKeyAsync(db, p, keyId, ct);
        await db.Accounts.DeleteAsync(account.Id, ct);
        await MirrorAsync(db, p, ct);
        return await ListAsync(db, p, ct);
    }

    /// <summary>New failover order (first = tried first).</summary>
    public static async Task<List<ProviderAccountDto>> ReorderAsync(AstraDatabase db, Provider p, IReadOnlyList<string> ids, CancellationToken ct)
    {
        await db.Accounts.ReorderAsync(p.Id, ids, ct);
        await MirrorAsync(db, p, ct);
        return await ListAsync(db, p, ct);
    }

    /// <summary>
    /// Keeps the provider row in step with its list: <see cref="Provider.ApiKeyEnc"/> is the first enabled key (none when every key is
    /// off), and a list that has emptied is gone — the provider is a plain, key-less one again. No-op for a provider without a list.
    /// </summary>
    public static async Task MirrorAsync(AstraDatabase db, Provider p, CancellationToken ct)
    {
        var keys = await KeysOfAsync(db, p.Id, ct);
        if (keys.Count == 0)
        {
            if (!p.KeyPool) return;
            p.Settings.Remove(Provider.KeyPoolKey);
            p.ApiKeyEnc = null;
        }
        else
            p.ApiKeyEnc = keys.FirstOrDefault(k => k.Enabled)?.AccessTokenEnc;
        await db.Providers.UpdateAsync(p, ct);
    }

    /// <summary>For the generic account endpoints: after a key account changed there, the provider row follows.</summary>
    internal static async Task MirrorAfterAccountWriteAsync(AstraDatabase db, string providerId, CancellationToken ct = default)
    {
        if (await db.Providers.GetAsync(providerId, ct) is { KeyPool: true } p) await MirrorAsync(db, p, ct);
    }

    /// <summary>Copies the key list of <paramref name="from"/> onto the new provider <paramref name="to"/> (provider duplication).</summary>
    public static async Task CopyAsync(AstraDatabase db, string from, string to, CancellationToken ct)
    {
        foreach (var key in await KeysOfAsync(db, from, ct))
        {
            key.Id = Ulid.NewUlid();
            key.ProviderId = to;
            key.IsCurrent = false;
            key.CooldownUntilUtc = null;
            key.LastError = null;
            key.CreatedAt = key.UpdatedAt = default;
            await db.Accounts.InsertAsync(key, ct);
        }
    }

    // ----- helpers -----

    private static string DefaultName(int n) => $"Key {n}";

    private static ProviderAccount NewAccount(string providerId, string name, string keyEnc, string key, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccount
        {
            Id = Ulid.NewUlid(), ProviderId = providerId, DisplayName = name, AccessTokenEnc = keyEnc, Enabled = enabled,
            Kind = AccountKinds.ApiKey, Source = AccountSources.Key, Status = AccountStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        account.Extra[SubscriptionEndpoints.KeyMaskedExtra] = SubscriptionEndpoints.MaskToken(key);
        return account;
    }

    private static async Task<ProviderAccount> RequireKeyAsync(AstraDatabase db, Provider p, string keyId, CancellationToken ct) =>
        (await KeysOfAsync(db, p.Id, ct)).FirstOrDefault(k => k.Id == keyId)
        ?? throw new AdminApiException(404, "Key not found");

    private static string? CleanKey(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Any(char.IsControl)) throw new AdminApiException(400, "key must not contain control characters");
        return trimmed;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Proxy(string? proxy)
    {
        try { return ProxiedHttpClients.Normalize(proxy); }
        catch (ArgumentException e) { throw new AdminApiException(400, e.Message); }
    }

    /// <summary>Applies the optional fields of an add / patch ("" clears protocol, proxy and notes).</summary>
    private static void Apply(ProviderAccount account, string? protocol, int? weight, string? httpProxy, string? notes, Provider p)
    {
        if (protocol is not null)
        {
            if (protocol.Length == 0) account.Extra.Remove("protocol");
            else
            {
                if (!ApiProtocols.TryParse(protocol, out var parsed)) throw new AdminApiException(400, $"Unknown protocol '{protocol}'");
                if (p.EndpointFor(parsed) is null) throw new AdminApiException(400, $"The provider has no {parsed.ToId()} endpoint");
                account.Extra["protocol"] = parsed.ToId();
            }
        }
        if (weight is { } w)
        {
            if (w is < 1 or > ProviderAccount.MaxKeyWeight) throw new AdminApiException(400, $"weight must be between 1 and {ProviderAccount.MaxKeyWeight}");
            if (w == 1) account.Extra.Remove("weight"); else account.Extra["weight"] = w;
        }
        if (httpProxy is not null) account.HttpProxy = Proxy(httpProxy);
        if (notes is not null)
        {
            if (notes.Length == 0) account.Extra.Remove("notes");
            else account.Extra["notes"] = Limit(notes.Trim(), MaxNotesLength);
        }
    }

    /// <summary>The route group of the key list, inside the provider group (its error filter and write lock).</summary>
    public static void MapProviderKeyEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id}/keys", async (string id, AstraDatabase db, CancellationToken ct) =>
            Results.Ok(await ListAsync(db, await ProviderEndpoints.RequireAsync(db, id, ct), ct)));
        group.MapPost("/{id}/keys", async (string id, ProviderKeyAdd body, AstraDatabase db, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await AddAsync(db, secrets, await ProviderEndpoints.RequireAsync(db, id, ct), body, ct)));
        group.MapPut("/{id}/keys/order", async (string id, AccountOrder body, AstraDatabase db, CancellationToken ct) =>
            Results.Ok(await ReorderAsync(db, await ProviderEndpoints.RequireAsync(db, id, ct), body.Ids ?? [], ct)));
        group.MapPatch("/{id}/keys/{keyId}", async (string id, string keyId, ProviderKeyPatch body, AstraDatabase db, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await PatchAsync(db, secrets, await ProviderEndpoints.RequireAsync(db, id, ct), keyId, body, ct)));
        group.MapDelete("/{id}/keys/{keyId}", async (string id, string keyId, AstraDatabase db, CancellationToken ct) =>
            Results.Ok(await RemoveAsync(db, await ProviderEndpoints.RequireAsync(db, id, ct), keyId, ct)));
        // Make this key the one requests start with (manual mode pins it); an off key is switched on.
        group.MapPost("/{id}/keys/{keyId}/activate", async (string id, string keyId, AstraDatabase db, CancellationToken ct) =>
        {
            var p = await ProviderEndpoints.RequireAsync(db, id, ct);
            var key = await RequireKeyAsync(db, p, keyId, ct);
            if (!key.Enabled) await db.Accounts.SetEnabledAsync(key.Id, true, ct);
            await db.Accounts.SetCurrentAsync(p.Id, key.Id, ct);
            await MirrorAsync(db, p, ct);
            return Results.Ok(await ListAsync(db, p, ct));
        });
    }
}
