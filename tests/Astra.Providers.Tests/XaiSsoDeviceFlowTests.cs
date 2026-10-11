using System.Net;
using Astra.Providers.Subscription;

namespace Astra.Providers.Tests;

public class XaiSsoDeviceFlowTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, string? Cookie)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("Cookie", out var cookie);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body, cookie?.FirstOrDefault()));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json) };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static readonly SubscriptionOAuthConfig Config = SubscriptionCatalog.Find("grok-subscription")!;
    private const string Sso = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

    private const string Consent =
        """<form method="post" action="/oauth2/device/approve"><input type="hidden" name="csrf" value="a&amp;b"><input name="x" value="no"></form>""";

    private static Func<HttpRequestMessage, string, HttpResponseMessage> Script(Func<int, HttpResponseMessage>? poll = null)
    {
        var polls = 0;
        return (req, body) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/") => Json("<html>ok</html>"),
            ("POST", "/oauth2/device/code") => Json(
                """{"device_code":"dc","user_code":"UC","verification_uri_complete":"https://accounts.x.ai/device?user_code=UC","interval":1,"expires_in":600}"""),
            ("GET", "/device") => Json("<html>enter</html>"),
            ("POST", "/oauth2/device/verify") => Redirect("https://accounts.x.ai/consent?u=UC"),
            ("GET", "/consent") => Json(Consent),
            ("POST", "/oauth2/device/approve") => Redirect("https://accounts.x.ai/done"),
            ("GET", "/done") => Json("done"),
            ("POST", "/oauth2/token") => poll?.Invoke(++polls) ?? Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}"""),
            _ => Json("{}", HttpStatusCode.NotFound),
        };
    }

    private static Handler Happy(Func<int, HttpResponseMessage>? poll = null) => new(Script(poll));

    private static Task<OAuthClient.TokenResult> Run(Handler h, string sso = Sso) =>
        XaiSsoDeviceFlow.ConvertAsync(new HttpClient(h), Config, sso, (_, _) => Task.CompletedTask, CancellationToken.None);

    [Fact]
    public async Task Converts_A_Sso_Cookie_And_Replays_The_Consent_Form()
    {
        var h = Happy(n => n == 1 ? Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest)
            : n == 2 ? Json("""{"error":"slow_down"}""", HttpStatusCode.BadRequest)
            : Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}"""));
        var token = await Run(h, $"a@b.com----pw----totp----{Sso}");

        Assert.Equal("at", token.AccessToken);
        Assert.Equal("rt", token.RefreshToken);
        Assert.All(h.Calls, c => Assert.Contains($"sso={Sso}", c.Cookie));

        var approve = h.Calls.Single(c => c.Url.EndsWith("/oauth2/device/approve", StringComparison.Ordinal));
        Assert.Contains("csrf=a%26b", approve.Body);
        Assert.Contains("action=allow", approve.Body);
        Assert.Contains("user_code=UC", approve.Body);
        Assert.DoesNotContain("x=no", approve.Body);
        Assert.Contains("client_id=" + Config.ClientId, h.Calls.First(c => c.Url.EndsWith("/device/code", StringComparison.Ordinal)).Body);
    }

    [Fact]
    public async Task An_Expired_Cookie_Is_Reported_As_Such()
    {
        var h = new Handler((req, _) => req.RequestUri!.AbsolutePath == "/" ? Redirect("https://accounts.x.ai/sign-in") : Json("login"));
        var e = await Assert.ThrowsAsync<OAuthProtocolException>(() => Run(h));
        Assert.Contains("SSO cookie 已失效", e.Message);
    }

    [Fact]
    public async Task A_Refused_Approval_Carries_The_Status()
    {
        var inner = Script();
        var h = new Handler((req, body) => req.RequestUri!.AbsolutePath == "/oauth2/device/approve"
            ? Json("blocked by cloudflare", HttpStatusCode.Forbidden)
            : inner.Invoke(req, body));
        var e = await Assert.ThrowsAsync<OAuthProtocolException>(() => Run(h));
        Assert.Contains("HTTP 403", e.Message);
        Assert.Contains("blocked by cloudflare", e.Message);
    }

    [Fact]
    public async Task Denied_Polling_Fails()
    {
        var h = Happy(_ => Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest));
        var e = await Assert.ThrowsAsync<OAuthProtocolException>(() => Run(h));
        Assert.Contains("拒绝", e.Message);
    }

    [Fact]
    public async Task Never_Follows_A_Redirect_Off_Xai()
    {
        var h = new Handler((_, _) => Redirect("https://evil.example.com/steal"));
        var e = await Assert.ThrowsAsync<OAuthProtocolException>(() => Run(h));
        Assert.Contains("不可信", e.Message);
        Assert.Single(h.Calls);
    }

    [Theory]
    [InlineData("https://auth.x.ai/a", true)]
    [InlineData("https://x.ai/", true)]
    [InlineData("http://auth.x.ai/a", false)]
    [InlineData("https://evilx.ai/", false)]
    [InlineData("https://auth.x.ai.evil.com/", false)]
    [InlineData("https://u:p@auth.x.ai/", false)]
    public void Trusts_Only_Https_Xai_Hosts(string url, bool trusted) => Assert.Equal(trusted, XaiSsoDeviceFlow.IsTrusted(url));
}
