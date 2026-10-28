using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.SmartRouting;

namespace Astra.Desktop.Tests.SmartRouting;

[Collection("Loc")]
public sealed class SmartRoutingLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public SmartRoutingLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    internal static readonly List<JudgeProtocolDto> Protocols =
    [
        new("typesafe", "https://api.typesafe.example", "jev-latest"),
        new("openai", "https://api.openai.com", "gpt-judge"),
    ];

    internal static SmartRoutingDto Saved(
        bool enabled = true, StatusOverride? status = null, string baseUrl = "", bool hasKey = true, string protocol = "typesafe")
        => new(
            enabled, "astra-auto",
            new SmartRoutingJudgeDto(protocol, string.IsNullOrEmpty(baseUrl) ? null : baseUrl, string.IsNullOrEmpty(baseUrl) ? "https://api.typesafe.example" : baseUrl, hasKey, hasKey ? "sk-…9f" : null, "jev-latest", 2000, null),
            [
                new SmartRoutingGradeDto("simple", "Simple", "Easy questions", "gpt-5-mini", "openai"),
                new SmartRoutingGradeDto("medium", "", "Normal work", "claude-sonnet-4", "anthropic"),
                new SmartRoutingGradeDto("hard", "Hard", "Deep reasoning", "gpt-5", "openai"),
            ],
            new SmartRoutingRulesDto("session", 30, "medium", 0.6, 3, 4000),
            new SmartRoutingStatusDto(status?.Breaker ?? false, status?.Pins ?? 0, status?.Recent ?? new RecentDecisionsDto(0, [], null, null, null), status?.Unreachable ?? []),
            "Pick the grade.", Protocols);

    internal sealed record StatusOverride(bool Breaker = false, int Pins = 0, RecentDecisionsDto? Recent = null, List<UnreachableGradeDto>? Unreachable = null);

    // ---- presets and origins ----

    [Theory]
    [InlineData("typesafe", "", "typesafe")]
    [InlineData("typesafe", "https://openrouter.ai/api/", "openrouter")]
    [InlineData("typesafe", " https://ai-gateway.vercel.sh/typesafe ", "vercel")]
    [InlineData("typesafe", "https://mine.example", "custom")]
    [InlineData("openai", "", "openai")]
    [InlineData("openai", "https://openrouter.ai/api", "custom")]
    public void A_preset_is_found_by_its_address(string protocol, string url, string id) => Assert.Equal(id, JudgePresets.PresetOf(protocol, url));

    [Theory]
    [InlineData("https://API.Example.com/v1/x?y=1", "https://api.example.com")]
    [InlineData("http://localhost:8080/api", "http://localhost:8080")]
    [InlineData("https://host:443/p", "https://host")]
    [InlineData("", "https://api.typesafe.example")]
    [InlineData("not a url", "not a url")]
    public void The_origin_decides_who_receives_the_key(string url, string origin) => Assert.Equal(origin, JudgePresets.OriginOf(url, "typesafe", Protocols));

    // ---- draft ----

    [Fact]
    public void A_draft_of_the_saved_settings_is_clean_and_complete()
    {
        var saved = Saved();
        var d = SmartRoutingDraft.Of(saved);
        Assert.True(d.SameAs(SmartRoutingDraft.Of(saved)));
        Assert.True(d.IsComplete);
        Assert.Equal("", d.ApiKey);
        Assert.Equal(3, d.Grades.Count);
        d.Grades[0].Name = "Easy";
        Assert.False(d.SameAs(SmartRoutingDraft.Of(saved)));
    }

    [Fact]
    public void An_empty_number_makes_the_draft_incomplete_and_is_left_out_of_the_update()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.TimeoutMs = null;
        d.MinConfidence = null;
        Assert.False(d.IsComplete);
        var u = d.ToUpdate();
        Assert.Null(u.Judge!.TimeoutMs);
        Assert.Null(u.Rules!.MinConfidence);
        Assert.Equal(30, u.Rules.SessionTtlMinutes);
    }

    [Fact]
    public void The_key_is_only_sent_when_typed_and_the_switch_rides_along()
    {
        var d = SmartRoutingDraft.Of(Saved());
        Assert.Null(d.ToUpdate().Judge!.ApiKey);
        Assert.Null(d.ToUpdate().Enabled);
        d.ApiKey = "sk-new";
        var on = d.ToUpdate(true);
        Assert.Equal("sk-new", on.Judge!.ApiKey);
        Assert.True(on.Enabled);
        Assert.Equal(["simple", "medium", "hard"], on.Grades!.Select(g => g.Id));
        Assert.Equal("anthropic", on.Grades![1].ProviderId);
        Assert.Equal("session", on.Rules!.Mode);
        Assert.Equal("medium", on.Rules.FallbackGrade);
    }

    [Fact]
    public void Changing_the_dialect_starts_from_its_own_address_model_and_no_key()
    {
        var d = SmartRoutingDraft.Of(Saved(baseUrl: "https://mine.example"));
        d.ApiKey = "typed";
        d.ChangeProtocol("openai", Protocols);
        Assert.Equal(("openai", "", "", "gpt-judge"), (d.Protocol, d.BaseUrl, d.ApiKey, d.Model));
    }

    [Fact]
    public void Choosing_a_preset_fills_address_and_model_and_custom_starts_an_https_address()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.ChoosePreset("openrouter");
        Assert.Equal(("https://openrouter.ai/api", "~typesafe/jev-latest"), (d.BaseUrl, d.Model));
        d.ChoosePreset("typesafe");
        Assert.Equal(("", "jev-latest"), (d.BaseUrl, d.Model));
        d.ChoosePreset("custom");
        Assert.Equal("https://", d.BaseUrl);
        d.BaseUrl = "https://mine.example";
        d.ChoosePreset("custom");
        Assert.Equal("https://mine.example", d.BaseUrl);
        d.Protocol = "openai";
        d.Model = "keep";
        d.ChoosePreset("openai");
        Assert.Equal(("", "keep"), (d.BaseUrl, d.Model));
    }

    [Fact]
    public void Adding_a_grade_takes_the_first_free_id_up_to_six()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.Grades[1].Id = "grade-4";
        d.FallbackGrade = "grade-4";
        d.AddGrade();
        Assert.Equal("grade-5", d.Grades[^1].Id);
        d.AddGrade();
        d.AddGrade();
        Assert.Equal(6, d.Grades.Count);
        d.AddGrade();
        Assert.Equal(6, d.Grades.Count);
    }

    [Fact]
    public void The_fallback_grade_follows_removals_to_the_middle_one()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.RemoveGrade(1); // the fallback "medium" goes
        Assert.Equal(["simple", "hard"], d.Grades.Select(g => g.Id));
        Assert.Equal("hard", d.FallbackGrade); // index 2 / 2 = 1
        d.RemoveGrade(0);
        Assert.Equal(2, d.Grades.Count); // never below two
    }

    [Fact]
    public void Moving_a_grade_swaps_neighbours_and_keeps_the_fallback()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.MoveGrade(0, 1);
        Assert.Equal(["medium", "simple", "hard"], d.Grades.Select(g => g.Id));
        Assert.Equal("medium", d.FallbackGrade);
        d.MoveGrade(0, -1);
        d.MoveGrade(2, 1);
        Assert.Equal(["medium", "simple", "hard"], d.Grades.Select(g => g.Id));
    }

    [Fact]
    public void Choosing_a_model_sets_model_and_provider_and_an_empty_provider_is_null()
    {
        var d = SmartRoutingDraft.Of(Saved());
        d.ChooseModel(0, JudgePresets.ChoiceOf("anthropic", "claude-haiku"));
        Assert.Equal(("claude-haiku", "anthropic"), (d.Grades[0].Model, d.Grades[0].ProviderId));
        d.ChooseModel(0, JudgePresets.ChoiceOf(null, "x"));
        Assert.Equal(("x", (string?)null), (d.Grades[0].Model, d.Grades[0].ProviderId));
    }

    [Fact]
    public void Model_choices_name_the_provider_only_when_an_id_is_ambiguous()
    {
        var served = new List<RoutingModelDto> { new("gpt-5", "gpt-5", "openai", "OpenAI"), new("gpt-5", "gpt-5", "azure", "Azure"), new("sonnet", "sonnet", "anthropic", "Anthropic") };
        var choices = RoutingModels.Choices(served);
        Assert.Equal(["gpt-5 · OpenAI", "gpt-5 · Azure", "sonnet"], choices.Select(c => c.Label));
        Assert.Equal([null, null, "Anthropic"], choices.Select(c => c.Detail));
        Assert.Equal("openai\ngpt-5", choices[0].Value);
    }

    [Fact]
    public void A_model_no_provider_serves_any_more_leads_the_list_as_unavailable()
    {
        var choices = RoutingModels.Choices([new RoutingModelDto("a", "a", "p", "P")]);
        var gone = new GradeDraft { Id = "x", Model = "old", ProviderId = "q" };
        var options = RoutingModels.OptionsFor(choices, gone);
        Assert.Equal("q\nold", options[0].Value);
        Assert.Contains("old", options[0].Label, StringComparison.Ordinal);
        Assert.Equal(2, options.Count);
        Assert.Single(RoutingModels.OptionsFor(choices, new GradeDraft { Id = "y", Model = "a", ProviderId = "p" }));
        Assert.Single(RoutingModels.OptionsFor(choices, new GradeDraft { Id = "z" }));
    }

    // ---- texts ----

    [Fact]
    public void A_known_problem_is_said_in_the_ui_language_with_its_grade_named()
    {
        var d = SmartRoutingDraft.Of(Saved());
        var text = RoutingTexts.ProblemText(new RoutingProblemDto("grade_model_missing", "english from the server", "grades[1].model"), d);
        Assert.Equal("Grade “medium” has no model yet.", text); // an unnamed grade shows its id
        d.Grades[1].Name = "Mid";
        Assert.Equal("Grade “Mid” has no model yet.", RoutingTexts.ProblemText(new RoutingProblemDto("grade_model_missing", "x", "grades[1].model"), d));
        Assert.Equal("server words", RoutingTexts.ProblemText(new RoutingProblemDto("brand_new_code", "server words", null), d));
        Assert.Equal("Grade “” has no model yet.", RoutingTexts.ProblemText(new RoutingProblemDto("grade_model_missing", "x", "grades[9].model"), d));
    }

    [Fact]
    public void A_refused_save_lists_every_problem_and_any_other_error_is_its_message()
    {
        var d = SmartRoutingDraft.Of(Saved());
        var details = JsonNode.Parse("""{"problems":[{"code":"judge_model_missing","message":"m"},{"code":"grades_count","message":"g"},{"code":"nope","message":"raw"}]}""");
        var lines = RoutingTexts.Explain(new ApiException(400, "invalid", details), d);
        Assert.Equal(3, lines.Count);
        Assert.Equal("The judge model has not been entered yet.", lines[0]);
        Assert.Equal("There must be 2 to 6 grades.", lines[1]);
        Assert.Equal("raw", lines[2]);
        Assert.Equal(["HTTP 500"], RoutingTexts.Explain(new ApiException(500, "HTTP 500"), d));
        Assert.Equal(["boom"], RoutingTexts.Explain(new InvalidOperationException("boom"), d));
        Assert.Equal(["bad"], RoutingTexts.Explain(new ApiException(400, "bad", JsonNode.Parse("""{"problems":[]}""")), d));
    }

    [Fact]
    public void Failure_reasons_and_unusable_reasons_fall_back_to_their_raw_text()
    {
        Assert.Equal("timed out", RoutingTexts.ReasonText("timeout"));
        Assert.Equal("some_new_reason", RoutingTexts.ReasonText("some_new_reason"));
        Assert.Equal("", RoutingTexts.ReasonText(null));
        Assert.Null(RoutingTexts.UnusableKey("made_up"));
        Assert.Null(RoutingTexts.UnusableKey(null));
        Assert.Equal("smartRouting.unusable.unknown", RoutingTexts.UnusableKey("unknown"));
    }

    [Theory]
    [InlineData("claude-code", "Claude Code")]
    [InlineData("plugin-aider", "aider")]
    [InlineData("mystery", "mystery")]
    public void Client_names_are_the_webs(string kind, string name) => Assert.Equal(name, RoutingTexts.ClientName(kind));

    // ---- status ----

    [Fact]
    public void The_state_line_follows_the_switch_the_breaker_and_the_pins()
    {
        Assert.Equal(PluginTone.Neutral, RoutingStatus.StateOf(Saved(enabled: false)).Tone);
        Assert.Equal(new RoutingState(PluginTone.Orange, "The judge kept failing; the fallback grade is used for now"), RoutingStatus.StateOf(Saved(status: new StatusOverride(Breaker: true))));
        Assert.Equal(new RoutingState(PluginTone.Green, "On"), RoutingStatus.StateOf(Saved()));
        Assert.Equal(new RoutingState(PluginTone.Green, "On · 4 sessions holding a grade"), RoutingStatus.StateOf(Saved(status: new StatusOverride(Pins: 4))));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recent_decisions_summarize_what_the_judge_did_and_why_the_rest_fell_back()
    {
        var recent = new RecentDecisionsDto(10, new Dictionary<string, int> { ["judge"] = 8, ["low_confidence"] = 1, ["timeout"] = 1 }, 640, "timeout", Now);
        var m = RoutingStatus.RecentOf(Saved(status: new StatusOverride(Recent: recent)), Now);
        Assert.Equal("8 of the last 10 routed by the judge · not confident enough ×1 · timed out ×1 · median judge time 640 ms", m.Detail);
        Assert.Null(m.Failing); // only two fell back, not most
    }

    [Fact]
    public void No_decisions_yet_says_so()
        => Assert.Equal("No fresh decision since the settings were saved.", RoutingStatus.RecentOf(Saved(), Now).Detail);

    [Fact]
    public void Mostly_fallbacks_warn_with_the_hint_of_the_commonest_reason_and_the_latest_one()
    {
        var recent = new RecentDecisionsDto(10, new Dictionary<string, int> { ["judge"] = 3, ["timeout"] = 5, ["other_thing"] = 2 }, null, "timeout", Now.AddMinutes(-3));
        var m = RoutingStatus.RecentOf(Saved(status: new StatusOverride(Recent: recent)), Now);
        Assert.NotNull(m.Failing);
        Assert.Equal("Most recent decisions fell back: those requests went to the “medium” grade", m.Failing!.Title);
        Assert.Equal(Loc.T("smartRouting.recent.hint.timeout"), m.Failing.Hint);
        Assert.StartsWith("Latest: timed out, ", m.Failing.Last, StringComparison.Ordinal);
        // an unknown commonest reason gets the generic hint
        var odd = new RecentDecisionsDto(4, new Dictionary<string, int> { ["new_reason"] = 4 }, null, null, null);
        var m2 = RoutingStatus.RecentOf(Saved(status: new StatusOverride(Recent: odd)), Now);
        Assert.Equal(Loc.T("smartRouting.recent.hint.other"), m2.Failing!.Hint);
        Assert.Null(m2.Failing.Last);
    }

    [Fact]
    public void Unreachable_callers_say_what_grade_they_got_instead_and_how_to_fix_it()
    {
        var u = new List<UnreachableGradeDto>
        {
            new("claude-code", "hard", "gpt-5", "medium", 3, Now, "unknown", "OpenAI"),
            new("my-token", "simple", "gpt-5-mini", null, 1, Now, "made_up_code", null),
        };
        var m = RoutingStatus.RecentOf(Saved(status: new StatusOverride(Unreachable: u)), Now);
        Assert.Equal(2, m.Unreachable.Count);
        Assert.StartsWith("Claude Code cannot use “Hard” (gpt-5 on OpenAI): ", m.Unreachable[0].Line, StringComparison.Ordinal);
        Assert.Contains("Given “medium” instead 3×", m.Unreachable[0].Line, StringComparison.Ordinal);
        Assert.NotNull(m.Unreachable[0].Fix);
        Assert.Contains("No other grade was usable either", m.Unreachable[1].Line, StringComparison.Ordinal);
        Assert.Contains("(gpt-5-mini)", m.Unreachable[1].Line, StringComparison.Ordinal);
        Assert.Null(m.Unreachable[1].Fix);
        Assert.True(m.ShowUnreachableHint);
    }
}
