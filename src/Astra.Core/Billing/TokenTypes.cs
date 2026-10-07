namespace Astra.Core.Billing;

/// <summary>Token categories. Each is stored as its own usage line; the set is open-ended (plain strings).</summary>
public static class TokenTypes
{
    public const string Input = "input";
    public const string CacheRead = "cache_read";
    public const string CacheWrite5m = "cache_write_5m";
    public const string CacheWrite1h = "cache_write_1h";
    public const string Output = "output";
    public const string Reasoning = "reasoning";
    public const string InputAudio = "input_audio";
    public const string OutputAudio = "output_audio";
    public const string InputImage = "input_image";
    public const string OutputImage = "output_image";

    /// <summary>Per-call billed items (not tokens).</summary>
    public const string WebSearchCall = "web_search_call";

    /// <summary>Canonical display order.</summary>
    public static readonly IReadOnlyList<string> Ordered =
    [
        Input, CacheRead, CacheWrite5m, CacheWrite1h, InputAudio, InputImage,
        Output, Reasoning, OutputAudio, OutputImage,
    ];

    private static readonly HashSet<string> InputLike =
        [Input, CacheRead, CacheWrite5m, CacheWrite1h, InputAudio, InputImage];

    /// <summary>True for token types that count toward the "total input" used to pick a context tier.</summary>
    public static bool IsInputLike(string type) =>
        InputLike.Contains(type) || type.StartsWith("input_", StringComparison.Ordinal) || type.StartsWith("cache_", StringComparison.Ordinal);

    public static int SortKey(string type)
    {
        for (var i = 0; i < Ordered.Count; i++)
        {
            if (Ordered[i] == type) return i;
        }
        return 100;
    }

    /// <summary>Price lookup fallback chain when a type has no explicit price.</summary>
    public static string? FallbackOf(string type) => type switch
    {
        Reasoning => Output,
        CacheWrite1h => Input,
        CacheWrite5m => Input,
        CacheRead => Input,
        InputAudio or InputImage => Input,
        OutputAudio or OutputImage => Output,
        Input or Output => null,
        _ => IsInputLike(type) ? Input : Output,
    };
}

/// <summary>Usage normalized across protocols: every token category is disjoint (no double counting).</summary>
public sealed class NormalizedUsage
{
    public Dictionary<string, long> Tokens { get; init; } = new();
    public Dictionary<string, long> Calls { get; init; } = new();
    public string? ServiceTier { get; set; }
    public List<string> Notes { get; init; } = [];

    public long Get(string type) => Tokens.GetValueOrDefault(type);

    public void Add(string type, long count)
    {
        if (count <= 0) return;
        Tokens[type] = Tokens.GetValueOrDefault(type) + count;
    }

    public long TotalInput => Tokens.Where(kv => TokenTypes.IsInputLike(kv.Key)).Sum(kv => kv.Value);
    public long TotalOutput => Tokens.Where(kv => !TokenTypes.IsInputLike(kv.Key)).Sum(kv => kv.Value);
    public bool IsEmpty => Tokens.Values.All(v => v == 0) && Calls.Values.All(v => v == 0);

    /// <summary>Merge a later usage report into this one, taking the max of each counter.
    /// Streams frequently repeat cumulative usage; max() makes repeated reports idempotent.</summary>
    public void MergeMax(NormalizedUsage other)
    {
        foreach (var (k, v) in other.Tokens) Tokens[k] = Math.Max(Tokens.GetValueOrDefault(k), v);
        foreach (var (k, v) in other.Calls) Calls[k] = Math.Max(Calls.GetValueOrDefault(k), v);
        ServiceTier = other.ServiceTier ?? ServiceTier;
        foreach (var n in other.Notes)
        {
            if (!Notes.Contains(n)) Notes.Add(n);
        }
    }
}
