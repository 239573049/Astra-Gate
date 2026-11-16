using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Api;

namespace Astra.Server.Backup;

/// <summary>
/// The file a whole-installation backup (and a sync file) is written as. Two shapes, both <c>"format": "astra-backup"</c>:
/// <list type="bullet">
/// <item>encrypted: <c>kdf</c> <c>pbkdf2-sha256</c>, <c>iterations</c>, <c>salt</c>, <c>nonce</c>, <c>data</c> — AES-256-GCM over the bundle JSON, the key derived
/// from the passphrase, and the header fields (<c>format/version/kdf/iterations/hex(salt)</c>) bound in as the associated data so none of them can be
/// changed unnoticed. This is magpie's envelope (MIT, <c>internal/backup</c>) with Astra's format name; <see cref="Open"/> reads a magpie envelope
/// when told its format, which the tests use as the interop check.</item>
/// <item>plain (<c>kdf</c> <c>none</c>): the bundle inline. Only ever written without secrets — <see cref="Seal"/> refuses a bundle that has keys.</item>
/// </list>
/// </summary>
public static class BackupEnvelope
{
    public const string Format = "astra-backup";
    public const int Version = 1;
    public const int DefaultIterations = 600_000;
    public const int MinPassphraseLength = 8;
    private const int SaltBytes = 16, NonceBytes = 12, TagBytes = 16;
    private const int MaxFileBytes = 96 * 1024 * 1024;

    /// <summary>Encrypts <paramref name="bundle"/>; with a null passphrase writes the plain shape (refused when the bundle holds secrets).</summary>
    public static byte[] Seal(JsonObject bundle, string? passphrase, bool hasKeys, int iterations = DefaultIterations, string format = Format, string? content = null)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            if (hasKeys) throw new AdminApiException(400, "A backup with keys must be encrypted with a passphrase");
            var file = new JsonObject { ["format"] = format, ["version"] = Version, ["kdf"] = "none", ["bundle"] = bundle.DeepClone() };
            if (content is not null) file["content"] = content;
            return Encoding.UTF8.GetBytes(file.ToJsonString());
        }
        RequirePassphrase(passphrase);
        var plain = Encoding.UTF8.GetBytes(bundle.ToJsonString());
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = Derive(passphrase, salt, iterations);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes)) aes.Encrypt(nonce, plain, cipher, tag, Header(format, Version, "pbkdf2-sha256", iterations, salt));
        var envelope = new JsonObject
        {
            ["format"] = format, ["version"] = Version, ["kdf"] = "pbkdf2-sha256", ["iterations"] = iterations,
            ["salt"] = Convert.ToBase64String(salt), ["nonce"] = Convert.ToBase64String(nonce), ["data"] = Convert.ToBase64String([.. cipher, .. tag]),
        };
        if (content is not null) envelope["content"] = content;
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    /// <summary>Whether the file is an encrypted one (it needs a passphrase); false for plain and for anything that is not a backup.</summary>
    public static bool IsEncrypted(byte[] file) =>
        TryParse(file, Format, out var o) && Text(o["kdf"]) is { } kdf && kdf != "none";

    /// <summary>Reads the bundle out of a file. Errors are <see cref="AdminApiException"/>s with a message the user can act on.</summary>
    public static JsonObject Open(byte[] file, string? passphrase, string format = Format)
    {
        if (file.Length > MaxFileBytes) throw new AdminApiException(400, "The backup file is too large");
        if (!TryParse(file, format, out var o)) throw new AdminApiException(400, "This is not an Astra backup file");
        if (o["version"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version < 1) throw new AdminApiException(400, "The backup file has no valid version");
        if (version > Version) throw new AdminApiException(400, "The backup file comes from a newer Astra; update Astra to read it");
        var kdf = Text(o["kdf"]);
        if (kdf == "none")
            return o["bundle"] as JsonObject ?? throw new AdminApiException(400, "The backup file has no content");
        if (kdf != "pbkdf2-sha256") throw new AdminApiException(400, "The backup file's encryption is not supported");
        if (string.IsNullOrEmpty(passphrase)) throw new AdminApiException(400, "This backup is encrypted: enter its passphrase");
        if (o["iterations"] is not JsonValue it || !it.TryGetValue<int>(out var iterations) || iterations < 100_000 || iterations > 10_000_000)
            throw new AdminApiException(400, "The backup file's encryption parameters are not valid");
        byte[] salt, nonce, body;
        try
        {
            salt = Convert.FromBase64String(Text(o["salt"]) ?? "");
            nonce = Convert.FromBase64String(Text(o["nonce"]) ?? "");
            body = Convert.FromBase64String(Text(o["data"]) ?? "");
        }
        catch (FormatException) { throw new AdminApiException(400, "The backup file is damaged"); }
        if (salt.Length < SaltBytes || nonce.Length != NonceBytes || body.Length < TagBytes) throw new AdminApiException(400, "The backup file is damaged");
        var key = Derive(passphrase, salt, iterations);
        var plain = new byte[body.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain, Header(format, version, kdf, iterations, salt));
        }
        catch (CryptographicException) { throw new AdminApiException(400, "Wrong passphrase, or the file was changed"); }
        try { return JsonNode.Parse(plain) as JsonObject ?? throw new AdminApiException(400, "The backup file is damaged"); }
        catch (System.Text.Json.JsonException) { throw new AdminApiException(400, "The backup file is damaged"); }
    }

    /// <summary>The associated data: magpie's <c>%s/%d/%s/%d/%x</c> over format, version, kdf, iterations and the salt.</summary>
    internal static byte[] Header(string format, int version, string kdf, int iterations, byte[] salt) =>
        Encoding.UTF8.GetBytes($"{format}/{version}/{kdf}/{iterations}/{Convert.ToHexStringLower(salt)}");

    internal static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static bool TryParse(byte[] file, string format, out JsonObject o)
    {
        o = new JsonObject();
        try
        {
            var span = file.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
            if (JsonNode.Parse(span) is not JsonObject parsed || Text(parsed["format"]) != format) return false;
            o = parsed;
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static void RequirePassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Length < MinPassphraseLength)
            throw new AdminApiException(400, $"The passphrase needs at least {MinPassphraseLength} characters");
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
