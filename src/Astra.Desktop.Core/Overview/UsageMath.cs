using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>Disjoint counters for display (<c>splitUsage</c>): cached input and reasoning output are already in the totals.</summary>
public sealed record UsageSplit(
    bool Reported, long Input, long Output, long Read, long Write, long Reasoning, long UncachedInput, long RegularOutput, double? HitRatio);

/// <summary>Prompt-cache math shared by the overview, token and request views (<c>web/src/lib/usage.ts</c>).</summary>
public static class UsageMath
{
    /// <summary>Prompt-cache hits as a share of ALL input tokens (including cache reads and writes); null when it is not defined (<c>cacheHitRatio</c>).</summary>
    public static double? CacheHitRatio(long cacheRead, long totalInput)
    {
        if (totalInput <= 0 || cacheRead < 0) return null;
        return Math.Min((double)cacheRead / totalInput, 1);
    }

    /// <summary>Splits one request's usage into display counters; hit ratio only for reported usage (<c>splitUsage</c>).</summary>
    public static UsageSplit Split(RequestSummaryDto r) =>
        Split(r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens, r.UsageSource);

    /// <summary>Splits raw counters (a detail view's too).</summary>
    public static UsageSplit Split(long totalInput, long totalOutput, long cacheRead, long cacheWrite, long reasoning, string usageSource)
    {
        var input = Math.Max(0, totalInput);
        var output = Math.Max(0, totalOutput);
        var read = Math.Max(0, cacheRead);
        var write = Math.Max(0, cacheWrite);
        var reason = Math.Max(0, reasoning);
        var reported = usageSource == "reported";
        return new UsageSplit(reported, input, output, read, write, reason, Math.Max(0, input - read - write), Math.Max(0, output - reason),
            reported ? CacheHitRatio(read, input) : null);
    }
}

/// <summary>The labels of the usage views (<c>web/src/components/TokenUsage.tsx</c>).</summary>
public static class UsageLabels
{
    private static readonly Dictionary<string, string> TokenLabels = new(StringComparer.Ordinal)
    {
        ["input"] = "usage.input",
        ["cache_read"] = "usage.cacheRead",
        ["cache_write_5m"] = "usage.cacheWrite5m",
        ["cache_write_30m"] = "usage.cacheWrite30m",
        ["cache_write_1h"] = "usage.cacheWrite1h",
        ["cache_write_12h"] = "usage.cacheWrite12h",
        ["output"] = "usage.output",
        ["reasoning"] = "usage.reasoning",
        ["input_audio"] = "usage.inputAudio",
        ["output_audio"] = "usage.outputAudio",
        ["input_image"] = "usage.inputImage",
        ["output_image"] = "usage.outputImage",
    };

    /// <summary>The i18n key naming a billing token type, or null for a type the catalog does not know (shown as is).</summary>
    public static string? TokenTypeKey(string type) => TokenLabels.GetValueOrDefault(type);

    /// <summary>The kind of emphasis a token type gets: cache reads green, cache writes orange.</summary>
    public static string? TokenTypeTone(string type) => type == "cache_read" ? "green" : type.StartsWith("cache_write_", StringComparison.Ordinal) ? "orange" : null;
}
