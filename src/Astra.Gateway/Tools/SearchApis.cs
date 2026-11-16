using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Tools;

namespace Astra.Gateway.Tools;

/// <summary>One page a web search found.</summary>
public sealed record FoundPage(string Title, string Url, string Text, string Age);

/// <summary>A search API turning the key away (401, 402, 403, 429 ...): the key rests, the next one is asked.</summary>
public sealed class SearchRefusedException(int status, string message, string? retryAfter) : Exception(message)
{
    public int Status { get; } = status;
    public string? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The requests and replies of the built-in search APIs (ported from magpie's internal/gateway/search_api.go, MIT): how to ask
/// each, how to read what it answers, and what an error of its own means. Pure functions over bytes, tested with fakes.
/// </summary>
public static partial class SearchApis
{
    /// <summary>The most characters of one page's text kept.</summary>
    public const int PageText = 1500;

    public const string UserAgent = "astra-search";

    /// <summary>DeepSeek's Search API takes a query of 1 to 128 characters; Doubao Search 1 to 100.</summary>
    private const int DeepSeekQueryMax = 128, VolcQueryMax = 100;

    /// <summary>You.com's search that takes no key (100 a day for an address).</summary>
    public const string YouFreeSearch = "https://api.you.com/v1/agents/search";

    /// <summary>The request asking <paramref name="vendor"/> for <paramref name="n"/> pages about <paramref name="query"/>.</summary>
    /// <param name="baseUrl">The API's address; empty is the vendor's own.</param>
    public static HttpRequestMessage BuildRequest(string vendor, string? baseUrl, string? key, string query, int n)
    {
        var info = SearchVendors.Find(vendor) ?? throw new InvalidOperationException($"Astra cannot ask {vendor}");
        var root = BaseOf(info, baseUrl);
        HttpRequestMessage request;
        switch (vendor)
        {
            case SearchVendors.Tavily:
                request = Post(root + "/search", new JsonObject { ["query"] = query, ["max_results"] = n, ["search_depth"] = "basic" });
                break;
            case SearchVendors.Brave:
                request = Get(root + "/res/v1/web/search", ("q", query), ("count", n.ToString()));
                break;
            case SearchVendors.Exa:
                request = Post(root + "/search", new JsonObject
                {
                    ["query"] = query, ["numResults"] = n,
                    ["contents"] = new JsonObject { ["text"] = new JsonObject { ["maxCharacters"] = PageText } },
                });
                break;
            case SearchVendors.Firecrawl:
                request = Post(root + "/v2/search", new JsonObject { ["query"] = query, ["limit"] = n });
                break;
            case SearchVendors.DeepSeek:
                request = Post(root + "/v1/tools/search", new JsonObject { ["query"] = FirstChars(query, DeepSeekQueryMax), ["count"] = n });
                break;
            case SearchVendors.Volcengine:
                request = Post(root + "/search_api/web_search", new JsonObject
                {
                    ["Query"] = FirstChars(query, VolcQueryMax), ["SearchType"] = "web", ["Count"] = n,
                    ["Filter"] = new JsonObject { ["NeedUrl"] = true },
                });
                break;
            case SearchVendors.Wsa:
                // Cnt is one of 10/20/30/40/50: the fewest that covers n, cut to n when read.
                request = Post(root + "/SearchPro", new JsonObject { ["Query"] = query, ["Cnt"] = CntFor(n) });
                break;
            case SearchVendors.TinyFish:
                request = Get(root + "/", ("query", query));
                break;
            case SearchVendors.SearxNg:
                request = Get(root + "/search", ("q", query), ("format", "json"));
                break;
            case SearchVendors.You:
                // With a key, its Search API; without one, its free search (at an address of the user's own when one is set).
                if (!string.IsNullOrEmpty(key)) request = Post(root + "/v1/search", new JsonObject { ["query"] = query, ["count"] = n });
                else if (!string.IsNullOrWhiteSpace(baseUrl)) request = Get(root + "/v1/agents/search", ("query", query), ("count", n.ToString()));
                else request = Get(YouFreeSearch, ("query", query), ("count", n.ToString()));
                break;
            default:
                throw new InvalidOperationException($"Astra cannot ask {vendor}");
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrEmpty(key))
        {
            switch (vendor)
            {
                case SearchVendors.Brave: request.Headers.Add("X-Subscription-Token", key); break;
                case SearchVendors.Exa or SearchVendors.You or SearchVendors.TinyFish: request.Headers.Add("X-API-Key", key); break;
                default: request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); break;
            }
        }
        return request;
    }

    /// <summary>The API's address: the user's own when given (a base ending in a path Astra adds itself is that base), else the vendor's.</summary>
    public static string BaseOf(SearchVendorInfo info, string? baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? info.BaseUrl ?? "" : baseUrl.Trim();
        url = url.TrimEnd('/');
        foreach (var path in (string[])["/search", "/v2/search", "/v1/search", "/v1/agents/search", "/res/v1/web/search", "/v1/tools/search", "/search_api/web_search", "/SearchPro"])
            if (url.EndsWith(path, StringComparison.Ordinal)) { url = url[..^path.Length]; break; }
        return url;
    }

    public static int CntFor(int n) => Math.Min(Math.Max((n + 9) / 10 * 10, 10), 50);

    /// <summary>
    /// What the answer stands for: its HTTP status, or for Doubao Search and Yuanbao Search — whose errors come in the body, even
    /// with a 200 — the status that error means (key turned away 403, out of credit 402, rate limited 429), and its words.
    /// </summary>
    public static (int Status, string? Message) StatusOf(string vendor, int status, string body)
    {
        if (vendor == SearchVendors.Volcengine) return VolcStatus(status, body);
        if (vendor == SearchVendors.Wsa) return WsaStatus(status, body);
        return (status, null);
    }

    private static (int, string?) VolcStatus(int status, string body)
    {
        var error = Parse(body)?["ResponseMetadata"]?["Error"] as JsonObject;
        if (error is null) return (status, null);
        var code = error["CodeN"] is JsonValue n && n.TryGetValue<int>(out var ci) ? ci
            : int.TryParse(Str(error, "Code"), out var cs) ? cs : 0;
        var message = $"{code} {Str(error, "Message")}".Trim();
        switch (code)
        {
            case 10401 or 10403 or 10409: return (403, message);
            case 10406 or 10408 or 10410 or 10412: return (402, message);
            case 700429: return (429, message);
        }
        return (status < 300 ? 502 : status, message);
    }

    private static (int, string?) WsaStatus(int status, string body)
    {
        var error = Parse(body)?["Response"]?["Error"] as JsonObject;
        var code = Str(error, "Code");
        if (error is null || code.Length == 0) return (status, null);
        var message = $"{code} {Str(error, "Message")}".Trim();
        var family = code.Split('.')[0];
        switch (family)
        {
            case "AuthFailure" or "UnauthorizedOperation" or "ResourceNotFound": return (403, message);
            case "ResourceUnavailable" or "LimitExceeded" or "ResourceInsufficient": return (402, message);
            case "RequestLimitExceeded": return (429, message);
        }
        return (status < 300 ? 502 : status, message);
    }

    /// <summary>The words of an API's error body: its <c>error</c> / <c>message</c> / <c>detail</c>, else the status line.</summary>
    public static string ErrorMessage(string body, string fallback)
    {
        if (Parse(body) is JsonObject o)
        {
            foreach (var key in (string[])["message", "error", "detail", "msg"])
            {
                var node = o[key];
                if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) return Clip(text, 300);
                if (node is JsonObject inner && inner["message"] is JsonValue m && m.TryGetValue<string>(out var innerText) && innerText.Length > 0)
                    return Clip(innerText, 300);
            }
        }
        return fallback;
    }

