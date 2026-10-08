namespace Astra.Core.Tokens;

/// <summary>Well-known token ids.</summary>
public static class TokenIds
{
    /// <summary>The default token: created by the 0006 migration, never deletable, the fallback for every client.</summary>
    public const string Default = "default";
}

/// <summary>
/// A gateway token (row of the <c>tokens</c> table). Clients get "&lt;token&gt;.&lt;kind&gt;" written into their
/// configuration; a bare token is a direct call routed to <see cref="ProviderId"/>.
/// </summary>
public sealed class TokenRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Encrypted plaintext token; null only until the server generated the default token's key.</summary>
    public string? KeyEnc { get; set; }

    public string? KeyHash { get; set; }

    /// <summary>Lookup prefix ("sk-astra-" plus the first random characters); not a secret.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>Provider for direct calls (bare token); null = direct calls are rejected.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Pinned subscription account of <see cref="ProviderId"/> for direct calls; null = the provider's default.</summary>
    public string? AccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Usage numbers of one token, either lifetime (persisted counters) or for a time range (from the request log).</summary>
public sealed class TokenUsage
{
    public string TokenId { get; set; } = "";
    public long Requests { get; set; }
    public long SuccessRequests { get; set; }
    public long CostNanoUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Part of <see cref="InputTokens"/> served from the prompt cache.</summary>
    public long CacheReadTokens { get; set; }

    public long CacheWriteTokens { get; set; }
    public long ReasoningTokens { get; set; }

    /// <summary>Output tokens of successful requests with a measured generation time (TPS numerator).</summary>
    public long TpsOutputTokens { get; set; }

    /// <summary>Generation time of those same requests in milliseconds (TPS denominator).</summary>
    public long TpsGenerationMs { get; set; }

    /// <summary>Cache reads / input tokens; null without input.</summary>
    public double? CacheHitRate => InputTokens > 0 ? (double)CacheReadTokens / InputTokens : null;

    /// <summary>Weighted output speed in tokens per second; null without a measured generation time.</summary>
    public double? Tps => TpsGenerationMs > 0 ? TpsOutputTokens * 1000.0 / TpsGenerationMs : null;
}
