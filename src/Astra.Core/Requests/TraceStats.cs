namespace Astra.Core.Requests;

/// <summary>Small statistics over trace samples (.plan/observability.md §4).</summary>
public static class TraceStats
{
    /// <summary>Nearest-rank percentile (<paramref name="p"/> in 0–100) of <paramref name="values"/>; null when empty.</summary>
    public static long? Percentile(IReadOnlyCollection<long> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(Math.Clamp(p, 0, 100) / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>The window a range id covers: live = the last 5 minutes, then 1h / 24h / 7d / 30d (default 1h).</summary>
    public static TimeSpan Window(string? range) => range switch
    {
        "live" => TimeSpan.FromMinutes(5),
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(1),
    };
}
