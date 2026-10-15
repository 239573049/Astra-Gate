namespace Astra.Core.Tokens;

/// <summary>How a <see cref="TokenPool"/> spreads requests over its members (issue #2).</summary>
public static class TokenPoolStrategies
{
    /// <summary>List order: always the first healthy member; the next one only when it fails. Each member is its own tier.</summary>
    public const string Failover = "failover";

    /// <summary>One tier, equal shares: each new conversation (or request, without sticky sessions) goes to the next member.</summary>
    public const string RoundRobin = "round_robin";

    /// <summary>One tier, smooth weighted round robin over <see cref="TokenPoolMember.Weight"/>.</summary>
    public const string Weighted = "weighted";

    /// <summary>The lowest non-empty <see cref="TokenPoolMember.Tier"/> first, weighted round robin inside it.</summary>
    public const string Tiered = "tiered";

    /// <summary>One tier: the member with the fewest requests in flight, then the fewest assigned recently.</summary>
    public const string LeastBusy = "least_busy";

    public static readonly IReadOnlyList<string> All = [Failover, RoundRobin, Weighted, Tiered, LeastBusy];
}

/// <summary>Whether a conversation keeps going to the member it started on (upstream prompt caches are per provider).</summary>
public static class TokenPoolSticky
{
    public const string Session = "session";
    public const string Off = "off";

    public static readonly IReadOnlyList<string> All = [Session, Off];
}

/// <summary>One provider of a <see cref="TokenPool"/>.</summary>
public sealed class TokenPoolMember
{
    public string ProviderId { get; set; } = "";

    /// <summary>Pinned account of an account-based provider; null = the provider's own account selection.</summary>
    public string? AccountId { get; set; }

    /// <summary>Share in <see cref="TokenPoolStrategies.Weighted"/> and inside a tier of <see cref="TokenPoolStrategies.Tiered"/>.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Tier of <see cref="TokenPoolStrategies.Tiered"/> (1 is used first).</summary>
    public int Tier { get; set; } = 1;
}

/// <summary>
/// The providers a token's direct calls (bare token, no client suffix) are spread over, and how (column
/// <c>tokens.pool_json</c>, issue #2). Null / no members = the token's single <see cref="TokenRecord.ProviderId"/>.
/// Weights and tiers are kept whatever the strategy, so switching strategies loses nothing; the strategy decides which
/// of them it reads.
/// </summary>
public sealed class TokenPool
{
    public const int DefaultMaxAttempts = 3;
    public const int MaxAttemptsLimit = 5;
    public const int MaxMembers = 16;
    public const int MaxWeight = 100;
    public const int MaxTier = 3;

    public string Strategy { get; set; } = TokenPoolStrategies.Failover;
    public string Sticky { get; set; } = TokenPoolSticky.Session;

    /// <summary>Whether a request that fails before anything reached the client is retried on the next member.</summary>
    public bool Failover { get; set; } = true;

    /// <summary>Members tried at most per request (failover included).</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    public List<TokenPoolMember> Members { get; set; } = [];

    public bool IsEmpty => Members.Count == 0;

    public static TokenPool? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<TokenPool>(json) is { IsEmpty: false } pool ? pool : null;

    /// <summary>Members a request may try out of <paramref name="candidates"/>: one when failover is off.</summary>
    public int AttemptLimit(int candidates) => Failover ? Math.Clamp(Math.Min(candidates, MaxAttempts), 1, MaxAttemptsLimit) : 1;

    /// <summary>Problems with this value (empty = valid).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!TokenPoolStrategies.All.Contains(Strategy)) errors.Add($"unknown strategy {Strategy}");
        if (!TokenPoolSticky.All.Contains(Sticky)) errors.Add($"unknown sticky mode {Sticky}");
        if (MaxAttempts is < 1 or > MaxAttemptsLimit) errors.Add($"maxAttempts must be between 1 and {MaxAttemptsLimit}");
        if (Members.Count > MaxMembers) errors.Add($"a pool has at most {MaxMembers} providers");
        foreach (var m in Members)
        {
            if (string.IsNullOrWhiteSpace(m.ProviderId)) errors.Add("providerId is required");
            if (m.Weight is < 1 or > MaxWeight) errors.Add($"weight of {m.ProviderId} must be between 1 and {MaxWeight}");
            if (m.Tier is < 1 or > MaxTier) errors.Add($"tier of {m.ProviderId} must be between 1 and {MaxTier}");
        }
        if (Members.GroupBy(m => m.ProviderId).Any(g => g.Count() > 1)) errors.Add("each provider can be in the pool only once");
        return errors;
    }
}
