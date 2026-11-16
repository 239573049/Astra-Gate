using System.Net;
using System.Text;
using Astra.Core.Clients;
using Astra.Core.Tools;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Gateway.Tests;

/// <summary>Several keys, rotation, rests and the order of APIs, against a fake HTTP transport.</summary>
public class SearchServiceTests
{
    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["enc:".Length..];
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Host, string? Bearer)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Seen) Seen.Add((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string Hit = """{"results":[{"title":"T","url":"https://a.example","content":"c"}]}""";

    private static (SearchService Service, SearchKeyRotation Rotation) Make(Fake fake, params SearchApiConfig[] apis)
    {
        var settings = new ToolsSettingsService();
        settings.UpdateAsync(s => s.Search.Apis.AddRange(apis)).GetAwaiter().GetResult();
        var rotation = new SearchKeyRotation();
        var http = new GatewayHttpClients { HandlerFactory = _ => fake };
        return (new SearchService(settings, new PlainSecrets(), http, rotation, NullLogger<SearchService>.Instance), rotation);
    }

    private static SearchApiConfig Tavily(string pick, params string[] keys) => new()
    {
        Vendor = SearchVendors.Tavily, Pick = pick,
        Keys = [.. keys.Select((k, i) => new SearchKeyConfig { Id = "k" + i, KeyEnc = "enc:" + k })],
    };

    [Fact]
    public async Task Keys_take_turns_by_default_and_the_first_until_refused_for_order()
    {
        var fake = new Fake(_ => Json(Hit));
        var (turns, _) = Make(fake, Tavily(SearchPicks.Turns, "key-a-0000", "key-b-0000"));
        for (var i = 0; i < 4; i++) await turns.SearchAsync("q", null, null, default);
        Assert.Equal(["key-a-0000", "key-b-0000", "key-a-0000", "key-b-0000"], fake.Seen.Select(s => s.Bearer));

        var fake2 = new Fake(_ => Json(Hit));
        var (order, _) = Make(fake2, Tavily(SearchPicks.Order, "key-a-0000", "key-b-0000"));
        for (var i = 0; i < 3; i++) await order.SearchAsync("q", null, null, default);
        Assert.All(fake2.Seen, s => Assert.Equal("key-a-0000", s.Bearer));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task A_refused_key_goes_to_the_next_and_then_is_asked_last(int status)
    {
        var fake = new Fake(r => r.Headers.Authorization!.Parameter == "bad-key-0000" ? Json("""{"detail":"nope"}""", (HttpStatusCode)status) : Json(Hit));
        var (service, rotation) = Make(fake, Tavily(SearchPicks.Order, "bad-key-0000", "good-key-000"));

        var first = await service.SearchAsync("q", null, null, default);
        Assert.Single(first.Pages);
        Assert.Equal(["bad-key-0000", "good-key-000"], fake.Seen.Select(s => s.Bearer));
        Assert.NotNull(rotation.RestOf(SearchVendors.Tavily, "bad-key-0000", DateTimeOffset.UtcNow));

        fake.Seen.Clear();
        await service.SearchAsync("q", null, null, default);
        Assert.Equal(["good-key-000"], fake.Seen.Select(s => s.Bearer)); // the resting key is not asked first
    }

    [Fact]
    public async Task A_resting_key_is_still_the_last_resort()
    {
        var fake = new Fake(_ => Json(Hit));
        var (service, rotation) = Make(fake, Tavily(SearchPicks.Order, "only-key-0000"));
        rotation.Rest(SearchVendors.Tavily, "only-key-0000", TimeSpan.FromMinutes(5), "rate limited", DateTimeOffset.UtcNow);
        var found = await service.SearchAsync("q", null, null, default);
        Assert.Single(found.Pages);
        Assert.Null(rotation.RestOf(SearchVendors.Tavily, "only-key-0000", DateTimeOffset.UtcNow)); // it answered: awake
    }

    [Fact]
    public async Task A_switched_off_api_or_key_is_never_asked_and_the_next_api_answers_when_one_finds_nothing()
    {
        var fake = new Fake(r => r.RequestUri!.Host == "api.tavily.com" ? Json("""{"results":[]}""") : Json("""{"web":{"results":[{"title":"B","url":"https://b.example","description":"d"}]}}"""));
        var tavily = Tavily(SearchPicks.Turns, "on-key-00000", "off-key-1111");
        tavily.Keys[1].Enabled = false;
        var brave = new SearchApiConfig { Vendor = SearchVendors.Brave, Keys = [new SearchKeyConfig { Id = "b", KeyEnc = "enc:brave-key-00" }] };
        var disabled = new SearchApiConfig { Vendor = SearchVendors.Exa, Enabled = false, Keys = [new SearchKeyConfig { Id = "e", KeyEnc = "enc:exa-key-0000" }] };
        var (service, _) = Make(fake, disabled, tavily, brave);
        var outcome = await service.SearchAsync("q", null, null, default);
        Assert.Equal("Brave Search", outcome.Source);
        Assert.DoesNotContain(fake.Seen, s => s.Bearer == "off-key-1111");
        Assert.DoesNotContain(fake.Seen, s => s.Host == "api.exa.ai");
    }

    [Fact]
    public async Task Failure_says_why_per_api_never_with_a_key_and_a_keyless_api_needs_none()
    {
        var fake = new Fake(r => r.RequestUri!.Host == "api.tavily.com" ? Json("""{"detail":"bad key secret-key-0000"}""", HttpStatusCode.Unauthorized) : Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));
        var searx = new SearchApiConfig { Vendor = SearchVendors.SearxNg, Url = "http://searx.test" };
        var (service, _) = Make(fake, Tavily(SearchPicks.Turns, "secret-key-0000"), searx);
        var ex = await Assert.ThrowsAsync<SearchFailedException>(() => service.SearchAsync("q", null, null, default));
        Assert.DoesNotContain("secret-key-0000", ex.Message);
        Assert.Contains("Tavily", ex.Message);
        Assert.Contains("SearXNG", ex.Message);
        Assert.Contains(fake.Seen, s => s.Host == "searx.test"); // asked with no key
    }

    [Fact]
    public async Task Only_asks_one_vendor_even_when_it_is_off_and_nothing_on_says_so()
    {
        var fake = new Fake(_ => Json(Hit));
        var api = Tavily(SearchPicks.Turns, "test-key-0000");
        api.Enabled = false;
        var (service, _) = Make(fake, api);
        Assert.False(service.HasSources);
        Assert.Single((await service.SearchAsync("q", null, SearchVendors.Tavily, default)).Pages);
        await Assert.ThrowsAsync<SearchFailedException>(() => service.SearchAsync("q", null, null, default));
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_key_stays_at_its_address()
    {
        var fake = new Fake(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example/steal");
            return r;
        });
        var (service, _) = Make(fake, new SearchApiConfig { Vendor = SearchVendors.Brave, Keys = [new SearchKeyConfig { Id = "b", KeyEnc = "enc:brave-key-00" }] });
        var ex = await Assert.ThrowsAsync<SearchFailedException>(() => service.SearchAsync("q", null, null, default));
        Assert.Contains("evil.example", ex.Message);
        Assert.Single(fake.Seen);
    }

    [Fact]
    public void Rotation_orders_the_soonest_awake_resting_key_first_and_rests_follow_the_status()
    {
        var rotation = new SearchKeyRotation();
        var now = DateTimeOffset.UtcNow;
        rotation.Rest("v", "k1", TimeSpan.FromMinutes(10), "turned away", now);
        rotation.Rest("v", "k2", TimeSpan.FromMinutes(1), "rate limited", now);
        Assert.Equal([2, 1, 0], rotation.Order("v", ["k1", "k2", "k3"], SearchPicks.Order, now));
        Assert.Equal(SearchKeyRotation.TurnedAwayRest, SearchKeyRotation.RestFor("tavily", 401, null, now).Duration);
        Assert.Equal(SearchKeyRotation.OutOfCreditRest, SearchKeyRotation.RestFor("tavily", 432, null, now).Duration);
        Assert.Equal(SearchKeyRotation.RateLimitRest, SearchKeyRotation.RestFor("exa", 432, null, now).Duration);
        Assert.Equal(TimeSpan.FromSeconds(120), SearchKeyRotation.RestFor("exa", 429, "120", now).Duration);
        Assert.Equal(SearchKeyRotation.RateLimitRestMax, SearchKeyRotation.RestFor("exa", 429, "99999", now).Duration);
    }
}