    /// <summary>The pages an API's answer holds. Throws <see cref="FormatException"/> for an answer it cannot read.</summary>
    public static List<FoundPage> ReadReply(string vendor, string body)
    {
        var root = Parse(body) as JsonObject ?? throw new FormatException("not JSON");
        switch (vendor)
        {
            case SearchVendors.You: return ReadYou(root);
            case SearchVendors.Volcengine: return ReadVolc(root);
            case SearchVendors.Wsa: return ReadWsa(root);
        }
        JsonArray? results = vendor switch
        {
            SearchVendors.Brave => root["web"]?["results"] as JsonArray,
            SearchVendors.Firecrawl => root["data"] is JsonObject d ? d["web"] as JsonArray : root["data"] as JsonArray,
            _ => root["results"] as JsonArray,
        };
        var pages = new List<FoundPage>();
        foreach (var item in results ?? [])
        {
            if (item is not JsonObject r) continue;
            var url = Str(r, "url");
            if (url.Length == 0) continue;
            // The first non-empty of the fields the APIs call their text (DeepSeek's summary is its longer one).
            var text = "";
            foreach (var field in (string[])["content", "summary", "text", "description", "markdown", "snippet"])
                if (text.Length == 0) text = Str(r, field);
            var title = Plain(Str(r, "title"));
            pages.Add(new FoundPage(title.Length == 0 ? url : title, url, Clip(Plain(text), PageText), Str(r, "date")));
        }
        return pages;
    }

