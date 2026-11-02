using System.Security.Cryptography;
using System.Text;

namespace Astra.Core.Tokens;

/// <summary>
/// A one-time join code of a gateway token (row of <c>token_invites</c>). The code itself is never stored, only its hash; whoever
/// redeems it first (before <see cref="ExpiresAt"/>, unless revoked) gets the token's key once.
/// </summary>
public sealed class TokenInvite
{
    public string Id { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>The address that redeemed it.</summary>
    public string? UsedIp { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Why the code cannot be redeemed at <paramref name="now"/>, or null when it still can.</summary>
    public string? DeadReason(DateTimeOffset now) =>
        UsedAt is not null ? TokenInvites.Used : RevokedAt is not null ? TokenInvites.Revoked : ExpiresAt <= now ? TokenInvites.Expired : null;
}

/// <summary>Join code rules: format, hashing, lifetime, and the reasons a code is dead.</summary>
public static class TokenInvites
{
    public const string CodePrefix = "ij_";
    public const string Used = "used";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string Unknown = "unknown";

    public const int DefaultTtlMinutes = 15;
    public const int MinTtlMinutes = 1;
    public const int MaxTtlMinutes = 24 * 60;

    /// <summary>A new code: 256 random bits, base64url, so guessing one is out of the question.</summary>
    public static string NewCode() =>
        CodePrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What is stored for a code (it carries 256 random bits, so a plain SHA-256 is enough).</summary>
    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
}
