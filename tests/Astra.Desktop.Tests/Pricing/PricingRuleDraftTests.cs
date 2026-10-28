using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

/// <summary>PricingRuleDialog's defaults, validity and result (web/src/test/pricing-dialogs.test.tsx and the dialog's own rules).</summary>
public class PricingRuleDraftTests
{
    private static string Json(PricingDocument d) => d.ToJson();

    [Theory]
    [InlineData(PricingRuleKind.Context, ">200K")]
    [InlineData(PricingRuleKind.Time, "off-peak")]
    [InlineData(PricingRuleKind.Service, "flex")]
    public void Defaults_are_the_dialogs(PricingRuleKind kind, string name)
    {
        var d = new PricingRuleDraft(kind);
        Assert.Equal(name, d.Name);
        Assert.Equal(200_000m, d.Threshold);
        Assert.Equal("whole", d.Mode);
        Assert.Equal("highest", d.OutputPolicy);
        Assert.Equal("Asia/Shanghai", d.Timezone);
        Assert.Equal("00:30", d.Start);
        Assert.Equal("08:30", d.End);
        Assert.Equal(0.5m, d.Multiplier);
        Assert.Equal(7, d.Days.Count);
        Assert.True(d.IsValid);
    }

    [Fact]
    public void Adding_a_context_tier_writes_the_web_shape()
    {
        var doc = new PricingRuleDraft(PricingRuleKind.Context).Apply(PricingDocument.Empty())!;
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":null,"output":null},"context_tiers":[{"name":">200K","threshold_input_tokens":200000,"mode":"whole","output_policy":"highest","prices":{}}]}""", Json(doc));
    }

    [Fact]
    public void A_context_tier_carries_the_typed_prices_exactly()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Context) { Name = "  >128K " };
        d.Threshold = 128_000;
        d.Mode = "progressive";
        d.OutputPolicy = "base";
        d.Prices["input"] = 0.000006m;
        d.Prices["output"] = 22.50m;
        d.Prices["cache_read"] = null;
        var doc = d.Apply(PricingDocument.Empty())!;
        Assert.Contains("""{"name":">128K","threshold_input_tokens":128000,"mode":"progressive","output_policy":"base","prices":{"input":0.000006,"output":22.5,"cache_read":null}}""", Json(doc));
    }

    [Fact]
    public void Adding_a_time_window_writes_the_web_shape()
    {
        var doc = new PricingRuleDraft(PricingRuleKind.Time).Apply(PricingDocument.Empty())!;
        Assert.Contains("""{"name":"off-peak","timezone":"Asia/Shanghai","start":"00:30","end":"08:30","multiplier":0.5,"days":null,"exclude_dates":null}""", Json(doc));
    }

    [Fact]
    public void A_time_window_with_some_days_and_excluded_dates_sorts_and_splits_like_the_web()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Time);
        d.Days.Clear();
        foreach (var day in new[] { 6, 1, 3 }) d.Days.Add(day);
        d.Excluded = " 2026-10-01,2026-10-02  2026-10-03 ";
        var doc = d.Apply(PricingDocument.Empty())!;
        Assert.Contains(""""days":[1,3,6],"exclude_dates":["2026-10-01","2026-10-02","2026-10-03"]"""", Json(doc));
    }

    [Fact]
    public void Adding_a_service_tier_sets_it_under_its_trimmed_name()
    {
        var doc = new PricingRuleDraft(PricingRuleKind.Service) { Name = " priority " }.Apply(PricingDocument.Empty())!;
        Assert.Contains("""{"priority":{"multiplier":0.5}}""", Json(doc));
    }

    [Fact]
    public void An_invalid_draft_changes_nothing()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Context) { Name = "  " };
        Assert.False(d.IsValid);
        Assert.Null(d.Apply(PricingDocument.Empty()));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(200000, true)]
    public void Context_threshold_must_be_a_positive_whole_number(int? threshold, bool valid)
    {
        var d = new PricingRuleDraft(PricingRuleKind.Context) { Threshold = threshold };
        Assert.Equal(valid, d.IsValid);
    }

    [Fact]
    public void A_fractional_threshold_is_invalid()
    {
        Assert.False(new PricingRuleDraft(PricingRuleKind.Context) { Threshold = 1000.5m }.IsValid);
    }

    [Fact]
    public void Context_prices_must_be_empty_or_non_negative()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Context);
        d.Prices["input"] = null;
        d.Prices["output"] = 0m;
        Assert.True(d.IsValid);
        d.Prices["reasoning"] = -0.000001m;
        Assert.False(d.PricesValid);
        Assert.False(d.IsValid);
    }

    [Theory]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("08:30", true)]
    [InlineData("24:00", false)]
    [InlineData("8:30", false)]
    [InlineData("08:60", false)]
    [InlineData("08:30\n", false)]
    [InlineData("", false)]
    [InlineData("٠٨:٣٠", false)] // non-ASCII digits are not times in JS either
    public void Clock_times(string text, bool valid) => Assert.Equal(valid, PricingRuleDraft.IsClockTime(text));

    [Theory]
    [InlineData("2026-10-01", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2028-02-29", true)]
    [InlineData("2026-13-01", false)]
    [InlineData("2026-1-1", false)]
    [InlineData("26-10-01", false)]
    [InlineData("2026-10-01\n", false)]
    public void Dates(string text, bool valid) => Assert.Equal(valid, PricingRuleDraft.IsDate(text));

    [Fact]
    public void Time_window_validity_covers_zone_times_days_multiplier_and_dates()
    {
        PricingRuleDraft Fresh() => new(PricingRuleKind.Time);
        Assert.True(Fresh().IsValid);

        var zone = Fresh();
        zone.Timezone = "Mars/Olympus";
        Assert.False(zone.TimezoneValid);
        Assert.False(zone.IsValid);
        zone.Timezone = "UTC";
        Assert.True(zone.IsValid);
        zone.Timezone = "";
        Assert.False(zone.IsValid);

        var time = Fresh();
        time.End = "25:00";
        Assert.False(time.TimeValid);
        Assert.False(time.IsValid);

        var days = Fresh();
        days.Days.Clear();
        Assert.False(days.IsValid);

        var mult = Fresh();
        mult.Multiplier = null;
        Assert.False(mult.IsValid);
        mult.Multiplier = -0.1m;
        Assert.False(mult.IsValid);
        mult.Multiplier = 0;
        Assert.True(mult.IsValid);

        var dates = Fresh();
        dates.Excluded = "2026-10-01, nope";
        Assert.False(dates.IsValid);
        dates.Excluded = "2026-10-01";
        Assert.True(dates.IsValid);
    }

    [Fact]
    public void A_service_tier_name_may_not_repeat_an_existing_one_in_any_case()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Service, ["Flex", "batch"]);
        Assert.True(d.NameTaken);
        Assert.False(d.IsValid);
        d.Name = "  BATCH ";
        Assert.True(d.NameTaken);
        d.Name = "priority";
        Assert.False(d.NameTaken);
        Assert.True(d.IsValid);
        d.Multiplier = null;
        Assert.False(d.IsValid);
    }

    [Fact]
    public void Name_taken_only_matters_for_service_tiers()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Context, [">200K"]);
        Assert.False(d.NameTaken);
        Assert.True(d.IsValid);
    }

    [Fact]
    public void Excluded_dates_split_on_commas_and_white_space()
    {
        var d = new PricingRuleDraft(PricingRuleKind.Time) { Excluded = "a,b  c\n,d" };
        Assert.Equal(["a", "b", "c", "d"], d.ExcludedDates);
        d.Excluded = "";
        Assert.Empty(d.ExcludedDates);
    }

    [Fact]
    public void Cancel_changes_nothing_because_apply_is_never_called()
    {
        // The dialog only calls Apply on Add: the document it was given stays as it was.
        var doc = PricingDocument.Empty();
        var before = Json(doc);
        _ = new PricingRuleDraft(PricingRuleKind.Time) { Name = "never-applied" };
        Assert.Equal(before, Json(doc));
    }
}
