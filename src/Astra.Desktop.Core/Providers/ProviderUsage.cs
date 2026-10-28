using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Providers;

/// <summary>One provider's last-week requests per local day (oldest first, aligned with the day list) and cost.</summary>
/// <param name="Requests">Requests per day.</param>
/// <param name="CostUsd">Cost over the window.</param>
public sealed record ProviderUsage(int[] Requests, decimal CostUsd)
{
    /// <summary>Sum of <see cref="Requests"/>.</summary>
    public long Total => Requests.Sum(r => (long)r);
}

/// <summary>
/// Buckets /api/stats/timeseries (range 7d, day, by provider) into per-provider cards. The endpoint keys provider series by
/// the logged provider name (not id), so cards look their usage up by <c>Name</c> (ProvidersPage.tsx <c>usage</c> memo).
/// </summary>
public static class ProviderUsageBuckets
{
    /// <summary>Days shown by the card sparkline (web <c>USAGE_DAYS</c>).</summary>
    public const int Days = 7;

    /// <summary>Groups the points by provider key over <paramref name="days"/>; points outside the window are ignored.</summary>
    public static Dictionary<string, ProviderUsage> Build(IEnumerable<TimeseriesPointDto>? series, IReadOnlyList<string> days)
    {
        var map = new Dictionary<string, ProviderUsage>(StringComparer.Ordinal);
        foreach (var pt in series ?? [])
        {
            var i = days.ToList().IndexOf(pt.Bucket);
            if (i < 0) continue;
            if (!map.TryGetValue(pt.Key, out var u)) map[pt.Key] = u = new ProviderUsage(new int[days.Count], 0);
            u.Requests[i] += (int)pt.Requests;
            map[pt.Key] = u with { CostUsd = u.CostUsd + pt.CostUsd };
        }
        return map;
    }
}
