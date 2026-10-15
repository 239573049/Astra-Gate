namespace Astra.Gateway.Pipeline;

/// <summary>What one upstream attempt said about its provider's health.</summary>
public enum ProviderOutcome
{
    /// <summary>The provider answered (2xx, or a 404 for one model: it is up).</summary>
    Success,

    /// <summary>The request itself was at fault (400, 413, 422, …): says nothing about the provider.</summary>
    Neutral,
    RateLimited,
    AuthRejected,
    ServerError,
    Unreachable,
    Timeout,
}

/// <summary>Health states of a provider as <see cref="ProviderHealth.Status"/> reports them.</summary>
public static class ProviderHealthStates
{
    public const string Healthy = "healthy";

    /// <summary>Rate limited or credentials rejected: skipped until <see cref="ProviderHealthStatus.Until"/>.</summary>
    public const string Cooling = "cooling";

    /// <summary>Circuit open after repeated server / network failures.</summary>
    public const string Open = "open";

    /// <summary>The open period is over: the next request is let through as a probe.</summary>
    public const string HalfOpen = "half_open";
}

/// <summary>A provider's health right now.</summary>
public sealed record ProviderHealthStatus(string State, DateTimeOffset? Until, string? Reason, int ConsecutiveFailures);

/// <summary>
/// In-memory health of API-key providers, shared by every token (issue #2): the upstream limits the provider's key, not
/// a token, so one token's 429 makes every pool skip that provider. A 429 cools the provider until the upstream's reset
/// (capped at <see cref="MaxRateLimitCooldown"/>), rejected credentials for <see cref="AuthCooldown"/>; server errors,
/// network failures and timeouts open a circuit after <see cref="BreakerThreshold"/> in a row, for
/// <see cref="BreakerBase"/> doubling up to <see cref="BreakerMax"/>. When the open period is over one request is let
/// through as a probe (half-open): its success closes the circuit, its failure reopens it for twice as long.
/// <para>
/// Account-based providers are not tracked here: their accounts already cool down (persisted) and fail over inside the
/// provider. Only pool routing reads this; every route reports to it. A restart forgets everything.
/// </para>
/// </summary>
public sealed class ProviderHealth(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan MaxRateLimitCooldown = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AuthCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BreakerBase = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BreakerMax = TimeSpan.FromMinutes(10);

    /// <summary>A probe that never reports back frees its slot after this long.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    public const int BreakerThreshold = 3;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _probeSeq;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset? Until;
        public string? Reason;
        public bool Breaker;
        public TimeSpan Backoff = BreakerBase;
        public long ProbeId;
        public DateTimeOffset ProbeTakenAt;
    }

    /// <summary>The slot of a half-open provider's probe request; disposing frees it unless the probe already reported.</summary>
    public sealed class Probe(ProviderHealth owner, string providerId, long id) : IDisposable
    {
        public string ProviderId { get; } = providerId;

        public void Dispose() => owner.ReleaseProbe(ProviderId, id);
    }

    /// <summary>Whether requests may go to the provider now (a read-only check: takes no probe slot).</summary>
    public bool IsAvailable(string providerId)
    {
        lock (_gate)
            return !_entries.TryGetValue(providerId, out var e) || e.Until is not { } until || _clock.GetUtcNow() >= until;
    }

    /// <summary>
    /// Whether a request may go to the provider now. A half-open provider admits one request at a time, as a probe, whose
    /// <paramref name="probe"/> slot the caller disposes when done (a report frees it too).
    /// </summary>
    public bool TryAdmit(string providerId, out Probe? probe)
    {
        probe = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(providerId, out var e) || e.Until is not { } until) return true;
            var now = _clock.GetUtcNow();
            if (now < until) return false;
            if (!e.Breaker)
            {
                // A plain cooldown is simply over.
                e.Until = null;
                e.Reason = null;
                return true;
            }
            if (e.ProbeId != 0 && now - e.ProbeTakenAt < ProbeTimeout) return false;
            e.ProbeId = ++_probeSeq;
            e.ProbeTakenAt = now;
            probe = new Probe(this, providerId, e.ProbeId);
            return true;
        }
    }

    /// <summary>Records what an attempt on the provider said. <paramref name="retryAt"/> is the upstream's own reset time for a 429.</summary>
    public void Report(string providerId, ProviderOutcome outcome, DateTimeOffset? retryAt = null)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            switch (outcome)
            {
                case ProviderOutcome.Success:
                case ProviderOutcome.Neutral:
                    _entries.Remove(providerId);
                    return;
                case ProviderOutcome.RateLimited:
                {
                    var e = Get(providerId);
                    var until = retryAt is { } at && at > now ? at : now + DefaultRateLimitCooldown;
                    if (until - now > MaxRateLimitCooldown) until = now + MaxRateLimitCooldown;
                    Cool(e, until, "rate_limited");
                    return;
                }
                case ProviderOutcome.AuthRejected:
                    Cool(Get(providerId), now + AuthCooldown, "auth_rejected");
                    return;
                default:
                {
                    var e = Get(providerId);
                    var reason = outcome switch
                    {
                        ProviderOutcome.Unreachable => "unreachable",
                        ProviderOutcome.Timeout => "timeout",
                        _ => "server_error",
                    };
                    if (e.Breaker)
                    {
                        // Requests admitted before the circuit opened still report: only a failure after the open period
                        // (the probe) reopens it, for twice as long.
                        if (e.Until is { } open && now < open) return;
                        e.Backoff = TimeSpan.FromTicks(Math.Min(e.Backoff.Ticks * 2, BreakerMax.Ticks));
                        e.Until = now + e.Backoff;
                        e.Reason = reason;
                        e.ProbeId = 0;
                        e.Failures++;
                        return;
                    }
                    e.Failures++;
                    e.Reason = reason;
                    if (e.Failures < BreakerThreshold) return;
                    e.Breaker = true;
                    e.Backoff = BreakerBase;
                    e.Until = now + e.Backoff;
                    e.ProbeId = 0;
                    return;
                }
            }
        }
    }

    public ProviderHealthStatus Status(string providerId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(providerId, out var e)) return new ProviderHealthStatus(ProviderHealthStates.Healthy, null, null, 0);
            if (e.Until is not { } until) return new ProviderHealthStatus(ProviderHealthStates.Healthy, null, e.Reason, e.Failures);
            if (_clock.GetUtcNow() < until)
                return new ProviderHealthStatus(e.Breaker ? ProviderHealthStates.Open : ProviderHealthStates.Cooling, until, e.Reason, e.Failures);
            return e.Breaker
                ? new ProviderHealthStatus(ProviderHealthStates.HalfOpen, until, e.Reason, e.Failures)
                : new ProviderHealthStatus(ProviderHealthStates.Healthy, null, null, 0);
        }
    }

    /// <summary>When the provider takes requests again; null = now.</summary>
    public DateTimeOffset? AvailableAt(string providerId)
    {
        lock (_gate)
            return _entries.TryGetValue(providerId, out var e) && e.Until is { } until && _clock.GetUtcNow() < until ? until : null;
    }

    private Entry Get(string providerId)
    {
        if (!_entries.TryGetValue(providerId, out var e)) _entries[providerId] = e = new Entry();
        return e;
    }

    private static void Cool(Entry e, DateTimeOffset until, string reason)
    {
        e.Breaker = false;
        e.Failures = 0;
        e.Backoff = BreakerBase;
        e.ProbeId = 0;
        e.Until = e.Until is { } current && current > until ? current : until;
        e.Reason = reason;
    }

    private void ReleaseProbe(string providerId, long id)
    {
        lock (_gate)
            if (_entries.TryGetValue(providerId, out var e) && e.ProbeId == id) e.ProbeId = 0;
    }
}
