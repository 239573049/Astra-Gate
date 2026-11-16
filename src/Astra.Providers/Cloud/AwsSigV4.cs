using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Providers.Cloud;

/// <summary>
/// An AWS IAM access key, as the provider's encrypted secret keeps it: <c>{"accessKeyId", "secretAccessKey", "sessionToken"?}</c>.
/// The spellings of <c>aws sts</c> output (<c>AccessKeyId</c> …) and snake_case are read as well.
/// </summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken)
{
    /// <summary>Reads the secret; <paramref name="error"/> says what is missing or malformed (never echoes a value).</summary>
    public static AwsCredentials? TryParse(string? json, out string? error)
    {
        error = null;
        JsonObject? o;
        try { o = JsonNode.Parse(json ?? "") as JsonObject; }
        catch (System.Text.Json.JsonException) { o = null; }
        if (o is null)
        {
            error = "AWS credentials must be a JSON object with accessKeyId, secretAccessKey and optionally sessionToken";
            return null;
        }
        // "Credentials" wrapper of `aws sts get-session-token` / `assume-role` output
        if (Find(o, "credentials") is JsonObject inner) o = inner;
        var id = Text(o, "accesskeyid");
        var secret = Text(o, "secretaccesskey");
        var token = Text(o, "sessiontoken");
        if (string.IsNullOrEmpty(id)) { error = "accessKeyId is missing"; return null; }
        if (string.IsNullOrEmpty(secret)) { error = "secretAccessKey is missing"; return null; }
        if (id.Any(c => c <= ' ' || c > '~') || secret.Any(c => c <= ' ' || c > '~') || token?.Any(c => c <= ' ' || c > '~') == true)
        {
            error = "AWS credentials must not contain spaces or non-ASCII characters";
            return null;
        }
        return new AwsCredentials(id, secret, string.IsNullOrEmpty(token) ? null : token);
    }

    private static string? Text(JsonObject o, string normalized) =>
        Find(o, normalized) is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static JsonNode? Find(JsonObject o, string normalized)
    {
        foreach (var (name, value) in o)
            if (value is JsonValue or JsonObject && name.Replace("_", "").Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>A masked form for the admin UI: the access key id's head and tail.</summary>
    public string Mask() => AccessKeyId.Length >= 12 ? AccessKeyId[..4] + "…" + AccessKeyId[^4..] : "••••";
}

/// <summary>How a request is to be signed: whose key, in which region, for which service (the SigV4 "credential scope").</summary>
public sealed record AwsSigning(AwsCredentials Credentials, string Region, string Service)
{
    /// <summary>Request option the credentials ride on until the body is final and <see cref="AwsSigV4.SignIfRequested"/> signs.</summary>
    public static readonly HttpRequestOptionsKey<AwsSigning> Option = new("astra.aws-signing");
}

/// <summary>
/// AWS Signature Version 4, written out by hand (no AWS SDK: it is not AOT-clean). Signs <c>host</c>, <c>x-amz-date</c> and
/// <c>x-amz-security-token</c> — enough for the Bedrock runtime — and puts the result in <c>Authorization</c>. Checked
/// against AWS's published test vectors (<c>AwsSigV4Tests</c>).
/// </summary>
public static class AwsSigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Signs the request when its credentials were attached (<see cref="AwsSigning.Option"/>); a no-op otherwise.</summary>
    public static void SignIfRequested(HttpRequestMessage request, ReadOnlySpan<byte> body)
    {
        if (request.Options.TryGetValue(AwsSigning.Option, out var signing))
            Sign(request, body, signing.Credentials, signing.Region, signing.Service, DateTimeOffset.UtcNow);
    }

    /// <summary>Adds <c>x-amz-date</c>, <c>x-amz-security-token</c> (with a session token) and <c>Authorization</c>.</summary>
    public static void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body, AwsCredentials credentials, string region, string service, DateTimeOffset now)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no address to sign");
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Remove("x-amz-security-token");
        if (credentials.SessionToken is { } token) request.Headers.TryAddWithoutValidation("x-amz-security-token", token);
        request.Headers.Remove("Authorization");

        // Header names sorted ordinally, as the canonical form wants them.
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host, ["x-amz-date"] = amzDate };
        if (credentials.SessionToken is { } sessionToken) signed["x-amz-security-token"] = sessionToken;

        var canonicalHeaders = new StringBuilder();
        foreach (var (name, value) in signed) canonicalHeaders.Append(name).Append(':').Append(value.Trim()).Append('\n');
        var signedHeaders = string.Join(';', signed.Keys);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(body));

        var canonicalRequest = string.Join('\n', request.Method.Method, CanonicalPath(uri.AbsolutePath), CanonicalQuery(uri.Query),
            canonicalHeaders.ToString(), signedHeaders, payloadHash);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signature = Convert.ToHexStringLower(Hmac(SigningKey(credentials.SecretAccessKey, date, region, service), stringToSign));
        request.Headers.TryAddWithoutValidation("Authorization",
            $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>The derived signing key: HMAC chain over date, region, service and <c>aws4_request</c>.</summary>
    public static byte[] SigningKey(string secret, string date, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>The region an AWS endpoint host names: <c>bedrock-runtime.us-east-1.amazonaws.com</c> and <c>….us-east-1.api.aws</c>.</summary>
    public static string? RegionOf(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        if (labels.Length >= 4 && (host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".api.aws", StringComparison.OrdinalIgnoreCase)))
            return labels[^3] is var region && region.Length > 0 && char.IsAsciiLetter(region[0]) && region.Contains('-') ? region : null;
        return null;
    }

    /// <summary>The signing service of an AWS endpoint host: the Bedrock runtime and agent hosts sign as <c>bedrock</c>, Mantle as its own name.</summary>
    public static string? ServiceOf(string host)
    {
        var first = host.ToLowerInvariant().Split('.')[0];
        if (first == "bedrock-mantle") return first;
        if (first.StartsWith("bedrock", StringComparison.Ordinal)) return "bedrock";
        return RegionOf(host) is null ? null : first;
    }

    // The request's own (already once-encoded) path, encoded again segment by segment: the rule for every service but S3.
    private static string CanonicalPath(string absolutePath)
    {
        if (absolutePath.Length == 0) return "/";
        return string.Join('/', absolutePath.Split('/').Select(segment => Encode(segment)));
    }

    private static string CanonicalQuery(string query)
    {
        if (query.Length <= 1) return "";
        var pairs = new List<(string Name, string Value)>();
        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add((Encode(Uri.UnescapeDataString(name.Replace('+', ' '))), Encode(Uri.UnescapeDataString(value.Replace('+', ' ')))));
        }
        pairs.Sort((a, b) => { var c = string.CompareOrdinal(a.Name, b.Name); return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value); });
        return string.Join('&', pairs.Select(p => p.Name + "=" + p.Value));
    }

    // RFC 3986 unreserved characters stay, everything else is %XX (upper case), over the UTF-8 bytes.
    private static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
