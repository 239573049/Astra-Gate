using System.Text.Json;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Privacy;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Privacy;
using Astra.Desktop.Tests.Api;

namespace Astra.Desktop.Tests.Privacy;

public class PrivacyMetaTests
{
    [Fact]
    public void Known_categories_have_catalog_names_unknown_ones_show_their_id()
    {
        foreach (var c in PrivacyMeta.Categories) Assert.True(Loc.Has("settings.privacy.category." + c), c);
        Assert.Equal("removed_rule", PrivacyMeta.CategoryLabel("removed_rule"));
        foreach (var a in PrivacyMeta.Actions) Assert.True(Loc.Has("settings.privacy.action." + a), a);
        Assert.Equal("red", PrivacyMeta.ActionTone("block"));
        Assert.Equal("orange", PrivacyMeta.ActionTone("redact"));
        Assert.Equal("neutral", PrivacyMeta.ActionTone("warn"));
    }

    [Fact]
    public void Client_names_follow_the_web_table_and_fall_back_to_the_kind()
    {
        Assert.Equal("Claude Code", ClientNames.Of("claude-code"));
        Assert.Equal("omp (oh-my-pi)", ClientNames.Of("omp"));
        Assert.Equal("plugin-x", ClientNames.Of("plugin-x"));
        Assert.Equal("—", ClientNames.Of(null));
    }

    [Fact]
    public void Rule_templates_have_texts_and_compile_with_the_servers_regex_engine()
    {
        Assert.Equal(19, PrivacyRuleTemplates.All.Count);
        Assert.Equal(PrivacyRuleTemplates.All.Count, PrivacyRuleTemplates.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in PrivacyRuleTemplates.All)
        {
            Assert.True(Loc.Has(t.NameKey), t.NameKey);
            Assert.True(Loc.Has(t.HintKey), t.HintKey);
            Assert.True(PrivacyPolicyLogic.TryValidatePattern(t.Pattern, out var error), $"{t.Id}: {error}");
        }
    }

    [Fact]
    public void Template_patterns_match_what_they_name()
    {
        bool Match(string id, string text) => System.Text.RegularExpressions.Regex.IsMatch(text, PrivacyRuleTemplates.Find(id)!.Pattern);
        Assert.True(Match("ipv4-all", "host 10.1.2.3 up"));
        Assert.False(Match("ipv4-all", "version 1.2.3"));
        Assert.True(Match("mac-address", "aa:bb:cc:dd:ee:ff"));
        Assert.True(Match("unix-home", "/Users/alice/code"));
        Assert.True(Match("windows-profile", @"C:\Users\bob"));
        Assert.True(Match("confidential-marker", "Internal Use Only"));
    }
}

public class PrivacyPolicyLogicTests
{
    private static PrivacySettings Policy() => new()
    {
        Enabled = true, DryRun = false, DefaultAction = "warn",
        CategoryActions = { ["email"] = "redact", ["phone"] = "off" },
        CustomRules = [new PrivacyCustomRule { Id = "c1", Name = "Rule", Pattern = "x+", Enabled = true }],
    };

    [Fact]
    public void Every_change_is_a_new_complete_policy_and_the_old_one_is_untouched()
    {
        var p = Policy();
        var next = PrivacyPolicyLogic.SetAction(p, "jwt", "block");
        Assert.Equal("block", next.CategoryActions["jwt"]);
        Assert.False(p.CategoryActions.ContainsKey("jwt"));
        Assert.Equal("redact", next.CategoryActions["email"]);
        Assert.Single(next.CustomRules);
        Assert.NotSame(p.CustomRules[0], next.CustomRules[0]);
        Assert.False(next.DryRun);
    }

    [Fact]
    public void Action_of_a_category_falls_back_to_the_default_and_off_means_off()
    {
        var p = Policy();
        Assert.Equal("redact", PrivacyPolicyLogic.ActionOf(p, "email"));
        Assert.Equal("warn", PrivacyPolicyLogic.ActionOf(p, "jwt"));
        Assert.True(PrivacyPolicyLogic.IsOff(p, "phone"));
        Assert.False(PrivacyPolicyLogic.IsOff(p, "email"));
        p.Enabled = false;
        Assert.True(PrivacyPolicyLogic.IsOff(p, "email"));
    }

