using System.Text.Json.Nodes;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Seed;

namespace Astra.Core.Tests;

public class UsageNormalizerTests
{
    private static NormalizedUsage N(ApiProtocol p, string json) => UsageNormalizer.Normalize(p, JsonNode.Parse(json))!;

    [Fact]
    public void Openai_chat_subtracts_cached_audio_and_reasoning()
    {
        var u = N(ApiProtocol.OpenAIChat, """
            {"prompt_tokens":2000,"completion_tokens":800,
             "prompt_tokens_details":{"cached_tokens":1500,"audio_tokens":100},
             "completion_tokens_details":{"reasoning_tokens":600,"audio_tokens":50}}
            """);
        Assert.Equal(400, u.Get("input"));
        Assert.Equal(1500, u.Get("cache_read"));
        Assert.Equal(100, u.Get("input_audio"));
        Assert.Equal(600, u.Get("reasoning"));
        Assert.Equal(50, u.Get("output_audio"));
        Assert.Equal(150, u.Get("output"));
    }

    [Fact]
    public void Openai_chat_subtracts_cache_writes_alongside_cached_and_multimodal_input()
    {
        var u = N(ApiProtocol.OpenAIChat, """
            {"prompt_tokens":2000,"completion_tokens":800,
             "prompt_tokens_details":{"cached_tokens":1500,"cache_write_tokens":200,"audio_tokens":100,"image_tokens":50},
             "completion_tokens_details":{"reasoning_tokens":600,"audio_tokens":50}}
            """);
        Assert.Equal(150, u.Get("input"));
        Assert.Equal(1500, u.Get("cache_read"));
        Assert.Equal(200, u.Get("cache_write_5m"));
        Assert.Equal(100, u.Get("input_audio"));
        Assert.Equal(50, u.Get("input_image"));
        Assert.Equal(2000, u.TotalInput);
        Assert.Equal(800, u.TotalOutput);
    }

    [Fact]
    public void Deepseek_cache_miss_excludes_reported_cache_writes()
    {
        var u = N(ApiProtocol.OpenAIChat, """
            {"prompt_tokens":2000,"prompt_cache_hit_tokens":1500,"prompt_cache_miss_tokens":500,
             "prompt_tokens_details":{"cache_write_tokens":200},"completion_tokens":8}
            """);
        Assert.Equal(300, u.Get("input"));
        Assert.Equal(1500, u.Get("cache_read"));
        Assert.Equal(200, u.Get("cache_write_5m"));
        Assert.Equal(2000, u.TotalInput);
    }

    [Fact]
    public void Anthropic_splits_cache_creation_by_ttl()
    {
        var u = N(ApiProtocol.Anthropic, """
            {"input_tokens":50,"cache_read_input_tokens":1000,"cache_creation_input_tokens":300,
             "cache_creation":{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":200},"output_tokens":70,
             "server_tool_use":{"web_search_requests":2}}
            """);
        Assert.Equal(50, u.Get("input"));
        Assert.Equal(1000, u.Get("cache_read"));
        Assert.Equal(100, u.Get("cache_write_5m"));
        Assert.Equal(200, u.Get("cache_write_1h"));
        Assert.Equal(70, u.Get("output"));
        Assert.Equal(2, u.Calls["web_search_call"]);
        Assert.Empty(u.Notes);
    }

    [Fact]
    public void Anthropic_unsplit_cache_creation_falls_back_to_5m_with_note()
    {
        var u = N(ApiProtocol.Anthropic, """{"input_tokens":10,"cache_creation_input_tokens":100,"output_tokens":5}""");
        Assert.Equal(100, u.Get("cache_write_5m"));
        Assert.Equal(0, u.Get("cache_write_1h"));
        Assert.Contains(UsageNotes.AnthropicCacheWriteUnsplit, u.Notes);
        var bill = BillingEngine.Calculate(u, new PricingSchedule(), new BillingContext());
        Assert.Contains("缓存写入全部按 5 分钟缓存计", bill.Description);
    }

    [Fact]
    public void Gemini_maps_cached_thoughts_and_tool_prompt()
    {
        var u = N(ApiProtocol.Gemini, """
            {"promptTokenCount":1200,"cachedContentTokenCount":1000,"candidatesTokenCount":90,
             "thoughtsTokenCount":40,"toolUsePromptTokenCount":30,"totalTokenCount":1360}
            """);
        Assert.Equal(230, u.Get("input"));
        Assert.Equal(1000, u.Get("cache_read"));
        Assert.Equal(90, u.Get("output"));
        Assert.Equal(40, u.Get("reasoning"));
    }

    [Fact]
    public void MergeMax_is_idempotent_for_repeated_stream_usage()
    {
        var a = N(ApiProtocol.Anthropic, """{"input_tokens":10,"output_tokens":1}""");
        var b = N(ApiProtocol.Anthropic, """{"input_tokens":10,"output_tokens":42}""");
        a.MergeMax(b);
        a.MergeMax(b);
        Assert.Equal(10, a.Get("input"));
        Assert.Equal(42, a.Get("output"));
    }
}

public class ModelTests
{
    [Theory]
    [InlineData("deepseek-ai/DeepSeek-V4-Pro", "deepseek-v4-pro")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("anthropic/claude-sonnet-4.6", "claude-sonnet-4-6")]
    [InlineData("claude-sonnet-4-6@default", "claude-sonnet-4-6")]
    [InlineData("~anthropic/claude-opus-latest", "claude-opus")]
    public void Normalize_model_ids(string input, string expected) => Assert.Equal(expected, ModelIdMatcher.Normalize(input));

