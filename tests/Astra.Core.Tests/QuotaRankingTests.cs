using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Core.Routing;

namespace Astra.Core.Tests;

/// <summary>Quota-aware ordering (magpie's account picker, MIT) over synthetic quota data, and the snapshot parsing under it.</summary>
public sealed class QuotaRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static QuotaStanding Standing(double weeklyUsed, double hoursToReset, double? sessionUsed = null)
    {
        var windows = new List<QuotaWindow> { new(QuotaWindow.Weekly, weeklyUsed, Now.AddHours(hoursToReset)) };
        if (sessionUsed is { } s) windows.Add(new QuotaWindow(QuotaWindow.Session, s, Now.AddHours(2)));
        return new QuotaStanding(windows, Now);
    }

    private static RankInput Input(string key, QuotaStanding standing, int index, double recent = 0) => new(key, standing, recent, index);

    private static string[] Order(string mode, params RankInput[] items) => QuotaRanking.Rank(mode, items, Now).Select(r => r.Key).ToArray();

    [Fact]
    public void Smart_puts_the_allowance_renewing_soonest_first_so_the_least_is_wasted()
    {
        var order = Order(QuotaModes.Smart,
            Input("later", Standing(20, 100), 0),
            Input("sooner", Standing(60, 10), 1),
            Input("middle", Standing(10, 50), 2));
        Assert.Equal(["sooner", "middle", "later"], order);
    }

    [Fact]
    public void Smart_compares_reset_times_to_the_hour_then_prefers_the_less_used()
    {
        // 13:05 and 13:55 are the same hour: the less used goes first, not the earlier one.
        var a = new QuotaStanding([new QuotaWindow(QuotaWindow.Weekly, 50, new DateTimeOffset(2026, 11, 17, 13, 55, 0, TimeSpan.Zero))], Now);
        var b = new QuotaStanding([new QuotaWindow(QuotaWindow.Weekly, 20, new DateTimeOffset(2026, 11, 17, 13, 5, 0, TimeSpan.Zero))], Now);
        Assert.Equal(["b", "a"], Order(QuotaModes.Smart, Input("a", a, 0), Input("b", b, 1)));
    }

    [Fact]
    public void An_account_past_ninety_percent_goes_behind_and_one_past_ninety_eight_goes_last()
    {
        var order = Order(QuotaModes.Smart,
            Input("spent", Standing(99, 1), 0),
            Input("high", Standing(92, 2), 1),
            Input("ok", Standing(10, 200), 2));
        Assert.Equal(["ok", "high", "spent"], order);
    }

    [Fact]
    public void The_fullest_window_counts_for_the_band_not_just_the_week()
    {
        // The week is fine but the five-hour session is at 95%: behind the account whose windows are both low.
        var order = Order(QuotaModes.Smart, Input("hot", Standing(10, 5, sessionUsed: 95), 0), Input("cool", Standing(40, 100, sessionUsed: 5), 1));
        Assert.Equal(["cool", "hot"], order);
    }

    [Fact]
    public void Unknown_quota_degrades_to_the_given_order_and_never_to_an_error()
    {
        Assert.Equal(["a", "b", "c"], Order(QuotaModes.Smart, Input("a", QuotaStanding.Unknown, 0), Input("b", QuotaStanding.Unknown, 1), Input("c", QuotaStanding.Unknown, 2)));
        Assert.Equal(["x", "y"], Order(QuotaModes.Pace, Input("x", QuotaStanding.Unknown, 0), Input("y", QuotaStanding.Unknown, 1)));
    }

    [Fact]
    public void An_unknown_candidate_goes_behind_the_known_ones_but_ahead_of_the_nearly_spent()
    {
        var order = Order(QuotaModes.Smart,
            Input("unknown", QuotaStanding.Unknown, 0), Input("near", Standing(95, 1), 1), Input("known", Standing(30, 80), 2));
        Assert.Equal(["known", "unknown", "near"], order);
    }

    [Fact]
    public void Usage_puts_the_least_used_first_and_breaks_ties_by_recent_sends()
    {
        var order = Order(QuotaModes.Usage,
            Input("busy", Standing(70, 10), 0), Input("idle", Standing(5, 90), 1), Input("tie-a", Standing(30, 20), 2, recent: 9), Input("tie-b", Standing(30, 50), 3, recent: 1));
        Assert.Equal(["idle", "tie-b", "tie-a", "busy"], order);
    }

    [Fact]
    public void Pace_puts_the_most_left_per_hour_until_the_reset_first()
    {
        // 60% left with 20 h to go is 3/h; 80% left with 160 h is 0.5/h; 10% left with 1 h is 10/h.
        var order = Order(QuotaModes.Pace,
            Input("steady", Standing(40, 20), 0), Input("slow", Standing(20, 160), 1), Input("burning", Standing(90, 1), 2));
        Assert.Equal(["steady", "slow"], order[..2]);
        // 90% used is a soft-limit candidate: behind the healthy ones whatever its pace.
        Assert.Equal("burning", order[2]);
    }

    [Fact]
    public void Pace_counts_candidates_within_a_tenth_as_alike_and_prefers_the_less_recently_used()
    {
        var order = Order(QuotaModes.Pace, Input("a", Standing(40, 20), 0, recent: 5), Input("b", Standing(41, 20), 1, recent: 1));
        Assert.Equal(["b", "a"], order);
    }

    [Fact]
    public void Pace_floors_the_hours_at_one()
    {
        Assert.Equal(50, Standing(50, 0.1).Pace(Now));
        Assert.Equal(3, Standing(40, 20).Pace(Now));
        Assert.Null(QuotaStanding.Unknown.Pace(Now));
    }

    [Fact]
    public void A_single_candidate_and_non_quota_modes_keep_their_order()
    {
        Assert.Equal(["only"], Order(QuotaModes.Smart, Input("only", Standing(99, 1), 0)));
        Assert.Equal(["a", "b"], Order("failover", Input("a", Standing(99, 1), 0), Input("b", Standing(1, 100), 1)));
    }

    [Fact]
    public void Reasons_say_why_the_first_candidate_stands_first()
    {
        var items = new[] { Input("a", Standing(20, 50), 0), Input("b", Standing(30, 10), 1) };
        Assert.Equal(RankReasons.SoonestReset, QuotaRanking.Rank(QuotaModes.Smart, items, Now)[0].Reason);
        Assert.Equal(RankReasons.LeastUsed, QuotaRanking.Rank(QuotaModes.Usage, items, Now)[0].Reason);
        Assert.Equal(RankReasons.Pace, QuotaRanking.Rank(QuotaModes.Pace, items, Now)[0].Reason);
        Assert.Equal(RankReasons.Order, QuotaRanking.Rank("order", items, Now)[0].Reason);
    }

    [Fact]
    public void A_stored_snapshot_is_read_and_a_stale_or_renewed_one_is_not_believed()
    {
        ProviderAccount Account(DateTimeOffset fetched, DateTimeOffset reset) => new()
        {
            Extra = { ["quota"] = new JsonObject
            {
                ["fetchedAtUtc"] = fetched.ToString("O"),
                ["weekly"] = new JsonObject { ["usedPercent"] = 42.5, ["resetsAtUtc"] = reset.ToString("O") },
                ["session"] = new JsonObject { ["usedPercent"] = 10, ["resetsAtUtc"] = Now.AddHours(3).ToString("O") },
            } },
        };

        var fresh = QuotaStanding.Of(Account(Now.AddMinutes(-10), Now.AddDays(2)), Now);
        Assert.True(fresh.Known);
        Assert.Equal(42.5, fresh.Used);
        Assert.Equal(QuotaWindow.Weekly, fresh.Deciding!.Kind);

        // Older than six hours says too little to route by.
        Assert.False(QuotaStanding.Of(Account(Now.AddHours(-7), Now.AddDays(2)), Now).Known);
        // The week renewed since the snapshot: only the session is left to believe.
        var renewed = QuotaStanding.Of(Account(Now.AddMinutes(-10), Now.AddMinutes(-1)), Now);
        Assert.Equal(10, renewed.Used);
        Assert.False(QuotaStanding.Of(new ProviderAccount(), Now).Known);
    }

    [Fact]
    public void A_provider_balance_plan_with_a_used_share_is_a_window()
    {
        var provider = new Provider
        {
            Quota = new JsonObject
            {
                ["fetchedAtUtc"] = Now.AddMinutes(-5).ToString("O"), ["isValid"] = true,
                ["plans"] = new JsonArray(new JsonObject { ["usedPercent"] = 80, ["resetsAtUtc"] = Now.AddDays(1).ToString("O") }, new JsonObject { ["remaining"] = 3 }),
            },
        };
        var standing = QuotaStanding.Of(provider, Now);
        Assert.Equal(80, standing.Used);
        Assert.Single(standing.Windows);
        provider.Quota["isValid"] = false;
        Assert.False(QuotaStanding.Of(provider, Now).Known);
    }
}
