using Astra.Core.Tokens;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Orders a token's provider pool for one request (issue #2): which member goes first, and which ones a failover tries
/// next. Every strategy is a preset of tiers and an in-tier pick:
/// <list type="bullet">
/// <item><see cref="TokenPoolStrategies.Failover"/>: each member is its own tier, in list order.</item>
/// <item><see cref="TokenPoolStrategies.RoundRobin"/>: one tier, a cursor per token.</item>
/// <item><see cref="TokenPoolStrategies.Weighted"/>: one tier, smooth weighted round robin (nginx) — proportional and interleaved, not random.</item>
/// <item><see cref="TokenPoolStrategies.Tiered"/>: <see cref="TokenPoolMember.Tier"/> ascending, weighted round robin inside a tier.</item>
/// <item><see cref="TokenPoolStrategies.LeastBusy"/>: one tier, fewest requests in flight, then fewest assigned in the last <see cref="Window"/>.</item>
/// </list>
/// The pick comes from the first tier; the failover order is the rest of that tier, then the later tiers.
/// <para>
/// Sticky sessions: a conversation (<see cref="StickyKeys"/>) bound to a member goes back to it while it is available,
/// whatever the tiers say — upstream prompt caches live per provider. Bindings are per token, follow the member that
/// actually served the request (<see cref="Bind"/>), and idle out after <see cref="BindingTtl"/>. A member that
/// recovers does not pull its old conversations back: they would lose their cache a second time.
/// </para>
/// <para>State is in memory only; a restart costs one cache miss per conversation and restarts the rotation.</para>
/// </summary>
public sealed class ProviderPoolScheduler(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan BindingTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public const int MaxBindings = 10_000;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;

    private sealed class Binding(string providerId, DateTimeOffset lastUsed)
    {
        public string ProviderId { get; set; } = providerId;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }

    /// <summary>One request's hold on a provider (least-busy accounting); disposing releases it (idempotent).</summary>
    public sealed class Lease(ProviderPoolScheduler owner, string providerId) : IDisposable
    {
        private int _released;

        public string ProviderId { get; } = providerId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(ProviderId);
        }
    }

    /// <summary>
    /// The order in which this request tries <paramref name="available"/> (members of <paramref name="pool"/>, in pool
    /// order, already filtered to those that can take it). With <paramref name="commit"/> false nothing advances: the
    /// rotation and bindings are only read (count_tokens, previews).
    /// </summary>
    public IReadOnlyList<TokenPoolMember> Order(
        string tokenId, TokenPool pool, IReadOnlyList<TokenPoolMember> available, string? stickyKey, bool commit)
    {
        if (available.Count == 0) return [];
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var bindingKey = pool.Sticky == TokenPoolSticky.Session && stickyKey is not null ? tokenId + "\n" + stickyKey : null;
            TokenPoolMember? bound = null;
            if (bindingKey is not null)
            {
                SweepBindings(now);
                if (_bindings.TryGetValue(bindingKey, out var binding))
                {
                    if (now - binding.LastUsed > BindingTtl) _bindings.Remove(bindingKey);
                    else
                    {
                        bound = available.FirstOrDefault(m => m.ProviderId == binding.ProviderId);
                        if (bound is not null && commit) binding.LastUsed = now;
                    }
                }
            }

            var tiers = Tiers(pool.Strategy, available);
            var first = tiers[0];
            // A sticky request neither advances the rotation nor counts as a new assignment of the tier.
            var pick = bound ?? Pick(tokenId, pool.Strategy, first, now, commit && bound is null);
            if (bindingKey is not null && bound is null && commit) _bindings[bindingKey] = new Binding(pick.ProviderId, now);

            // A bound member from a later tier goes first; the first tier still comes right after it.
            var result = new List<TokenPoolMember>(available.Count) { pick };
            foreach (var m in Rest(tokenId, pool.Strategy, first, pick, now)) result.Add(m);
            foreach (var tier in tiers.Skip(1))
                foreach (var m in Rest(tokenId, pool.Strategy, tier, null, now))
                    if (!ReferenceEquals(m, pick)) result.Add(m);
            return result;
        }
    }

    /// <summary>Takes an in-flight slot on the provider for the request's lifetime.</summary>
    public Lease Begin(string providerId)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            _inflight[providerId] = _inflight.GetValueOrDefault(providerId) + 1;
            if (!_recent.TryGetValue(providerId, out var queue)) _recent[providerId] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(now);
            return new Lease(this, providerId);
        }
    }

    /// <summary>Binds the conversation to the member that served it (no-op without a key or with sticky sessions off).</summary>
    public void Bind(string tokenId, TokenPool pool, string? stickyKey, string providerId)
    {
        if (pool.Sticky != TokenPoolSticky.Session || stickyKey is null) return;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var key = tokenId + "\n" + stickyKey;
            if (_bindings.TryGetValue(key, out var binding))
            {
                binding.ProviderId = providerId;
                binding.LastUsed = now;
            }
            else _bindings[key] = new Binding(providerId, now);
        }
    }

    /// <summary>Drops a token's rotation and bindings (its pool changed or it was deleted).</summary>
    public void Forget(string tokenId)
    {
        lock (_gate)
        {
            var prefix = tokenId + "\n";
            foreach (var key in _bindings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _bindings.Remove(key);
            foreach (var key in _weights.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _weights.Remove(key);
            _cursors.Remove(tokenId);
        }
    }

    /// <summary>The provider a conversation is bound to (diagnostics / tests).</summary>
    public string? BoundProvider(string tokenId, string stickyKey)
    {
        lock (_gate) return _bindings.TryGetValue(tokenId + "\n" + stickyKey, out var b) ? b.ProviderId : null;
    }

    /// <summary>Requests in flight on the provider (diagnostics / tests).</summary>
    public int InFlight(string providerId)
    {
        lock (_gate) return _inflight.GetValueOrDefault(providerId);
    }

    private static List<List<TokenPoolMember>> Tiers(string strategy, IReadOnlyList<TokenPoolMember> members) => strategy switch
    {
        TokenPoolStrategies.Failover => members.Select(m => new List<TokenPoolMember> { m }).ToList(),
        TokenPoolStrategies.Tiered => members.GroupBy(m => m.Tier).OrderBy(g => g.Key).Select(g => g.ToList()).ToList(),
        _ => [members.ToList()],
    };

    /// <summary>The first member of a tier. Call under the lock.</summary>
    private TokenPoolMember Pick(string tokenId, string strategy, List<TokenPoolMember> tier, DateTimeOffset now, bool advance)
    {
        if (tier.Count == 1) return tier[0];
        switch (strategy)
        {
            case TokenPoolStrategies.RoundRobin:
            {
                var cursor = _cursors.GetValueOrDefault(tokenId);
                var pick = tier[(int)((uint)cursor % (uint)tier.Count)];
                if (advance) _cursors[tokenId] = cursor + 1;
                return pick;
            }
            case TokenPoolStrategies.Weighted:
            case TokenPoolStrategies.Tiered:
            {
                // Smooth weighted round robin: every member gains its weight, the leader is picked and pays the total.
                long total = 0;
                TokenPoolMember? best = null;
                long bestWeight = long.MinValue;
                var current = new long[tier.Count];
                for (var i = 0; i < tier.Count; i++)
                {
                    var m = tier[i];
                    current[i] = _weights.GetValueOrDefault(WeightKey(tokenId, m)) + Math.Max(1, m.Weight);
                    total += Math.Max(1, m.Weight);
                    if (current[i] > bestWeight)
                    {
                        bestWeight = current[i];
                        best = m;
                    }
                }
                if (advance)
                    for (var i = 0; i < tier.Count; i++)
                        _weights[WeightKey(tokenId, tier[i])] = ReferenceEquals(tier[i], best) ? current[i] - total : current[i];
                return best!;
            }
            case TokenPoolStrategies.LeastBusy:
                return tier.OrderBy(m => _inflight.GetValueOrDefault(m.ProviderId)).ThenBy(m => RecentCount(m.ProviderId, now))
                    .ThenBy(tier.IndexOf).First();
            default:
                return tier[0];
        }
    }

    /// <summary>A tier's failover order after <paramref name="pick"/> (excluded). Never advances anything. Call under the lock.</summary>
    private IEnumerable<TokenPoolMember> Rest(string tokenId, string strategy, List<TokenPoolMember> tier, TokenPoolMember? pick, DateTimeOffset now)
    {
        IEnumerable<TokenPoolMember> ordered = strategy switch
        {
            // The rotation continues after the pick.
            TokenPoolStrategies.RoundRobin when pick is not null =>
                Enumerable.Range(1, tier.Count).Select(i => tier[(tier.IndexOf(pick) + i) % tier.Count]),
            TokenPoolStrategies.Weighted or TokenPoolStrategies.Tiered =>
                tier.OrderByDescending(m => m.Weight).ThenBy(tier.IndexOf),
            TokenPoolStrategies.LeastBusy =>
                tier.OrderBy(m => _inflight.GetValueOrDefault(m.ProviderId)).ThenBy(m => RecentCount(m.ProviderId, now)).ThenBy(tier.IndexOf),
            _ => tier,
        };
        return ordered.Where(m => !ReferenceEquals(m, pick)).ToList();
    }

    private static string WeightKey(string tokenId, TokenPoolMember m) => tokenId + "\n" + m.ProviderId;

    private void Release(string providerId)
    {
        lock (_gate)
        {
            var left = _inflight.GetValueOrDefault(providerId) - 1;
            if (left > 0) _inflight[providerId] = left;
            else _inflight.Remove(providerId);
        }
    }

    /// <summary>Assignments within <see cref="Window"/>; prunes older entries. Call under the lock.</summary>
    private int RecentCount(string providerId, DateTimeOffset now)
    {
        if (!_recent.TryGetValue(providerId, out var queue)) return 0;
        while (queue.Count > 0 && now - queue.Peek() > Window) queue.Dequeue();
        return queue.Count;
    }

    /// <summary>Drops idle bindings and, past <see cref="MaxBindings"/>, the least recently used. Call under the lock.</summary>
    private void SweepBindings(DateTimeOffset now)
    {
        if (_bindings.Count == 0) return;
        if (now >= _nextSweep)
        {
            _nextSweep = now + SweepInterval;
            List<string>? stale = null;
            foreach (var (key, binding) in _bindings)
                if (now - binding.LastUsed > BindingTtl) (stale ??= []).Add(key);
            if (stale is not null)
                foreach (var key in stale) _bindings.Remove(key);
        }
        if (_bindings.Count < MaxBindings) return;
        foreach (var key in _bindings.OrderBy(kv => kv.Value.LastUsed).Take(_bindings.Count - MaxBindings + 1).Select(kv => kv.Key).ToList())
            _bindings.Remove(key);
    }
}
