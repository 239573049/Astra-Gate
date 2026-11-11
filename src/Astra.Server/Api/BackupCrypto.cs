using System.Security.Cryptography;
using System.Text;

namespace Astra.Server.Api;

/// <summary>
/// Passphrase encryption for the API keys in a provider backup file (<see cref="ProviderBackupService"/>): PBKDF2-SHA256 derives
/// one AES-256 key per file from the passphrase and a random salt, and every key is sealed with AES-GCM under its own random
/// nonce. Only the BCL is used (AOT-safe). A sealed value is <c>v1:&lt;nonce&gt;:&lt;ciphertext+tag&gt;</c> (base64), so a format change
/// can be told apart later.
/// </summary>
internal sealed class BackupCrypto
{
    /// <summary>OWASP's 2023 floor for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    public const int MinPassphraseLength = 8;
    private const int SaltBytes = 16, KeyBytes = 32, NonceBytes = 12, TagBytes = 16;
    private const string Prefix = "v1:";

    /// <summary>Sealed under the same key, so a wrong passphrase is recognised before any real key is touched.</summary>
    private const string CheckPlain = "astra-providers-backup";

    private readonly byte[] _key;

    private BackupCrypto(byte[] key) => _key = key;

    public static BackupCrypto Create(string passphrase, out byte[] salt, int iterations = DefaultIterations)
    {
        RequirePassphrase(passphrase);
        salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new BackupCrypto(Derive(passphrase, salt, iterations));
    }

    public static BackupCrypto Open(string passphrase, byte[] salt, int iterations)
    {
        RequirePassphrase(passphrase);
        if (salt.Length < SaltBytes || iterations < 100_000 || iterations > 10_000_000)
            throw new AdminApiException(400, "The backup file's encryption parameters are not valid");
        return new BackupCrypto(Derive(passphrase, salt, iterations));
    }

    public string Check() => Seal(CheckPlain);

    /// <summary>False for a wrong passphrase (or a file that was changed).</summary>
    public bool Verify(string? check) => check is not null && TryOpen(check, out var plain) && plain == CheckPlain;

    public string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(_key, TagBytes)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String(nonce) + ":" + Convert.ToBase64String([.. cipher, .. tag]);
    }

    public bool TryOpen(string sealedValue, out string plain)
    {
        plain = "";
        if (!sealedValue.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = sealedValue[Prefix.Length..].Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var nonce = Convert.FromBase64String(parts[0]);
            var body = Convert.FromBase64String(parts[1]);
            if (nonce.Length != NonceBytes || body.Length < TagBytes) return false;
            var cipher = body[..^TagBytes];
            var data = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagBytes)) aes.Decrypt(nonce, cipher, body[^TagBytes..], data);
            plain = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static void RequirePassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Length < MinPassphraseLength)
            throw new AdminApiException(400, $"The passphrase needs at least {MinPassphraseLength} characters");
    }
}
