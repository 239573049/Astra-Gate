using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;

namespace Astra.Gateway.Tests;

/// <summary>
/// The quota-aware account switch modes (<see cref="AccountScheduler"/>) and the routing group scheduler
/// (<see cref="GroupScheduler"/>) over synthetic quota data: every mode, missing data, and cache-aware stays.
/// </summary>
public sealed class QuotaSchedulingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ProviderAccount Account(Clock clock, string id, int order, double? weeklyUsed = null, double hoursToReset = 48, double? sessionUsed = null)
    {
        var account = new ProviderAccount { Id = id, ProviderId = "p", DisplayName = id, SortOrder = order, Status = AccountStatus.Active };
        if (weeklyUsed is { } used)
        {
            var quota = new JsonObject
            {
                ["fetchedAtUtc"] = clock.Now.AddMinutes(-3).ToString("O"),
                ["weekly"] = new JsonObject { ["usedPercent"] = used, ["resetsAtUtc"] = clock.Now.AddHours(hoursToReset).ToString("O") },
            };
            if (sessionUsed is { } s) quota["session"] = new JsonObject { ["usedPercent"] = s, ["resetsAtUtc"] = clock.Now.AddHours(3).ToString("O") };
            account.Extra["quota"] = quota;
        }
        return account;
    }

    private static string PickId(AccountScheduler scheduler, IReadOnlyList<ProviderAccount> accounts, string? sticky, string mode, params string[] exclude)
    {
        using var lease = scheduler.Pick("p", accounts, sticky, exclude, mode)!;
        return lease.AccountId;
    }

    [Fact]
    public void Smart_picks_the_account_whose_allowance_renews_soonest()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "far", 0, 20, 120), Account(clock, "soon", 1, 70, 6), Account(clock, "mid", 2, 10, 40)];
        using var lease = scheduler.Pick("p", accounts, null, null, SwitchModes.Smart)!;
        Assert.Equal("soon", lease.AccountId);
        Assert.Equal(RankReasons.SoonestReset, lease.Reason);
    }

    [Fact]
    public void Pace_picks_the_most_left_per_hour_and_usage_the_least_used()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        // steady: 60% left / 20 h = 3 per hour; slow: 80% left / 160 h = 0.5; fresh: 95% left / 100 h = 0.95.
        List<ProviderAccount> accounts = [Account(clock, "steady", 0, 40, 20), Account(clock, "slow", 1, 20, 160), Account(clock, "fresh", 2, 5, 100)];
        Assert.Equal("steady", PickId(scheduler, accounts, null, SwitchModes.Pace));
        Assert.Equal("fresh", PickId(scheduler, accounts, null, SwitchModes.Usage));
    }

    [Fact]
    public void An_account_past_ninety_percent_is_avoided_and_past_ninety_eight_is_last()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "full", 0, 99, 1), Account(clock, "high", 1, 93, 2), Account(clock, "ok", 2, 30, 90)];
        Assert.Equal("ok", PickId(scheduler, accounts, null, SwitchModes.Smart));
        Assert.Equal("high", PickId(scheduler, accounts, null, SwitchModes.Smart, "ok"));
        Assert.Equal("full", PickId(scheduler, accounts, null, SwitchModes.Smart, "ok", "high"));
    }

    [Fact]
    public void Missing_quota_data_degrades_to_the_failover_order_not_to_an_error()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "first", 0), Account(clock, "second", 1)];
        foreach (var mode in new[] { SwitchModes.Smart, SwitchModes.Pace, SwitchModes.Usage })
            Assert.Equal("first", PickId(scheduler, accounts, null, mode));
        // A stale snapshot is as good as none.
        var stale = Account(clock, "stale", 0, 5, 100);
        stale.Extra["quota"]!["fetchedAtUtc"] = clock.Now.AddHours(-10).ToString("O");
        Assert.Equal("stale", PickId(scheduler, [stale, Account(clock, "other", 1)], null, SwitchModes.Smart));
    }

    [Fact]
    public void A_conversation_stays_on_its_account_while_the_cache_is_worth_keeping_then_moves()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "far", 0, 20, 120), Account(clock, "soon", 1, 20, 6)];
        // New conversation goes to the one renewing soonest and is bound to it.
        Assert.Equal("soon", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
        // Another account becomes the better choice, but the conversation's cache is warm: it stays.
        accounts[0] = Account(clock, "far", 0, 10, 1);
        clock.Now += TimeSpan.FromMinutes(2);
        scheduler.NoteCache("p", "conv", 40_000);
        Assert.Equal("soon", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
        // The cache went cold (six minutes idle): the conversation may move to the better account.
        clock.Now += TimeSpan.FromMinutes(6);
        accounts[0] = Account(clock, "far", 0, 10, 1);
        accounts[1] = Account(clock, "soon", 1, 20, 6);
        Assert.Equal("far", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
    }

    [Fact]
    public void A_cached_prefix_too_small_to_matter_does_not_hold_the_conversation()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "a", 0, 50, 100), Account(clock, "b", 1, 50, 10)];
        Assert.Equal("b", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
        scheduler.NoteCache("p", "conv", 300);
        accounts[0] = Account(clock, "a", 0, 10, 2);
        Assert.Equal("a", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
    }

    [Fact]
    public void A_bound_account_that_hit_its_cap_is_left_even_with_a_warm_cache()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "a", 0, 30, 10), Account(clock, "b", 1, 40, 100)];
        Assert.Equal("a", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
        scheduler.NoteCache("p", "conv", 90_000);
        accounts[0] = Account(clock, "a", 0, 99, 10);
        Assert.Equal("b", PickId(scheduler, accounts, "conv", SwitchModes.Smart));
    }

    [Fact]
    public void Balanced_mode_is_untouched_by_quota()
    {
        var clock = new Clock();
        var scheduler = new AccountScheduler(clock);
        List<ProviderAccount> accounts = [Account(clock, "a", 0, 99, 1), Account(clock, "b", 1, 1, 100)];
        Assert.Equal("a", PickId(scheduler, accounts, null, SwitchModes.Balanced));
    }

    [Fact]
    public void The_quota_modes_are_switch_modes_that_use_the_scheduler()
    {
        foreach (var mode in new[] { SwitchModes.Balanced, SwitchModes.Smart, SwitchModes.Pace, SwitchModes.Usage })
        {
            Assert.Contains(mode, SwitchModes.All);
            Assert.True(SwitchModes.UsesScheduler(mode));
        }
        Assert.False(SwitchModes.UsesScheduler(SwitchModes.Failover));
        Assert.False(SwitchModes.UsesScheduler(SwitchModes.Manual));
    }

    // ---- Routing groups ----

    private static GroupSlot Slot(string key, int index, QuotaStanding? standing = null, int weight = 1) => new(key, standing ?? QuotaStanding.Unknown, weight, index);

    private static QuotaStanding Standing(Clock clock, double used, double hours) =>
        new([new QuotaWindow(QuotaWindow.Weekly, used, clock.Now.AddHours(hours))], clock.Now);

    private static RoutingGroup Group(string mode, string stays = Stays.Auto) => new() { Slug = "g", Mode = mode, Stays = stays };

    private static string[] Keys(IReadOnlyList<Ranked> ranked) => ranked.Select(r => r.Key).ToArray();

    [Fact]
    public void Order_mode_keeps_the_given_order()
    {
        var scheduler = new GroupScheduler(new Clock());
        var ranked = scheduler.Order(Group(GroupModes.Order), [Slot("b", 1), Slot("a", 0), Slot("c", 2)], null, null, commit: true);
        Assert.Equal(["a", "b", "c"], Keys(ranked));
        Assert.Equal(RankReasons.Order, ranked[0].Reason);
    }

    [Fact]
    public void Smart_usage_and_pace_modes_rank_members_by_quota_and_fall_back_to_the_order_without_it()
    {
        var clock = new Clock();
        var scheduler = new GroupScheduler(clock);
        GroupSlot[] slots = [Slot("far", 0, Standing(clock, 20, 120)), Slot("soon", 1, Standing(clock, 70, 6)), Slot("idle", 2, Standing(clock, 2, 90))];
        Assert.Equal("soon", Keys(scheduler.Order(Group(GroupModes.Smart), slots, null, null, true))[0]);
        Assert.Equal("idle", Keys(scheduler.Order(Group(GroupModes.Usage), slots, null, null, true))[0]);
        // 30% left / 6 h = 5 per hour beats 80% left / 120 h = 0.67 and 98% left / 90 h = 1.1.
        Assert.Equal("soon", Keys(scheduler.Order(Group(GroupModes.Pace), slots, null, null, true))[0]);
        Assert.Equal(["x", "y"], Keys(scheduler.Order(Group(GroupModes.Smart), [Slot("x", 0), Slot("y", 1)], null, null, true)));
    }

    [Fact]
    public void Rotate_mode_starts_each_new_conversation_one_member_further()
    {
        var scheduler = new GroupScheduler(new Clock());
        var group = Group(GroupModes.Rotate);
        GroupSlot[] slots = [Slot("a", 0), Slot("b", 1), Slot("c", 2)];
        var firsts = Enumerable.Range(0, 6).Select(_ => Keys(scheduler.Order(group, slots, null, null, true))[0]).ToArray();
        Assert.Equal(["a", "b", "c", "a", "b", "c"], firsts);
        // A preview does not turn the wheel.
        Assert.Equal("a", Keys(scheduler.Order(group, slots, null, null, commit: false))[0]);
        Assert.Equal("a", Keys(scheduler.Order(group, slots, null, null, commit: false))[0]);
    }

    [Fact]
    public void Rotate_mode_keeps_a_turn_on_its_member_and_moves_the_next_turn()
    {
        var scheduler = new GroupScheduler(new Clock());
        var group = Group(GroupModes.Rotate);
        GroupSlot[] slots = [Slot("a", 0), Slot("b", 1)];
        var first = Keys(scheduler.Order(group, slots, "conv", "t1", true))[0];
        scheduler.Bind("g", "conv", first, "t1");
        // The tool-result request of the same turn stays...
        Assert.Equal(first, Keys(scheduler.Order(group, slots, "conv", "t1", true))[0]);
        // ...and the next user turn goes to the next member.
        var next = Keys(scheduler.Order(group, slots, "conv", "t2", true))[0];
        Assert.NotEqual(first, next);
    }

    [Fact]
    public void Weight_mode_interleaves_by_weight_smoothly()
    {
        var scheduler = new GroupScheduler(new Clock());
        var group = Group(GroupModes.Weight);
        GroupSlot[] slots = [Slot("heavy", 0, weight: 3), Slot("light", 1, weight: 1)];
        var firsts = Enumerable.Range(0, 8).Select(_ => Keys(scheduler.Order(group, slots, null, null, true))[0]).ToArray();
        Assert.Equal(6, firsts.Count(k => k == "heavy"));
        Assert.Equal(2, firsts.Count(k => k == "light"));
        // Smooth: the light member is never starved for more than the cycle and the heavy one never takes more than three in a row.
        Assert.DoesNotContain("heavy,heavy,heavy,heavy", string.Join(",", firsts));
    }

    [Fact]
    public void Weight_mode_survives_a_member_leaving_and_a_preview_spends_nothing()
    {
        var scheduler = new GroupScheduler(new Clock());
        var group = Group(GroupModes.Weight);
        GroupSlot[] two = [Slot("a", 0, weight: 2), Slot("b", 1, weight: 1)];
        Assert.Equal("a", Keys(scheduler.Order(group, two, null, null, commit: false))[0]);
        Assert.Equal("a", Keys(scheduler.Order(group, two, null, null, commit: false))[0]);
        scheduler.Order(group, two, null, null, true);
        Assert.Equal(["a"], Keys(scheduler.Order(group, [Slot("a", 0, weight: 2)], null, null, true)));
    }

    [Fact]
    public void A_conversation_stays_on_its_member_while_the_cache_is_warm()
    {
        var clock = new Clock();
        var scheduler = new GroupScheduler(clock);
        var group = Group(GroupModes.Order);
        GroupSlot[] slots = [Slot("a", 0), Slot("b", 1)];
        scheduler.Bind("g", "conv", "b", "t1");
        var ranked = scheduler.Order(group, slots, "conv", "t2", true);
        Assert.Equal(["b", "a"], Keys(ranked));
        Assert.Equal(RankReasons.Affinity, ranked[0].Reason);
        // Cold (idle six minutes) and the order decides again.
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(["a", "b"], Keys(scheduler.Order(group, slots, "conv", "t3", true)));
    }

    [Fact]
    public void Stays_off_never_holds_and_strict_holds_a_spent_member()
    {
        var clock = new Clock();
        var scheduler = new GroupScheduler(clock);
        scheduler.Bind("g", "conv", "b", "t1");
        var spent = Standing(clock, 99.5, 3);
        GroupSlot[] slots = [Slot("a", 0), Slot("b", 1, spent)];
        Assert.Equal("a", Keys(scheduler.Order(Group(GroupModes.Order, Stays.Off), slots, "conv", "t1", true))[0]);
        Assert.Equal("a", Keys(scheduler.Order(Group(GroupModes.Order, Stays.Session), slots, "conv", "t1", true))[0]);
        Assert.Equal("b", Keys(scheduler.Order(Group(GroupModes.Order, Stays.Strict), slots, "conv", "t9", true))[0]);
    }

    [Fact]
    public void A_bound_member_that_is_no_longer_a_candidate_is_ignored()
    {
        var scheduler = new GroupScheduler(new Clock());
        scheduler.Bind("g", "conv", "gone", "t1");
        Assert.Equal(["a", "b"], Keys(scheduler.Order(Group(GroupModes.Order), [Slot("a", 0), Slot("b", 1)], "conv", "t1", true)));
    }

    [Fact]
    public void The_status_remembers_the_last_decision_and_recent_sends()
    {
        var clock = new Clock();
        var scheduler = new GroupScheduler(clock);
        scheduler.Order(Group(GroupModes.Order), [Slot("a", 0)], null, null, true);
        scheduler.Bind("g", null, "a", null);
        scheduler.Bind("g", null, "a", null);
        Assert.Equal(2, scheduler.Activity("g", "a").Recent);
        Assert.Equal(RankReasons.OnlyOne, scheduler.LastDecision("g")!.Value.Reason);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(0, scheduler.Activity("g", "a").Recent);
    }
}
