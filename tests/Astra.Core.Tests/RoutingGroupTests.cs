using Astra.Core.Routing;

namespace Astra.Core.Tests;

/// <summary>Routing groups: shape rules, nesting with cycle and depth checks, and cache-aware stays.</summary>
public sealed class RoutingGroupTests
{
    private static GroupMember Model(string provider, string model, int weight = 1) => new() { Provider = provider, Model = model, Weight = weight };
    private static GroupMember Nested(string slug) => new() { Group = slug };

    private static RoutingGroup Make(string slug, params GroupMember[] members)
    {
        var g = new RoutingGroup { Id = slug, Slug = slug, Name = slug.ToUpperInvariant() };
        g.SetMembers(members);
        return g;
    }

    private static readonly IReadOnlySet<string> Providers = new HashSet<string> { "p1", "p2" };

    private static string[] Codes(RoutingGroup group, params RoutingGroup[] others) =>
        RoutingGroupValidator.Check(group, others, Providers).Select(p => p.Code).ToArray();

    [Fact]
    public void A_valid_group_has_no_problems()
    {
        Assert.Empty(Codes(Make("opus", Model("p1", "claude-opus-5-5"), Model("p2", "claude/opus"))));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("a1-b2")]
    public void Slugs_follow_the_documented_shape(string slug) => Assert.True(RoutingGroup.ValidSlug(slug));

    [Theory]
    [InlineData("")]
    [InlineData("Opus")]
    [InlineData("-a")]
    [InlineData("a_b")]
    [InlineData("a/b")]
    public void Bad_slugs_are_refused(string slug) => Assert.Contains(RoutingGroupValidator.Codes.SlugInvalid, Codes(Make(slug, Model("p1", "m"))));

    [Fact]
    public void A_slug_is_made_from_a_name()
    {
        Assert.Equal("opus-anywhere", RoutingGroup.SlugFrom("Opus anywhere"));
        Assert.Equal("a-b", RoutingGroup.SlugFrom("  A -- B! "));
        Assert.Equal("", RoutingGroup.SlugFrom("！！"));
        Assert.Equal("opus", RoutingGroup.SlugOf("group/opus"));
        Assert.Null(RoutingGroup.SlugOf("openai/gpt-5"));
        Assert.Null(RoutingGroup.SlugOf("group/Bad Slug"));
    }

    [Fact]
    public void Duplicate_slug_empty_members_mode_and_stays_are_refused()
    {
        var other = Make("opus", Model("p1", "m"));
        Assert.Contains(RoutingGroupValidator.Codes.SlugDuplicate, Codes(Make("opus", Model("p1", "m")), other));
        Assert.Contains(RoutingGroupValidator.Codes.MembersEmpty, Codes(Make("x")));
        var odd = Make("y", Model("p1", "m"));
        odd.Mode = "nonsense";
        odd.Stays = "forever";
        Assert.Contains(RoutingGroupValidator.Codes.ModeInvalid, Codes(odd));
        Assert.Contains(RoutingGroupValidator.Codes.StaysInvalid, Codes(odd));
    }

    [Fact]
    public void A_member_is_a_provider_and_model_or_a_group_and_a_provider_must_exist()
    {
        Assert.Contains(RoutingGroupValidator.Codes.MemberInvalid, Codes(Make("g", new GroupMember { Provider = "p1" })));
        Assert.Contains(RoutingGroupValidator.Codes.MemberInvalid, Codes(Make("g", new GroupMember { Provider = "p1", Model = "m", Group = "other" })));
        Assert.Contains(RoutingGroupValidator.Codes.MemberProviderMissing, Codes(Make("g", Model("gone", "m"))));
        Assert.Contains(RoutingGroupValidator.Codes.MemberDuplicate, Codes(Make("g", Model("p1", "m"), Model("p1", "m"))));
        Assert.Contains(RoutingGroupValidator.Codes.WeightRange, Codes(Make("g", Model("p1", "m", 0))));
        Assert.Contains(RoutingGroupValidator.Codes.WeightRange, Codes(Make("g", Model("p1", "m", 1001))));
    }

