using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Routing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.RoutingGroups;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Tests.RoutingGroups;

/// <summary>The routing group editor's logic (web <c>RoutingGroups.tsx</c> helpers) and how a group's decision reads in the request log.</summary>
public sealed class RoutingGroupDraftTests
{
    private static RoutingGroupDto Group(string mode = "smart") => new(
        "g1", "opus", "group/opus", "Opus anywhere", mode, "auto", true, true,
        [new RoutingGroupMemberDto("p1", "claude-opus", null), new RoutingGroupMemberDto(null, null, "inner", 4)],
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_slug_is_made_from_a_name_the_way_the_server_does()
    {
        Assert.Equal("opus-anywhere", RoutingGroupDraft.SlugFrom("Opus anywhere"));
        Assert.Equal("a-b", RoutingGroupDraft.SlugFrom("  A -- B! "));
        Assert.Equal("", RoutingGroupDraft.SlugFrom("！！"));
        Assert.Equal(48, RoutingGroupDraft.SlugFrom(new string('x', 60)).Length);
        Assert.Equal(RoutingGroup.SlugFrom("Opus anywhere"), RoutingGroupDraft.SlugFrom("Opus anywhere"));
    }

    [Fact]
    public void The_slug_rule_matches_the_servers()
    {
        foreach (var good in new[] { "opus", "a1-b2" }) Assert.True(RoutingGroupDraft.ValidSlug(good) && RoutingGroup.ValidSlug(good));
        foreach (var bad in new[] { "", "Opus", "-a", "a_b", "a/b" }) Assert.False(RoutingGroupDraft.ValidSlug(bad) || RoutingGroup.ValidSlug(bad));
    }

    [Fact]
    public void A_new_group_follows_the_name_until_the_slug_is_typed_and_needs_a_member()
    {
        var draft = new RoutingGroupDraft();
        draft.SetName("Fast pair");
        Assert.Equal("fast-pair", draft.Slug);
        Assert.False(draft.IsValid); // no member yet
        draft.Members.Add(new RoutingGroupMemberDto("p1", "m", null));
        Assert.True(draft.IsValid);
        draft.SlugTouched = true;
        draft.Slug = "mine";
        draft.SetName("Another name");
        Assert.Equal("mine", draft.Slug);
    }

    [Fact]
    public void An_existing_group_never_changes_its_slug_and_the_body_omits_it()
    {
        var existing = new RoutingGroupDraft(Group());
        existing.SetName("Renamed");
        Assert.Equal("opus", existing.Slug);
        Assert.Null(existing.ToBody().Slug);
        var fresh = new RoutingGroupDraft();
        fresh.SetName("New one");
        Assert.Equal("new-one", fresh.ToBody().Slug);
    }

    [Fact]
    public void The_body_clamps_weights_and_keeps_the_members_in_order()
    {
        var draft = new RoutingGroupDraft(Group("weight"));
        draft.Members[0] = draft.Members[0] with { Weight = 5000 };
        draft.Members[1] = draft.Members[1] with { Weight = 0 };
        var body = draft.ToBody();
        Assert.Equal([1000, 1], body.Members!.Select(m => m.Weight));
        Assert.Equal(["p1/claude-opus", "group/inner"], body.Members!.Select(RoutingGroupDraft.KeyOf));
        Assert.Equal(("weight", "auto", true, true), (body.Mode, body.Stays, body.Enabled, body.Listed));
    }

    [Fact]
    public void A_refusal_is_told_in_the_UIs_words_and_an_unknown_code_in_the_servers()
    {
        var details = JsonNode.Parse("""{"problems":[{"code":"member_cycle","message":"cycle"},{"code":"brand_new","message":"a new thing"}]}""");
        var lines = RoutingGroupDraft.Explain(new ApiException(400, "x", details));
        Assert.Equal(Loc.T("routingGroups.problem.member_cycle"), lines[0]);
        Assert.Equal("a new thing", lines[1]);
        Assert.Equal(["boom"], RoutingGroupDraft.Explain(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Every_validator_code_and_rank_reason_has_a_message()
    {
        foreach (var field in typeof(RoutingGroupValidator.Codes).GetFields().Where(f => f.IsLiteral))
            Assert.True(Loc.Has("routingGroups.problem." + field.GetValue(null)), (string)field.GetValue(null)!);
        foreach (var field in typeof(RankReasons).GetFields().Where(f => f.IsLiteral))
            Assert.True(Loc.Has("routingGroups.reason." + field.GetValue(null)), (string)field.GetValue(null)!);
        Assert.Equal("mystery", RoutingGroupDraft.ReasonText("mystery"));
        Assert.Equal("—", RoutingGroupDraft.ReasonText(null));
    }

    [Fact]
    public void A_cooling_member_says_until_when()
    {
        var s = new RoutingGroupMemberStatusDto("p1/m", "p1", "Claude", "m", null, 1, "cooling", new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero), null, null, null, null, null, 0, null);
        Assert.Contains("12", RoutingGroupDraft.HealthText(s, "en", TimeZoneInfo.Utc));
        Assert.Equal(Loc.T("routingGroups.health.healthy"), RoutingGroupDraft.HealthText(s with { Health = "healthy" }, "en"));
    }

    [Fact]
    public void A_group_decision_reads_green_with_its_reason_and_orange_after_a_failover()
    {
        TraceText t = (key, args) => Loc.T(key, args);
        var ok = RequestPresentation.Routing(new RoutingReport("group/opus", "claude-opus", "group", "smart", Reason: "soonest_reset"), t)!;
        Assert.True(ok.Ok);
        Assert.Contains("group/opus", ok.Text);
        Assert.Contains(Loc.T("routingGroups.reason.soonest_reset"), ok.Text);
        var moved = RequestPresentation.Routing(new RoutingReport("group/opus", "claude-opus", "group", "order", Reason: "failover"), t)!;
        Assert.False(moved.Ok);
        Assert.Equal(Loc.T("routingGroups.mode.smart"), RequestPresentation.RouteMode("smart", t));
        Assert.Equal(Loc.T("traces.route.source.group"), RequestPresentation.RouteSource("group", t));
    }

    [Fact]
    public void Every_switching_mode_has_a_label_and_a_sentence()
    {
        foreach (var (value, label) in Astra.Desktop.Core.Accounts.SubscriptionAccountsModel.SwitchModeOptions())
        {
            Assert.NotEmpty(label);
            Assert.NotEmpty(Astra.Desktop.Core.Accounts.SubscriptionAccountsModel.SwitchModeDetail(value));
        }
        Assert.Equal(Loc.T("providers.subscription.policy.smartDetail"), Astra.Desktop.Core.Accounts.SubscriptionAccountsModel.SwitchModeDetail("smart"));
        Assert.Equal(Loc.T("providers.subscription.policy.paceDetail"), Astra.Desktop.Core.Accounts.SubscriptionAccountsModel.SwitchModeDetail("pace"));
        Assert.Equal(Loc.T("providers.subscription.policy.usageDetail"), Astra.Desktop.Core.Accounts.SubscriptionAccountsModel.SwitchModeDetail("usage"));
    }
}
