using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class ProviderPoolSchedulerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TokenPool Pool(string strategy, params (string Id, int Weight, int Tier)[] members) => new()
    {
        Strategy = strategy,
        Sticky = TokenPoolSticky.Off,
        Members = members.Select(m => new TokenPoolMember { ProviderId = m.Id, Weight = m.Weight, Tier = m.Tier }).ToList(),
    };

    private static string Pick(ProviderPoolScheduler s, TokenPool pool, string token = "t", string? sticky = null, bool commit = true) =>
        s.Order(token, pool, pool.Members, sticky, commit)[0].ProviderId;

    private static string[] Ids(IReadOnlyList<TokenPoolMember> order) => order.Select(m => m.ProviderId).ToArray();

    [Fact]
    public void Failover_Always_Starts_With_The_First_Member_And_Keeps_List_Order()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.Failover, ("a", 1, 1), ("b", 5, 1), ("c", 1, 1));
        for (var i = 0; i < 3; i++) Assert.Equal(["a", "b", "c"], Ids(s.Order("t", pool, pool.Members, null, true)));
        // Without the first member the next one leads.
        Assert.Equal(["b", "c"], Ids(s.Order("t", pool, pool.Members.Skip(1).ToList(), null, true)));
    }

    [Fact]
    public void Round_Robin_Rotates_Per_Token_And_Fails_Over_To_The_Next_In_Turn()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.RoundRobin, ("a", 1, 1), ("b", 1, 1), ("c", 1, 1));
        Assert.Equal(["a", "b", "c"], Ids(s.Order("t", pool, pool.Members, null, true)));
        Assert.Equal(["b", "c", "a"], Ids(s.Order("t", pool, pool.Members, null, true)));
        // Another token has its own cursor.
        Assert.Equal("a", Pick(s, pool, "other"));
        Assert.Equal("c", Pick(s, pool));
        Assert.Equal("a", Pick(s, pool));
    }

    [Fact]
    public void Weighted_Is_Smooth_And_Proportional()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.Weighted, ("a", 5, 1), ("b", 1, 1), ("c", 1, 1));
        var picks = Enumerable.Range(0, 7).Select(_ => Pick(s, pool)).ToArray();
        // nginx's smooth weighted round robin for 5:1:1.
        Assert.Equal(["a", "a", "b", "a", "c", "a", "a"], picks);
        // The failover order behind the pick is by weight.
        var heavy = Pool(TokenPoolStrategies.Weighted, ("a", 2, 1), ("b", 3, 1), ("c", 1, 1));
        Assert.Equal(["b", "a", "c"], Ids(s.Order("x", heavy, heavy.Members, null, false)));
    }

    [Fact]
    public void A_Preview_Advances_Nothing()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.RoundRobin, ("a", 1, 1), ("b", 1, 1));
        Assert.Equal("a", Pick(s, pool, commit: false));
        Assert.Equal("a", Pick(s, pool, commit: false));
        Assert.Equal("a", Pick(s, pool));
        Assert.Equal("b", Pick(s, pool));
    }

    [Fact]
    public void Tiered_Uses_The_First_Tier_And_Falls_Back_To_The_Next()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.Tiered, ("a", 1, 1), ("b", 2, 1), ("backup", 1, 2));
        var picks = Enumerable.Range(0, 30).Select(_ => Pick(s, pool)).ToList();
        Assert.DoesNotContain("backup", picks);
        Assert.Equal(20, picks.Count(p => p == "b"));
        // The second tier comes after the whole first tier.
        Assert.Equal("backup", Ids(s.Order("t", pool, pool.Members, null, true))[2]);
        // With the first tier unavailable, the second one leads.
        Assert.Equal(["backup"], Ids(s.Order("t", pool, [pool.Members[2]], null, true)));
    }

    [Fact]
    public void Least_Busy_Prefers_Fewest_In_Flight_Then_Fewest_Recent()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.LeastBusy, ("a", 1, 1), ("b", 1, 1));
        using var held = s.Begin("a");
        Assert.Equal("b", Pick(s, pool));
        held.Dispose();
        Assert.Equal(0, s.InFlight("a"));
        // Both idle: a was assigned once (the lease above), b never — b again.
        Assert.Equal("b", Pick(s, pool));
    }

    [Fact]
    public void Sticky_Sessions_Stay_Rebind_And_Do_Not_Return_After_Recovery()
    {
        var clock = new Clock();
        var s = new ProviderPoolScheduler(clock);
        var pool = Pool(TokenPoolStrategies.RoundRobin, ("a", 1, 1), ("b", 1, 1));
        pool.Sticky = TokenPoolSticky.Session;

        Assert.Equal("a", Pick(s, pool, sticky: "s1"));
        Assert.Equal("b", Pick(s, pool, sticky: "s2"));
        // The same conversations stay put, and do not move the rotation.
        Assert.Equal("a", Pick(s, pool, sticky: "s1"));
        Assert.Equal("b", Pick(s, pool, sticky: "s2"));
        Assert.Equal("a", Pick(s, pool, sticky: "s3"));

        // a is unavailable: s1 goes elsewhere and is re-bound to whoever served it.
        Assert.Equal("b", s.Order("t", pool, [pool.Members[1]], "s1", true)[0].ProviderId);
        s.Bind("t", pool, "s1", "b");
        // a is back, but s1 keeps its new home (its cache is there now).
        Assert.Equal("b", Pick(s, pool, sticky: "s1"));
        Assert.Equal("b", s.BoundProvider("t", "s1"));

        // Bindings idle out.
        clock.Now += ProviderPoolScheduler.BindingTtl + TimeSpan.FromMinutes(1);
        s.Order("t", pool, pool.Members, "s1", false);
        Assert.Null(s.BoundProvider("t", "s1"));
        s.Forget("t");
        Assert.Null(s.BoundProvider("t", "s2"));
    }

    [Fact]
    public void Sticky_Is_Ignored_When_Turned_Off()
    {
        var s = new ProviderPoolScheduler(new Clock());
        var pool = Pool(TokenPoolStrategies.RoundRobin, ("a", 1, 1), ("b", 1, 1));
        Assert.Equal("a", Pick(s, pool, sticky: "s1"));
        Assert.Equal("b", Pick(s, pool, sticky: "s1"));
        s.Bind("t", pool, "s1", "a");
        Assert.Null(s.BoundProvider("t", "s1"));
    }
}
