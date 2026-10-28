using System.Text.Json.Nodes;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

/// <summary>Ports of web/src/test/logic.test.ts "parsePricing / compactSchedule" and the PricingEditor edits.</summary>
public class PricingDocumentTests
{
    private static string Json(PricingDocument d) => d.ToJson();

    [Fact]
    public void Empty_schedule_is_usd_per_million_with_unset_base()
    {
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":null,"output":null}}""", Json(PricingDocument.Empty()));
    }

    [Fact]
    public void Parse_blank_is_no_document_and_no_error()
    {
        Assert.Equal((null, null), PricingDocument.Parse(""));
        Assert.Equal((null, null), PricingDocument.Parse("  \n "));
    }

    [Fact]
    public void Parse_reports_bad_json_and_the_two_structural_errors()
    {
        Assert.NotNull(PricingDocument.Parse("{").Error);
        Assert.Equal("not an object", PricingDocument.Parse("[]").Error);
        Assert.Equal("not an object", PricingDocument.Parse("5").Error);
        Assert.Equal("not an object", PricingDocument.Parse("null").Error);
        Assert.Equal("\"base\" is required", PricingDocument.Parse("""{"currency":"USD"}""").Error);
        Assert.Equal("\"base\" is required", PricingDocument.Parse("""{"base":3}""").Error);
        Assert.Equal("\"base\" is required", PricingDocument.Parse("""{"base":null}""").Error);
    }

    [Fact]
    public void Parse_fills_currency_and_unit()
    {
        var (doc, error) = PricingDocument.Parse("""{"base":{"input":3}}""");
        Assert.Null(error);
        Assert.Equal("USD", doc!.ToNode()["currency"]!.GetValue<string>());
        Assert.Equal("per_1m_tokens", doc.ToNode()["unit"]!.GetValue<string>());
        Assert.Equal(3m, doc.BasePrice("input"));
    }

    [Fact]
    public void Parse_keeps_existing_currency_and_unit_and_unknown_fields()
    {
        var (doc, _) = PricingDocument.Parse("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":1,"per_call":{"search":0.01}},"future":[1,2]}""");
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":1,"per_call":{"search":0.01}},"future":[1,2]}""", Json(doc!));
    }

    [Fact]
    public void Parse_reads_tiny_and_exponent_prices_exactly()
    {
        var (doc, _) = PricingDocument.Parse("""{"base":{"input":0.000003,"output":1e-7,"cache_read":"0.0000025"}}""");
        Assert.Equal(0.000003m, doc!.BasePrice("input"));
        Assert.Equal(0.0000001m, doc.BasePrice("output"));
        Assert.Equal(0.0000025m, doc.BasePrice("cache_read"));
    }

    [Fact]
    public void Parse_rejects_a_repeated_key_instead_of_throwing_later()
    {
        Assert.NotNull(PricingDocument.Parse("""{"base":{"input":1,"input":2}}""").Error);
    }

    [Fact]
    public void Compact_removes_empty_rule_lists_and_unset_fields()
    {
        var (doc, _) = PricingDocument.Parse("""{"base":{"input":1},"context_tiers":[],"time_windows":null,"service_tiers":{},"per_request":null,"notes":""}""");
        Assert.Equal("""{"base":{"input":1},"currency":"USD","unit":"per_1m_tokens"}""", Json(doc!.Compacted()));
    }

    [Fact]
    public void Compact_keeps_filled_lists_and_zero_per_request()
    {
        var (doc, _) = PricingDocument.Parse("""{"base":{"input":1},"context_tiers":[{"name":"x"}],"per_request":0,"notes":"n"}""");
        var compact = doc!.Compacted();
        Assert.Equal(1, compact.Count(PricingDocument.ContextTiersKey));
        Assert.Equal(0m, compact.PerRequest);
        Assert.Equal("n", compact.Notes);
    }

    [Fact]
    public void Setting_a_base_price_writes_a_decimal_number_and_keeps_the_rest()
    {
        var doc = PricingDocument.Empty().WithBasePrice("input", 0.000003m).WithBasePrice("output", 15m).WithBasePrice("cache_read", 1.50m);
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":0.000003,"output":15,"cache_read":1.5}}""", Json(doc));
    }

    [Fact]
    public void Clearing_a_base_price_leaves_an_explicit_null_like_the_web()
    {
        var doc = PricingDocument.Empty().WithBasePrice("input", 3m).WithBasePrice("input", null);
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":null,"output":null}}""", Json(doc));
        Assert.Null(doc.BasePrice("input"));
    }

    [Fact]
    public void Edits_do_not_change_the_original()
    {
        var original = PricingDocument.Empty();
        var edited = original.WithBasePrice("input", 3m);
        Assert.Null(original.BasePrice("input"));
        Assert.Equal(3m, edited.BasePrice("input"));
    }

    [Fact]
    public void Per_request_and_notes_are_removed_when_cleared()
    {
        var doc = PricingDocument.Empty().WithPerRequest(0.02m).WithNotes("hello");
        Assert.Equal(0.02m, doc.PerRequest);
        Assert.Equal("hello", doc.Notes);
        var cleared = doc.WithPerRequest(null).WithNotes("");
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":null,"output":null}}""", Json(cleared));
        Assert.Equal("", cleared.Notes);
        Assert.Null(cleared.PerRequest);
    }

    [Fact]
    public void Rule_fields_can_be_edited_and_removed()
    {
        var draft = new PricingRuleDraft(PricingRuleKind.Context);
        var doc = draft.Apply(PricingDocument.Empty())!;
        Assert.Equal(1, doc.Count(PricingDocument.ContextTiersKey));
        doc = doc.WithRule(PricingDocument.ContextTiersKey, 0, r => r["name"] = ">128K");
        doc = doc.WithRule(PricingDocument.ContextTiersKey, 0, r => r["prices"] = new JsonObject { ["input"] = PriceNumber.ToNode(0.000004m) });
        var rule = doc.Rule(PricingDocument.ContextTiersKey, 0);
        Assert.Equal(">128K", rule.Text("name"));
        Assert.Equal(200_000, rule.Whole("threshold_input_tokens"));
        Assert.Equal(0.000004m, rule.Price("input"));
        Assert.Null(rule.Price("output"));
        doc = doc.WithoutRule(PricingDocument.ContextTiersKey, 0);
        Assert.Equal(0, doc.Count(PricingDocument.ContextTiersKey));
        Assert.DoesNotContain("context_tiers", Json(doc));
    }

    [Fact]
    public void Out_of_range_rule_edits_are_ignored()
    {
        var doc = PricingDocument.Empty();
        Assert.Equal(Json(doc), Json(doc.WithRule(PricingDocument.TimeWindowsKey, 3, r => r["name"] = "x")));
        Assert.Equal(Json(doc), Json(doc.WithoutRule(PricingDocument.TimeWindowsKey, 0)));
        Assert.Equal("", doc.Rule(PricingDocument.TimeWindowsKey, 0).Text("name"));
    }

    [Fact]
    public void Time_window_days_and_exclusions_read_back()
    {
        var draft = new PricingRuleDraft(PricingRuleKind.Time);
        draft.Days.Remove(7);
        draft.Excluded = "2026-10-01, 2026-10-02";
        var doc = draft.Apply(PricingDocument.Empty())!;
        var rule = doc.Rule(PricingDocument.TimeWindowsKey, 0);
        Assert.Equal([1, 2, 3, 4, 5, 6], rule.Days());
        Assert.Equal(["2026-10-01", "2026-10-02"], rule.ExcludeDates());
        Assert.Equal(0.5m, rule.Number("multiplier"));
        // a window without a days list runs every day
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], new PricingRuleDraft(PricingRuleKind.Time).Apply(PricingDocument.Empty())!.Rule(PricingDocument.TimeWindowsKey, 0).Days());
    }

    [Fact]
    public void Service_tiers_add_rename_scale_and_remove()
    {
        var doc = new PricingRuleDraft(PricingRuleKind.Service).Apply(PricingDocument.Empty())!;
        Assert.Equal(["flex"], doc.ServiceTierNames);
        Assert.Equal(0.5m, doc.ServiceTiers[0].Multiplier);

        doc = doc.WithServiceTierMultiplier(0, 0.75m).WithServiceTierRenamed(0, "priority");
        Assert.Equal(["priority"], doc.ServiceTierNames);
        Assert.Equal(0.75m, doc.ServiceTiers[0].Multiplier);
        Assert.False(doc.ServiceTiers[0].HasOwnPrices);

        doc = doc.WithServiceTierMultiplier(0, null);
        Assert.Null(doc.ServiceTiers[0].Multiplier);

        doc = doc.WithoutServiceTier(0);
        Assert.Empty(doc.ServiceTiers);
        Assert.DoesNotContain("service_tiers", Json(doc));
    }

    [Fact]
    public void Service_tier_with_its_own_prices_keeps_them_when_the_multiplier_changes()
    {
        var (doc, _) = PricingDocument.Parse("""{"base":{"input":1},"service_tiers":{"batch":{"multiplier":0.5,"prices":{"input":0.4}}}}""");
        Assert.True(doc!.ServiceTiers[0].HasOwnPrices);
        var edited = doc.WithServiceTierMultiplier(0, 0.25m);
        Assert.True(edited.ServiceTiers[0].HasOwnPrices);
        Assert.Contains("\"prices\":{\"input\":0.4}", Json(edited));
    }

    [Fact]
    public void Renaming_a_tier_to_another_tiers_name_replaces_it_at_the_first_position()
    {
        // Object.fromEntries([["a",r1],["a",r2]]) = { a: r2 }, the entry that comes later wins
        var (doc, _) = PricingDocument.Parse("""{"base":{},"service_tiers":{"a":{"multiplier":1},"b":{"multiplier":2}}}""");
        var renamed = doc!.WithServiceTierRenamed(1, "a");
        Assert.Equal(["a"], renamed.ServiceTierNames);
        Assert.Equal(2m, renamed.ServiceTiers[0].Multiplier);
    }

    [Fact]
    public void Pretty_is_two_space_indented_json_without_ascii_escaping()
    {
        var doc = PricingDocument.Empty().WithNotes("价格 <ok>");
        var pretty = doc.Pretty();
        Assert.Contains("\n  \"currency\": \"USD\"", pretty);
        Assert.Contains("价格 <ok>", pretty);
        Assert.Equal("", PricingDocument.PrettyJson(null));
    }

    [Fact]
    public void Canonical_and_same_ignore_number_spelling_but_not_values_or_key_order()
    {
        Assert.True(PricingDocument.Same(JsonNode.Parse("""{"a":2,"b":[1.0]}"""), JsonNode.Parse("""{"a":2.00,"b":[1]}""")));
        Assert.True(PricingDocument.Same(JsonNode.Parse("""{"a":1e-7}"""), JsonNode.Parse("""{"a":0.0000001}""")));
        Assert.False(PricingDocument.Same(JsonNode.Parse("""{"a":2}"""), JsonNode.Parse("""{"a":3}""")));
        Assert.False(PricingDocument.Same(JsonNode.Parse("""{"a":1,"b":2}"""), JsonNode.Parse("""{"b":2,"a":1}""")));
        Assert.True(PricingDocument.Same(null, null));
        Assert.False(PricingDocument.Same(null, JsonNode.Parse("{}")));
    }

    [Fact]
    public void From_node_copies_and_rejects_non_objects()
    {
        var node = JsonNode.Parse("""{"base":{"input":1}}""")!;
        var doc = PricingDocument.FromNode(node)!;
        node["base"]!["input"] = 9;
        Assert.Equal(1m, doc.BasePrice("input"));
        Assert.Null(PricingDocument.FromNode(null));
        Assert.Null(PricingDocument.FromNode(JsonNode.Parse("[]")));
    }

    [Fact]
    public void Token_types_are_the_twelve_columns_of_the_editor()
    {
        Assert.Equal(12, PricingDocument.TokenTypes.Count);
        Assert.Equal("input", PricingDocument.TokenTypes[0]);
        Assert.Equal("output_image", PricingDocument.TokenTypes[^1]);
    }
}
