using System.Text.RegularExpressions;

namespace Astra.Core.Mcp;

/// <summary>
/// The names the aggregate MCP endpoint shows: <c>&lt;slug&gt;__&lt;tool&gt;</c>. Upstreams often share tool names, so each is prefixed with its server's
/// slug, and a slug has no underscore, so the first <c>__</c> always ends it. This is a published contract — clients put these names
/// in their tool allow-lists — so the rule must not change (plan mcp-gateway §4).
/// </summary>
public static partial class McpNames
{
    public const string Separator = "__";
    public const int MaxSlugLength = 32;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    /// <summary>Why <paramref name="slug"/> cannot be a server's slug, or null when it can.</summary>
    public static string? ValidateSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return "slug is required";
        if (slug.Length > MaxSlugLength) return $"slug must be at most {MaxSlugLength} characters";
        return SlugPattern().IsMatch(slug) ? null : "slug may only hold lower-case letters, digits and single hyphens (no underscore)";
    }

    /// <summary>A slug made from a display name (best effort; the caller still validates it and checks it is free).</summary>
    public static string SlugFrom(string? name)
    {
        var chars = (name ?? "").Trim().ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray();
        var slug = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? "server" : slug;
    }

    public static string Prefixed(string slug, string tool) => slug + Separator + tool;

    /// <summary>Splits an aggregate tool name at its first <c>__</c>; false when it has no server part or no tool part.</summary>
    public static bool TrySplit(string? name, out string slug, out string tool)
    {
        slug = tool = "";
        if (string.IsNullOrEmpty(name)) return false;
        var at = name.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0 || at + Separator.Length >= name.Length) return false;
        slug = name[..at];
        tool = name[(at + Separator.Length)..];
        return true;
    }
}
