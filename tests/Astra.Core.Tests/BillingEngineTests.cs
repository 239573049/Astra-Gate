using Astra.Core.Billing;

namespace Astra.Core.Tests;

public class BillingEngineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static PriceSet P(params (string Type, decimal Price)[] prices)
    {
        var set = new PriceSet();
        foreach (var (t, p) in prices) set[t] = p;
        return set;
    }

    private static NormalizedUsage U(params (string Type, long Count)[] tokens)
    {
        var u = new NormalizedUsage();
        foreach (var (t, c) in tokens) u.Add(t, c);
        return u;
    }

    private static BillingContext Ctx(DateTimeOffset? at = null, decimal multiplier = 1m,
        PricingSource source = PricingSource.SystemDefault, string locale = "zh") => new()
    {
        RequestTimeUtc = at ?? Noon,
        ProviderMultiplier = multiplier,
        PricingSource = source,
        ModelLabel = "m",
        ProviderLabel = "p",
        Locale = locale,
    };

    private static PricingSchedule Claude(string mode = "whole") => new()
    {
        Base = P(("input", 3m), ("cache_read", 0.3m), ("cache_write_5m", 3.75m), ("cache_write_1h", 6m), ("output", 15m)),
        ContextTiers =
        [
            new ContextTier
            {
                Name = ">200K", ThresholdInputTokens = 200_000, Mode = mode,
                Prices = P(("input", 6m), ("cache_read", 0.6m), ("cache_write_5m", 7.5m), ("cache_write_1h", 12m), ("output", 22.5m)),
            },
        ],
    };

    [Fact]
    public void Claude_cache_writes_5m_and_1h_are_priced_separately()
    {
        var r = BillingEngine.Calculate(U(("input", 1000), ("cache_write_5m", 2000), ("cache_write_1h", 3000), ("output", 500)),
            Claude(), Ctx());
        Assert.Equal(3_000_000, r.Items.Single(i => i.TokenType == "input").CostNanos);
        Assert.Equal(7_500_000, r.Items.Single(i => i.TokenType == "cache_write_5m").CostNanos);
        Assert.Equal(18_000_000, r.Items.Single(i => i.TokenType == "cache_write_1h").CostNanos);
        Assert.Equal(7_500_000, r.Items.Single(i => i.TokenType == "output").CostNanos);
        Assert.Equal(36_000_000, r.TotalNanos);
        Assert.All(r.Items, i => Assert.Equal("base", i.Tier));
    }

    [Fact]
    public void Claude_over_200k_bills_the_whole_request_at_the_tier()
    {
        var r = BillingEngine.Calculate(U(("input", 12_000), ("cache_read", 210_000), ("cache_write_1h", 9_420), ("output", 1_850)),
            Claude(), Ctx());
        Assert.Equal(352_665_000, r.TotalNanos);
        Assert.All(r.Items, i => Assert.Equal(">200K", i.Tier));
        Assert.Contains(r.Trace, s => s.Code == "context_tier_whole");
        Assert.Contains("input 12,000 × $6.0000/M = $0.072000 [档位 >200K]", r.Description);
        Assert.Contains("cache_write_1h 9,420 × $12.0000/M = $0.113040 [档位 >200K]", r.Description);
        Assert.EndsWith("合计 $0.352665", r.Description);
    }

    [Fact]
    public void Exactly_at_threshold_stays_on_base_tier()
    {
        var r = BillingEngine.Calculate(U(("input", 200_000)), Claude(), Ctx());
        Assert.Equal("base", r.Items.Single().Tier);
        Assert.Contains(r.Trace, s => s.Code == "context_tier_none");
    }

    [Fact]
    public void Progressive_tier_splits_inputs_proportionally_and_rounds_remainder_into_last_segment()
    {
        var r = BillingEngine.Calculate(U(("input", 100_000), ("cache_read", 200_000), ("output", 1_000)), Claude("progressive"), Ctx());
        var input = r.Items.Where(i => i.TokenType == "input").ToList();
        Assert.Equal([66_667L, 33_333L], input.Select(i => i.Tokens));
        Assert.Equal(["base", ">200K"], input.Select(i => i.Tier));
        var cache = r.Items.Where(i => i.TokenType == "cache_read").ToList();
        Assert.Equal([133_333L, 66_667L], cache.Select(i => i.Tokens));
        var output = r.Items.Single(i => i.TokenType == "output");
        Assert.Equal(">200K", output.Tier);
        Assert.Equal(22.5m, output.UnitPrice);
        Assert.Equal(200_001_000 + 199_998_000 + 39_999_900 + 40_000_200 + 22_500_000, r.TotalNanos);
        Assert.Equal(r.Items.Sum(i => i.CostNanos), r.TotalNanos);
    }

    [Fact]
    public void Progressive_output_policy_base_bills_outputs_at_base()
    {
        var pricing = Claude("progressive");
        pricing.ContextTiers![0].OutputPolicy = "base";
        var r = BillingEngine.Calculate(U(("input", 300_000), ("output", 1_000)), pricing, Ctx());
        Assert.Equal(15m, r.Items.Single(i => i.TokenType == "output").UnitPrice);
    }

    private static PricingSchedule Gpt() => new()
    {
        Base = P(("input", 1.25m), ("cache_read", 0.125m), ("output", 10m)),
        ServiceTiers = new()
        {
            ["flex"] = new ServiceTierRule { Multiplier = 0.5m },
            ["priority"] = new ServiceTierRule { Prices = P(("input", 2.5m), ("cache_read", 0.25m), ("output", 20m)) },
        },
    };

    private static NormalizedUsage GptUsage(string? tier = null)
    {
        var u = UsageNormalizer.Normalize(ApiProtocol.OpenAIResponses, System.Text.Json.Nodes.JsonNode.Parse("""
            {"input_tokens":10000,"input_tokens_details":{"cached_tokens":4000},
             "output_tokens":2000,"output_tokens_details":{"reasoning_tokens":1500}}
            """), tier)!;
        return u;
    }

    [Fact]
    public void Gpt_cached_and_reasoning_are_split_and_reasoning_falls_back_to_output_price()
    {
        var u = GptUsage();
        Assert.Equal(6000, u.Get("input"));
        Assert.Equal(4000, u.Get("cache_read"));
        Assert.Equal(500, u.Get("output"));
        Assert.Equal(1500, u.Get("reasoning"));
        var r = BillingEngine.Calculate(u, Gpt(), Ctx());
        Assert.Equal(28_000_000, r.TotalNanos);
        var reasoning = r.Items.Single(i => i.TokenType == "reasoning");
        Assert.Equal("output", reasoning.PricedAs);
        Assert.Equal("fallback:output", reasoning.Note);
    }

    [Theory]
    [InlineData("flex", 14_000_000)]
    [InlineData("priority", 56_000_000)]
    [InlineData("default", 28_000_000)]
    [InlineData("scale", 28_000_000)]
    public void Gpt_service_tiers(string tier, long expected)
    {
        var r = BillingEngine.Calculate(GptUsage(tier), Gpt(), Ctx());
        Assert.Equal(expected, r.TotalNanos);
        if (tier == "scale") Assert.Contains(r.Trace, s => s.Code == "service_tier_unmatched");
    }

    private static TimeWindowRule Window(string tz, string start, string end, List<int>? days = null, List<string>? exclude = null) =>
        new() { Name = "w", Timezone = tz, Start = start, End = end, Days = days, ExcludeDates = exclude, Multiplier = 2m };

    [Theory]
    [InlineData("2026-10-05T16:30:00Z", true)]   // 00:30 Shanghai: start is inclusive
    [InlineData("2026-10-05T16:29:00Z", false)]  // 00:29 Shanghai
    [InlineData("2026-10-06T00:29:00Z", true)]   // 08:29 Shanghai
    [InlineData("2026-10-06T00:30:00Z", false)]  // 08:30 Shanghai: end is exclusive
    public void Time_window_uses_its_timezone_and_is_half_open(string utc, bool expected) =>
        Assert.Equal(expected, Window("Asia/Shanghai", "00:30", "08:30").Contains(DateTimeOffset.Parse(utc)));

    [Theory]
    [InlineData("2026-10-05T15:00:00Z", true)]   // Mon 23:00 Shanghai
    [InlineData("2026-10-05T17:00:00Z", true)]   // Tue 01:00 Shanghai, window started Monday
    [InlineData("2026-10-06T15:00:00Z", false)]  // Tue 23:00 Shanghai, window starts Tuesday
    [InlineData("2026-10-04T17:00:00Z", false)]  // Mon 01:00 Shanghai, window started Sunday
    [InlineData("2026-10-05T18:00:00Z", false)]  // Tue 02:00 Shanghai: end exclusive
    public void Cross_midnight_window_is_keyed_by_its_start_day(string utc, bool expected) =>
        Assert.Equal(expected, Window("Asia/Shanghai", "22:00", "02:00", days: [1]).Contains(DateTimeOffset.Parse(utc)));

    [Theory]
    [InlineData("2026-10-06T02:00:00Z", false)]  // excluded holiday
    [InlineData("2026-10-07T02:00:00Z", true)]   // Wednesday
    [InlineData("2026-10-10T02:00:00Z", false)]  // Saturday
    public void Window_weekdays_and_excluded_dates(string utc, bool expected) =>
        Assert.Equal(expected, Window("UTC", "01:00", "04:00", days: [1, 2, 3, 4, 5], exclude: ["2026-10-06"])
            .Contains(DateTimeOffset.Parse(utc)));

    private static PricingSchedule DeepSeek() => new()
    {
        Base = P(("input", 0.15m), ("cache_read", 0.003m), ("output", 0.6m)),
        TimeWindows =
        [
            new TimeWindowRule { Name = "peak-01-04", Timezone = "UTC", Start = "01:00", End = "04:00", Days = [1, 2, 3, 4, 5], Multiplier = 2m },
            new TimeWindowRule { Name = "peak-06-10", Timezone = "UTC", Start = "06:00", End = "10:00", Days = [1, 2, 3, 4, 5], Multiplier = 2m },
        ],
    };

    [Theory]
    [InlineData("2026-10-06T02:00:00Z", 1_500_000_000)]
    [InlineData("2026-10-06T07:00:00Z", 1_500_000_000)]
    [InlineData("2026-10-06T05:00:00Z", 750_000_000)]
    [InlineData("2026-10-10T02:00:00Z", 750_000_000)]
    public void DeepSeek_peak_windows_double_the_off_peak_price(string utc, long expected)
    {
        var r = BillingEngine.Calculate(U(("input", 1_000_000), ("output", 1_000_000)), DeepSeek(), Ctx(DateTimeOffset.Parse(utc)));
        Assert.Equal(expected, r.TotalNanos);
    }

    [Fact]
    public void Deepseek_chat_usage_maps_cache_hit_and_miss()
    {
        var u = UsageNormalizer.Normalize(ApiProtocol.OpenAIChat, System.Text.Json.Nodes.JsonNode.Parse("""
            {"prompt_tokens":1000,"completion_tokens":300,"prompt_cache_hit_tokens":800,"prompt_cache_miss_tokens":200,
             "completion_tokens_details":{"reasoning_tokens":100}}
            """))!;
        Assert.Equal(800, u.Get("cache_read"));
        Assert.Equal(200, u.Get("input"));
        Assert.Equal(100, u.Get("reasoning"));
        Assert.Equal(200, u.Get("output"));
    }

    [Fact]
    public void Provider_multiplier_applies_to_system_price_but_not_to_override()
    {
        var pricing = new PricingSchedule { Base = P(("input", 1m), ("output", 2m)) };
        var usage = U(("input", 1_000_000), ("output", 1_000_000));
        Assert.Equal(2_400_000_000, BillingEngine.Calculate(usage, pricing, Ctx(multiplier: 0.8m)).TotalNanos);
        Assert.Equal(3_000_000_000, BillingEngine.Calculate(usage, pricing, Ctx(multiplier: 0.8m, source: PricingSource.ProviderOverride)).TotalNanos);
    }

    [Fact]
    public void Pricing_is_resolved_per_provider()
    {
        var system = new PricingSchedule { Base = P(("input", 1m), ("output", 1m)) };
        var siliconflow = new PricingSchedule { Base = P(("input", 2m), ("output", 2m)) };
        var over = new PricingSchedule { Base = P(("input", 5m), ("output", 5m)) };
        var prices = new Dictionary<string, PricingSchedule> { ["siliconflow"] = siliconflow };

        var r1 = PricingResolver.Resolve(null, prices, "siliconflow", system, 0.5m);
        Assert.Equal(PricingSource.ProviderPrice, r1.Source);
        Assert.Same(siliconflow, r1.Schedule);
        Assert.Equal(0.5m, r1.Multiplier);

        var r2 = PricingResolver.Resolve(null, prices, "openrouter", system, 1m);
        Assert.Equal(PricingSource.SystemDefault, r2.Source);
        Assert.Same(system, r2.Schedule);

        var r3 = PricingResolver.Resolve(over, prices, "siliconflow", system, 0.5m);
        Assert.Equal(PricingSource.ProviderOverride, r3.Source);
        Assert.Equal(1m, r3.Multiplier);

        Assert.Equal(PricingSource.None, PricingResolver.Resolve(null, null, null, null, 1m).Source);

        var usage = U(("input", 1_000_000), ("output", 1_000_000));
        var bill = BillingEngine.Calculate(usage, r1.Schedule, BillingContext.From(r1, Noon, "deepseek-v4-pro", "SiliconFlow"));
        Assert.Equal(2_000_000_000, bill.TotalNanos);
        Assert.Contains("提供商 \"siliconflow\"", bill.Description);
    }

    [Fact]
    public void Missing_pricing_records_zero_with_note()
    {
        var r = BillingEngine.Calculate(U(("input", 10), ("output", 5)), null, Ctx());
        Assert.False(r.Priced);
        Assert.Equal(0, r.TotalNanos);
        Assert.All(r.Items, i => Assert.Equal("no_pricing", i.Note));
        Assert.Contains("未配置价格", r.Description);
    }

    [Fact]
    public void Missing_cache_price_falls_back_to_input()
    {
        var r = BillingEngine.Calculate(U(("cache_read", 1000), ("cache_write_1h", 1000)),
            new PricingSchedule { Base = P(("input", 2m), ("output", 4m)) }, Ctx(locale: "en"));
        Assert.All(r.Items, i => Assert.Equal("input", i.PricedAs));
        Assert.Equal(4_000_000, r.TotalNanos);
        Assert.Contains("cache_read has no price; priced as input", r.Description);
    }

    [Fact]
    public void Costs_round_to_nano_usd_away_from_zero()
    {
        var r1 = BillingEngine.Calculate(U(("input", 3)), new PricingSchedule { Base = P(("input", 0.1234567m)) }, Ctx());
        Assert.Equal(370, r1.TotalNanos); // 370.3701 nanos
        var r2 = BillingEngine.Calculate(U(("input", 1)), new PricingSchedule { Base = P(("input", 0.0005m)) }, Ctx());
        Assert.Equal(1, r2.TotalNanos);   // 0.5 nanos → 1
    }

    [Fact]
    public void Per_call_items_and_per_request_fee()
    {
        var usage = U(("input", 1000));
        usage.Calls["web_search_call"] = 3;
        var pricing = new PricingSchedule { Base = P(("input", 1m)), PerRequest = 0.001m };
        pricing.Base.PerCall["web_search_call"] = 0.01m;
        var r = BillingEngine.Calculate(usage, pricing, Ctx());
        Assert.Equal(1_000_000 + 30_000_000 + 1_000_000, r.TotalNanos);
    }

    [Fact]
    public void Pricing_json_round_trips_in_snake_case()
    {
        var json = """
            {"currency":"USD","unit":"per_1m_tokens","base":{"input":3,"cache_write_1h":6,"reasoning":null,"per_call":{"web_search_call":0.01}},
             "context_tiers":[{"name":">200K","threshold_input_tokens":200000,"mode":"whole","output_policy":"highest","prices":{"input":6}}],
             "time_windows":[{"name":"n","timezone":"Asia/Shanghai","start":"00:30","end":"08:30","days":null,"exclude_dates":["2026-10-01"],"multiplier":0.5}]}
            """;
        var p = Json.Deserialize<PricingSchedule>(json)!;
        Assert.Equal(6m, p.Base["cache_write_1h"]);
        Assert.Null(p.Base["reasoning"]);
        Assert.Equal(0.01m, p.Base.PerCall["web_search_call"]);
        Assert.Equal(200_000, p.ContextTiers![0].ThresholdInputTokens);
        Assert.Equal(["2026-10-01"], p.TimeWindows![0].ExcludeDates!);
        Assert.Empty(p.Validate());
        var again = Json.Deserialize<PricingSchedule>(Json.Serialize(p))!;
        Assert.Equal(Json.Serialize(p), Json.Serialize(again));
    }
}
