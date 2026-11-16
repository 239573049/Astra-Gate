using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Providers.Cloud;

namespace Astra.Providers.Tests;

public class GoogleAccessTokensTests
{
    // A throwaway key made for the test run; it never signs anything real.
    private static readonly RSA Key = RSA.Create(2048);

    private static string KeyFile(string? tokenUri = null, string? project = "demo-project", string? type = "service_account")
    {
        var o = new JsonObject
        {
            ["client_email"] = "astra@demo-project.iam.gserviceaccount.com",
            ["private_key"] = Key.ExportPkcs8PrivateKeyPem(),
        };
        if (type is not null) o["type"] = type;
        if (project is not null) o["project_id"] = project;
        if (tokenUri is not null) o["token_uri"] = tokenUri;
        return o.ToJsonString();
    }

    private static GoogleServiceAccount Account(string? tokenUri = null) =>
        GoogleServiceAccount.TryParse(KeyFile(tokenUri), out var error) ?? throw new Xunit.Sdk.XunitException(error);

    private sealed class FakeGoogle(Func<HttpRequestMessage, string, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!, body));
            return respond?.Invoke(request, body) ?? Json("""{"access_token":"ya29.token","expires_in":3600,"token_type":"Bearer"}""");
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string FormField(string body, string name) =>
        Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..].Replace('+', ' '));

    private static byte[] Unbase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    [Fact]
    public void The_Assertion_Is_A_Verifiable_RS256_Jwt_For_The_Cloud_Platform_Scope()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var jwt = GoogleAccessTokens.CreateAssertion(Account(), now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("""{"alg":"RS256","typ":"JWT"}""", Encoding.UTF8.GetString(Unbase64Url(parts[0])));
        var claims = JsonNode.Parse(Unbase64Url(parts[1]))!.AsObject();
        Assert.Equal("astra@demo-project.iam.gserviceaccount.com", claims["iss"]!.GetValue<string>());
        Assert.Equal("https://www.googleapis.com/auth/cloud-platform", claims["scope"]!.GetValue<string>());
        Assert.Equal("https://oauth2.googleapis.com/token", claims["aud"]!.GetValue<string>());
        Assert.Equal(now.ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
        Assert.Equal(now.ToUnixTimeSeconds() + 3600, claims["exp"]!.GetValue<long>());
        Assert.True(Key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Unbase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.DoesNotContain("=", jwt);
    }

    [Fact]
    public async Task A_Token_Is_Minted_Once_And_Reused_Until_Shortly_Before_It_Expires()
    {
        var clock = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var tokens = new GoogleAccessTokens(() => clock);
        var google = new FakeGoogle();
        using var http = new HttpClient(google);
        var account = Account();

        Assert.Equal("ya29.token", await tokens.GetAsync(account, http, default));
        var (url, body) = Assert.Single(google.Requests);
        Assert.Equal("https://oauth2.googleapis.com/token", url.ToString());
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", FormField(body, "grant_type"));
        Assert.Equal(3, FormField(body, "assertion").Split('.').Length);

        clock = clock.AddMinutes(54); // 6 minutes left: still fresh
        await tokens.GetAsync(account, http, default);
        Assert.Single(google.Requests);

        clock = clock.AddMinutes(2); // 4 minutes left: inside the margin, so it is renewed
        await tokens.GetAsync(account, http, default);
        Assert.Equal(2, google.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_Callers_Share_One_Exchange()
    {
        var tokens = new GoogleAccessTokens();
        var google = new FakeGoogle();
        using var http = new HttpClient(google);
        var account = Account();
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => tokens.GetAsync(account, http, default)));
        Assert.All(all, t => Assert.Equal("ya29.token", t));
        Assert.Single(google.Requests);
    }

    [Fact]
    public async Task A_Refusal_Names_The_Reason_And_Not_The_Key()
    {
        var google = new FakeGoogle((_, _) => FakeGoogle.Json("""{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""", HttpStatusCode.BadRequest));
        using var http = new HttpClient(google);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new GoogleAccessTokens().GetAsync(Account(), http, default));
        Assert.Contains("invalid_grant: Invalid JWT Signature.", e.Message);
        Assert.DoesNotContain("PRIVATE KEY", e.Message);
    }

    [Fact]
    public async Task An_Answer_Without_A_Token_Is_An_Error_And_A_Failure_Is_Not_Cached()
    {
        var n = 0;
        var google = new FakeGoogle((_, _) => ++n == 1 ? FakeGoogle.Json("{}") : FakeGoogle.Json("""{"access_token":"second","expires_in":3600}"""));
        using var http = new HttpClient(google);
        var tokens = new GoogleAccessTokens();
        await Assert.ThrowsAsync<HttpRequestException>(() => tokens.GetAsync(Account(), http, default));
        Assert.Equal("second", await tokens.GetAsync(Account(), http, default));
    }

    [Fact]
    public void The_Key_File_Is_Checked_Without_Echoing_It()
    {
        Assert.NotNull(GoogleServiceAccount.TryParse(KeyFile(), out _));
        Assert.Equal("demo-project", GoogleServiceAccount.TryParse(KeyFile(), out _)!.ProjectId);

        Assert.Null(GoogleServiceAccount.TryParse("nope", out var notJson));
        Assert.Contains("JSON", notJson);
        Assert.Null(GoogleServiceAccount.TryParse(KeyFile(type: "authorized_user"), out var wrongType));
        Assert.Contains("authorized_user", wrongType);
        Assert.Null(GoogleServiceAccount.TryParse("""{"client_email":"a@b.c","private_key":"-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n"}""", out var badPem));
        Assert.Contains("PEM", badPem);
        Assert.Null(GoogleServiceAccount.TryParse("""{"private_key":"x"}""", out var noEmail));
        Assert.Contains("client_email", noEmail);
    }

    [Theory]
    [InlineData("https://evil.example/token")]
    [InlineData("http://oauth2.googleapis.com/token")]
    [InlineData("https://googleapis.com.evil.example/token")]
    public void A_Token_Address_Outside_Google_Is_Refused(string tokenUri)
    {
        Assert.Null(GoogleServiceAccount.TryParse(KeyFile(tokenUri), out var error));
        Assert.Contains("token_uri", error);
    }

    [Fact]
    public void The_Mask_Is_The_Service_Accounts_Address_Never_The_Key() =>
        Assert.DoesNotContain("PRIVATE", Account().Mask());
}
