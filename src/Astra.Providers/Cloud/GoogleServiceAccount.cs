using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Providers.Cloud;

/// <summary>A Google service account's key file (the JSON Google Cloud hands out), as the provider's encrypted secret keeps it.</summary>
public sealed record GoogleServiceAccount(string ClientEmail, string PrivateKeyPem, string ProjectId, string TokenUri)
{
    /// <summary>Where Google's tokens are traded; a key file naming another address is refused (the signed assertion goes there).</summary>
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>Reads and checks the key file; <paramref name="error"/> says what is wrong (never echoes the key).</summary>
    public static GoogleServiceAccount? TryParse(string? json, out string? error)
    {
        error = null;
        JsonObject? o;
        try { o = JsonNode.Parse(json ?? "") as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) { error = "A Google service account key must be the JSON file downloaded from Google Cloud"; return null; }
        var type = Text(o, "type");
        if (type is not null && type != "service_account") { error = $"The key's type is \"{type}\"; only service_account keys are supported"; return null; }
        var email = Text(o, "client_email");
        var pem = Text(o, "private_key");
        if (string.IsNullOrEmpty(email)) { error = "client_email is missing from the key file"; return null; }
        if (string.IsNullOrEmpty(pem)) { error = "private_key is missing from the key file"; return null; }
        try { using var rsa = RSA.Create(); rsa.ImportFromPem(pem); }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            error = "private_key is not a valid PEM private key";
            return null;
        }
        var uri = Text(o, "token_uri") ?? DefaultTokenUri;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
            || !(parsed.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) || parsed.Host == "accounts.google.com"))
        {
            error = "token_uri must be an https address at googleapis.com";
            return null;
        }
        return new GoogleServiceAccount(email, pem, Text(o, "project_id") ?? "", uri);
    }

    /// <summary>What the admin UI shows instead of the key: the service account's address, which is no secret.</summary>
    public string Mask() => ClientEmail;

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
}

/// <summary>
/// Trades a service account's signed JWT (RS256, <see cref="System.Security.Cryptography.RSA"/>) for a short-lived OAuth
/// access token and caches it per account until shortly before it expires. The assertion goes to the key file's
/// <c>token_uri</c> (Google's, checked in <see cref="GoogleServiceAccount.TryParse"/>). The flow is the one every Google
/// client library implements for service accounts (RFC 7523); magpie's <c>vertex.go</c> does the same.
/// </summary>
public sealed class GoogleAccessTokens
{
    /// <summary>Everything Vertex AI needs.</summary>
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly Func<DateTimeOffset> _clock;

    public GoogleAccessTokens(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>A valid access token for the account, minted when none is cached or the cached one is about to expire.</summary>
    /// <exception cref="HttpRequestException">Google refused the assertion or could not be reached.</exception>
    public async Task<string> GetAsync(GoogleServiceAccount account, HttpClient http, CancellationToken ct)
    {
        // The key is the account plus a hash of the private key, so a replaced key is never served an old account's token.
        var key = account.ClientEmail + "|" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account.PrivateKeyPem)))[..16];
        if (Fresh(key) is { } cached) return cached;
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(key) is { } again) return again;
            var now = _clock();
            using var request = new HttpRequestMessage(HttpMethod.Post, account.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = CreateAssertion(account, now),
                }),
            };
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google refused the service account's sign-in (HTTP {(int)response.StatusCode}): {Describe(text)}");
            var answer = JsonNode.Parse(text) as JsonObject;
            var token = answer?["access_token"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrEmpty(token)) throw new HttpRequestException("Google's token answer has no access_token");
            var seconds = answer?["expires_in"] is JsonValue e && e.TryGetValue<long>(out var n) && n > 0 ? n : 3600;
            _cache[key] = (token, now.AddSeconds(seconds));
            return token;
        }
        finally { gate.Release(); }
    }

    private string? Fresh(string key) =>
        _cache.TryGetValue(key, out var hit) && hit.ExpiresAt - Margin > _clock() ? hit.Token : null;

    /// <summary>The signed JWT (<c>header.claims.signature</c>, base64url) that asks for a token for <see cref="Scope"/>.</summary>
    public static string CreateAssertion(GoogleServiceAccount account, DateTimeOffset now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var iat = now.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = account.ClientEmail, ["scope"] = Scope, ["aud"] = account.TokenUri,
            ["iat"] = iat, ["exp"] = iat + 3600,
        };
        var signingInput = header + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Google's error body names the problem ("invalid_grant: Invalid JWT Signature") and carries no secret.
    private static string Describe(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                var error = o["error"]?.ToString();
                var description = o["error_description"]?.ToString();
                return string.Join(": ", new[] { error, description }.Where(x => !string.IsNullOrEmpty(x)));
            }
        }
        catch (JsonException) { /* not JSON */ }
        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>The cached tokens are dropped (a key replaced, a provider deleted).</summary>
    public void Clear() { _cache.Clear(); }
}
