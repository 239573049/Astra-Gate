using Astra.Core.Tokens;

namespace Astra.Core.Tests;

public class TokenPoolTests
{
    private static TokenPool Pool(params string[] providers) =>
        new() { Members = providers.Select(p => new TokenPoolMember { ProviderId = p }).ToList() };

    [Fact]
    public void Defaults_Are_Failover_With_Sticky_Sessions_And_Three_Attempts()
    {
        var pool = new TokenPool();
        Assert.Equal(TokenPoolStrategies.Failover, pool.Strategy);
        Assert.Equal(TokenPoolSticky.Session, pool.Sticky);
        Assert.True(pool.Failover);
        Assert.Equal(3, pool.MaxAttempts);
    }

    [Fact]
    public void Parse_Round_Trips_Snake_Case_And_Treats_An_Empty_Pool_As_None()
    {
        var pool = Pool("a", "b");
        pool.Strategy = TokenPoolStrategies.Tiered;
        pool.Members[1].Weight = 4;
        pool.Members[1].Tier = 2;
        pool.Members[1].AccountId = "acc";
        var json = Json.Serialize(pool);
        Assert.Contains("\"provider_id\":\"b\"", json);
        Assert.Contains("\"max_attempts\":3", json);

        var back = TokenPool.Parse(json)!;
        Assert.Equal(TokenPoolStrategies.Tiered, back.Strategy);
        Assert.Equal(["a", "b"], back.Members.Select(m => m.ProviderId));
        Assert.Equal((4, 2, "acc"), (back.Members[1].Weight, back.Members[1].Tier, back.Members[1].AccountId));

        Assert.Null(TokenPool.Parse(null));
        Assert.Null(TokenPool.Parse(" "));
        Assert.Null(TokenPool.Parse("""{"strategy":"weighted","members":[]}"""));
    }

    [Fact]
    public void Attempt_Limit_Follows_Failover_And_The_Candidates()
    {
        var pool = Pool("a", "b", "c", "d");
        Assert.Equal(3, pool.AttemptLimit(4));
        Assert.Equal(2, pool.AttemptLimit(2));
        Assert.Equal(1, pool.AttemptLimit(0));
        pool.Failover = false;
        Assert.Equal(1, pool.AttemptLimit(4));
    }

    [Fact]
    public void Validate_Reports_Every_Problem()
    {
        Assert.Empty(Pool("a", "b").Validate());

        var bad = Pool("a", "a", "");
        bad.Strategy = "random";
        bad.Sticky = "forever";
        bad.MaxAttempts = 9;
        bad.Members[0].Weight = 0;
        bad.Members[1].Tier = 4;
        var errors = bad.Validate();
        Assert.Contains("unknown strategy random", errors);
        Assert.Contains("unknown sticky mode forever", errors);
        Assert.Contains(errors, e => e.StartsWith("maxAttempts", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("weight of a", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("tier of a", StringComparison.Ordinal));
        Assert.Contains("providerId is required", errors);
        Assert.Contains("each provider can be in the pool only once", errors);

        var many = Pool(Enumerable.Range(0, TokenPool.MaxMembers + 1).Select(i => $"p{i}").ToArray());
        Assert.Contains(many.Validate(), e => e.Contains($"at most {TokenPool.MaxMembers}", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Record_Has_A_Direct_Route_With_A_Provider_Or_A_Pool()
    {
        Assert.False(new TokenRecord().HasDirectRoute());
        Assert.True(new TokenRecord { ProviderId = "p" }.HasDirectRoute());
        Assert.True(new TokenRecord { PoolJson = Json.Serialize(Pool("a", "b")) }.HasDirectRoute());
    }
}
