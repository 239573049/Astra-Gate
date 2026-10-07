using System.Security.Cryptography;
using System.Text;
using Astra.Core.Clients;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Per-client local keys: "astra-&lt;kind&gt;-&lt;32 random base62 chars&gt;". Lookup by a short prefix, then constant-time
/// comparison of SHA-256 hashes; the key itself is stored encrypted (to show it again / rewrite client configs).
/// </summary>
public static class LocalKeys
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int PrefixLength = 16;

    public static string Generate(string clientKind)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var chars = new char[32];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return $"astra-{clientKind}-{new string(chars)}";
    }

    public static bool LooksLikeLocalKey(string? key) => key is not null && key.StartsWith("astra-", StringComparison.Ordinal) && key.Length > PrefixLength;

    /// <summary>Lookup prefix: the kind plus the first random characters (unique enough, not a secret).</summary>
    public static string PrefixOf(string key)
    {
        var lastDash = key.LastIndexOf('-');
        var randomPart = lastDash >= 0 ? key[(lastDash + 1)..] : key;
        return key[..(lastDash + 1)] + randomPart[..Math.Min(6, randomPart.Length)];
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static bool Verify(string key, string? storedHash)
    {
        if (storedHash is null) return false;
        var actual = Encoding.ASCII.GetBytes(Hash(key));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Assigns a fresh key to <paramref name="client"/> and returns the plaintext.</summary>
    public static string Rotate(ClientRecord client, ISecretProtector protector)
    {
        var key = Generate(client.Kind);
        client.LocalKeyEnc = protector.Protect(key);
        client.LocalKeyHash = Hash(key);
        client.LocalKeyPrefix = PrefixOf(key);
        return key;
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
