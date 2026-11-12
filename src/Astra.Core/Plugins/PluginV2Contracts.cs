namespace Astra.Core.Plugins;

/// <summary>One key of a v2 plugin's own storage (<c>astra.storage</c>).</summary>
public sealed record PluginKvItem(string Key, string ValueJson, DateTimeOffset UpdatedAtUtc);

/// <summary>A line of a v2 plugin's activity log: a sensitive call it made, never what it carried (design/plugin-v2 01 §6).</summary>
/// <param name="Call">The host call, e.g. <c>http.fetch</c>, <c>secrets.set</c>, <c>permissions.request</c>.</param>
/// <param name="Target">What it was aimed at: an origin, a secret's name, the permissions asked for; never a value.</param>
/// <param name="Outcome">ok, denied, failed or a short reason.</param>
public sealed record PluginActivity(string PluginId, DateTimeOffset AtUtc, string Call, string? Target, string Outcome);

/// <summary>
/// What v2 plugins keep in SQLite (migration 0024): their key-value storage, their secrets (stored protected —
/// the caller protects and unprotects them), the permissions the user granted, the activity log, and the v2
/// successors the user said not to be reminded of. Everything is keyed by the plugin's manifest id, so it survives
/// an update or a reinstall; v1 plugins have none of it.
/// </summary>
public interface IPluginDataStore
{
    Task<string?> GetAsync(string pluginId, string key, CancellationToken ct = default);

    Task SetAsync(string pluginId, string key, string valueJson, CancellationToken ct = default);

    Task<bool> DeleteAsync(string pluginId, string key, CancellationToken ct = default);

    /// <summary>Keys starting with <paramref name="prefix"/>, in key order, after <paramref name="after"/> (a cursor).</summary>
    Task<IReadOnlyList<PluginKvItem>> ListAsync(string pluginId, string prefix, int limit, string? after, CancellationToken ct = default);

    /// <summary>The bytes the plugin's keys and values take (for its quota).</summary>
    Task<long> SizeAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Drops the plugin's storage and secrets (it was removed and the user chose to forget its data).</summary>
    Task ClearAsync(string pluginId, CancellationToken ct = default);

    Task<string?> GetSecretAsync(string pluginId, string name, CancellationToken ct = default);

    Task SetSecretAsync(string pluginId, string name, string protectedValue, CancellationToken ct = default);

    Task<bool> DeleteSecretAsync(string pluginId, string name, CancellationToken ct = default);

    /// <summary>The names of its secrets (never the values), for the settings form's "set" markers.</summary>
    Task<IReadOnlyList<string>> SecretNamesAsync(string pluginId, CancellationToken ct = default);

    /// <summary>The permissions the user granted the plugin; empty when none (or not installed as v2).</summary>
    Task<IReadOnlyList<string>> GrantsAsync(string pluginId, CancellationToken ct = default);

    Task SetGrantsAsync(string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default);

    Task AddActivityAsync(PluginActivity activity, CancellationToken ct = default);

    /// <summary>The newest lines first.</summary>
    Task<IReadOnlyList<PluginActivity>> ActivityAsync(string pluginId, int limit, CancellationToken ct = default);

    /// <summary>The v2 successor version the user dismissed for the v1 plugin <paramref name="spec"/>; null when none.</summary>
    Task<string?> DismissedSuccessorAsync(string spec, CancellationToken ct = default);

    /// <summary>Records (or with null clears) a dismissed successor.</summary>
    Task SetDismissedSuccessorAsync(string spec, string? version, CancellationToken ct = default);
}

/// <summary>An in-memory <see cref="IPluginDataStore"/>: the relay-free test hosts and the plugin tests use it.</summary>
public sealed class MemoryPluginDataStore : IPluginDataStore
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<(string, string), PluginKvItem> _kv = new(Comparer<(string, string)>.Create(Compare));
    private readonly Dictionary<(string, string), string> _secrets = [];
    private readonly Dictionary<string, List<string>> _grants = [];
    private readonly List<PluginActivity> _activity = [];
    private readonly Dictionary<string, string> _dismissed = [];

    private static int Compare((string, string) a, (string, string) b)
    {
        var c = string.CompareOrdinal(a.Item1, b.Item1);
        return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
    }

    public Task<string?> GetAsync(string pluginId, string key, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_kv.TryGetValue((pluginId, key), out var v) ? v.ValueJson : null);
    }

    public Task SetAsync(string pluginId, string key, string valueJson, CancellationToken ct = default)
    {
        lock (_lock) _kv[(pluginId, key)] = new PluginKvItem(key, valueJson, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string pluginId, string key, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_kv.Remove((pluginId, key)));
    }

    public Task<IReadOnlyList<PluginKvItem>> ListAsync(string pluginId, string prefix, int limit, string? after, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PluginKvItem>>(_kv
                .Where(e => e.Key.Item1 == pluginId && e.Key.Item2.StartsWith(prefix, StringComparison.Ordinal)
                            && (after is null || string.CompareOrdinal(e.Key.Item2, after) > 0))
                .Take(limit).Select(e => e.Value).ToList());
    }

    public Task<long> SizeAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_kv.Where(e => e.Key.Item1 == pluginId).Sum(e => (long)e.Key.Item2.Length + e.Value.ValueJson.Length));
    }

    public Task ClearAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var k in _kv.Keys.Where(k => k.Item1 == pluginId).ToList()) _kv.Remove(k);
            foreach (var k in _secrets.Keys.Where(k => k.Item1 == pluginId).ToList()) _secrets.Remove(k);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string pluginId, string name, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_secrets.TryGetValue((pluginId, name), out var v) ? v : null);
    }

    public Task SetSecretAsync(string pluginId, string name, string protectedValue, CancellationToken ct = default)
    {
        lock (_lock) _secrets[(pluginId, name)] = protectedValue;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSecretAsync(string pluginId, string name, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_secrets.Remove((pluginId, name)));
    }

    public Task<IReadOnlyList<string>> SecretNamesAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<string>>(_secrets.Keys.Where(k => k.Item1 == pluginId).Select(k => k.Item2).Order(StringComparer.Ordinal).ToList());
    }

    public Task<IReadOnlyList<string>> GrantsAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<string>>(_grants.TryGetValue(pluginId, out var g) ? [.. g] : []);
    }

    public Task SetGrantsAsync(string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        lock (_lock) _grants[pluginId] = [.. permissions];
        return Task.CompletedTask;
    }

    public Task AddActivityAsync(PluginActivity activity, CancellationToken ct = default)
    {
        lock (_lock) _activity.Add(activity);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PluginActivity>> ActivityAsync(string pluginId, int limit, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<PluginActivity>>(_activity.Where(a => a.PluginId == pluginId).Reverse().Take(limit).ToList());
    }

    public Task<string?> DismissedSuccessorAsync(string spec, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_dismissed.TryGetValue(spec, out var v) ? v : null);
    }

    public Task SetDismissedSuccessorAsync(string spec, string? version, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (version is null) _dismissed.Remove(spec);
            else _dismissed[spec] = version;
        }
        return Task.CompletedTask;
    }
}
