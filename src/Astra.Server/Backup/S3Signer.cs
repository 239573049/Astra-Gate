using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Astra.Providers.Cloud;

namespace Astra.Server.Backup;

/// <summary>
/// AWS Signature Version 4 for S3 and the S3-compatible servers (R2, MinIO, B2, …): every header the request has at signing time plus
/// <c>host</c> is signed, the path is encoded once (S3's rule — <see cref="AwsSigV4"/>, the Bedrock signer, encodes twice and signs a fixed header
/// set), and the payload hash goes in <c>x-amz-content-sha256</c>. The signing-key chain is <see cref="AwsSigV4.SigningKey"/>.
/// Ported from magpie's <c>internal/davsync/sigv4.go</c> (MIT); checked against AWS's published examples (<c>S3SignerTests</c>).
/// </summary>
public static class S3Signer
{
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The <c>Authorization</c> value for the request. <paramref name="headers"/> are the headers that will be sent (and signed); <c>x-amz-date</c> is
    /// added here and returned through <paramref name="amzDate"/>, the caller sends it. <paramref name="escapedPath"/> is the request path as it goes on the wire.
    /// </summary>
    public static string Sign(string method, string host, string escapedPath, string? query, IDictionary<string, string> headers, string payloadHash,
        string accessKey, string secretKey, string region, string service, DateTimeOffset now, out string amzDate)
    {
        amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host, ["x-amz-date"] = amzDate };
        foreach (var (name, value) in headers)
        {
            var lower = name.ToLowerInvariant();
            if (lower is "authorization" or "host" or "x-amz-date") continue;
            signed[lower] = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        var canonicalHeaders = new StringBuilder();
        foreach (var (k, v) in signed) canonicalHeaders.Append(k).Append(':').Append(v).Append('\n');
        var signedHeaders = string.Join(';', signed.Keys);
        var canonical = string.Join('\n', method, escapedPath.Length == 0 ? "/" : escapedPath, CanonicalQuery(query), canonicalHeaders.ToString(), signedHeaders, payloadHash);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonical))}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(AwsSigV4.SigningKey(secretKey, date, region, service), Encoding.UTF8.GetBytes(toSign)));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>The query, each name and value escaped as AWS does and sorted: <c>?lifecycle</c> is <c>lifecycle=</c>.</summary>
    public static string CanonicalQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var pairs = new List<string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            pairs.Add(Escape(name, false) + "=" + Escape(value, false));
        }
        pairs.Sort(StringComparer.Ordinal);
        return string.Join('&', pairs);
    }

    /// <summary>URI encoding as SigV4 has it: everything but A–Z a–z 0–9 - _ . ~ as upper-case %XX over UTF-8; <c>/</c> kept in a path.</summary>
    public static string Escape(string value, bool path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (path && c == '/'))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
