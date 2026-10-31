using System.Text.Json.Nodes;

namespace Astra.Core.Billing;

/// <summary>Usage note codes recorded on <see cref="NormalizedUsage.Notes"/>.</summary>
public static class UsageNotes
{
    public const string AnthropicCacheWriteUnsplit = "anthropic_cache_write_unsplit";
    public const string OpenAICacheWriteUnsplit = "openai_cache_write_unsplit";
    public const string AnthropicThinkingInOutput = "anthropic_thinking_in_output";
    public const string UsageMissing = "usage_missing";

    public static string Describe(string code, bool zh) => code switch
    {
        AnthropicCacheWriteUnsplit => zh
            ? "上游只返回了 cache_creation_input_tokens，缓存写入全部按 5 分钟缓存计"
            : "Upstream reported only cache_creation_input_tokens; all cache writes billed as 5-minute cache",
        OpenAICacheWriteUnsplit => zh
            ? "上游返回的 cache_write_tokens 未区分缓存时长，缓存写入全部按 5 分钟缓存计"
            : "Upstream cache_write_tokens did not specify a cache TTL; all cache writes billed as 5-minute cache",
        AnthropicThinkingInOutput => zh
            ? "Anthropic 的 thinking token 包含在 output 中，无法单独拆分"
            : "Anthropic thinking tokens are included in output and cannot be split",
        UsageMissing => zh ? "上游未返回 usage，费用记为 0" : "Upstream returned no usage; cost recorded as 0",
        _ => code,
    };
}

/// <summary>Converts each protocol's usage object into disjoint token categories (see plan §4.2).</summary>
public static class UsageNormalizer
{
    public static NormalizedUsage? Normalize(ApiProtocol protocol, JsonNode? usage, string? serviceTier = null)
    {
        if (usage is not JsonObject obj) return null;
        var result = protocol switch
        {
            ApiProtocol.OpenAIChat => FromOpenAIChat(obj),
            ApiProtocol.OpenAIResponses => FromOpenAIResponses(obj),
            ApiProtocol.Anthropic => FromAnthropic(obj),
            ApiProtocol.Gemini => FromGemini(obj),
            _ => null,
        };
        if (result is not null && serviceTier is not null) result.ServiceTier = serviceTier;
        return result;
    }

    public static NormalizedUsage FromOpenAIChat(JsonObject u)
    {
        var usage = new NormalizedUsage();
        var prompt = L(u, "prompt_tokens");
        var completion = L(u, "completion_tokens");
        var promptDetails = u["prompt_tokens_details"] as JsonObject;
        var completionDetails = u["completion_tokens_details"] as JsonObject;

        long cached;
        long cacheMiss = -1;
        if (u.ContainsKey("prompt_cache_hit_tokens"))
        {
            // DeepSeek style: explicit hit / miss counters.
            cached = L(u, "prompt_cache_hit_tokens");
            cacheMiss = L(u, "prompt_cache_miss_tokens");
        }
        else
        {
            cached = L(promptDetails, "cached_tokens");
        }
        var inputAudio = L(promptDetails, "audio_tokens");
        var inputImage = L(promptDetails, "image_tokens");
        var cacheWrite = AddOpenAICacheWrite(usage, promptDetails);
        var reasoning = L(completionDetails, "reasoning_tokens");
        var outputAudio = L(completionDetails, "audio_tokens");
        var outputImage = L(completionDetails, "image_tokens");

        usage.Add(TokenTypes.CacheRead, cached);
        usage.Add(TokenTypes.Input, Math.Max(0, (cacheMiss >= 0 ? cacheMiss : prompt - cached - inputAudio - inputImage) - cacheWrite));
        usage.Add(TokenTypes.InputAudio, inputAudio);
        usage.Add(TokenTypes.InputImage, inputImage);
        usage.Add(TokenTypes.Reasoning, reasoning);
        usage.Add(TokenTypes.OutputAudio, outputAudio);
        usage.Add(TokenTypes.OutputImage, outputImage);
        usage.Add(TokenTypes.Output, Math.Max(0, completion - reasoning - outputAudio - outputImage));
        return usage;
    }

    public static NormalizedUsage FromOpenAIResponses(JsonObject u)
    {
        var usage = new NormalizedUsage();
        var input = L(u, "input_tokens");
        var output = L(u, "output_tokens");
        var inDetails = u["input_tokens_details"] as JsonObject;
        var outDetails = u["output_tokens_details"] as JsonObject;
        var cached = L(inDetails, "cached_tokens");
        var cacheWrite = AddOpenAICacheWrite(usage, inDetails);
        var reasoning = L(outDetails, "reasoning_tokens");
        usage.Add(TokenTypes.CacheRead, cached);
        usage.Add(TokenTypes.Input, Math.Max(0, input - cached - cacheWrite));
        usage.Add(TokenTypes.Reasoning, reasoning);
        usage.Add(TokenTypes.Output, Math.Max(0, output - reasoning));
        return usage;
    }

