using System.Text.RegularExpressions;

namespace Astra.Providers.Subscription;

/// <summary>
/// Normalizes pasted Grok Web SSO input: a bare JWT, <c>sso=…</c> cookie text, or a share-site combo line such as
/// <c>email----password----totp----sso</c>. Pure functions, no network (plan §5.4 account import).
/// </summary>
public static partial class XaiSsoNormalizer
{
    public const string ComboSeparator = "----";

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    /// <summary>True when the text has the shape of a Grok SSO entry (JWT, cookie text or combo line) rather than an opaque refresh token.</summary>
    public static bool LooksLikeSso(string entry)
    {
        var text = entry.Trim();
        return JwtPattern().IsMatch(text)
               || text.Contains(ComboSeparator, StringComparison.Ordinal)
               || text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)
               || HasSsoCookieField(text);
    }

    /// <summary>The SSO token inside one entry; empty when there is none.</summary>
    public static string Normalize(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return "";

        // 已经是 JWT 的行不能再拆：签名里合法地可能出现 ----。
        if (value.Contains(ComboSeparator, StringComparison.Ordinal) && !JwtPattern().IsMatch(value))
            value = PickComboField(value);

        if (value.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            value = value["cookie:".Length..].Trim();

        foreach (var part in value.Split(';'))
        {
            var segment = part.Trim();
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;
            var name = segment[..separator].Trim();
            if (name.Equals("sso", StringComparison.OrdinalIgnoreCase) || name.Equals("sso-rw", StringComparison.OrdinalIgnoreCase))
                return Sanitize(segment[(separator + 1)..]);
        }

        var firstSegment = value.IndexOf(';');
        if (firstSegment >= 0) value = value[..firstSegment].Trim();
        return Sanitize(value);
    }

    private static string PickComboField(string line)
    {
        var fields = line.Split(ComboSeparator, StringSplitOptions.None)
            .Select(field => field.Trim())
            .Where(field => field.Length > 0)
            .ToList();
        if (fields.Count == 0) return "";

        for (var i = fields.Count - 1; i >= 0; i--)
            if (JwtPattern().IsMatch(fields[i])) return fields[i];
        for (var i = fields.Count - 1; i >= 0; i--)
            if (HasSsoCookieField(fields[i])) return fields[i];
        return fields[^1];
    }

    private static bool HasSsoCookieField(string field) =>
        field.Contains("sso=", StringComparison.OrdinalIgnoreCase) || field.Contains("sso-rw=", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value) =>
        value.Replace("\r", "").Replace("\n", "").Replace("\0", "").Trim();
}