    [Fact]
    public void Groups_nest_and_a_missing_or_self_member_is_refused()
    {
        var inner = Make("inner", Model("p1", "m"));
        Assert.Empty(Codes(Make("outer", Nested("inner"), Model("p2", "n")), inner));
        Assert.Contains(RoutingGroupValidator.Codes.MemberGroupMissing, Codes(Make("outer", Nested("nope"))));
        Assert.Contains(RoutingGroupValidator.Codes.MemberSelf, Codes(Make("outer", Nested("outer"))));
    }

    [Fact]
    public void A_cycle_through_stored_groups_is_refused()
    {
        var a = Make("a", Model("p1", "m"));
        var b = Make("b", Nested("a"));
        // Saving a as containing b would close the loop a -> b -> a.
        var edited = Make("a", Nested("b"));
        Assert.Contains(RoutingGroupValidator.Codes.MemberCycle, Codes(edited, b));
        Assert.Empty(Codes(a, b));
    }

    [Fact]
    public void Nesting_deeper_than_three_is_refused_also_for_the_groups_it_deepens()
    {
        var g3 = Make("g3", Model("p1", "m"));
        var g2 = Make("g2", Nested("g3"));
        var g1 = Make("g1", Nested("g2"));
        // g1 -> g2 -> g3 is three deep: fine.
        Assert.Empty(Codes(g1, g2, g3));
        // A fourth level on top is not.
        Assert.Contains(RoutingGroupValidator.Codes.MemberDepth, Codes(Make("g0", Nested("g1")), g1, g2, g3));
        // Editing the bottom group to hold a deeper chain deepens the groups above it.
        var g4 = Make("g4", Model("p1", "m"));
        var deeper = Make("g3", Nested("g4"));
        Assert.Contains(RoutingGroupValidator.Codes.MemberDepth, Codes(deeper, g1, g2, g4));
    }

    [Fact]
    public void Members_round_trip_through_their_json_column()
    {
        var g = Make("g", Model("p1", "vendor/model", 3), Nested("inner"));
        var back = g.Members();
        Assert.Equal(2, back.Count);
        Assert.Equal("p1/vendor/model", back[0].Key);
        Assert.Equal(3, back[0].Weight);
        Assert.True(back[1].IsGroup);
        Assert.Equal("group/inner", back[1].Key);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Auto_stays_while_the_cache_is_warm_and_big_enough_and_moves_otherwise()
    {
        // First answer reported nothing yet: the second request stays to read what the first wrote.
        Assert.True(ConversationStays.Holds(Stays.Auto, new StayEntry(Now.AddMinutes(-1), null, "t1"), Now, "t2", spent: false));
        Assert.True(ConversationStays.Holds(Stays.Auto, new StayEntry(Now.AddMinutes(-4), 50_000, "t1"), Now, "t2", spent: false));
        // The cache is cold (past five minutes) or too small to be worth an account.
        Assert.False(ConversationStays.Holds(Stays.Auto, new StayEntry(Now.AddMinutes(-6), 50_000, "t1"), Now, "t2", spent: false));
        Assert.False(ConversationStays.Holds(Stays.Auto, new StayEntry(Now.AddMinutes(-1), 200, "t1"), Now, "t2", spent: false));
        // Within a turn the cache is always worth keeping.
        Assert.True(ConversationStays.Holds(Stays.Auto, new StayEntry(Now.AddMinutes(-20), 200, "t1"), Now, "t1", spent: false));
    }

    [Fact]
    public void Stays_modes_session_turn_off_and_strict()
    {
        var old = new StayEntry(Now.AddMinutes(-30), 10, "t1");
        Assert.True(ConversationStays.Holds(Stays.Session, old, Now, "t2", spent: false));
        Assert.False(ConversationStays.Holds(Stays.Session, old, Now, "t2", spent: true));
        Assert.True(ConversationStays.Holds(Stays.Turn, old, Now, "t1", spent: false));
        Assert.False(ConversationStays.Holds(Stays.Turn, old, Now, "t2", spent: false));
        Assert.False(ConversationStays.Holds(Stays.Off, old, Now, "t1", spent: false));
        // Strict keeps the conversation on its member even when it is spent.
        Assert.True(ConversationStays.Holds(Stays.Strict, old, Now, "t2", spent: true));
        // Nothing holds past the session lifetime but strict's own.
        Assert.False(ConversationStays.Holds(Stays.Session, new StayEntry(Now.AddHours(-2), null, "t1"), Now, "t1", spent: false));
    }
}
