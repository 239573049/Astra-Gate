using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Routing;

/// <summary>
/// One place a routing-group request may go: the provider target and the model it is asked for, the groups it was reached
/// through (outermost first, each with the member key it sits under), and why its member stood where it did.
/// </summary>
public sealed record GroupCandidate(
    RouteTarget Target, string Model, IReadOnlyList<(string Slug, string Member)> Path, string Reason, QuotaStanding Standing)
{
    /// <summary>The model id of the outermost group the client asked for.</summary>
    public string Group => Path[0].Slug;

    /// <summary><c>provider/model</c>, the leaf's own key.</summary>
    public string Key => Target.Provider.Id + "/" + Model;
}

/// <summary>
/// The candidates one request to a group tries, in order, and at most how many (<see cref="MaxAttempts"/>). Disposing frees the
/// half-open probe slots the plan took. <see cref="Pool"/> is the same plan in the shape the pipeline's failover loop knows.
/// </summary>
public sealed class GroupPlan(RoutingGroup group, IReadOnlyList<GroupCandidate> order, int maxAttempts, bool fallback, List<ProviderHealth.Probe> probes) : IDisposable
{
    public RoutingGroup Group { get; } = group;
    public IReadOnlyList<GroupCandidate> Order { get; } = order;
    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>Every member is cooling down: the one that recovers first is tried, once.</summary>
    public bool Fallback { get; } = fallback;

    /// <summary>The attempt list in the shape of a provider pool's, for the pipeline's failover loop.</summary>
    public PoolPlan Pool { get; } = new(order.Select(c => c.Target).ToList(), maxAttempts, false, probes);

    public void Dispose() => Pool.Dispose();
}

