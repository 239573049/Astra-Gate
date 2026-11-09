using Astra.Core.Models;
using Astra.Core.Tokens;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The providers one request of a pool route tries, in order (<see cref="Order"/>), and at most how many
/// (<see cref="MaxAttempts"/>). Disposing frees the half-open probe slots the plan took.
/// </summary>
public sealed class PoolPlan(IReadOnlyList<RouteTarget> order, int maxAttempts, bool modelFallback, List<ProviderHealth.Probe> probes) : IDisposable
{
    public IReadOnlyList<RouteTarget> Order { get; } = order;
    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>No member serves the model: the request goes to the first member, once, so the upstream can say so.</summary>
    public bool ModelFallback { get; } = modelFallback;

    public void Dispose()
    {
        foreach (var probe in probes) probe.Dispose();
        probes.Clear();
    }
}

/// <summary>
/// Plans a provider-pool request (issue #2): the members serving the requested model (<see cref="ClientRouting.MatchAllAsync"/>),
/// minus those that cannot take it now — an API-key provider cooling down or with its circuit open
/// (<see cref="ProviderHealth"/>), an account-based provider without a usable account — ordered by the pool's strategy
/// (<see cref="ProviderPoolScheduler"/>).
/// </summary>
public sealed class PoolRouting(ClientRouting routing, UpstreamAuthResolver auth, ProviderHealth health, ProviderPoolScheduler scheduler)
{
    /// <summary>
    /// <paramref name="commit"/> = this request is really sent: the rotation advances, the sticky binding is taken and
    /// half-open probes are claimed. Without it the plan is a read-only preview (count_tokens).
    /// </summary>
    public async Task<PoolPlan> PlanAsync(GatewayRoute route, string clientModel, bool isClaudeCode, string? stickyKey, bool commit, CancellationToken ct)
    {
        var pool = route.Pool ?? throw new InvalidOperationException("Not a pool route.");
        var serving = await routing.MatchAllAsync(route.Targets, clientModel, isClaudeCode, ct);
        // Nobody serves the model: the first member answers for it, exactly like a single provider would.
        if (serving.Count == 0) return new PoolPlan([route.Targets[0]], 1, true, []);

        var probes = new List<ProviderHealth.Probe>();
        var available = await AdmitAsync(serving, commit, probes, ct);
        if (available.Count == 0)
        {
            // Everything is cooling down: one try at the member that recovers first beats refusing outright.
            var soonest = serving.OrderBy(t => health.AvailableAt(t.Provider.Id) ?? DateTimeOffset.MinValue).First();
            return new PoolPlan([soonest], 1, false, probes);
        }

        var members = available.Select(t => MemberOf(pool, t)).ToList();
        var ordered = scheduler.Order(route.Token.Id, pool, members, stickyKey, commit);
        var order = ordered.Select(m => available[members.IndexOf(m)]).ToList();
        return new PoolPlan(order, pool.AttemptLimit(order.Count), false, probes);
    }

    /// <summary>
    /// A client bound to several providers (issue #28): the bound providers serving the model, in binding order, minus those
    /// that cannot take it now (same availability rules as a pool), tried one after another while an attempt fails before
    /// anything reached the client. Null when there is nothing to choose between (fewer than two serve the model, or every one is cooling down) — the caller keeps the plain
    /// <see cref="ClientRouting"/> choice (first provider serving the model, else the primary).
    /// </summary>
    public async Task<PoolPlan?> PlanBindingsAsync(GatewayRoute route, string clientModel, bool isClaudeCode, CancellationToken ct)
    {
        var serving = await routing.MatchAllAsync(route.Targets, clientModel, isClaudeCode, ct);
        if (serving.Count < 2) return null;
        var probes = new List<ProviderHealth.Probe>();
        var available = await AdmitAsync(serving, true, probes, ct);
        if (available.Count == 0)
        {
            foreach (var probe in probes) probe.Dispose();
            return null;
        }
        return new PoolPlan(available, Math.Min(available.Count, TokenPool.MaxAttemptsLimit), false, probes);
    }

    private async Task<List<RouteTarget>> AdmitAsync(List<RouteTarget> serving, bool commit, List<ProviderHealth.Probe> probes, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var available = new List<RouteTarget>();
        foreach (var target in serving)
        {
            if (AuthSchemes.UsesAccounts(target.Provider.AuthScheme))
            {
                var accounts = await auth.ListAccountsAsync(target.Provider.Id, ct);
                var usable = target.AccountId is { } pinned
                    ? accounts.Any(a => a.Id == pinned && SubscriptionSupport.IsUsable(a, now))
                    : accounts.Any(a => SubscriptionSupport.IsUsable(a, now));
                if (usable) available.Add(target);
            }
            else if (!commit)
            {
                if (health.IsAvailable(target.Provider.Id)) available.Add(target);
            }
            else if (health.TryAdmit(target.Provider.Id, out var probe))
            {
                available.Add(target);
                if (probe is not null) probes.Add(probe);
            }
        }
        return available;
    }

    private static TokenPoolMember MemberOf(TokenPool pool, RouteTarget target) =>
        pool.Members.FirstOrDefault(m => m.ProviderId == target.Provider.Id) ?? new TokenPoolMember { ProviderId = target.Provider.Id };
}
