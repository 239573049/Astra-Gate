using System.Collections.Concurrent;

namespace Astra.Data;

/// <summary>
/// Read-through cache for rows the gateway reads on every request (tokens, providers, models, clients). An entry is
/// served only while <see cref="SqliteConnectionFactory.Generation"/> still has the value it had before the entry was
/// read, so any change this process commits invalidates every entry at once; a change another process commits
/// (<c>astra-server restore-all</c> beside a running server) is seen within <see cref="MaxAge"/>. Values must be
/// immutable or copied by the caller: cache the raw row and map it per call. Null results are not cached unless
/// <paramref name="cacheMisses"/> is enabled (provider model names can legitimately be absent). Both hits and misses
/// share the generation, expiration and capacity bound; the cache is emptied when it reaches <paramref name="capacity"/>.
/// </summary>
internal sealed class RowCache<TKey, TValue>(SqliteConnectionFactory factory, int capacity = 1024, bool cacheMisses = false)
    where TKey : notnull
    where TValue : class
{
    /// <summary>How long an entry is served at most, for writes this process does not see.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);

    private static readonly long MaxAgeMs = (long)MaxAge.TotalMilliseconds;
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    public async Task<TValue?> GetAsync(TKey key, Func<TKey, CancellationToken, Task<TValue?>> load, CancellationToken ct)
    {
        var generation = factory.Generation;
        var now = Environment.TickCount64;
        if (_entries.TryGetValue(key, out var hit) && hit.Generation == generation && now - hit.ReadAt < MaxAgeMs) return hit.Value;
        var value = await load(key, ct);
        if (value is not null || cacheMisses)
        {
            if (_entries.Count >= capacity) _entries.Clear();
            _entries[key] = new Entry(generation, now, value);
        }
        return value;
    }

    private sealed record Entry(long Generation, long ReadAt, TValue? Value);
}
