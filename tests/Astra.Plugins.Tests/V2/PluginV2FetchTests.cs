using System.Net;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Tests.V2;

/// <summary>http.fetch's redirect option, Cookie / Set-Cookie and the rules that keep a redirect inside the plugin's net: grants
/// (design/plugin-v2 02 §http).</summary>
public class PluginV2FetchTests
{
    private const string Code = """
        import { http, commands, definePlugin } from 'astra';
        export default definePlugin({
          activate() {
            commands.register('get', async ({ url, init }) => {
              try {
                const r = await http.fetch(url, init);
                return { status: r.status, url: r.url, location: r.headers.location ?? null, cookies: r.setCookies, body: await r.text() };
              } catch (e) { return { error: e.code, message: e.message }; }
            });
          },
        });
        """;

    private static async Task<(V2TestHost Host, List<HttpRequestMessage> Seen)> Start(Func<HttpRequestMessage, HttpResponseMessage> handler, params string[] origins)
    {
        var t = new V2TestHost();
        var seen = new List<HttpRequestMessage>();
        t.Services.Handler = req =>
        {
            lock (seen) seen.Add(req);
            return handler(req);
        };
        await t.AddAsync("acme.fetch", new JsonObject
        {
            ["main"] = "main.js",
            ["permissions"] = new JsonArray([.. origins.Select(o => (JsonNode?)("net:" + o))]),
            ["contributes"] = new JsonObject { ["commands"] = new JsonArray(new JsonObject { ["id"] = "get" }) },
        }, [("main.js", Code)]);
        return (t, seen);
    }

    private static async Task<JsonNode> Get(V2TestHost t, string url, JsonObject? init = null) =>
        (await t.Host.ExecuteCommandAsync("acme.fetch:get", new JsonObject { ["url"] = url, ["init"] = init }, null, default))!;

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found, params string[] cookies)
    {
        var res = new HttpResponseMessage(code);
        res.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        if (cookies.Length > 0) res.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        return res;
    }

    [Fact]
    public async Task A_cookie_goes_out_and_every_set_cookie_comes_back()
    {
        var (t, seen) = await Start(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hi") };
            res.Headers.TryAddWithoutValidation("Set-Cookie", ["sid=abc; Path=/; HttpOnly", "theme=dark; Max-Age=60"]);
            return res;
        }, "https://sso.example.com");
        await using var _ = t;
        var r = await Get(t, "https://sso.example.com/login", new JsonObject { ["headers"] = new JsonObject { ["cookie"] = "a=1; b=2" } });
        Assert.Equal("a=1; b=2", seen.Single().Headers.GetValues("Cookie").Single());
        Assert.Equal(new[] { "sid=abc; Path=/; HttpOnly", "theme=dark; Max-Age=60" }, r["cookies"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal("hi", r["body"]!.ToString());
    }

    [Fact]
    public async Task Redirect_manual_returns_the_redirect_with_its_cookies_and_follows_nothing()
    {
        var (t, seen) = await Start(_ => Redirect("https://sso.example.com/next?code=1", HttpStatusCode.Found, "sso=1; Path=/"), "https://sso.example.com");
        await using var _ = t;
        var r = await Get(t, "https://sso.example.com/start", new JsonObject { ["redirect"] = "manual" });
        Assert.Equal(302, r["status"]!.GetValue<int>());
        Assert.Equal("https://sso.example.com/next?code=1", r["location"]!.ToString());
        Assert.Equal("sso=1; Path=/", r["cookies"]![0]!.ToString());
        Assert.Single(seen);
    }

    [Fact]
    public async Task Redirect_error_refuses_a_redirect_and_an_unknown_mode_is_an_argument_error()
    {
        var (t, seen) = await Start(_ => Redirect("/elsewhere"), "https://sso.example.com");
        await using var _ = t;
        Assert.Equal("redirect_refused", (await Get(t, "https://sso.example.com/a", new JsonObject { ["redirect"] = "error" }))["error"]!.ToString());
        Assert.Single(seen);
        Assert.Equal("invalid_argument", (await Get(t, "https://sso.example.com/a", new JsonObject { ["redirect"] = "later" }))["error"]!.ToString());
    }

    [Fact]
    public async Task Credentials_follow_a_same_origin_redirect_but_never_leave_the_origin()
    {
        var (t, seen) = await Start(req => req.RequestUri!.AbsolutePath switch
        {
            "/start" => Redirect("/same"),
            "/same" => Redirect("https://other.example.com/landed"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") },
        }, "https://a.example.com", "https://other.example.com");
        await using var _ = t;
        var headers = new JsonObject { ["cookie"] = "sid=1", ["authorization"] = "Bearer secret", ["x-keep"] = "yes" };
        var r = await Get(t, "https://a.example.com/start", new JsonObject { ["headers"] = headers });
        Assert.Equal("done", r["body"]!.ToString());
        Assert.Equal("https://other.example.com/landed", r["url"]!.ToString());
        Assert.Equal(3, seen.Count);
        foreach (var same in seen.Take(2))
        {
            Assert.True(same.Headers.Contains("Cookie"));
            Assert.True(same.Headers.Contains("Authorization"));
        }
        var cross = seen[2];
        Assert.False(cross.Headers.Contains("Cookie"));
        Assert.False(cross.Headers.Contains("Authorization"));
        Assert.Equal("yes", cross.Headers.GetValues("x-keep").Single()); // an ordinary header the plugin chose still goes
    }

    [Fact]
    public async Task A_credential_dropped_by_a_cross_origin_hop_stays_dropped_when_the_chain_returns()
    {
        var (t, seen) = await Start(req => req.RequestUri!.Host switch
        {
            "a.example.com" when req.RequestUri.AbsolutePath == "/start" => Redirect("https://b.example.com/x"),
            "b.example.com" => Redirect("https://a.example.com/back"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("end") },
        }, "https://a.example.com", "https://b.example.com");
        await using var _ = t;
        await Get(t, "https://a.example.com/start", new JsonObject { ["headers"] = new JsonObject { ["cookie"] = "sid=1" } });
        Assert.Equal(3, seen.Count);
        Assert.True(seen[0].Headers.Contains("Cookie"));
        Assert.False(seen[2].Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task A_redirect_outside_the_grants_or_to_a_private_address_is_refused()
    {
        var (t, seen) = await Start(req => req.RequestUri!.AbsolutePath switch
        {
            "/evil" => Redirect("https://evil.example.com/steal"),
            "/lan" => Redirect("https://10.0.0.5/admin"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        }, "https://a.example.com", "https://10.0.0.5");
        await using var _ = t;
        var evil = await Get(t, "https://a.example.com/evil", new JsonObject { ["headers"] = new JsonObject { ["authorization"] = "Bearer secret" } });
        Assert.Equal("permission_denied", evil["error"]!.ToString());
        var lan = await Get(t, "https://a.example.com/lan");
        Assert.Equal("permission_denied", lan["error"]!.ToString());
        Assert.Contains("private", lan["message"]!.ToString());
        Assert.DoesNotContain(seen, r => r.RequestUri!.Host is "evil.example.com" or "10.0.0.5");
        // asked for directly, a granted private address is refused too
        Assert.Equal("permission_denied", (await Get(t, "https://10.0.0.5/"))["error"]!.ToString());
        // and redirect: 'manual' hands back the Location without checking or following it
        var manual = await Get(t, "https://a.example.com/evil", new JsonObject { ["redirect"] = "manual" });
        Assert.Equal("https://evil.example.com/steal", manual["location"]!.ToString());
    }
}
