namespace Astra.Core.Routing;

/// <summary>The quota-aware ways to order accounts (a provider's <c>switch_mode</c>) or the members of a routing group.</summary>
public static class QuotaModes
{
    /// <summary>Of the candidates with room, the allowance renewing soonest goes first: what it has left is lost at the reset.</summary>
    public const string Smart = "smart";

    /// <summary>The most of its week left per hour until it renews goes first, so the least of a week is lost at its reset.</summary>
    public const string Pace = "pace";

    /// <summary>The least used first.</summary>
    public const string Usage = "usage";

    public static bool IsQuotaAware(string? mode) => mode is Smart or Pace or Usage;
}

/// <summary>An item to rank: a caller's key, what is known of its quota, how much it was sent lately, and its place in the given order.</summary>
public sealed record RankInput(string Key, QuotaStanding Standing, double Recent, int Index);

/// <summary>A ranked item and the reason it stands where it does (<see cref="RankReasons"/>), for the trace and the console.</summary>
public sealed record Ranked(string Key, string Reason, double? Used);

/// <summary>Why a candidate was placed first (<c>astra.pick.reason</c>, the console's "last decision").</summary>
public static class RankReasons
{
    public const string Order = "order";
    public const string SoonestReset = "soonest_reset";
    public const string Pace = "pace";
    public const string LeastUsed = "least_used";
    public const string QuotaUnknown = "quota_unknown";
    public const string NearLimit = "near_limit";
    public const string Affinity = "affinity";
    public const string Rotate = "rotate";
    public const string Weight = "weight";
    public const string OnlyOne = "only_one";
    public const string Failover = "failover";
}

/// <summary>
/// Orders candidates by their quota (ported in spirit from magpie's "How magpie picks an account", MIT). Candidates split into
/// four ranks: under 90% with a known figure, unknown (no snapshot, or a stale one), 90%–98% and 98% or more; within the first
/// rank the mode decides, in the others the least used goes first. Unknown quota never fails the pick: it falls behind the known
/// candidates but ahead of the nearly spent ones, and when nothing is known at all the given order stands.
/// </summary>
public static class QuotaRanking
{
    /// <summary>Candidates whose pace is within this share of the leader's count as alike, and go by what they were sent lately.</summary>
    public const double PaceTolerance = 0.9;

    public static IReadOnlyList<Ranked> Rank(string mode, IReadOnlyList<RankInput> items, DateTimeOffset now)
    {
        if (items.Count <= 1)
            return items.Select(i => new Ranked(i.Key, RankReasons.OnlyOne, i.Standing.Known ? i.Standing.Used : null)).ToList();
        if (!QuotaModes.IsQuotaAware(mode) || items.All(i => !i.Standing.Known))
            return items.OrderBy(i => i.Index).Select(i => new Ranked(i.Key, RankReasons.Order, null)).ToList();

        var result = new List<Ranked>(items.Count);
        var healthy = items.Where(i => i.Standing is { Known: true, Band: 0 }).ToList();
        var unknown = items.Where(i => !i.Standing.Known).ToList();
        var soft = items.Where(i => i.Standing is { Known: true, Band: 1 }).ToList();
        var hard = items.Where(i => i.Standing is { Known: true, Band: 2 }).ToList();

        foreach (var (item, reason) in OrderHealthy(mode, healthy, now))
            result.Add(new Ranked(item.Key, reason, item.Standing.Used));
        foreach (var item in unknown.OrderBy(i => i.Recent).ThenBy(i => i.Index))
            result.Add(new Ranked(item.Key, RankReasons.QuotaUnknown, null));
        foreach (var item in soft.OrderBy(i => i.Standing.Used).ThenBy(i => i.Index))
            result.Add(new Ranked(item.Key, RankReasons.NearLimit, item.Standing.Used));
        foreach (var item in hard.OrderBy(i => i.Standing.Used).ThenBy(i => i.Index))
            result.Add(new Ranked(item.Key, RankReasons.NearLimit, item.Standing.Used));
        return result;
    }

    private static IEnumerable<(RankInput Item, string Reason)> OrderHealthy(string mode, List<RankInput> items, DateTimeOffset now)
    {
        switch (mode)
        {
            case QuotaModes.Usage:
                foreach (var i in items.OrderBy(i => i.Standing.Used).ThenBy(i => i.Recent).ThenBy(i => i.Index))
                    yield return (i, RankReasons.LeastUsed);
                break;
            case QuotaModes.Pace:
            {
                // Pace descending; candidates within a tenth of the leader's count as alike and go by what they were sent lately.
                var withPace = items.Where(i => i.Standing.Pace(now) is not null).OrderByDescending(i => i.Standing.Pace(now)).ThenBy(i => i.Index).ToList();
                var rest = items.Where(i => i.Standing.Pace(now) is null).OrderBy(i => i.Standing.Used).ThenBy(i => i.Index).ToList();
                while (withPace.Count > 0)
                {
                    var lead = withPace[0].Standing.Pace(now)!.Value;
                    var alike = withPace.TakeWhile(i => i.Standing.Pace(now)!.Value >= lead * PaceTolerance).ToList();
                    withPace.RemoveRange(0, alike.Count);
                    foreach (var i in alike.OrderBy(i => i.Recent).ThenBy(i => i.Index)) yield return (i, RankReasons.Pace);
                }
                foreach (var i in rest) yield return (i, RankReasons.LeastUsed);
                break;
            }
            default:
            {
                // Smart: the window renewing soonest first, compared to the hour; no renewal time goes last; then the least used.
                foreach (var i in items
                             .OrderBy(i => ResetHour(i.Standing))
                             .ThenBy(i => i.Standing.Used).ThenBy(i => i.Recent).ThenBy(i => i.Index))
                    yield return (i, RankReasons.SoonestReset);
                break;
            }
        }
    }

    private static long ResetHour(QuotaStanding standing) =>
        standing.Deciding?.ResetsAt is { } reset ? reset.UtcTicks / TimeSpan.TicksPerHour : long.MaxValue;
}