    [Fact]
    public void Enabling_a_switched_off_category_gives_it_the_default_action_or_warn()
    {
        var p = Policy();
        Assert.Equal("warn", PrivacyPolicyLogic.EnabledAction(p));
        p.DefaultAction = "block";
        Assert.Equal("block", PrivacyPolicyLogic.EnabledAction(p));
        p.DefaultAction = "off";
        Assert.Equal("warn", PrivacyPolicyLogic.EnabledAction(p));
    }

    [Fact]
    public void Custom_rules_toggle_remove_and_add()
    {
        var p = Policy();
        Assert.False(PrivacyPolicyLogic.SetRuleEnabled(p, "c1", false).CustomRules[0].Enabled);
        Assert.True(p.CustomRules[0].Enabled);
        Assert.Empty(PrivacyPolicyLogic.RemoveRule(p, "c1").CustomRules);
        var added = PrivacyPolicyLogic.AddRule(p, new PrivacyCustomRule { Id = "c2", Name = "n", Pattern = "p" });
        Assert.Equal(["c1", "c2"], added.CustomRules.Select(r => r.Id));
    }

    [Fact]
    public void New_rule_needs_name_and_pattern_and_gets_a_clock_based_id()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        Assert.Null(PrivacyPolicyLogic.NewRule(" ", "x", now));
        Assert.Null(PrivacyPolicyLogic.NewRule("n", "", now));
        var rule = PrivacyPolicyLogic.NewRule(" Name ", " x+ ", now)!;
        Assert.Equal("Name", rule.Name);
        Assert.Equal("x+", rule.Pattern);
        Assert.True(rule.Enabled);
        Assert.StartsWith("custom-", rule.Id);
    }

    [Fact]
    public void Base36_ids_match_the_javascript_form()
    {
        // Known (n).toString(36) values: 35 -> "z", 36 -> "10", 1295 -> "zz", 1296 -> "100",.
        Assert.Equal("custom-z", PrivacyPolicyLogic.NewRuleId(DateTimeOffset.FromUnixTimeMilliseconds(35)));
        Assert.Equal("custom-10", PrivacyPolicyLogic.NewRuleId(DateTimeOffset.FromUnixTimeMilliseconds(36)));
        Assert.Equal("custom-zz", PrivacyPolicyLogic.NewRuleId(DateTimeOffset.FromUnixTimeMilliseconds(1295)));
        Assert.Equal("custom-100", PrivacyPolicyLogic.NewRuleId(DateTimeOffset.FromUnixTimeMilliseconds(1296)));
    }

    [Fact]
    public void Pattern_validation_reports_the_engines_message()
    {
        Assert.True(PrivacyPolicyLogic.TryValidatePattern(@"\d{3}-\d{4}", out _));
        Assert.False(PrivacyPolicyLogic.TryValidatePattern("(unclosed", out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Categories_come_from_the_rules_in_server_order_without_duplicates()
    {
        PrivacyRuleDto[] rules = [new("a", "api_key", "d1"), new("b", "api_key", "d2"), new("c", "jwt", "d3")];
        Assert.Equal(["api_key", "jwt"], PrivacyPolicyLogic.Categories(rules));
        Assert.Equal("d1", PrivacyPolicyLogic.Description(rules, "api_key"));
        Assert.Equal(["warn", "block", "redact"], PrivacyPolicyLogic.SelectableActions(false));
        Assert.Equal(4, PrivacyPolicyLogic.SelectableActions(true).Count);
    }

    [Fact]
    public void The_policy_serializes_with_the_api_options_the_server_reads()
    {
        var json = JsonSerializer.Serialize(Policy(), JsonContexts.Info<PrivacySettings>(Json.Api));
        Assert.Contains("\"defaultAction\":\"warn\"", json);
        Assert.Contains("\"categoryActions\":{\"email\":\"redact\",\"phone\":\"off\"}", json);
        Assert.Contains("\"customRules\":[{\"id\":\"c1\"", json);
    }
}

public class PrivacyEventsLogicTests
{
    private static PrivacyEventDto Event(bool blocked = false, int redactions = 0, bool dryRun = false, params PrivacyHit[] hits)
        => new("r1", new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero), "claude-code", "p1", "OpenAI", "gpt-5", "success", dryRun, blocked, redactions, hits);

    private static PrivacyHit Hit(string rule = "email", string category = "email", string action = "redact", int count = 1, params string[] samples)
        => new() { RuleId = rule, Category = category, Action = action, Count = count, Samples = [.. samples] };

    [Fact]
    public void Outcome_is_blocked_then_redacted_then_dry_run_then_warned()
    {
        Assert.Equal(PrivacyOutcome.Blocked, PrivacyEventRows.OutcomeOf(Event(blocked: true, redactions: 2)));
        Assert.Equal(PrivacyOutcome.Redacted, PrivacyEventRows.OutcomeOf(Event(redactions: 2, dryRun: true)));
        Assert.Equal(PrivacyOutcome.DryRun, PrivacyEventRows.OutcomeOf(Event(dryRun: true)));
        Assert.Equal(PrivacyOutcome.Warned, PrivacyEventRows.OutcomeOf(Event()));
        var (text, tone) = PrivacyEventRows.OutcomeBadge(Event(redactions: 3));
        Assert.Equal(("privacy.logs.outcome.redacted", "orange"), (text.Key, tone));
        Assert.Equal("red", PrivacyEventRows.OutcomeBadge(Event(blocked: true)).Tone);
    }

    [Fact]
    public void Rows_sum_the_hits_and_name_the_client()
    {
        var row = Assert.Single(PrivacyEventRows.Rows([Event(hits: [Hit(count: 2), Hit("k", "api_key", "block", 3)])]));
        Assert.Equal(5, row.Hits);
        Assert.Equal("Claude Code", row.Client);
        Assert.Equal("gpt-5", row.Model);
    }

    [Fact]
    public void Hits_summary_shows_two_and_counts_the_rest()
    {
        var (shown, more) = PrivacyEventRows.HitsSummary(Event(hits: [Hit(), Hit("b"), Hit("c"), Hit("d")]));
        Assert.Equal(2, shown.Count);
        Assert.Equal(2, more);
        Assert.Equal("  ", PrivacyEventRows.Samples(Hit(samples: ["a", "b"]), 2).Replace("a", "").Replace("b", ""));
        Assert.Equal("a", PrivacyEventRows.Samples(Hit(samples: ["a", "b"]), 1));
    }

    [Fact]
    public void Dry_run_badges()
    {
        Assert.Equal(["green"], PrivacyEventRows.DryRunBadges(new PrivacyDryRunResultDto(false, [], 0, [])).Select(b => b.Tone));
        Assert.Equal(["red", "orange"], PrivacyEventRows.DryRunBadges(new PrivacyDryRunResultDto(true, [Hit()], 2, ["email"])).Select(b => b.Tone));
    }

    [Fact]
    public void Filters_reset_the_page_and_build_the_query_without_empty_values()
    {
        var f = new PrivacyEventFilters();
        Assert.Equal("/api/privacy/events?page=1&pageSize=50", f.Path);
        f.GoTo(3);
        f.Set(PrivacyFilter.Category, "email");
        Assert.Equal(1, f.Page);
        f.SetModel("gpt");
        f.Set(PrivacyFilter.Blocked, "true");
        Assert.Equal("/api/privacy/events?model=gpt&category=email&blocked=true&page=1&pageSize=50", f.Path);
        Assert.True(f.Any && f.Live);
        f.GoTo(2);
        Assert.False(f.Live);
        f.ClearAll();
        Assert.False(f.Any);
        Assert.Equal("gpt", f.Model);
        Assert.Equal(1, f.Page);
    }

    [Fact]
    public void Query_keys_differ_per_filter_and_page_so_each_page_caches_alone()
    {
        var a = new PrivacyEventFilters();
        var b = new PrivacyEventFilters();
        Assert.Equal(a.Key, b.Key);
        b.GoTo(2);
        Assert.NotEqual(a.Key, b.Key);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(500, 10)]
    public void Pages_round_up(long total, int pages) => Assert.Equal(pages, PrivacyEventFilters.Pages(total));

    [Fact]
    public void Filter_ids_round_trip_and_options_come_from_the_data()
    {
        foreach (var f in Enum.GetValues<PrivacyFilter>()) Assert.Equal(f, PrivacyEventFilters.FromId(PrivacyEventFilters.Id(f)));
        var clients = PrivacyEventFilters.Options(PrivacyFilter.Client, [("codex", "Codex")], []);
        Assert.Equal([new Astra.Desktop.Core.Settings.Option("codex", "Codex")], clients);
        Assert.Equal(["true", "false"], PrivacyEventFilters.Options(PrivacyFilter.Blocked, [], []).Select(o => o.Value));
        Assert.Equal(["block", "redact", "warn"], PrivacyEventFilters.Options(PrivacyFilter.Action, [], []).Select(o => o.Value));
        Assert.Equal("email", PrivacyEventFilters.Options(PrivacyFilter.Category, [], ["email"])[0].Value);
    }

    [Fact]
    public void Stats_sort_by_hits_descending()
    {
        var rows = PrivacyStatsLogic.ByHits([new("a", 1, 5, 0, 0, 0), new("b", 1, 9, 0, 0, 0), new("c", 1, 1, 0, 0, 0)]);
        Assert.Equal(["b", "a", "c"], rows.Select(r => r.Category));
    }
}

public class PrivacyApiTests
{
    [Fact]
    public async Task Update_puts_the_whole_policy_and_replaces_only_the_settings_in_the_cache()
    {
        var policy = """{"settings":{"enabled":false,"dryRun":true,"restoreResponses":true,"recordSamples":true,"defaultAction":"warn","categoryActions":{},"clientDefaults":{},"customRules":[]},"rules":[{"id":"r","category":"email","description":"d"}]}""";
        var saved = """{"enabled":true,"dryRun":true,"restoreResponses":true,"recordSamples":true,"defaultAction":"warn","categoryActions":{},"clientDefaults":{},"customRules":[]}""";
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, ctx.Request.HttpMethod == "PUT" ? saved : policy));
        var client = new ApiClient(server.Url);
        var queries = new QueryClient();
        var api = new PrivacyApi(client, queries, new ClientsStore(queries, client));
        await api.Policy.RefetchAsync();
        Assert.False(api.Policy.Data!.Settings.Enabled);
        var result = await api.UpdateAsync(PrivacyPolicyLogic_With(api.Policy.Data.Settings));
        Assert.True(result.Enabled);
        await Task.Delay(50);
        Assert.True(api.Policy.Data.Settings.Enabled);
        Assert.Single(api.Policy.Data.Rules);
        var put = server.Requests.Single(r => r.Method == "PUT");
        Assert.Equal("1", put.Admin);
        Assert.Contains("\"enabled\":true", put.Body);

        static PrivacySettings PrivacyPolicyLogic_With(PrivacySettings s) => Astra.Desktop.Core.Privacy.PrivacyPolicyLogic.With(s, n => n.Enabled = true);
    }

    [Fact]
    public async Task Events_query_follows_the_filters_and_only_page_one_polls()
    {
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, """{"items":[],"total":0,"page":1,"pageSize":50}"""));
        var client = new ApiClient(server.Url);
        var queries = new QueryClient();
        var api = new PrivacyApi(client, queries, new ClientsStore(queries, client));
        var f = new PrivacyEventFilters();
        f.Set(PrivacyFilter.Action, "block");
        var q = api.Events(f);
        await q.RefetchAsync();
        Assert.Equal(0, q.Data!.Total);
        Assert.Contains(server.Requests, r => r.Url == "/api/privacy/events?action=block&page=1&pageSize=50");
        Assert.Same(q, api.Events(f));
        f.GoTo(2);
        Assert.NotSame(q, api.Events(f));
    }

    [Fact]
    public async Task Dry_run_posts_the_text_only()
    {
        using var server = new FakeServer(ctx => FakeServer.Json(ctx, 200, """{"blocked":false,"hits":[{"ruleId":"email","category":"email","action":"redact","count":1,"samples":["jo••om"]}],"wouldRedact":1,"categories":["email"]}"""));
        var client = new ApiClient(server.Url);
        var queries = new QueryClient();
        var api = new PrivacyApi(client, queries, new ClientsStore(queries, client));
        var r = await api.DryRunAsync("mail me at john@example.com");
        Assert.Equal(1, r.WouldRedact);
        Assert.Equal("jo••om", r.Hits[0].Samples[0]);
        Assert.Equal("""{"text":"mail me at john@example.com"}""", server.Requests.Single().Body);
    }
}