    private static List<FoundPage> ReadYou(JsonObject root)
    {
        if (root["results"] is not JsonObject results) throw new FormatException("no results in it");
        var pages = new List<FoundPage>();
        foreach (var item in ((results["web"] as JsonArray) ?? []).Concat((results["news"] as JsonArray) ?? []))
        {
            if (item is not JsonObject h) continue;
            var url = Str(h, "url");
            if (url.Length == 0) continue;
            var snippets = string.Join(" ", ((h["snippets"] as JsonArray) ?? []).Select(s => s is JsonValue v && v.TryGetValue<string>(out var t) ? t : ""));
            var text = snippets.Length > 0 ? snippets : Str(h, "description");
            var title = Plain(Str(h, "title"));
            pages.Add(new FoundPage(title.Length == 0 ? url : title, url, Clip(Plain(text), PageText), Str(h, "page_age")));
        }
        return pages;
    }

    private static List<FoundPage> ReadVolc(JsonObject root)
    {
        if (root["Result"] is not JsonObject result) throw new FormatException("no Result in it");
        var pages = new List<FoundPage>();
        foreach (var item in (result["WebResults"] as JsonArray) ?? [])
        {
            if (item is not JsonObject h) continue;
            var url = Str(h, "Url");
            if (url.Length == 0) continue;
            var text = Str(h, "Summary");
            if (string.IsNullOrWhiteSpace(text)) text = Str(h, "Snippet");
            var age = Str(h, "PublishTime");
            if (age.StartsWith("1970-01-01", StringComparison.Ordinal)) age = "";
            var title = Plain(Str(h, "Title"));
            pages.Add(new FoundPage(title.Length == 0 ? url : title, url, Clip(Plain(text), PageText), age));
        }
        return pages;
    }

    // Yuanbao Search: Response.Pages, each page a JSON object in a string.
    private static List<FoundPage> ReadWsa(JsonObject root)
    {
        if (root["Response"]?["Pages"] is not JsonArray list) throw new FormatException("no Response.Pages in it");
        var pages = new List<FoundPage>();
        foreach (var item in list)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var raw) || Parse(raw) is not JsonObject h) continue;
            var url = Str(h, "URL");
            if (url.Length == 0) url = Str(h, "url");
            if (url.Length == 0) continue;
            var title = Plain(Str(h, "Title").Length > 0 ? Str(h, "Title") : Str(h, "title"));
            var text = Pick(h, "Passage", "passage");
            // A passage that is only its title says less than the page's content.
            if (Plain(text).Length == 0 || Plain(text) == title)
            {
                var content = Pick(h, "Content", "content");
                if (Plain(content).Length > 0) text = content;
            }
            pages.Add(new FoundPage(title.Length == 0 ? url : title, url, Clip(Plain(text), PageText), Pick(h, "Date", "date")));
        }
        return pages;
    }

    private static string Pick(JsonObject o, string a, string b) => Str(o, a).Length > 0 ? Str(o, a) : Str(o, b);

    /// <summary>A page's address as two APIs may write it apart: lower-case host, http and https alike, no fragment, no closing slash.</summary>
    public static string PageKey(string raw)
    {
        raw = raw.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Host.Length == 0) return raw;
        return "https://" + u.Host.ToLowerInvariant() + u.AbsolutePath.TrimEnd('/') + u.Query;
    }

    private static HttpRequestMessage Post(string url, JsonObject body) =>
        new(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Get(string url, params (string Key, string Value)[] query) =>
        new(HttpMethod.Get, url + (url.Contains('?') ? "&" : "?") + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")));

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    /// <summary>A search API's text without the tags it marks matches with (Brave's &lt;strong&gt;), on one line.</summary>
    public static string Plain(string s) =>
        string.Join(' ', WebUtility.HtmlDecode(Tags().Replace(s, "")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Clip(string s, int n) => s.Length <= n ? s : s[..(char.IsLowSurrogate(s[n]) ? n - 1 : n)] + "…";

    public static string FirstChars(string s, int n)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1)
            if (count++ == n) return s[..i];
        return s;
    }
}
