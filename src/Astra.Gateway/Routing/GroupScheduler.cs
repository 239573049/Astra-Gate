using Astra.Core.Routing;

namespace Astra.Gateway.Routing;

/// <summary>One member of a group as the scheduler sees it: a key, what is known of its quota, its weight and its place in the group's order.</summary>
public sealed record GroupSlot(string Key, QuotaStanding Standing, int Weight, int Index);

/// <summary>What the scheduler remembers of one group member (the console's live status).</summary>
public sealed record MemberActivity(DateTimeOffset? LastUsed, string? LastReason, int Recent);

/// <summary>
/// Orders a routing group's members for one request and keeps the in-memory state its modes need: the rotation cursor, the smooth
/// weights, how much each member was sent lately, and which member each conversation is on. Nothing is stored: a restart loses
/// the bindings (one upstream cache miss) and nothing more. Ordering after magpie's routing groups (MIT).
/// <list type="bullet">
/// <item><b>smart / pace / usage</b> — <see cref="QuotaRanking"/>; members with no quota figure go behind the known ones, and with none
/// known at all the given order stands (never an error).</item>
/// <item><b>order</b> — the given order.</item>
/// <item><b>rotate</b> — each new turn starts one member further; a turn keeps its member.</item>
/// <item><b>weight</b> — smooth weighted round robin (nginx's algorithm): a weight-3 member takes three requests for every one a
/// weight-1 member takes, interleaved.</item>
/// </list>
/// Then conversation affinity (<see cref="ConversationStays"/>) moves the member that served the conversation to the front while
/// it is worth keeping.
/// </summary>
public sealed class GroupScheduler(TimeProvider? timeProvider = null)
{
    /// <summary>How long a send counts as "lately" for the usage tie-breaks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>Upper bound of the conversation table; the least recently used bindings go first.</summary>
    public const int MaxBindings = 10_000;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _rotation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, string Reason)> _last = new(StringComparer.Ordinal);

    private sealed class Binding(string member, DateTimeOffset lastUsed, string? turn)
    {
        public string Member { get; set; } = member;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
        public string? TurnId { get; set; } = turn;
        public long? CachedTokens { get; set; }
    }

    /// <summary>
    /// The members in the order the request tries them, each with why it stands there. <paramref name="commit"/> = the request is
    /// really sent: the rotation advances and the weights are spent; without it the order is a read-only preview.
    /// </summary>
    public IReadOnlyList<Ranked> Order(RoutingGroup group, IReadOnlyList<GroupSlot> slots, string? stickyKey, string? turnId, bool commit)
    {
        var now = _clock.GetUtcNow();
        if (slots.Count == 0) return [];
        lock (_gate)
        {
            var bound = FindBound(group, slots, stickyKey, turnId, now);
            var ordered = group.Mode switch
            {
                GroupModes.Order => slots.OrderBy(s => s.Index).Select(s => new Ranked(s.Key, slots.Count == 1 ? RankReasons.OnlyOne : RankReasons.Order, null)).ToList(),
                GroupModes.Rotate => Rotate(group.Slug, slots, bound is not null, commit),
                GroupModes.Weight => Weigh(group.Slug, slots, commit),
                _ => QuotaRanking.Rank(group.Mode, slots.Select(s => new RankInput(s.Key, s.Standing, RecentCount(group.Slug, s.Key, now), s.Index)).ToList(), now).ToList(),
            };
            if (bound is not null)
            {
                var at = ordered.FindIndex(r => r.Key == bound);
                var first = ordered[at] with { Reason = RankReasons.Affinity };
                ordered.RemoveAt(at);
                ordered.Insert(0, first);
            }
            if (commit) _last[group.Slug] = (now, ordered[0].Reason);
            return ordered;
        }
    }

    /// <summary>
    /// Records that <paramref name="member"/> served the conversation (and counts the send against it). A conversation with no sticky
    /// key is only counted.
    /// </summary>
    public void Bind(string slug, string? stickyKey, string member, string? turnId)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_recent.TryGetValue(slug + "\n" + member, out var queue)) _recent[slug + "\n" + member] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(now);
            RecentCount(slug, member, now);
            if (stickyKey is null) return;
            Sweep(now);
            var key = slug + "\n" + stickyKey;
            if (_bindings.TryGetValue(key, out var binding) && binding.Member == member)
            {
                binding.LastUsed = now;
                binding.TurnId = turnId;
            }
            else _bindings[key] = new Binding(member, now, turnId);
        }
    }

    /// <summary>Records what a finished request read from and wrote to the provider's prompt cache (see <see cref="ConversationStays"/>).</summary>
    public void NoteCache(string slug, string stickyKey, long cachedTokens)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(slug + "\n" + stickyKey, out var binding)) return;
            binding.CachedTokens = cachedTokens;
            binding.LastUsed = _clock.GetUtcNow();
        }
    }

    /// <summary>The member a conversation is on right now, if any (diagnostics / tests).</summary>
    public string? BoundMember(string slug, string stickyKey)
    {
        lock (_gate) return _bindings.TryGetValue(slug + "\n" + stickyKey, out var b) ? b.Member : null;
    }

    /// <summary>The console's live status of a member: when it last served a request through the group, and how many lately.</summary>
    public MemberActivity Activity(string slug, string memberKey)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var recent = RecentCount(slug, memberKey, now);
            DateTimeOffset? last = _recent.TryGetValue(slug + "\n" + memberKey, out var queue) && queue.Count > 0 ? queue.Last() : null;
            return new MemberActivity(last, _last.TryGetValue(slug, out var decision) ? decision.Reason : null, (int)recent);
        }
    }

    /// <summary>The group's last decision (when and why its first member stood first), if it has made one.</summary>
    public (DateTimeOffset At, string Reason)? LastDecision(string slug)
    {
        lock (_gate) return _last.TryGetValue(slug, out var d) ? d : null;
    }

    // The member the conversation is on, when it is still among the candidates and staying is worth it.
    private string? FindBound(RoutingGroup group, IReadOnlyList<GroupSlot> slots, string? stickyKey, string? turnId, DateTimeOffset now)
    {
        if (stickyKey is null || !_bindings.TryGetValue(group.Slug + "\n" + stickyKey, out var binding)) return null;
        var slot = slots.FirstOrDefault(s => s.Key == binding.Member);
        if (slot is null) return null;
        // Rotating members means a new turn goes to the next one: only strict or session pinning overrides that.
        var stays = group.Mode == GroupModes.Rotate && group.Stays == Stays.Auto ? Stays.Turn : group.Stays;
        var entry = new StayEntry(binding.LastUsed, binding.CachedTokens, binding.TurnId);
        var spent = slot.Standing is { Known: true, Band: 2 };
        return ConversationStays.Holds(stays, entry, now, turnId, spent) ? slot.Key : null;
    }

    private List<Ranked> Rotate(string slug, IReadOnlyList<GroupSlot> slots, bool held, bool commit)
    {
        var sorted = slots.OrderBy(s => s.Index).ToList();
        _rotation.TryGetValue(slug, out var cursor);
        var start = cursor % sorted.Count;
        // A conversation that stays where it is does not advance the rotation: the next new turn does.
        if (commit && !held) _rotation[slug] = (cursor + 1) % Math.Max(sorted.Count, 1);
        var result = new List<Ranked>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
            result.Add(new Ranked(sorted[(start + i) % sorted.Count].Key, sorted.Count == 1 ? RankReasons.OnlyOne : RankReasons.Rotate, null));
        return result;
    }

    // Smooth weighted round robin: every member gains its weight, the richest is picked and pays the total. The members behind the
    // pick follow by their current weight, so a failover goes to the member that was next in line.
    private List<Ranked> Weigh(string slug, IReadOnlyList<GroupSlot> slots, bool commit)
    {
        if (!_weights.TryGetValue(slug, out var state)) _weights[slug] = state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stale in state.Keys.Where(k => slots.All(s => s.Key != k)).ToList()) state.Remove(stale);
        var total = slots.Sum(s => s.Weight);
        var current = slots.ToDictionary(s => s.Key, s => state.GetValueOrDefault(s.Key) + s.Weight, StringComparer.Ordinal);
        var ordered = slots.OrderByDescending(s => current[s.Key]).ThenBy(s => s.Index).ToList();
        if (commit)
        {
            foreach (var s in slots) state[s.Key] = current[s.Key];
            state[ordered[0].Key] -= total;
        }
        return ordered.Select(s => new Ranked(s.Key, slots.Count == 1 ? RankReasons.OnlyOne : RankReasons.Weight, null)).ToList();
    }

    private double RecentCount(string slug, string member, DateTimeOffset now)
    {
        if (!_recent.TryGetValue(slug + "\n" + member, out var queue)) return 0;
        while (queue.Count > 0 && now - queue.Peek() > Window) queue.Dequeue();
        return queue.Count;
    }

    private void Sweep(DateTimeOffset now)
    {
        if (_bindings.Count < MaxBindings) return;
        foreach (var key in _bindings.Where(kv => now - kv.Value.LastUsed > ConversationStays.SessionTtl).Select(kv => kv.Key).ToList())
            _bindings.Remove(key);
        if (_bindings.Count < MaxBindings) return;
        foreach (var key in _bindings.OrderBy(kv => kv.Value.LastUsed).Take(_bindings.Count - MaxBindings + 1).Select(kv => kv.Key).ToList())
            _bindings.Remove(key);
    }
}
