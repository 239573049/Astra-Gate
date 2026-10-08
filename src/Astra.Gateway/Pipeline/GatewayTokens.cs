using System.Security.Cryptography;
using System.Text;
using Astra.Core.Clients;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Gateway tokens: "sk-astra-&lt;32 random base62 chars&gt;". A client configuration holds "&lt;token&gt;.&lt;client kind&gt;"
/// so the gateway knows which client called; a bare token is a direct call. Lookup by a short prefix, then
/// constant-time comparison of SHA-256 hashes; the token itself is stored encrypted (to show it again and to
/// rewrite client configs).
/// </summary>
public static class GatewayTokens
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const string Prefix = "sk-astra-";
    private const int RandomLength = 32;
    private const int LookupChars = 6;

    /// <summary>Prefix of the per-client keys used before tokens existed ("astra-&lt;kind&gt;-…"); no longer accepted.</summary>
    public const string LegacyPrefix = "astra-";

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomLength);
        var chars = new char[RandomLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return Prefix + new string(chars);
    }

    /// <summary>Lookup prefix: "sk-astra-" plus the first random characters (unique enough, not a secret).</summary>
    public static string PrefixOf(string token) => token[..Math.Min(token.Length, Prefix.Length + LookupChars)];

    /// <summary>Masked form for display: the lookup prefix followed by an ellipsis.</summary>
    public static string Mask(string? prefix) => prefix is null ? "" : prefix + "…";

    /// <summary>The key written into a client's configuration.</summary>
    public static string ForClient(string token, string clientKind) => $"{token}.{clientKind}";

    public static bool IsLegacyKey(string key) => key.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Splits a presented key into the token and the optional client kind ("&lt;token&gt;.&lt;kind&gt;"). False when it is not
    /// a token or the suffix names no known client.
    /// </summary>
    public static bool TryParse(string key, out string token, out string? clientKind)
    {
        token = key;
        clientKind = null;
        var dot = key.LastIndexOf('.');
        if (dot >= 0)
        {
            var kind = key[(dot + 1)..];
            if (!ClientKinds.All.Contains(kind)) return false;
            token = key[..dot];
            clientKind = kind;
        }
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length != Prefix.Length + RandomLength) return false;
        foreach (var c in token.AsSpan(Prefix.Length))
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool Verify(string token, string? storedHash)
    {
        if (storedHash is null) return false;
        var actual = Encoding.ASCII.GetBytes(Hash(token));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Extracts a key from Authorization: Bearer, x-api-key, x-goog-api-key or ?key=.</summary>
    public static string? Extract(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        foreach (var header in new[] { "x-api-key", "x-goog-api-key" })
        {
            var v = request.Headers[header].ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        var q = request.Query["key"].ToString();
        return string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    }
}
