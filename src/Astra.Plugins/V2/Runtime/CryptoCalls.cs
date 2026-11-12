using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Plugins.V2.Runtime;

/// <summary>
/// The v2 sandbox's crypto and encoding host calls (design/plugin-v2 02 §crypto): pure computation on <c>System.Security.Cryptography</c>
/// (no reflection, AOT-safe), so they need no permission and a gateway hook may call them. MD5 and SHA-1 are here to speak to
/// vendors that still use them; they are not for new security designs. Bytes cross the JS boundary as a spec object —
/// <c>{text}</c> (UTF-8), <c>{base64}</c>, <c>{hex}</c> or <c>{bin}</c> (a "binary string", one char per byte: what the sandbox's
/// <c>Uint8Array</c> converts to fastest) — or a bare string (UTF-8 text); results are text in the asked encoding or a <c>{bin}</c>.
/// </summary>
internal static class CryptoCalls
{
    /// <summary>Calls this class answers.</summary>
    public static readonly HashSet<string> Names =
    [
        "crypto.digest", "crypto.hmac", "crypto.aes", "crypto.rsa.encrypt", "crypto.sign", "crypto.verify", "crypto.pbkdf2", "crypto.hkdf",
        "encoding.utf8Encode", "encoding.utf8Decode", "encoding.encode", "encoding.decode",
    ];

    /// <summary>The most input one call takes (bytes).</summary>
    public const int InputLimit = 8 * 1024 * 1024;

    private sealed class BadArgument(string message) : Exception(message);

    public static HostCallResult Call(string name, JsonNode? args)
    {
        var o = args as JsonObject ?? [];
        try
        {
            return name switch
            {
                "crypto.digest" => Out(Hash(Alg(o["alg"], "sha256"), Input(o["data"])), o, "hex"),
                // the first signature took { key, text, algorithm }: still answered
                "crypto.hmac" => Out(Hmac(Alg(o["alg"] ?? o["algorithm"], "sha256"), Input(o["key"]), Input(o["data"] ?? o["text"])), o, "hex"),
                "crypto.aes" => Out(Aes(o), o, o["op"]?.GetValue<string>() == "decrypt" ? "bytes" : "base64"),
                "crypto.rsa.encrypt" => Out(RsaEncrypt(o), o, "base64"),
                "crypto.sign" => Out(Sign(o), o, "base64"),
                "crypto.verify" => HostCallResult.Value(Verify(o)),
                "crypto.pbkdf2" => Out(Pbkdf2(o), o, "hex"),
                "crypto.hkdf" => Out(Hkdf(o), o, "hex"),
                "encoding.utf8Encode" => HostCallResult.Value(Bin(Encoding.UTF8.GetBytes(o["text"]?.GetValue<string>() ?? ""))),
                "encoding.utf8Decode" => HostCallResult.Value(Utf8Decode(o)),
                "encoding.encode" => HostCallResult.Value(EncodeText(Unbin(o["bin"]?.GetValue<string>() ?? ""), o["to"]?.GetValue<string>() ?? "base64")),
                "encoding.decode" => HostCallResult.Value(Bin(DecodeText(o["text"]?.GetValue<string>() ?? "", o["from"]?.GetValue<string>() ?? "base64"))),
                _ => HostCallResult.Fail("not_supported", name),
            };
        }
        catch (BadArgument e)
        {
            return HostCallResult.Fail("invalid_argument", e.Message);
        }
        catch (CryptographicException e)
        {
            return HostCallResult.Fail("invalid_argument", "the crypto operation failed: " + e.Message);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return HostCallResult.Fail("invalid_argument", e.Message);
        }
    }

    // ---- bytes in and out

    internal static string Bin(byte[] bytes) => string.Create(bytes.Length, bytes, static (span, b) =>
    {
        for (var i = 0; i < b.Length; i++) span[i] = (char)b[i];
    });