/// <summary>
/// Routes a request for <c>group/&lt;slug&gt;</c> (magpie's routing groups, MIT): each member a caller can use — a
/// <c>provider/model</c> target or a nested group — becomes a candidate; those that cannot take a request now (cooling, no usable
/// account) are left out; <see cref="GroupScheduler"/> orders the rest by the group's mode with the conversation's own member first
/// while it is worth keeping. The pipeline then tries them in turn on the failover rules token pools use. A member is judged for the
/// caller exactly like a smart-routing grade (<see cref="IGatewayRouter.GradeUseAsync"/>): a limited token skips members it cannot
/// reach, and when none is left the request is refused like any unknown model.
/// </summary>
public sealed class GroupRouter(
    AstraDatabase db, IGatewayRouter router, IUpstreamAuthResolver auth, PoolRouting pools, ProviderHealth health, GroupScheduler scheduler,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The group a model id names, whether on or off; null when the id is no group's.</summary>
    public async Task<RoutingGroup?> FindAsync(string? model, CancellationToken ct = default) =>
        RoutingGroup.SlugOf(model) is { } slug ? await db.RoutingGroups.FindCachedAsync(slug, ct) : null;

    private sealed class Node(string key, int weight, int index)
    {
        public string Key { get; } = key;
        public int Weight { get; } = weight;
        public int Index { get; } = index;
        public GroupCandidate? Leaf { get; set; }
        public RoutingGroup? Child { get; set; }
        public List<Node> Kids { get; } = [];
    }

    private sealed record Context(GatewayRoute Route, bool IsClaudeCode, IReadOnlyList<RoutingGroup> All);

    /// <param name="requestedModel">What the client asked for (a smart-routing grade may name a group: the virtual model stays the request's model).</param>
    /// <param name="commit">The request is really sent: rotation advances, probes are claimed. Without it the plan is a read-only preview (count_tokens).</param>
    /// <exception cref="GatewayException">The group is off, or no member is left for this caller.</exception>
    public async Task<GroupPlan> PlanAsync(
        GatewayRoute route, RoutingGroup group, string? stickyKey, string? turnId, bool isClaudeCode, bool commit, CancellationToken ct)
    {
        if (!group.Enabled)
            throw new GatewayException(404, "not_found_error", $"模型 {group.ModelId} 已停用。");
        var all = await db.RoutingGroups.ListCachedAsync(ct);
        var ctx = new Context(route, isClaudeCode, all);
        var tree = await BuildAsync(group, ctx, 1, [group.Slug], ct);
        var leaves = Leaves(tree).ToList();
        if (leaves.Count == 0)
            throw new GatewayException(404, "not_found_error", $"模型 {group.ModelId} 当前没有可供此调用方使用的成员（成员已停用、不提供该模型，或令牌不允许）。");

        var probes = new List<ProviderHealth.Probe>();
        var distinct = leaves.Select(l => l.Leaf!.Target).GroupBy(t => t.Provider.Id).Select(g => g.First()).ToList();
        var admitted = (await pools.AdmitAsync(distinct, commit, probes, ct)).Select(t => t.Provider.Id).ToHashSet(StringComparer.Ordinal);
        if (admitted.Count == 0)
        {
            // Everything is cooling down: one try at the member that recovers first beats refusing outright.
            var soonest = leaves.OrderBy(l => health.AvailableAt(l.Leaf!.Target.Provider.Id) ?? DateTimeOffset.MinValue).First().Leaf!;
            return new GroupPlan(group, [soonest with { Path = [(group.Slug, soonest.Path[0].Member)], Reason = RankReasons.OnlyOne }], 1, true, probes);
        }
        Prune(tree, admitted);

        var order = Flatten(group, tree, stickyKey, turnId, commit);
        var deduped = new List<GroupCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in order)
            if (seen.Add(c.Key)) deduped.Add(c);
        return new GroupPlan(group, deduped, Math.Min(deduped.Count, TokenPool.MaxAttemptsLimit), false, probes);
    }

    /// <summary>
    /// Counts a request that a candidate answered successfully: the conversation is bound to it (and to the nested groups it went
    /// through), so its next request stays while the cache is worth keeping.
    /// </summary>
    public void Bind(GroupCandidate served, string? stickyKey, string? turnId)
    {
        foreach (var (slug, member) in served.Path) scheduler.Bind(slug, stickyKey, member, turnId);
    }

    /// <summary>What a finished request read from and wrote to the provider's prompt cache (feeds <see cref="ConversationStays"/>).</summary>
    public void NoteCache(GroupCandidate served, string stickyKey, long cachedTokens)
    {
        foreach (var (slug, _) in served.Path) scheduler.NoteCache(slug, stickyKey, cachedTokens);
    }

    // ---- Building the tree of members the caller can reach ----

    private async Task<List<Node>> BuildAsync(RoutingGroup group, Context ctx, int depth, HashSet<string> path, CancellationToken ct)
    {
        var nodes = new List<Node>();
        var members = group.Members();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var node = new Node(m.Key, Math.Clamp(m.Weight, 1, 1000), i);
            if (m.IsGroup)
            {
                // A cycle or a chain past the limit cannot be saved, but a row edited by hand must not loop here.
                if (depth >= RoutingGroup.MaxDepth || path.Contains(m.Group!)) continue;
                if (ctx.All.FirstOrDefault(g => g.Slug == m.Group) is not { Enabled: true } child) continue;
                node.Child = child;
                node.Kids.AddRange(await BuildAsync(child, ctx, depth + 1, [.. path, child.Slug], ct));
                if (node.Kids.Count == 0) continue;
            }
            else
            {
                var (target, _) = await router.GradeUseAsync(ctx.Route, m.Provider, m.Model!, ctx.IsClaudeCode, ct);
                if (target is null) continue;
                var standing = await StandingOfAsync(target, ct);
                node.Leaf = new GroupCandidate(target, m.Model!, [(group.Slug, m.Key)], RankReasons.Order, standing);
            }
            nodes.Add(node);
        }
        return nodes;
    }

    private static IEnumerable<Node> Leaves(IEnumerable<Node> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Leaf is not null) yield return n;
            foreach (var k in Leaves(n.Kids)) yield return k;
        }
    }

    private static void Prune(List<Node> nodes, HashSet<string> admitted)
    {
        nodes.RemoveAll(n => n.Leaf is not null && !admitted.Contains(n.Leaf.Target.Provider.Id));
        foreach (var n in nodes) Prune(n.Kids, admitted);
        nodes.RemoveAll(n => n.Child is not null && n.Kids.Count == 0);
    }

    // What a target has left, as the best of its usable accounts (the one a quota-aware pick would take) or its balance plan.
    private async Task<QuotaStanding> StandingOfAsync(RouteTarget target, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!target.Provider.UsesAccounts) return QuotaStanding.Of(target.Provider, now);
        var accounts = (await auth.ListAccountsAsync(target.Provider.Id, ct))
            .Where(a => (target.AccountId is null || a.Id == target.AccountId) && SubscriptionSupport.IsUsable(a, now)).ToList();
        if (accounts.Count == 0) return QuotaStanding.Unknown;
        var standings = accounts.ToDictionary(a => a.Id, a => QuotaStanding.Of(a, now));
        var best = QuotaRanking.Rank(QuotaModes.Smart, accounts.Select((a, i) => new RankInput(a.Id, standings[a.Id], 0, i)).ToList(), now)[0];
        return standings[best.Key];
    }

    // ---- Ordering ----

    private List<GroupCandidate> Flatten(RoutingGroup group, List<Node> nodes, string? stickyKey, string? turnId, bool commit)
    {
        var slots = nodes.Select(n => new GroupSlot(n.Key, StandingOf(n), n.Weight, n.Index)).ToList();
        var ranked = scheduler.Order(group, slots, stickyKey, turnId, commit);
        var result = new List<GroupCandidate>();
        var first = true;
        foreach (var r in ranked)
        {
            var node = nodes.First(n => n.Key == r.Key);
            if (node.Leaf is not null)
                result.Add(node.Leaf with { Reason = r.Reason, Path = [(group.Slug, node.Key)] });
            else
            {
                // Only the member that goes first spends the nested group's rotation / weights.
                foreach (var c in Flatten(node.Child!, node.Kids, stickyKey, turnId, commit && first))
                    result.Add(c with { Reason = r.Reason, Path = [(group.Slug, node.Key), .. c.Path] });
            }
            first = false;
        }
        return result;
    }

    // A nested group stands for its best member (by smart ranking, read-only).
    private QuotaStanding StandingOf(Node node)
    {
        if (node.Leaf is not null) return node.Leaf.Standing;
        var now = _clock.GetUtcNow();
        var inputs = Leaves(node.Kids).Select((l, i) => new RankInput(l.Key, l.Leaf!.Standing, 0, i)).ToList();
        if (inputs.Count == 0) return QuotaStanding.Unknown;
        var best = QuotaRanking.Rank(QuotaModes.Smart, inputs, now)[0];
        return inputs.First(i => i.Key == best.Key).Standing;
    }

    // ---- Lists and the read-only view ----

    /// <summary>
    /// The groups a caller is offered in model lists: on, listed, and with at least one member whose provider the caller reaches
    /// (<paramref name="targets"/> are its own providers; a token that allows any client reaches any usable provider, like a smart-routing
    /// grade). A group's name is all a client sees — never its members.
    /// </summary>
    public async Task<List<RoutingGroup>> AdvertisedAsync(IReadOnlyList<RouteTarget> targets, bool anyProvider, CancellationToken ct = default)
    {
        var all = await db.RoutingGroups.ListCachedAsync(ct);
        if (all.Count == 0) return [];
        var providers = anyProvider
            ? (await db.Providers.ListAsync(ct)).Where(ClientRouting.IsUsable).Select(p => p.Id).ToHashSet(StringComparer.Ordinal)
            : targets.Select(t => t.Provider.Id).ToHashSet(StringComparer.Ordinal);
        bool Reaches(RoutingGroup g, HashSet<string> path, int depth)
        {
            foreach (var m in g.Members())
            {
                if (!m.IsGroup) { if (providers.Contains(m.Provider!)) return true; continue; }
                if (depth >= RoutingGroup.MaxDepth || path.Contains(m.Group!)) continue;
                if (all.FirstOrDefault(x => x.Slug == m.Group) is { Enabled: true } child && Reaches(child, [.. path, child.Slug], depth + 1)) return true;
            }
            return false;
        }
        return all.Where(g => g is { Enabled: true, Listed: true } && Reaches(g, [g.Slug], 1)).ToList();
    }

    /// <summary>Whether a caller can use a group at all right now (a smart-routing grade naming it needs this), without claiming anything.</summary>
    public async Task<bool> UsableAsync(GatewayRoute route, RoutingGroup group, bool isClaudeCode, CancellationToken ct)
    {
        if (!group.Enabled) return false;
        var ctx = new Context(route, isClaudeCode, await db.RoutingGroups.ListCachedAsync(ct));
        return (await BuildAsync(group, ctx, 1, [group.Slug], ct)).Count > 0;
    }

    /// <summary>
    /// The route for a read-only look at a group's first choice (count_tokens): the first candidate of a preview plan, as a route
    /// whose routed model is that member's. Null when the model is no group.
    /// </summary>
    public async Task<GatewayRoute?> PreviewAsync(GatewayRoute route, string requestedModel, bool isClaudeCode, CancellationToken ct)
    {
        if (await FindAsync(route.ClientModelFor(requestedModel), ct) is not { } group) return null;
        using var plan = await PlanAsync(route, group, null, null, isClaudeCode, commit: false, ct);
        return RouteFor(route, requestedModel, plan.Order[0]);
    }

    /// <summary>The route that sends the request to one candidate: its provider alone, its model routed in the candidate's place.</summary>
    public static GatewayRoute RouteFor(GatewayRoute route, string requestedModel, GroupCandidate candidate) =>
        GatewayRouter.WithTarget(
            route with { Routed = new RoutedModel(requestedModel, candidate.Model, route.Routed?.GradeId ?? "group:" + candidate.Group, candidate.Target.Provider.Id) },
            candidate.Target);

    // ---- The console's live view ----

    /// <summary>One member's live state for the console (<see cref="StatusAsync"/>).</summary>
    public sealed record MemberStatus(
        GroupMember Member, Provider? Provider, string Health, DateTimeOffset? CoolingUntil, QuotaStanding Standing, MemberActivity Activity, string? Problem);

    /// <summary>Every member's health, quota standing and recent use, and the group's last decision. Reads only; nothing is claimed.</summary>
    public async Task<(List<MemberStatus> Members, (DateTimeOffset At, string Reason)? Last)> StatusAsync(RoutingGroup group, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var all = await db.RoutingGroups.ListCachedAsync(ct);
        var result = new List<MemberStatus>();
        foreach (var m in group.Members())
        {
            var activity = scheduler.Activity(group.Slug, m.Key);
            if (m.IsGroup)
            {
                var exists = all.Any(g => g.Slug == m.Group);
                result.Add(new MemberStatus(m, null, "group", null, QuotaStanding.Unknown, activity, exists ? null : "group_missing"));
                continue;
            }
            var provider = await db.Providers.GetAsync(m.Provider!, ct);
            if (provider is null)
            {
                result.Add(new MemberStatus(m, null, "missing", null, QuotaStanding.Unknown, activity, "provider_missing"));
                continue;
            }
            if (!ClientRouting.IsUsable(provider))
            {
                result.Add(new MemberStatus(m, provider, "off", null, QuotaStanding.Unknown, activity, "provider_off"));
                continue;
            }
            var served = await db.Providers.GetModelAsync(provider.Id, provider.MapModel(m.Model!), ct) is { Enabled: true };
            string state;
            DateTimeOffset? until = null;
            if (provider.UsesAccounts)
            {
                var usable = (await auth.ListAccountsAsync(provider.Id, ct)).Any(a => SubscriptionSupport.IsUsable(a, now));
                state = usable ? ProviderHealthStates.Healthy : "no_account";
            }
            else
            {
                var h = health.Status(provider.Id);
                state = h.State;
                until = h.Until;
            }
            result.Add(new MemberStatus(m, provider, state, until, await StandingOfAsync(new RouteTarget(provider, null), ct), activity, served ? null : "model_missing"));
        }
        return (result, scheduler.LastDecision(group.Slug));
    }
}
