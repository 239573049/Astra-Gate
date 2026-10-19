using Astra.Core.Requests;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The generation time and output speed of a streamed answer — what the request log shows as 生成耗时 / 输出速度 and
/// what the token stats weight into TPS. One rule for the gateway and the connection test.
/// </summary>
public static class GenerationTiming
{
    /// <summary>
    /// Faster than this over the span the stream itself took (first to last output) is not decoding speed but a
    /// buffered answer being replayed: no model streams a long answer at this rate, and a fast one that does is
    /// only ever withheld when its headers also came later than everything after them (see <see cref="Apply"/>).
    /// </summary>
    public const double ReplayTokensPerSecond = 1000;

    /// <summary>
    /// Sets <see cref="RequestRecord.GenerationMs"/> and <see cref="RequestRecord.OutputTps"/> for a stream whose
    /// first token was seen. <paramref name="generated"/> is every token billed as output (reasoning included);
    /// <paramref name="lastOutputMs"/> is when the last output event arrived.
    /// <para>
    /// Generation runs from the response start (TTFB), not the first visible token: hidden reasoning (e.g.
    /// Anthropic thinking with display "omitted") is billed as output but streams nothing until it is done, so
    /// timing from the first token would divide those tokens by the last few ms. A streamed first token is still
    /// required — a whole body's headers arrive only after the generation, leaving nothing to time.
    /// </para>
    /// <para>
    /// The same window is wrong the other way round when the headers come late — a relay that holds them until it
    /// has the whole answer and then replays it as a stream: the generation happened before the window opened,
    /// and the window holds only the replay (271 tokens in 218 ms is "1243 tok/s"). When the wait for the headers
    /// is longer than everything after them, only the stream's own pace can vouch for the speed; an answer that
    /// arrived faster than <see cref="ReplayTokensPerSecond"/> across its own span has none, so generation time
    /// and speed stay unset (TTFT is still real — it is when the client saw its first token).
    /// </para>
    /// </summary>
    public static void Apply(RequestRecord record, long generated, long? lastOutputMs)
    {
        if (record.TtftMs is not { } ttft || record.TtfbMs is not { } ttfb || record.TotalMs is not { } total || total <= ttfb) return;
        var generation = total - ttfb;
        if (generated > 0 && ttfb > generation && Replayed(generated, ttft, lastOutputMs)) return;
        record.GenerationMs = generation;
        if (generated > 0) record.OutputTps = Math.Round(generated / (generation / 1000.0), 2);
    }

    private static bool Replayed(long generated, long firstOutputMs, long? lastOutputMs)
    {
        var span = (lastOutputMs ?? firstOutputMs) - firstOutputMs;
        return span <= 0 || generated * 1000.0 / span > ReplayTokensPerSecond;
    }
}
