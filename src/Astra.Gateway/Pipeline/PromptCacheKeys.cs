using System.Security.Cryptography;
using System.Text;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// A <c>prompt_cache_key</c> for a translated request whose client sent none. OpenAI-style upstreams route a request to
/// a cache by this key (else by a hash of the prompt's head), and GitHub Copilot's GPT models are Responses-only, so
/// every Chat client reaches them translated and, without a key, hits a cold cache far more often. The key is a
/// fingerprint of the conversation's start — its leading instructions (late ones excluded: they change per turn) and
/// first user text — so every turn of one conversation carries the same key and different conversations do not.
/// </summary>
public static class PromptCacheKeys
{
    /// <summary>The derived key, or null when the request has neither instructions nor user text.</summary>
    public static string? Derive(UnifiedRequest request)
    {
        var late = new HashSet<object>(request.LateSystem.Select(l => l.Text), ReferenceEqualityComparer.Instance);
        var system = string.Join("\n\n", request.System.OfType<TextPart>().Where(p => !late.Contains(p)).Select(p => p.Text));
        var first = string.Join("\n\n", request.Messages
            .FirstOrDefault(m => m.Role == MessageRole.User && m.Parts.Any(p => p is TextPart))?.Parts.OfType<TextPart>().Select(p => p.Text) ?? []);
        if (system.Length == 0 && first.Length == 0) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(system + "\n\0\n" + first));
        return "astra-" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }
}