    internal static byte[] Unbin(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] > 255) throw new BadArgument("a binary string holds one byte per character");
            b[i] = (byte)s[i];
        }
        return b;
    }

    private static byte[] Input(JsonNode? n)
    {
        byte[] bytes;
        if (n is JsonValue v && v.TryGetValue<string>(out var text)) bytes = Encoding.UTF8.GetBytes(text);
        else if (n is JsonObject o)
        {
            if (o["bin"] is JsonValue b && b.TryGetValue<string>(out var bin)) bytes = Unbin(bin);
            else if (o["base64"] is JsonValue b64 && b64.TryGetValue<string>(out var s64)) bytes = DecodeText(s64, "base64");
            else if (o["hex"] is JsonValue hx && hx.TryGetValue<string>(out var hex)) bytes = DecodeText(hex, "hex");
            else if (o["text"] is JsonValue t && t.TryGetValue<string>(out var tx)) bytes = Encoding.UTF8.GetBytes(tx);
            else throw new BadArgument("data must be a string, a Uint8Array or { base64 | hex | text }");
        }
        else throw new BadArgument("data must be a string, a Uint8Array or { base64 | hex | text }");
        if (bytes.Length > InputLimit) throw new BadArgument($"input is larger than {InputLimit / 1024 / 1024} MiB");
        return bytes;
    }

    private static HostCallResult Out(byte[] bytes, JsonObject o, string fallback)
    {
        var enc = o["encoding"]?.GetValue<string>() ?? fallback;
        return enc switch
        {
            "bytes" => HostCallResult.Value(new JsonObject { ["bin"] = Bin(bytes) }),
            "utf8" => HostCallResult.Value(new UTF8Encoding(false, true).GetString(bytes)),
            "hex" or "base64" or "base64url" => HostCallResult.Value(EncodeText(bytes, enc)),
            _ => throw new BadArgument("encoding must be hex, base64, base64url, utf8 or bytes"),
        };
    }

    internal static string EncodeText(byte[] bytes, string to) => to switch
    {
        "base64" => Convert.ToBase64String(bytes),
        "base64url" => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        "hex" => Convert.ToHexStringLower(bytes),
        _ => throw new BadArgument("encoding must be base64, base64url or hex"),
    };

    internal static byte[] DecodeText(string text, string from)
    {
        switch (from)
        {
            case "hex":
                if (text.Length % 2 != 0) throw new BadArgument("hex text has an odd length");
                try { return Convert.FromHexString(text); }
                catch (FormatException) { throw new BadArgument("not valid hex text"); }
            case "base64":
            case "base64url":
            {
                var sb = new StringBuilder(text.Length + 3);
                foreach (var c in text)
                {
                    if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '=') continue;
                    sb.Append(c == '-' ? '+' : c == '_' ? '/' : c);
                }
                if (sb.Length % 4 == 1) throw new BadArgument("not valid base64 text");
                while (sb.Length % 4 != 0) sb.Append('=');
                try { return Convert.FromBase64String(sb.ToString()); }
                catch (FormatException) { throw new BadArgument("not valid base64 text"); }
            }
            default:
                throw new BadArgument("encoding must be base64, base64url or hex");
        }
    }

    private static string Utf8Decode(JsonObject o)
    {
        var bytes = Unbin(o["bin"]?.GetValue<string>() ?? "");
        var fatal = o["fatal"]?.GetValue<bool>() == true;
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF && o["ignoreBOM"]?.GetValue<bool>() != true ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, fatal).GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            throw new BadArgument("the data is not valid UTF-8");
        }
    }

    // ---- hashes

    private static HashAlgorithmName Alg(JsonNode? n, string fallback) => (n?.GetValue<string>() ?? fallback).ToLowerInvariant().Replace("-", "") switch
    {
        "md5" => HashAlgorithmName.MD5,
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha384" => HashAlgorithmName.SHA384,
        "sha512" => HashAlgorithmName.SHA512,
        _ => throw new BadArgument("algorithm must be md5, sha1, sha256, sha384 or sha512"),
    };

    private static byte[] Hash(HashAlgorithmName a, byte[] data) =>
        a == HashAlgorithmName.MD5 ? MD5.HashData(data) : a == HashAlgorithmName.SHA1 ? SHA1.HashData(data)
        : a == HashAlgorithmName.SHA256 ? SHA256.HashData(data) : a == HashAlgorithmName.SHA384 ? SHA384.HashData(data) : SHA512.HashData(data);

    private static byte[] Hmac(HashAlgorithmName a, byte[] key, byte[] data) =>
        a == HashAlgorithmName.MD5 ? HMACMD5.HashData(key, data) : a == HashAlgorithmName.SHA1 ? HMACSHA1.HashData(key, data)
        : a == HashAlgorithmName.SHA256 ? HMACSHA256.HashData(key, data) : a == HashAlgorithmName.SHA384 ? HMACSHA384.HashData(key, data) : HMACSHA512.HashData(key, data);

    // ---- AES

    private static byte[] Aes(JsonObject o)
    {
        var mode = o["mode"]?.GetValue<string>() ?? "cbc";
        var encrypt = (o["op"]?.GetValue<string>() ?? "encrypt") == "encrypt";
        var key = Input(o["key"]);
        if (key.Length is not (16 or 24 or 32)) throw new BadArgument("an AES key is 16, 24 or 32 bytes (AES-128/192/256)");
        var data = Input(o["data"]);
        var iv = o["iv"] is null ? [] : Input(o["iv"]);
        switch (mode)
        {
            case "cbc":
            case "ecb":
            {
                var padding = (o["padding"]?.GetValue<string>() ?? "pkcs7") switch
                {
                    "pkcs7" => PaddingMode.PKCS7,
                    "none" => PaddingMode.None,
                    _ => throw new BadArgument("padding must be pkcs7 or none"),
                };
                if (mode == "cbc" && iv.Length != 16) throw new BadArgument("AES-CBC needs a 16-byte iv");
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                if (mode == "ecb")
                    return encrypt ? aes.EncryptEcb(data, padding) : aes.DecryptEcb(data, padding);
                return encrypt ? aes.EncryptCbc(data, iv, padding) : aes.DecryptCbc(data, iv, padding);
            }
            case "gcm":
            {
                if (iv.Length != 12) throw new BadArgument("AES-GCM needs a 12-byte iv");
                var aad = o["aad"] is null ? [] : Input(o["aad"]);
                using var gcm = new AesGcm(key, 16);
                if (encrypt)
                {
                    var cipher = new byte[data.Length + 16];
                    gcm.Encrypt(iv, data, cipher.AsSpan(0, data.Length), cipher.AsSpan(data.Length), aad);
                    return cipher;
                }
                // ciphertext || 16-byte tag (what Node's getAuthTag appended, and WebCrypto's output), or the tag apart
                byte[] tag, body;
                if (o["tag"] is not null) { tag = Input(o["tag"]); body = data; }
                else
                {
                    if (data.Length < 16) throw new BadArgument("AES-GCM data must end with its 16-byte tag");
                    body = data[..^16];
                    tag = data[^16..];
                }
                if (tag.Length != 16) throw new BadArgument("an AES-GCM tag is 16 bytes");
                var plain = new byte[body.Length];
                gcm.Decrypt(iv, body, tag, plain, aad);
                return plain;
            }
            default:
                throw new BadArgument("mode must be cbc, ecb or gcm");
        }
    }

    // ---- RSA / ECDSA

    private static string KeyText(JsonNode? n)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var t) ? t : n is JsonObject ob && ob["text"] is JsonValue tv && tv.TryGetValue<string>(out var tt) ? tt : null;
        if (string.IsNullOrWhiteSpace(s)) throw new BadArgument("key must be a PEM string (or base64 DER)");
        if (s.Length > 20_000) throw new BadArgument("the key is too large");
        return s.Trim();
    }

    /// <summary>Reads PEM (SPKI, PKCS#1, PKCS#8) or bare base64 DER; <paramref name="importer"/> gets the DER for the bare form.</summary>
    private static void ImportRsa(RSA rsa, string key, bool priv)
    {
        if (key.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            rsa.ImportFromPem(key);
        }
        else
        {
            var der = DecodeText(key, "base64");
            if (priv)
            {
                try { rsa.ImportPkcs8PrivateKey(der, out _); }
                catch (CryptographicException) { rsa.ImportRSAPrivateKey(der, out _); }
            }
            else
            {
                try { rsa.ImportSubjectPublicKeyInfo(der, out _); }
                catch (CryptographicException) { rsa.ImportRSAPublicKey(der, out _); }
            }
        }
        if (rsa.KeySize is < 512 or > 8192) throw new BadArgument("an RSA key is 512 to 8192 bits");
    }

    private static byte[] RsaEncrypt(JsonObject o)
    {
        using var rsa = RSA.Create();
        ImportRsa(rsa, KeyText(o["key"]), priv: false);
        var data = Input(o["data"]);
        var padding = (o["padding"]?.GetValue<string>() ?? "pkcs1") switch
        {
            "pkcs1" => RSAEncryptionPadding.Pkcs1,
            "oaep" => RSAEncryptionPadding.CreateOaep(Alg(o["hash"], "sha1")),
            _ => throw new BadArgument("padding must be pkcs1 or oaep"),
        };
        return rsa.Encrypt(data, padding);
    }

    private static (string Kind, HashAlgorithmName Hash) SignAlg(JsonNode? n)
    {
        var s = (n?.GetValue<string>() ?? "").ToLowerInvariant();
        return s switch
        {
            "rsa-sha1" => ("rsa", HashAlgorithmName.SHA1),
            "rsa-sha256" => ("rsa", HashAlgorithmName.SHA256),
            "rsa-sha384" => ("rsa", HashAlgorithmName.SHA384),
            "rsa-sha512" => ("rsa", HashAlgorithmName.SHA512),
            "rsa-pss-sha256" => ("pss", HashAlgorithmName.SHA256),
            "ecdsa-p256-sha256" => ("p256", HashAlgorithmName.SHA256),
            "ecdsa-p384-sha384" => ("p384", HashAlgorithmName.SHA384),
            _ => throw new BadArgument("algorithm must be rsa-sha1|sha256|sha384|sha512, rsa-pss-sha256, ecdsa-p256-sha256 or ecdsa-p384-sha384"),
        };
    }

    private static ECDsa EcKey(string key, bool priv, string curve)
    {
        var ec = ECDsa.Create();
        if (key.Contains("-----BEGIN", StringComparison.Ordinal)) ec.ImportFromPem(key);
        else
        {
            var der = DecodeText(key, "base64");
            if (priv)
            {
                try { ec.ImportPkcs8PrivateKey(der, out _); }
                catch (CryptographicException) { ec.ImportECPrivateKey(der, out _); }
            }
            else ec.ImportSubjectPublicKeyInfo(der, out _);
        }
        var bits = ec.KeySize;
        if (bits != (curve == "p256" ? 256 : 384)) { ec.Dispose(); throw new BadArgument($"the key is not a {curve.ToUpperInvariant()} key"); }
        return ec;
    }

    private static DSASignatureFormat EcFormat(JsonObject o) => (o["format"]?.GetValue<string>() ?? "der") switch
    {
        "der" => DSASignatureFormat.Rfc3279DerSequence,
        "raw" or "p1363" => DSASignatureFormat.IeeeP1363FixedFieldConcatenation,
        _ => throw new BadArgument("format must be der or raw"),
    };

    private static byte[] Sign(JsonObject o)
    {
        var (kind, hash) = SignAlg(o["algorithm"]);
        var key = KeyText(o["key"]);
        var data = Input(o["data"]);
        if (kind is "rsa" or "pss")
        {
            using var rsa = RSA.Create();
            ImportRsa(rsa, key, priv: true);
            return rsa.SignData(data, hash, kind == "rsa" ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
        }
        using var ec = EcKey(key, true, kind);
        return ec.SignData(data, hash, EcFormat(o));
    }

    private static bool Verify(JsonObject o)
    {
        var (kind, hash) = SignAlg(o["algorithm"]);
        var key = KeyText(o["key"]);
        var data = Input(o["data"]);
        var sig = Input(o["signature"]);
        if (kind is "rsa" or "pss")
        {
            using var rsa = RSA.Create();
            ImportRsa(rsa, key, priv: false);
            return rsa.VerifyData(data, sig, hash, kind == "rsa" ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
        }
        using var ec = EcKey(key, false, kind);
        return ec.VerifyData(data, sig, hash, EcFormat(o));
    }

    // ---- key derivation

    private static byte[] Pbkdf2(JsonObject o)
    {
        var iterations = o["iterations"]?.GetValue<int>() ?? 100_000;
        var length = o["length"]?.GetValue<int>() ?? 32;
        if (iterations is < 1 or > 1_000_000) throw new BadArgument("iterations is 1 to 1,000,000");
        if (length is < 1 or > 1024) throw new BadArgument("length is 1 to 1024 bytes");
        return Rfc2898DeriveBytes.Pbkdf2(Input(o["password"]), Input(o["salt"]), iterations, Alg(o["hash"], "sha256"), length);
    }

    private static byte[] Hkdf(JsonObject o)
    {
        var length = o["length"]?.GetValue<int>() ?? 32;
        if (length is < 1 or > 1024) throw new BadArgument("length is 1 to 1024 bytes");
        return HKDF.DeriveKey(Alg(o["hash"], "sha256"), Input(o["key"]), length, o["salt"] is null ? [] : Input(o["salt"]), o["info"] is null ? [] : Input(o["info"]));
    }
}
