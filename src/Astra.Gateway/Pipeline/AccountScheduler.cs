using Astra.Core.Models;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Spreads requests over a subscription provider's accounts (switch mode <see cref="SwitchModes.Balanced"/>, plan §5.4)
/// and keeps one conversation on one account.
/// <para>
/// A new conversation goes to the usable account with the fewest requests in flight, then the fewest assigned in the
/// last <see cref="Window"/>, then the lowest <see cref="ProviderAccount.SortOrder"/>. A conversation that carries a sticky
/// key (<see cref="StickyKeys"/>) is bound to the account it got and returns there while that account stays usable;
/// otherwise it is re-bound to a fresh pick. Bindings idle out after <see cref="BindingTtl"/>.
/// </para>
/// <para>State is in memory only: a restart loses the bindings, which costs one upstream cache miss, nothing more.</para>
/// </summary>
public sealed class AccountScheduler(TimeProvider? timeProvider = null)
{
    /// <summary>How long an unused binding survives.</summary>
    public static readonly TimeSpan BindingTtl = TimeSpan.FromHours(1);

    /// <summary>Look-back of the "recently assigned" tie-breaker.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of the binding table; the least recently used bindings go first.</summary>
    public const int MaxBindings = 10_000;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;

    private sealed class Binding(string accountId, DateTimeOffset lastUsed)
    {
        public string AccountId { get; set; } = accountId;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }

    /// <summary>One request's hold on an account; disposing releases its in-flight slot (idempotent).</summary>
    public sealed class Lease(AccountScheduler owner, string accountId) : IDisposable
    {
        private int _released;

        public string AccountId { get; } = accountId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(AccountId);
        }
    }

    /// <summary>
    /// Picks the account for one request and takes an in-flight slot on it. Accounts in <paramref name="exclude"/>
    /// (already tried by this request) are skipped. Null when no account is usable — the caller then falls back to
    /// <see cref="SubscriptionSupport.SelectAccount"/>, which still hands out an expired account for a clear error.
    /// </summary>
    public Lease? Pick(
        string providerId, IReadOnlyList<ProviderAccount> accounts, string? stickyKey,
        IReadOnlyCollection<string>? exclude = null)
    {
        var now = _clock.GetUtcNow();
        var candidates = accounts
            .Where(a => SubscriptionSupport.IsUsable(a, now) && exclude?.Contains(a.Id) != true)
            .ToList();
        if (candidates.Count == 0) return null;

        lock (_gate)
        {
            var bindingKey = stickyKey is null ? null : providerId + "\n" + stickyKey;
            Binding? binding = null;
            if (bindingKey is not null)
            {
                SweepBindings(now);
                // A binding idle past its TTL is dead even when the periodic sweep has not reached it yet.
                if (_bindings.TryGetValue(bindingKey, out binding) && now - binding.LastUsed > BindingTtl)
                {
                    _bindings.Remove(bindingKey);
                    binding = null;
                }
            }

            var chosen = (binding is null ? null : candidates.FirstOrDefault(a => a.Id == binding.AccountId))
                         ?? candidates
                             .OrderBy(a => _inflight.GetValueOrDefault(a.Id))
                             .ThenBy(a => RecentCount(a.Id, now))
                             .ThenBy(a => a.SortOrder)
                             .ThenBy(a => a.CreatedAt)
                             .ThenBy(a => a.Id, StringComparer.Ordinal)
                             .First();

            if (bindingKey is not null)
            {
                if (binding is null) _bindings[bindingKey] = new Binding(chosen.Id, now);
                else
                {
                    binding.AccountId = chosen.Id;
                    binding.LastUsed = now;
                }
            }

            _inflight[chosen.Id] = _inflight.GetValueOrDefault(chosen.Id) + 1;
            if (!_recent.TryGetValue(chosen.Id, out var queue))
                _recent[chosen.Id] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(now);
            return new Lease(this, chosen.Id);
        }
    }

    /// <summary>Requests currently in flight on the account (diagnostics / tests).</summary>
    public int InFlight(string accountId)
    {
        lock (_gate) return _inflight.GetValueOrDefault(accountId);
    }

    /// <summary>The account a sticky key is bound to right now, if any (diagnostics / tests).</summary>
    public string? BoundAccount(string providerId, string stickyKey)
    {
        lock (_gate) return _bindings.TryGetValue(providerId + "\n" + stickyKey, out var b) ? b.AccountId : null;
    }

    private void Release(string accountId)
    {
        lock (_gate)
        {
            var left = _inflight.GetValueOrDefault(accountId) - 1;
            if (left > 0) _inflight[accountId] = left;
            else _inflight.Remove(accountId);
        }
    }

    /// <summary>Assignments within <see cref="Window"/>; prunes older entries. Call under the lock.</summary>
    private int RecentCount(string accountId, DateTimeOffset now)
    {
        if (!_recent.TryGetValue(accountId, out var queue)) return 0;
        while (queue.Count > 0 && now - queue.Peek() > Window) queue.Dequeue();
        return queue.Count;
    }

    /// <summary>Drops idle bindings and, past <see cref="MaxBindings"/>, the least recently used. Call under the lock.</summary>
    private void SweepBindings(DateTimeOffset now)
    {
        if (_bindings.Count == 0) return;
        // The idle scan is linear, so it runs at most once per interval.
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
