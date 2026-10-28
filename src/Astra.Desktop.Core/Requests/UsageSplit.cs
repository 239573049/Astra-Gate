using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// Disjoint token counters for display only: cached input and reasoning output are already in the totals (port of
/// <c>splitUsage</c> in <c>web/src/lib/usage.ts</c>). Missing usage is NOT a 0% cache hit: <see cref="HitRatio"/> is null then.
/// </summary>
public sealed record UsageSplit(bool Reported, long Input, long Output, long Read, long Write, long Reasoning, long UncachedInput, long RegularOutput, double? HitRatio)
{
    private static long Count(long n) => Math.Max(0, n);

    /// <summary>Splits a request's counters.</summary>
    public static UsageSplit Of(long totalInput, long totalOutput, long cacheRead, long cacheWrite, long reasoning, string usageSource)
    {
        var input = Count(totalInput);
        var output = Count(totalOutput);
        var read = Count(cacheRead);
        var write = Count(cacheWrite);
        var think = Count(reasoning);
        var reported = usageSource == "reported";
        return new UsageSplit(reported, input, output, read, write, think, Math.Max(0, input - read - write), Math.Max(0, output - think),
            reported ? CacheHitRatio(read, input) : null);
    }

    /// <summary>Of a list row.</summary>
    public static UsageSplit Of(RequestSummaryDto r) => Of(r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens, r.UsageSource);

    /// <summary>Of a detail.</summary>
    public static UsageSplit Of(RequestDetailDto r) => Of(r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens, r.UsageSource);

    /// <summary>Prompt-cache hits as a share of ALL input tokens, including cache reads and writes (web <c>cacheHitRatio</c>).</summary>
    public static double? CacheHitRatio(long cacheRead, long totalInput)
        => totalInput <= 0 || cacheRead < 0 ? null : Math.Min((double)cacheRead / totalInput, 1);
}
