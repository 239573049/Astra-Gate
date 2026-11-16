using System.Net;
using System.Text;
using Astra.Core.Tools;
using Astra.Gateway.Tools;

namespace Astra.Gateway.Tests;

/// <summary>web_fetch: the SSRF guard, redirects, limits and the HTML-to-text conversion.</summary>
public class WebFetchTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)] // cloud metadata
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("64:ff9b::7f00:1", true)] // NAT64 of 127.0.0.1
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void The_guard_blocks_every_address_that_is_not_public(string address, bool blocked) =>
        Assert.Equal(blocked, SsrfGuard.IsBlocked(IPAddress.Parse(address)));

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static WebFetcher Fetcher(Fake fake, Action<WebFetchSettings>? configure = null, Dictionary<string, string>? dns = null)
    {
        var settings = new ToolsSettingsService();
        settings.UpdateAsync(s => configure?.Invoke(s.Fetch)).GetAwaiter().GetResult();
        return new WebFetcher(settings)
        {
            HandlerFactory = () => fake,
            Resolve = (host, _) => Task.FromResult(dns is not null && dns.TryGetValue(host, out var ip) ? new[] { IPAddress.Parse(ip) } : new[] { IPAddress.Parse("93.184.216.34") }),
        };
    }

    private static HttpResponseMessage Html(string html, string type = "text/html; charset=utf-8") =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, type.Split(';')[0]) };

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://10.0.0.5:8080/")]
    public async Task Private_loopback_and_metadata_addresses_are_refused_before_anything_is_sent(string url)
    {
        var fake = new Fake(_ => Html("secret"));
        var fetcher = Fetcher(fake, dns: new() { ["localhost"] = "127.0.0.1" });
        var ex = await Assert.ThrowsAsync<FetchRefusedException>(() => fetcher.FetchAsync(url, null, 0, default));
        Assert.Contains("private", ex.Message);
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_even_when_one_answer_is_public()
    {
        var fake = new Fake(_ => Html("x"));
        var settings = new ToolsSettingsService();
        var fetcher = new WebFetcher(settings)
        {
            HandlerFactory = () => fake,
            Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.1") }),
        };
        await Assert.ThrowsAsync<FetchRefusedException>(() => fetcher.FetchAsync("https://rebind.example/", null, 0, default));
        Assert.Empty(fake.Seen);
    }

    [Fact]
    public async Task A_redirect_into_a_private_address_is_refused_at_the_hop()
    {
        var fake = new Fake(r => r.RequestUri!.Host == "public.example"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1:9/internal") } }
            : Html("internal secret"));
        var fetcher = Fetcher(fake, dns: new() { ["public.example"] = "93.184.216.34" });
        await Assert.ThrowsAsync<FetchRefusedException>(() => fetcher.FetchAsync("https://public.example/go", null, 0, default));
        Assert.Single(fake.Seen); // never asked the internal address
    }

    [Fact]
    public async Task Redirects_between_public_pages_are_followed_up_to_a_limit()
    {
        var n = 0;
        var fake = new Fake(r => ++n < 3 ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri($"https://b{n}.example/") } } : Html("<p>arrived</p>"));
        var page = await Fetcher(fake).FetchAsync("https://a.example/", null, 0, default);
        Assert.Equal("arrived", page.Text);
        Assert.Equal("https://b2.example/", page.Url);

        var loop = new Fake(r => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://loop.example/") } });
        var ex = await Assert.ThrowsAsync<FetchRefusedException>(() => Fetcher(loop).FetchAsync("https://loop.example/", null, 0, default));
        Assert.Contains("redirects", ex.Message);
        Assert.Equal(WebFetcher.MaxRedirects + 1, loop.Seen.Count);
    }

    [Fact]
    public async Task Private_addresses_can_be_allowed_explicitly()
    {
        var fake = new Fake(_ => Html("local page", "text/plain"));
        var page = await Fetcher(fake, f => f.AllowPrivate = true).FetchAsync("http://127.0.0.1:8080/", null, 0, default);
        Assert.Equal("local page", page.Text);
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("not a url")]
    public async Task Only_plain_http_and_https_addresses_without_credentials(string url) =>
        await Assert.ThrowsAsync<FetchRefusedException>(() => Fetcher(new Fake(_ => Html("x"))).FetchAsync(url, null, 0, default));

    [Fact]
    public async Task Html_becomes_markdown_like_text_with_headings_lists_and_links_and_without_scripts()
    {
        const string html = """
            <html><head><title>My &amp; page</title><style>p{color:red}</style><script>alert(1)</script></head>
            <body><h1>Hello</h1><p>Some <b>bold</b> text and <a href="https://x.example/a">a link</a>.</p>
            <ul><li>one</li><li>two</li></ul><!-- hidden --><script>evil()</script><a href="#top">skip</a></body></html>
            """;
        var page = await Fetcher(new Fake(_ => Html(html))).FetchAsync("https://example.com/", null, 0, default);
        Assert.Equal("My & page", page.Title);
        Assert.Contains("# Hello", page.Text);
        Assert.Contains("[a link](https://x.example/a)", page.Text);
        Assert.Contains("- one", page.Text);
        Assert.Contains("- two", page.Text);
        Assert.DoesNotContain("alert", page.Text);
        Assert.DoesNotContain("evil", page.Text);
        Assert.DoesNotContain("hidden", page.Text);
        Assert.DoesNotContain("color:red", page.Text);
        Assert.Contains("skip", page.Text);
        Assert.DoesNotContain("#top", page.Text);
    }

    [Fact]
    public async Task A_long_page_comes_in_parts_and_the_answer_says_where_to_read_on()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789", 300)); // 3000 characters
        var fetcher = Fetcher(new Fake(_ => Html(text, "text/plain")));
        var first = await fetcher.FetchAsync("https://example.com/", 1000, 0, default);
        Assert.Equal(1000, first.Text.Length);
        Assert.Equal(1000, first.NextOffset);
        Assert.Equal(3000, first.Total);
        var last = await fetcher.FetchAsync("https://example.com/", 1000, 2500, default);
        Assert.Equal(500, last.Text.Length);
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public async Task A_page_is_read_for_at_most_the_byte_limit_and_binary_types_are_refused()
    {
        var big = new string('a', 200_000);
        var small = await Fetcher(new Fake(_ => Html(big, "text/plain")), f => f.MaxBytes = 64 * 1024).FetchAsync("https://example.com/", 100_000, 0, default);
        Assert.Equal(64 * 1024, small.Total);

        var image = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } });
        var ex = await Assert.ThrowsAsync<FetchRefusedException>(() => Fetcher(image).FetchAsync("https://example.com/x.png", null, 0, default));
        Assert.Contains("image/png", ex.Message);

        var missing = new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var notFound = await Assert.ThrowsAsync<FetchRefusedException>(() => Fetcher(missing).FetchAsync("https://example.com/gone", null, 0, default));
        Assert.Contains("404", notFound.Message);
    }
}
