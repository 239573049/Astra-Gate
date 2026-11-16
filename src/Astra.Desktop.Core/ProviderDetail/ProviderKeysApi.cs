using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>Where an API key of a provider's key list stands for the gateway right now (web <c>keyState</c>).</summary>
public enum KeyState
{
    /// <summary>In rotation.</summary>
    Ok,

    /// <summary>Switched off.</summary>
    Off,

    /// <summary>Rejected by the upstream (401 / 403) and resting until its cooldown ends.</summary>
    AuthFailed,

    /// <summary>Rate limited (429) and resting until its cooldown ends.</summary>
    Cooling,
}

/// <summary>Rules of the key list shared by the page and its tests (web <c>ProviderKeyList.tsx</c>).</summary>
public static class ProviderKeyLogic
{
    /// <summary>Largest weight a key may take.</summary>
    public const int MaxWeight = 1000;

    /// <summary>
    /// A rejected key (401 / 403) rests with a "401 …" reason (the gateway writes it); any other cooldown is a rate limit; a lapsed cooldown
    /// is none.
    /// </summary>
    public static KeyState StateOf(ProviderAccountDto key, DateTimeOffset now)
    {
        if (!key.Enabled) return KeyState.Off;
        if (key.CooldownUntilUtc is { } until && until > now)
            return key.LastError is { } e && (e.StartsWith("401 ", StringComparison.Ordinal) || e.StartsWith("403 ", StringComparison.Ordinal))
                ? KeyState.AuthFailed : KeyState.Cooling;
        return KeyState.Ok;
    }

    /// <summary>Whether the provider holds a key list (<c>settings.key_pool</c>).</summary>
    public static bool IsPool(ProviderDto provider) =>
        provider.Settings["key_pool"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var on) && on;

    /// <summary>The display name of a wire protocol id.</summary>
    public static string ProtocolName(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIChat => "Chat",
        ApiProtocol.OpenAIResponses => "Responses",
        ApiProtocol.Anthropic => "Anthropic",
        _ => "Gemini",
    };

    /// <summary>Whether <paramref name="weight"/> is a valid key weight (empty counts as 1).</summary>
    public static bool WeightOk(decimal? weight) => weight is null || (weight == Math.Truncate(weight.Value) && weight >= 1 && weight <= MaxWeight);

    /// <summary>The ids in the order after moving the key at <paramref name="index"/> by <paramref name="delta"/>; null when it cannot move.</summary>
    public static List<string>? Moved(IReadOnlyList<ProviderAccountDto> keys, int index, int delta)
    {
        var j = index + delta;
        if (index < 0 || j < 0 || j >= keys.Count) return null;
        var ids = keys.Select(k => k.Id).ToList();
        (ids[index], ids[j]) = (ids[j], ids[index]);
        return ids;
    }
}

/// <summary>
/// The queries and writes of a provider's API key list (web/src/api/hooks.ts "API key list"): <c>['provider-keys', id]</c> holds the list, every
/// write answers with the whole list, and the provider row (<c>settings.key_pool</c>, the mirrored first key) and usage are refetched.
/// </summary>
public sealed class ProviderKeysApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly string _id;

    /// <summary>The key list of provider <paramref name="id"/>.</summary>
    public ProviderKeysApi(QueryClient queries, ApiClient api, string id)
    {
        _api = api;
        _queries = queries;
        _id = id;
        Keys = queries.Query(KeysKey(id), async ct => await api.GetAsync<List<ProviderAccountDto>>(Base(id) + "/keys", ct) ?? []);
    }

    /// <summary>GET …/keys; key ['provider-keys', id] (empty while the provider holds its single key in the row).</summary>
    public Query<List<ProviderAccountDto>> Keys { get; }

    /// <summary>['provider-keys', id]</summary>
    public static QueryKey KeysKey(string id) => QueryKey.Of("provider-keys", id);

    private static string Base(string id) => "/api/providers/" + ApiClient.Enc(id);

    private string KeyPath(string keyId) => Base(_id) + "/keys/" + ApiClient.Enc(keyId);

    private void Written(List<ProviderAccountDto>? list)
    {
        if (list is not null) _queries.SetData(KeysKey(_id), list);
        _queries.Invalidate("provider", _id);
        _queries.Invalidate("providers");
        _queries.Invalidate(ProviderDetailKeys.ProviderUsage(_id));
    }

    /// <summary>POST …/keys.</summary>
    public async Task AddAsync(ProviderKeyAdd body, CancellationToken ct = default)
        => Written(await _api.PostAsync<ProviderKeyAdd, List<ProviderAccountDto>>(Base(_id) + "/keys", body, ct));

    /// <summary>PATCH …/keys/{keyId}.</summary>
    public async Task PatchAsync(string keyId, ProviderKeyPatch body, CancellationToken ct = default)
        => Written(await _api.PatchAsync<ProviderKeyPatch, List<ProviderAccountDto>>(KeyPath(keyId), body, ct));

    /// <summary>DELETE …/keys/{keyId}.</summary>
    public async Task RemoveAsync(string keyId, CancellationToken ct = default)
        => Written(await _api.DeleteAsync<List<ProviderAccountDto>>(KeyPath(keyId), ct));

    /// <summary>PUT …/keys/order: the new failover order.</summary>
    public async Task ReorderAsync(List<string> ids, CancellationToken ct = default)
        => Written(await _api.PutAsync<AccountOrder, List<ProviderAccountDto>>(Base(_id) + "/keys/order", new AccountOrder(ids), ct));

    /// <summary>POST …/keys/{keyId}/activate: the key requests start with (what manual mode pins).</summary>
    public async Task ActivateAsync(string keyId, CancellationToken ct = default)
        => Written(await _api.PostAsync<List<ProviderAccountDto>>(KeyPath(keyId) + "/activate", ct));
}
