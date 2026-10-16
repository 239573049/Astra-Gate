using System.Text.RegularExpressions;

namespace Astra.Core.Models;

/// <summary>
/// GitHub Copilot names its Claude models with a dotted version (<c>claude-haiku-4.5</c>, <c>claude-3.5-sonnet</c>),
/// while Anthropic-style clients ask for <c>claude-haiku-4-5</c> (often with a <c>-20251001</c> date suffix).
/// <see cref="ToUpstream"/> turns the latter into the former; ids that are not Claude versions pass through.
/// </summary>
public static partial class CopilotModelIds
{
    /// <summary>Provider family key of the GitHub Copilot subscription template.</summary>
    public const string ProviderKey = "github-copilot-subscription";

    /// <summary>Whether <paramref name="provider"/> is a GitHub Copilot subscription (same key rule as the gateway's SubscriptionSupport).</summary>
    public static bool IsCopilot(Provider provider) =>
        (provider.TemplateId ?? provider.PriceKey ?? provider.Id) == ProviderKey;

    /// <summary>
    /// <c>claude-haiku-5-5</c> / <c>claude-haiku-5-5-20260101</c> → <c>claude-haiku-5.5</c>;
    /// <c>claude-3-5-sonnet</c> → <c>claude-3.5-sonnet</c>; <c>claude-sonnet-4-20250514</c> → <c>claude-sonnet-4</c>;
    /// <c>claude-opus-4-8-fast</c> → <c>claude-opus-4.8-fast</c> (a trailing variant word is kept).
    /// Claude Code's context-window marker (<c>[1m]</c>) is dropped first — it is a client hint, not part of the id.
    /// Anything else (already dotted, other vendors, no version match) is returned unchanged.
    /// </summary>
    public static string ToUpstream(string modelId)
    {
        if (modelId.EndsWith(ContextMarker, StringComparison.OrdinalIgnoreCase))
            modelId = modelId[..^ContextMarker.Length];
        var m = NewStyleRegex().Match(modelId);
        if (m.Success)
        {
            var family = m.Groups["family"].Value;
            var major = m.Groups["major"].Value;
            var variant = m.Groups["variant"].Value;
            if (m.Groups["minor"].Success) return $"claude-{family}-{major}.{m.Groups["minor"].Value}{variant}";
            return m.Groups["date"].Success ? $"claude-{family}-{major}{variant}" : modelId;
        }
        m = OldStyleRegex().Match(modelId);
        return m.Success ? $"claude-{m.Groups["major"].Value}.{m.Groups["minor"].Value}-{m.Groups["family"].Value}" : modelId;
    }

    private const string ContextMarker = "[1m]";

    [GeneratedRegex(@"^claude-(?<family>[a-z]+)-(?<major>\d+)(?:-(?<minor>\d{1,2}))?(?:-(?<date>\d{8}))?(?<variant>(?:-[a-z][a-z0-9]*)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStyleRegex();

    [GeneratedRegex(@"^claude-(?<major>\d+)-(?<minor>\d{1,2})-(?<family>haiku|sonnet|opus)(?:-\d{8})?$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyleRegex();
}