    /// <summary>
    /// OpenAI Images API usage (gpt-image-*; issue #17): <c>input_tokens</c> split by <c>input_tokens_details</c> into
    /// image and text tokens (cached ones read from the cache), <c>output_tokens</c> are image tokens unless
    /// <c>output_tokens_details</c> says how many of them are text. Image tokens fall back to the input / output price
    /// when the model has no image price (<see cref="TokenTypes.FallbackOf"/>).
    /// </summary>
    public static NormalizedUsage FromOpenAIImages(JsonObject u)
    {
        var usage = new NormalizedUsage();
        var input = Math.Max(0, L(u, "input_tokens"));
        var output = Math.Max(0, L(u, "output_tokens"));
        var inDetails = u["input_tokens_details"] as JsonObject;
        var outDetails = u["output_tokens_details"] as JsonObject;
        var cached = Math.Clamp(L(inDetails, "cached_tokens"), 0, input);
        var inputImage = Math.Clamp(L(inDetails, "image_tokens"), 0, input - cached);
        var outputImage = outDetails is not null && outDetails.ContainsKey("image_tokens")
            ? Math.Clamp(L(outDetails, "image_tokens"), 0, output)
            : output;
        usage.Add(TokenTypes.CacheRead, cached);
        usage.Add(TokenTypes.Input, Math.Max(0, input - cached - inputImage));
        usage.Add(TokenTypes.InputImage, inputImage);
        usage.Add(TokenTypes.Output, Math.Max(0, output - outputImage));
        usage.Add(TokenTypes.OutputImage, outputImage);
        return usage;
    }

    public static NormalizedUsage FromAnthropic(JsonObject u)
    {
        var usage = new NormalizedUsage();
        usage.Add(TokenTypes.Input, L(u, "input_tokens"));
        usage.Add(TokenTypes.CacheRead, L(u, "cache_read_input_tokens"));
        var creation = u["cache_creation"] as JsonObject;
        var w5 = L(creation, "ephemeral_5m_input_tokens");
        var w1 = L(creation, "ephemeral_1h_input_tokens");
        var total = L(u, "cache_creation_input_tokens");
        if (creation is not null && (w5 > 0 || w1 > 0 || total == 0))
        {
            usage.Add(TokenTypes.CacheWrite5m, w5);
            usage.Add(TokenTypes.CacheWrite1h, w1);
        }
        else if (total > 0)
        {
            usage.Add(TokenTypes.CacheWrite5m, total);
            usage.Notes.Add(UsageNotes.AnthropicCacheWriteUnsplit);
        }
        usage.Add(TokenTypes.Output, L(u, "output_tokens"));
        if (u["server_tool_use"] is JsonObject stu)
        {
            var searches = L(stu, "web_search_requests");
            if (searches > 0) usage.Calls[TokenTypes.WebSearchCall] = searches;
        }
        if (u["service_tier"]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
            usage.ServiceTier = u["service_tier"]!.GetValue<string>();
        return usage;
    }

    public static NormalizedUsage FromGemini(JsonObject u)
    {
        var usage = new NormalizedUsage();
        var prompt = L(u, "promptTokenCount");
        var cached = L(u, "cachedContentTokenCount");
        usage.Add(TokenTypes.CacheRead, cached);
        usage.Add(TokenTypes.Input, Math.Max(0, prompt - cached) + L(u, "toolUsePromptTokenCount"));
        usage.Add(TokenTypes.Output, L(u, "candidatesTokenCount"));
        usage.Add(TokenTypes.Reasoning, L(u, "thoughtsTokenCount"));
        return usage;
    }

    /// <summary>
    /// Writes the cache-write lines of an OpenAI usage details object (<c>prompt_tokens_details</c> /
    /// <c>input_tokens_details</c>): the total, plus OpenAI's TTL split when the usage holds 30m or 12h writes.
    /// Anthropic 5m/1h writes have no OpenAI TTL and appear in the total only.
    /// </summary>
    public static void WriteOpenAICacheWrite(JsonObject details, NormalizedUsage usage)
    {
        var total = usage.CacheWriteTotal;
        if (total <= 0) return;
        details["cache_write_tokens"] = total;
        var w30 = usage.Get(TokenTypes.CacheWrite30m);
        var w12 = usage.Get(TokenTypes.CacheWrite12h);
        if (w30 > 0 || w12 > 0)
            details["cache_write_tokens_details"] = new JsonObject { ["ttl_30m_tokens"] = w30, ["ttl_12h_tokens"] = w12 };
    }

    private static long AddOpenAICacheWrite(NormalizedUsage usage, JsonObject? details)
    {
        // Cache writes are part of total input. OpenAI (GPT-5.6+) splits them by TTL in
        // cache_write_tokens_details (the two counts are disjoint and sum to cache_write_tokens);
        // OpenAI-compatible upstreams that send only the total get the 5m bucket and a note.
        var written = Math.Max(0, L(details, "cache_write_tokens"));
        if (details?["cache_write_tokens_details"] is JsonObject split)
        {
            var w30 = Math.Max(0, L(split, "ttl_30m_tokens"));
            var w12 = Math.Max(0, L(split, "ttl_12h_tokens"));
            usage.Add(TokenTypes.CacheWrite30m, w30);
            usage.Add(TokenTypes.CacheWrite12h, w12);
            // A total larger than the split (an unknown future TTL) keeps the rest as an unsplit write.
            var rest = written - w30 - w12;
            if (rest > 0)
            {
                usage.Add(TokenTypes.CacheWrite5m, rest);
                usage.Notes.Add(UsageNotes.OpenAICacheWriteUnsplit);
            }
            return Math.Max(written, w30 + w12);
        }
        if (written > 0)
        {
            usage.Add(TokenTypes.CacheWrite5m, written);
            usage.Notes.Add(UsageNotes.OpenAICacheWriteUnsplit);
        }
        return written;
    }

    private static long L(JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var node) || node is null) return 0;
        return node.GetValueKind() switch
        {
            // Wire-parsed numbers convert to anything; in-memory JsonValue.Create(int/long) only to its own type.
            System.Text.Json.JsonValueKind.Number => node.AsValue().TryGetValue<long>(out var l) ? l
                : node.AsValue().TryGetValue<int>(out var i) ? i
                : node.AsValue().TryGetValue<double>(out var d) ? (long)d : 0,
            System.Text.Json.JsonValueKind.String => long.TryParse(node.GetValue<string>(), out var v) ? v : 0,
            _ => 0,
        };
    }
}
