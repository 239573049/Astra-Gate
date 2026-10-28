using Astra.Api.Contracts;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tokens;

/// <summary>The pool editor's logic. <c>ExpectedShares</c> mirrors the server's ProviderPoolScheduler (linked update 18): the values here are the web's.</summary>
public sealed class PoolDraftTests
{
    private static PoolDraft Draft(string strategy, params (string Id, int Weight, int Tier)[] members)
        => new(strategy, "session", true, 3, members.Select(m => new PoolDraftMember(m.Id, null, m.Weight, m.Tier)).ToList());

    private static TokenDto Token(string? providerId = null, string? accountId = null, TokenPoolDto? pool = null) => Dtos.Token(providerId: providerId, accountId: accountId, pool: pool);

    [Fact]
    public void A_single_direct_provider_is_a_pool_of_one_with_the_new_pool_defaults()
    {
        var draft = PoolDrafts.Of(Token("prov", "acc"));
        Assert.Equal((PoolStrategies.Failover, "session", true, 3), (draft.Strategy, draft.Sticky, draft.Failover, draft.MaxAttempts));
        Assert.Equal([new PoolDraftMember("prov", "acc", 1, 1)], draft.Members);
        Assert.Empty(PoolDrafts.Of(Token()).Members);
    }

    [Fact]
    public void A_saved_pool_is_edited_as_it_is()
    {
        var pool = new TokenPoolDto("weighted", "off", false, 4,
        [
            new TokenPoolMemberDto("a", "acc", 3, 1, false, new PoolMemberHealthDto("healthy", null, null)),
            new TokenPoolMemberDto("b", null, 1, 2, false, new PoolMemberHealthDto("cooling", Fakes.Noon, null)),
        ]);
        var draft = PoolDrafts.Of(Token("a", pool: pool));
        Assert.Equal(("weighted", "off", false, 4), (draft.Strategy, draft.Sticky, draft.Failover, draft.MaxAttempts));
        Assert.Equal([new PoolDraftMember("a", "acc", 3, 1), new PoolDraftMember("b", null, 1, 2)], draft.Members);
    }

    [Fact]
    public void The_api_body_drops_unknown_providers_and_clamps_counts()
    {
        var draft = new PoolDraft("tiered", "off", true, 99, [new("a", "acc", 0, 1), new("gone", null, 5, 1), new("b", null, 7, 2)]);
        var json = PoolDrafts.ToJson(draft, ["a", "b"]);
        Assert.Equal("tiered", (string?)json["strategy"]);
        Assert.Equal("off", (string?)json["sticky"]);
        Assert.True((bool)json["failover"]!);
        Assert.Equal(5, (int)json["maxAttempts"]!);
        var members = json["members"]!.AsArray();
        Assert.Equal(2, members.Count);
        Assert.Equal(("a", "acc", 1, 1), ((string?)members[0]!["providerId"], (string?)members[0]!["accountId"], (int)members[0]!["weight"]!, (int)members[0]!["tier"]!));
        Assert.Equal(("b", null, 7, 2), ((string?)members[1]!["providerId"], (string?)members[1]!["accountId"], (int)members[1]!["weight"]!, (int)members[1]!["tier"]!));
        Assert.Equal(1, (int)PoolDrafts.ToJson(draft with { MaxAttempts = -3 }, ["a"])["maxAttempts"]!);
    }

    [Fact]
    public void Failover_sends_everything_to_the_first_member()
        => Assert.Equal([1d, 0d, 0d], PoolDrafts.ExpectedShares(Draft(PoolStrategies.Failover, ("a", 1, 1), ("b", 1, 1), ("c", 1, 1))));

    [Fact]
    public void Weighted_splits_by_weight_with_a_floor_of_one()
    {
        var shares = PoolDrafts.ExpectedShares(Draft(PoolStrategies.Weighted, ("a", 3, 1), ("b", 1, 1), ("c", 0, 1)));
        Assert.Equal([3d / 5, 1d / 5, 1d / 5], shares);
        Assert.Equal(1.0, shares.Sum(), 12);
    }

    [Fact]
    public void Tiered_shares_the_lowest_tier_by_weight_and_leaves_the_rest_on_standby()
    {
        var shares = PoolDrafts.ExpectedShares(Draft(PoolStrategies.Tiered, ("a", 1, 2), ("b", 3, 1), ("c", 1, 1), ("d", 9, 3)));
        Assert.Equal([0d, 0.75, 0.25, 0d], shares);
    }

    [Theory]
    [InlineData(PoolStrategies.RoundRobin)]
    [InlineData(PoolStrategies.LeastBusy)]
    public void The_other_strategies_split_evenly(string strategy)
        => Assert.Equal([0.25, 0.25, 0.25, 0.25], PoolDrafts.ExpectedShares(Draft(strategy, ("a", 1, 1), ("b", 5, 2), ("c", 1, 1), ("d", 1, 1))));

    [Fact]
    public void An_empty_pool_has_no_shares() => Assert.Empty(PoolDrafts.ExpectedShares(Draft(PoolStrategies.Weighted)));

    [Theory]
    [InlineData("healthy", HealthTone.Green)]
    [InlineData("cooling", HealthTone.Orange)]
    [InlineData("open", HealthTone.Orange)]
    [InlineData("half_open", HealthTone.Orange)]
    [InlineData("no_account", HealthTone.Red)]
    [InlineData("disabled", HealthTone.Red)]
    [InlineData("missing", HealthTone.Red)]
    public void Health_tones(string state, HealthTone tone) => Assert.Equal(tone, PoolDrafts.ToneOf(state));

    [Fact]
    public void Health_text_says_until_when_for_cooling_and_open_only()
    {
        var until = Fakes.Noon.AddMinutes(5);
        Assert.Equal("tokens.health.cooling(time=12:05:00)", PoolDrafts.HealthLabel("cooling", until, Fakes.Noon, TimeSpan.Zero, "zh", Fakes.T));
        Assert.Equal("tokens.health.open(time=—)", PoolDrafts.HealthLabel("open", null, Fakes.Noon, TimeSpan.Zero, "zh", Fakes.T));
        Assert.Equal("tokens.health.healthy", PoolDrafts.HealthLabel("healthy", until, Fakes.Noon, TimeSpan.Zero, "zh", Fakes.T));
    }

    [Fact]
    public void A_missing_or_disabled_provider_overrides_the_live_health()
    {
        var live = new PoolMemberHealthDto("healthy", null, null);
        Assert.Equal("missing", PoolDrafts.EffectiveState(null, live));
        Assert.Equal("healthy", PoolDrafts.EffectiveState(Provider(true), live));
        Assert.Equal("disabled", PoolDrafts.EffectiveState(Provider(false), live));
        Assert.Null(PoolDrafts.EffectiveState(Provider(true), null));
    }

    [Fact]
    public void Only_subscription_and_plugin_providers_have_accounts()
    {
        Assert.True(PoolDrafts.IsAccountBased("oauth-subscription"));
        Assert.True(PoolDrafts.IsAccountBased("plugin"));
        Assert.False(PoolDrafts.IsAccountBased("bearer"));
    }

    private static ProviderDto Provider(bool enabled) => Dtos.Provider("p", enabled: enabled);
}
