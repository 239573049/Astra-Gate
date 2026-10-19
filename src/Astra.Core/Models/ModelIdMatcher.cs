using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Astra.Core.Models;

/// <summary>Links an upstream model id to a system model: exact → provider-specific id / alias → normalized.</summary>
public static partial class ModelIdMatcher
{
    public sealed record Candidate(string SystemModelId, IReadOnlyList<string> Aliases);

    /// <summary>
    /// Immutable lookup for repeated matches against one catalog snapshot. Normalizes the catalog once, rather
    /// than once per request. Like <see cref="Match"/>, ties in each matching tier go to the first candidate.
    /// </summary>
    public sealed class Index
    {
        private readonly FrozenDictionary<string, string> _exact;
        private readonly FrozenDictionary<string, string> _aliases;
        private readonly FrozenDictionary<string, string> _normalized;

        public Index(IEnumerable<Candidate> candidates)
        {
            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                exact.TryAdd(candidate.SystemModelId, candidate.SystemModelId);
                normalized.TryAdd(Normalize(candidate.SystemModelId), candidate.SystemModelId);
                foreach (var alias in candidate.Aliases)
                {
                    aliases.TryAdd(alias, candidate.SystemModelId);
                    normalized.TryAdd(Normalize(alias), candidate.SystemModelId);
                }
            }
            _exact = exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _aliases = aliases.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _normalized = normalized.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public string? Match(string upstreamModelId, IReadOnlyDictionary<string, string>? upstreamIdIndex = null)
        {
            if (_exact.TryGetValue(upstreamModelId, out var exact)) return exact;
            if (upstreamIdIndex is not null && upstreamIdIndex.TryGetValue(upstreamModelId, out var upstream)) return upstream;
            if (_aliases.TryGetValue(upstreamModelId, out var alias)) return alias;
            var normalized = Normalize(upstreamModelId);
            return normalized.Length > 0 && _normalized.TryGetValue(normalized, out var match) ? match : null;
        }
    }

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
