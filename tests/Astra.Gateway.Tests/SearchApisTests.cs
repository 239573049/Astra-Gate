using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Tools;
using Astra.Gateway.Tools;

namespace Astra.Gateway.Tests;

/// <summary>How each built-in search API is asked and how its answer is read (fake bytes, no network).</summary>
public class SearchApisTests
{
    private static async Task<(HttpRequestMessage Request, string Body)> BuildAsync(string vendor, string? key = "k-123", string? url = null, int n = 5, string query = "magpie facts")
    {
        var request = SearchApis.BuildRequest(vendor, url, key, query, n);
        return (request, request.Content is null ? "" : await request.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tavily_is_a_bearer_post_with_the_count()
    {
        var (r, body) = await BuildAsync(SearchVendors.Tavily);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.tavily.com/search", r.RequestUri!.ToString());
        Assert.Equal("Bearer k-123", r.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("magpie facts", json["query"]!.GetValue<string>());
        Assert.Equal(5, json["max_results"]!.GetValue<int>());
    }

    [Fact]
    public async Task Brave_is_a_get_with_its_own_token_header()
    {
        var (r, _) = await BuildAsync(SearchVendors.Brave);
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal("https://api.search.brave.com/res/v1/web/search?q=magpie%20facts&count=5", r.RequestUri!.AbsoluteUri);
        Assert.Equal("k-123", r.Headers.GetValues("X-Subscription-Token").Single());
        Assert.Null(r.Headers.Authorization);
    }

    [Theory]
    [InlineData(SearchVendors.Exa, "https://api.exa.ai/search")]
    [InlineData(SearchVendors.Firecrawl, "https://api.firecrawl.dev/v2/search")]
    [InlineData(SearchVendors.DeepSeek, "https://api.deepseek.com/v1/tools/search")]
    [InlineData(SearchVendors.Volcengine, "https://open.feedcoopapi.com/search_api/web_search")]
    [InlineData(SearchVendors.Wsa, "https://api.wsa.cloud.tencent.com/SearchPro")]
    public async Task Post_apis_go_to_their_paths(string vendor, string url)
    {
        var (r, body) = await BuildAsync(vendor);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal(url, r.RequestUri!.ToString());
        Assert.Contains("magpie facts", body);
    }

    [Fact]
    public async Task Exa_and_tinyfish_use_x_api_key_and_deepseek_cuts_the_query_it_cannot_take()
    {
        Assert.Equal("k-123", (await BuildAsync(SearchVendors.Exa)).Request.Headers.GetValues("X-API-Key").Single());
        Assert.Equal("https://api.search.tinyfish.ai/?query=magpie%20facts", (await BuildAsync(SearchVendors.TinyFish)).Request.RequestUri!.AbsoluteUri);
        var (_, body) = await BuildAsync(SearchVendors.DeepSeek, query: new string('x', 300));
        Assert.Equal(128, JsonNode.Parse(body)!["query"]!.GetValue<string>().Length);
        var (_, volc) = await BuildAsync(SearchVendors.Volcengine, query: new string('y', 300));
        Assert.Equal(100, JsonNode.Parse(volc)!["Query"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(99, 50)]
    public void Yuanbao_takes_counts_in_tens(int n, int cnt) => Assert.Equal(cnt, SearchApis.CntFor(n));

    [Fact]
    public async Task Searxng_needs_the_users_address_and_a_base_ending_in_search_is_that_base()
    {
        var (r, _) = await BuildAsync(SearchVendors.SearxNg, key: null, url: "http://127.0.0.1:8888/search/");
        Assert.Equal("http://127.0.0.1:8888/search?q=magpie%20facts&format=json", r.RequestUri!.AbsoluteUri);
        Assert.Null(r.Headers.Authorization);
    }

    [Fact]
    public async Task You_asks_its_api_with_a_key_and_its_free_search_without_one()
    {
        var (keyed, body) = await BuildAsync(SearchVendors.You);
        Assert.Equal("https://ydc-index.io/v1/search", keyed.RequestUri!.ToString());
        Assert.Contains("magpie facts", body);
        var (free, _) = await BuildAsync(SearchVendors.You, key: null);
        Assert.StartsWith(SearchApis.YouFreeSearch, free.RequestUri!.ToString());
        Assert.False(free.Headers.Contains("X-API-Key"));
    }

    [Fact]
    public void Tavily_exa_searxng_and_deepseek_replies_are_read_by_the_field_that_holds_their_text()
    {
        var pages = SearchApis.ReadReply(SearchVendors.Tavily,
            """{"results":[{"title":"A","url":"https://a.example","content":"text a"},{"title":"","url":"https://b.example","summary":"sum b","date":"2026-01-02"},{"title":"no url","content":"x"}]}""");
        Assert.Equal(2, pages.Count);
        Assert.Equal("text a", pages[0].Text);
        Assert.Equal("https://b.example", pages[1].Title); // no title: the address
        Assert.Equal("sum b", pages[1].Text);
        Assert.Equal("2026-01-02", pages[1].Age);
    }

    [Fact]
    public void Brave_replies_lose_their_match_tags_and_firecrawl_reads_both_shapes()
    {
        var brave = SearchApis.ReadReply(SearchVendors.Brave, """{"web":{"results":[{"title":"The <strong>magpie</strong> &amp; co","url":"https://a.example","description":"A <b>clever</b> bird"}]}}""");
        Assert.Equal("The magpie & co", brave[0].Title);
        Assert.Equal("A clever bird", brave[0].Text);
        Assert.Single(SearchApis.ReadReply(SearchVendors.Firecrawl, """{"data":{"web":[{"title":"t","url":"https://a.example","description":"d"}]}}"""));
        Assert.Single(SearchApis.ReadReply(SearchVendors.Firecrawl, """{"data":[{"title":"t","url":"https://a.example","markdown":"# d"}]}"""));
    }

    [Fact]
    public void You_volcengine_and_yuanbao_replies_have_their_own_shapes()
    {
        var you = SearchApis.ReadReply(SearchVendors.You,
            """{"results":{"web":[{"title":"W","url":"https://w.example","description":"desc","snippets":["s1","s2"],"page_age":"2026-02-03"}],"news":[{"title":"N","url":"https://n.example","description":"news"}]}}""");
        Assert.Equal(["https://w.example", "https://n.example"], you.Select(p => p.Url));
        Assert.Equal("s1 s2", you[0].Text);
        Assert.Equal("news", you[1].Text);

        var volc = SearchApis.ReadReply(SearchVendors.Volcengine,
            """{"Result":{"WebResults":[{"Title":"V","Url":"https://v.example","Summary":"","Snippet":"snip","PublishTime":"1970-01-01T00:00:00Z"}]}}""");
        Assert.Equal("snip", volc[0].Text);
        Assert.Equal("", volc[0].Age);

        var inner = new JsonObject { ["Title"] = "Y", ["URL"] = "https://y.example", ["Passage"] = "Y", ["Content"] = "the content", ["Date"] = "2026-03-04" }.ToJsonString();
        var wsa = SearchApis.ReadReply(SearchVendors.Wsa, new JsonObject { ["Response"] = new JsonObject { ["Pages"] = new JsonArray(inner) } }.ToJsonString());
        Assert.Equal("the content", wsa[0].Text); // a passage that is only the title says less than the content
        Assert.Equal("2026-03-04", wsa[0].Age);
    }

    [Fact]
    public void An_unreadable_reply_is_a_format_error_not_zero_pages()
    {
        Assert.Throws<FormatException>(() => SearchApis.ReadReply(SearchVendors.Tavily, "not json"));
        Assert.Throws<FormatException>(() => SearchApis.ReadReply(SearchVendors.You, "{}"));
        Assert.Throws<FormatException>(() => SearchApis.ReadReply(SearchVendors.Volcengine, "{}"));
        Assert.Throws<FormatException>(() => SearchApis.ReadReply(SearchVendors.Wsa, "{}"));
    }

    [Theory]
    [InlineData("""{"ResponseMetadata":{"Error":{"CodeN":10403,"Message":"no access"}}}""", 200, 403)]
    [InlineData("""{"ResponseMetadata":{"Error":{"CodeN":10406,"Message":"out of credit"}}}""", 200, 402)]
    [InlineData("""{"ResponseMetadata":{"Error":{"Code":"700429","Message":"slow down"}}}""", 200, 429)]
    [InlineData("""{"ResponseMetadata":{"Error":{"CodeN":1,"Message":"other"}}}""", 200, 502)]
    [InlineData("""{"Result":{}}""", 200, 200)]
    public void Doubao_errors_in_the_body_stand_for_the_status_they_mean(string body, int status, int expected) =>
        Assert.Equal(expected, SearchApis.StatusOf(SearchVendors.Volcengine, status, body).Status);

    [Theory]
    [InlineData("AuthFailure.SignatureFailure", 403)]
    [InlineData("ResourceUnavailable.InArrears", 402)]
    [InlineData("RequestLimitExceeded", 429)]
    [InlineData("InternalError", 502)]
    public void Yuanbao_errors_in_the_body_stand_for_the_status_they_mean(string code, int expected) =>
        Assert.Equal(expected, SearchApis.StatusOf(SearchVendors.Wsa, 200, new JsonObject { ["Response"] = new JsonObject { ["Error"] = new JsonObject { ["Code"] = code, ["Message"] = "m" } } }.ToJsonString()).Status);

    [Fact]
    public void Tavily_usage_is_the_keys_own_limit_else_the_plans_and_a_missing_field_is_unknown()
    {
        var keyLimit = SearchService.ReadTavilyUsage("k", """{"key":{"usage":950,"limit":1000},"account":{"current_plan":"Researcher","plan_usage":5000,"plan_limit":10000}}""");
        Assert.True(keyLimit.Known);
        Assert.Equal(("key", 950L, 1000L, false), (keyLimit.Of, keyLimit.Used, keyLimit.Limit, keyLimit.UsedUp));

        var plan = SearchService.ReadTavilyUsage("k", """{"key":{"usage":5,"limit":null},"account":{"current_plan":"Free","plan_usage":1000,"plan_limit":1000,"paygo_usage":0,"paygo_limit":0}}""");
        Assert.Equal(("plan", 1000L, true), (plan.Of, plan.Used, plan.UsedUp));
        Assert.Equal("Free", plan.Plan);

        var paygo = SearchService.ReadTavilyUsage("k", """{"key":{"usage":5,"limit":null},"account":{"plan_usage":1000,"plan_limit":1000,"paygo_usage":10,"paygo_limit":50}}""");
        Assert.False(paygo.UsedUp);
        Assert.Equal(40, paygo.PaygoLeft);

        var unlimited = SearchService.ReadTavilyUsage("k", """{"key":{"usage":7,"limit":null},"account":{"plan_limit":null}}""");
        Assert.True(unlimited.Unlimited);

        var unknown = SearchService.ReadTavilyUsage("k", """{"key":{"usage":7}}""");
        Assert.False(unknown.Known);
        Assert.NotNull(unknown.Error);
        Assert.False(SearchService.ReadTavilyUsage("k", "[]").Known);
    }

    [Fact]
    public void Two_spellings_of_a_page_address_are_one_page()
    {
        Assert.Equal(SearchApis.PageKey("http://Example.com/a/#top"), SearchApis.PageKey("https://example.com/a"));
    }

    [Fact]
    public void Clip_never_splits_a_character()
    {
        var clipped = SearchApis.Clip(string.Concat(Enumerable.Repeat("😀", 10)), 5);
        Assert.EndsWith("…", clipped);
        Assert.Equal(clipped, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(clipped))); // round-trips: no lone surrogate
    }
}
