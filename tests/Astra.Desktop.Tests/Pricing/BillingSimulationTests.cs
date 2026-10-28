using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

// Reads the global Loc language, which the Loc collection's tests switch: run with them, not beside them.
[Collection("Loc")]
public class BillingSimulationTests
{
    private static Dictionary<string, decimal?> Tokens(params (string, decimal?)[] items) => items.ToDictionary(i => i.Item1, i => i.Item2);

    [Fact]
    public void Default_usage_is_one_million_in_and_a_hundred_thousand_out()
    {
        var d = BillingSimulation.DefaultTokens();
        Assert.Equal(1_000_000m, d["input"]);
        Assert.Equal(100_000m, d["output"]);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void A_chosen_provider_prices_the_model_there_and_ignores_the_document()
    {
        var r = BillingSimulation.BuildRequest("gpt-5", JsonNode.Parse("{}"), "prov_1", BillingSimulation.DefaultTokens(), "", null, "zh");
        Assert.Equal("gpt-5", r.ModelId);
        Assert.Equal("prov_1", r.ProviderId);
        Assert.Null(r.Pricing);
    }

    [Fact]
    public void A_saved_model_without_a_document_prices_the_system_model()
    {
        var r = BillingSimulation.BuildRequest("gpt-5", null, "", BillingSimulation.DefaultTokens(), "", null, "en");
        Assert.Equal("gpt-5", r.ModelId);
        Assert.Null(r.ProviderId);
        Assert.Null(r.Pricing);
        Assert.Equal("en", r.Locale);
    }

    [Fact]
    public void An_unsaved_document_is_priced_as_it_is_and_copied()
    {
        var doc = JsonNode.Parse("""{"base":{"input":1}}""")!;
        var r = BillingSimulation.BuildRequest("gpt-5", doc, "", BillingSimulation.DefaultTokens(), "", null, "zh");
        Assert.Null(r.ModelId);
        Assert.Equal("""{"base":{"input":1}}""", r.Pricing!.ToJsonString());
        doc["base"]!["input"] = 5;
        Assert.Equal("""{"base":{"input":1}}""", r.Pricing.ToJsonString());
    }

    [Fact]
    public void Without_a_model_and_a_document_pricing_is_null()
    {
        var r = BillingSimulation.BuildRequest(null, null, "", BillingSimulation.DefaultTokens(), "", null, "zh");
        Assert.Null(r.ModelId);
        Assert.Null(r.Pricing);
    }

    [Fact]
    public void Only_positive_token_amounts_are_sent_and_fractions_are_cut()
    {
        var r = BillingSimulation.BuildRequest(null, null, "", Tokens(("input", 1_000_000), ("output", 0), ("cache_read", null), ("reasoning", 12.9m), ("input_audio", -5)), "", null, "zh");
        Assert.Equal(new Dictionary<string, long> { ["input"] = 1_000_000, ["reasoning"] = 12 }, r.Usage.Tokens);
    }

    [Fact]
    public void Service_tier_and_request_time()
    {
        var local = new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.FromHours(8));
        var r = BillingSimulation.BuildRequest(null, null, "", BillingSimulation.DefaultTokens(), "flex", local, "zh");
        Assert.Equal("flex", r.Usage.ServiceTier);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), r.RequestTimeUtc);
        Assert.Equal(TimeSpan.Zero, r.RequestTimeUtc!.Value.Offset);
        Assert.Null(BillingSimulation.BuildRequest(null, null, "", BillingSimulation.DefaultTokens(), "", null, "zh").Usage.ServiceTier);
    }

    [Fact]
    public void Item_rows_print_like_the_web_table()
    {
        var item = new BillingItemDto("cache_read", 1_000_000, false, 0.5m, 0.25m, 0.5m, ["flex"], "base", "input", 250_000_000, null);
        var row = new BillingSimulation.ItemRow(item);
        Assert.Equal("cache_read → input", row.TypeText);
        Assert.Equal("1,000,000", row.TokensText);
        Assert.Equal("$0.25 (×0.5)", row.UnitPriceText);
        Assert.Equal("$0.25", row.CostText);

        var plain = new BillingSimulation.ItemRow(new BillingItemDto("input", 5, true, 3m, 3m, 1m, [], "base", "input", 15, null));
        Assert.Equal("input", plain.TypeText);
        Assert.Equal("5×", plain.TokensText);
        Assert.Equal("$3", plain.UnitPriceText);
        Assert.Equal("$0.000000015", plain.CostText);
    }

    [Fact]
    public void Multiplier_text_never_uses_float_noise()
    {
        var row = new BillingSimulation.ItemRow(new BillingItemDto("input", 1, false, 1m, 0.3m, 0.1m + 0.2m, [], "base", "input", 0, null));
        Assert.Equal("$0.3 (×0.3)", row.UnitPriceText);
    }

    [Fact]
    public void Source_badges()
    {
        Assert.Equal((Loc.T("pricingSource.override"), "orange"), BillingSimulation.SourceBadge("provider_override", null));
        Assert.Equal((Loc.T("pricingSource.provider", ("key", "openai")), "accent"), BillingSimulation.SourceBadge("provider_price", "openai"));
        Assert.Equal((Loc.T("pricingSource.default"), "neutral"), BillingSimulation.SourceBadge("system_default", null));
        Assert.Equal((Loc.T("pricingSource.none"), "red"), BillingSimulation.SourceBadge("none", null));
        Assert.Equal((Loc.T("pricingSource.none"), "red"), BillingSimulation.SourceBadge(null, null));
    }

    [Fact]
    public void Rows_follow_the_result()
    {
        var result = new BillingResultDto([new BillingItemDto("input", 1, false, 1, 1, 1, [], "", "input", 0, null)], 0, 0, true, [], "", "none", null);
        Assert.Single(BillingSimulation.Rows(result));
    }

    [Fact]
    public void Provider_options_come_from_the_raw_provider_list()
    {
        var list = JsonNode.Parse("""[{"id":"p1","name":"OpenAI","priceKey":"openai","templateId":"t"},{"id":"p2","name":"Mine","templateId":"custom"},{"id":"p3"},{"name":"no id"},5]""")!.AsArray();
        var options = BillingSimulation.ProviderOptions(list);
        Assert.Equal([new BillingSimulation.ProviderOption("p1", "OpenAI", "openai"), new BillingSimulation.ProviderOption("p2", "Mine", "custom"), new BillingSimulation.ProviderOption("p3", "p3", null)], options);
        Assert.Empty(BillingSimulation.ProviderOptions(null));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("2026-10-26 20:00", true)]
    [InlineData("2026-10-26T20:00", true)]
    [InlineData("2026-10-26 20:00:30", true)]
    [InlineData("2026-10-26", false)]
    [InlineData("tomorrow", false)]
    [InlineData("2026-13-26 20:00", false)]
    public void Request_time_text(string text, bool valid)
        => Assert.Equal(valid, BillingSimulation.TryParseRequestTime(text, TimeZoneInfo.Utc, out _));

    [Fact]
    public void Request_time_is_read_in_the_given_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(8), "t", "t");
        Assert.True(BillingSimulation.TryParseRequestTime("2026-10-26 20:00", zone, out var time));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), time!.Value.ToUniversalTime());
        Assert.True(BillingSimulation.TryParseRequestTime("", zone, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Request_time_needs_both_the_date_and_the_time()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("p8", TimeSpan.FromHours(8), "p8", "p8");
        Assert.Null(BillingSimulation.RequestTime(null, null, zone));
        Assert.Null(BillingSimulation.RequestTime(new DateOnly(2026, 10, 27), null, zone));
        Assert.Null(BillingSimulation.RequestTime(null, new TimeOnly(22, 5), zone));
        var at = BillingSimulation.RequestTime(new DateOnly(2026, 10, 27), new TimeOnly(22, 5), zone);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 22, 5, 0, TimeSpan.FromHours(8)), at);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 14, 5, 0, TimeSpan.Zero), at!.Value.ToUniversalTime());
    }
}
