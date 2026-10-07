using System.Text.RegularExpressions;

namespace Astra.Core.Models;

/// <summary>Links an upstream model id to a system model: exact → provider-specific id / alias → normalized.</summary>
public static partial class ModelIdMatcher
{
    public sealed record Candidate(string SystemModelId, IReadOnlyList<string> Aliases);

    public static string? Match(string upstreamModelId, IEnumerable<Candidate> candidates,
        IReadOnlyDictionary<string, string>? upstreamIdIndex = null)
    {
        var list = candidates as IReadOnlyList<Candidate> ?? candidates.ToList();
        var exact = list.FirstOrDefault(c => string.Equals(c.SystemModelId, upstreamModelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.SystemModelId;

        if (upstreamIdIndex is not null && upstreamIdIndex.TryGetValue(upstreamModelId, out var indexed)) return indexed;

        var alias = list.FirstOrDefault(c => c.Aliases.Any(a => string.Equals(a, upstreamModelId, StringComparison.OrdinalIgnoreCase)));
        if (alias is not null) return alias.SystemModelId;

        var normalized = Normalize(upstreamModelId);
        if (normalized.Length == 0) return null;
        return list.FirstOrDefault(c => Normalize(c.SystemModelId) == normalized
                                        || c.Aliases.Any(a => Normalize(a) == normalized))?.SystemModelId;
    }

    /// <summary>Lower-case, drop "vendor/" prefixes and "~", treat '.' '_' ':' as '-', strip date / version suffixes.</summary>
    public static string Normalize(string id)
    {
        var s = id.Trim().TrimStart('~').ToLowerInvariant();
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        var at = s.IndexOf('@');
        if (at >= 0) s = s[..at];
        s = SeparatorRegex().Replace(s, "-");
        s = DateSuffixRegex().Replace(s, "");
        s = LatestSuffixRegex().Replace(s, "");
        return s.Trim('-');
    }

    [GeneratedRegex(@"[._:\s]+")]
    private static partial Regex SeparatorRegex();

    // -20250929, -2025-09-29, -250929, -0731
    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}|\d{8}|\d{6}|\d{4})$")]
    private static partial Regex DateSuffixRegex();

    [GeneratedRegex(@"-latest$")]
    private static partial Regex LatestSuffixRegex();
}
