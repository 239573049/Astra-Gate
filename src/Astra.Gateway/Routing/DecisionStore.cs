namespace Astra.Gateway.Routing;

/// <summary>
/// Decisions kept for a while under a key: a session's pin, a turn's grade. The lifetime renews on use when asked to,
/// the table is bounded (the least recently used entry goes first), and a restart forgets everything — the next request
/// is judged again, which is the same thing that happens to a session nobody has asked about for a while. An entry made
/// under older settings (<c>generation</c>) is never served.
/// </summary>
internal sealed class DecisionStore(TimeProvider clock, int capacity)
{
    private sealed class Entry(RoutingDecision decision, int generation, TimeSpan ttl, DateTimeOffset now)
    {
        public RoutingDecision Decision { get; } = decision;
        public int Generation { get; } = generation;
        public TimeSpan Ttl { get; } = ttl;
        public DateTimeOffset LastUsed { get; set; } = now;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _sinceSweep;

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    /// <summary>The decision under <paramref name="key"/>, or null when there is none, it has expired or the settings changed since.</summary>
    public RoutingDecision? Get(string key, int generation, bool slide)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.Generation != generation || now - entry.LastUsed > entry.Ttl)
            {
                _entries.Remove(key);
                return null;
            }
            if (slide) entry.LastUsed = now;
            return entry.Decision;
        }
    }

    public void Set(string key, RoutingDecision decision, int generation, TimeSpan ttl)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (++_sinceSweep >= 256) Sweep(now);
            if (!_entries.ContainsKey(key) && _entries.Count >= capacity) DropLeastRecentlyUsed();
            _entries[key] = new Entry(decision, generation, ttl, now);
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        _sinceSweep = 0;
        List<string>? expired = null;
        foreach (var (key, entry) in _entries)
            if (now - entry.LastUsed > entry.Ttl) (expired ??= []).Add(key);
        if (expired is not null)
            foreach (var key in expired) _entries.Remove(key);
    }

    private void DropLeastRecentlyUsed()
    {
        string? oldest = null;
        var at = DateTimeOffset.MaxValue;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastUsed >= at) continue;
            at = entry.LastUsed;
            oldest = key;
        }
        if (oldest is not null) _entries.Remove(oldest);
    }
}