    [Fact]
    public void Matcher_prefers_exact_then_index_then_alias_then_normalized()
    {
        var candidates = new[]
        {
            new ModelIdMatcher.Candidate("claude-sonnet-4-6", []),
            new ModelIdMatcher.Candidate("gpt-4o", ["gpt-4o-2024-08-06"]),
        };
        Assert.Equal("gpt-4o", ModelIdMatcher.Match("GPT-4o", candidates));
        Assert.Equal("gpt-4o", ModelIdMatcher.Match("gpt-4o-2024-08-06", candidates));
        Assert.Equal("claude-sonnet-4-6", ModelIdMatcher.Match("anthropic/claude-sonnet-4.6", candidates));
        Assert.Equal("gpt-4o", ModelIdMatcher.Match("weird-id", candidates, new Dictionary<string, string> { ["weird-id"] = "gpt-4o" }));
        Assert.Null(ModelIdMatcher.Match("llama-3", candidates));
    }

    [Fact]
    public void Effective_model_inherits_and_overrides_per_field_with_provider_scoped_pricing()
    {
        var system = new SystemModel
        {
            Id = "deepseek-v4-pro", DisplayName = "DeepSeek V4 Pro", Vendor = "deepseek", ContextWindow = 1_000_000,
            MaxOutputTokens = 384_000, Capabilities = new ModelCapabilities { Tools = true, Reasoning = true },
            Pricing = new PricingSchedule(),
        };
        var sfPrice = new PricingSchedule();
        var provider = new Provider { Id = "p1", TemplateId = "siliconflow", PriceMultiplier = 0.9m };
        var pm = new ProviderModel
        {
            ProviderId = "p1", ModelId = "deepseek-ai/DeepSeek-V4-Pro", SystemModelId = system.Id,
            Overrides = new ModelOverrides { MaxOutputTokens = 32_000, Capabilities = new ModelCapabilities { Vision = true } },
        };
        var eff = ModelMerger.Merge(provider, pm, system, new Dictionary<string, PricingSchedule> { ["siliconflow"] = sfPrice });

        Assert.Equal("DeepSeek V4 Pro", eff.DisplayName);
        Assert.Equal(1_000_000, eff.ContextWindow);
        Assert.Equal(32_000, eff.MaxOutputTokens);
        Assert.True(eff.Capabilities.Tools);
        Assert.True(eff.Capabilities.Vision);
        Assert.Equal(FieldOrigin.Inherited, eff.Origins["contextWindow"]);
        Assert.Equal(FieldOrigin.Overridden, eff.Origins["maxOutputTokens"]);
        Assert.Equal(PricingSource.ProviderPrice, eff.Pricing.Source);
        Assert.Same(sfPrice, eff.Pricing.Schedule);
        Assert.Equal(0.9m, eff.Pricing.Multiplier);

        // A provider without a provider-scoped price falls back to the official default.
        var other = ModelMerger.Merge(new Provider { Id = "p2", TemplateId = "custom-openai" }, pm, system, null);
        Assert.Equal(PricingSource.SystemDefault, other.Pricing.Source);

        // No system model link: only overrides, no pricing.
        var bare = ModelMerger.Merge(provider, new ProviderModel { ModelId = "x" }, null, null);
        Assert.Equal("x", bare.DisplayName);
        Assert.Equal(PricingSource.None, bare.Pricing.Source);
        Assert.Equal(FieldOrigin.Unset, bare.Origins["pricing"]);
    }
}

public class SeedTests
{
    [Fact]
    public void Seed_loads_with_valid_unique_models_and_provider_prices()
    {
        var seed = SeedCatalog.Current;
        Assert.True(seed.SeedVersion >= 1);
        Assert.True(seed.Models.Count > 50);
        Assert.Equal(seed.Models.Count, seed.Models.Select(m => m.Id).Distinct().Count());
        foreach (var m in seed.Models)
        {
            if (m.Pricing is not null) Assert.True(m.Pricing.Validate().Count == 0, $"{m.Id}: {string.Join(", ", m.Pricing.Validate())}");
            foreach (var (key, pp) in m.ProviderPricing)
                Assert.True(pp.Pricing.Validate().Count == 0, $"{m.Id}@{key}: {string.Join(", ", pp.Pricing.Validate())}");
        }
    }

    [Fact]
    public void Seed_contains_vendor_specific_rules()
    {
        var seed = SeedCatalog.Current;
        var deepseek = seed.Models.Where(m => m.Vendor == "deepseek").ToList();
        Assert.NotEmpty(deepseek);
        Assert.All(deepseek, m => Assert.Equal(2, m.Pricing!.TimeWindows!.Count));
        // Third-party DeepSeek hosting does not inherit DeepSeek's peak windows.
        Assert.Contains(deepseek, m => m.ProviderPricing.Count > 0);
        Assert.All(deepseek.SelectMany(m => m.ProviderPricing.Values), pp => Assert.Null(pp.Pricing.TimeWindows));

        var claude = seed.Models.Where(m => m.Vendor == "anthropic").ToList();
        Assert.NotEmpty(claude);
        Assert.All(claude, m => Assert.NotNull(m.Pricing!.Base["cache_write_1h"]));
        Assert.Contains(seed.Models, m => m.Pricing?.ContextTiers is { Count: > 0 });
        Assert.Contains(seed.Models, m => m.Pricing?.ServiceTiers?.ContainsKey("priority") == true);
    }
}
