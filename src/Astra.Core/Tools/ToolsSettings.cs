namespace Astra.Core.Tools;

/// <summary>
/// Astra's built-in tools (web search, web fetch, image generation; magpie parity #8). Stored under its own key in
/// <c>settings</c> (<see cref="StorageKey"/>, no migration). API keys are kept protected (<c>KeyEnc</c>, through
/// <see cref="Clients.ISecretProtector"/>) and never leave the server: the admin API reports masks only.
/// </summary>
public sealed class ToolsSettings
{
    public const string StorageKey = "tools";

    public WebSearchSettings Search { get; set; } = new();
    public WebFetchSettings Fetch { get; set; } = new();
    public ImageToolSettings Image { get; set; } = new();

    public ToolsSettings Clone() => Json.Deserialize<ToolsSettings>(Json.Serialize(this))!;
}

/// <summary>How the gateway answers a web search a model cannot run (<see cref="WebSearchSettings.Mode"/>).</summary>
public static class SearchModes
{
    /// <summary>Search only for a request whose upstream does not serve the search tool itself.</summary>
    public const string Auto = "auto";

    /// <summary>Search for every request that offers a web-search tool, even one the upstream could serve.</summary>
    public const string Always = "always";

    public static readonly IReadOnlyList<string> All = [Auto, Always];
}

/// <summary>How a search API with several keys picks one: in turn, or the first until it is refused.</summary>
public static class SearchPicks
{
    public const string Turns = "turns";
    public const string Order = "order";
}

public sealed class WebSearchSettings
{
    /// <summary>
    /// The global switch of "search on behalf of models that cannot search": a request offering a web-search server tool
    /// to an upstream that cannot serve it gets Astra's search instead. Off by default. The built-in MCP tool
    /// <c>web_search</c> works whenever an API is on, independent of this.
    /// </summary>
    public bool OnBehalf { get; set; }

    public string Mode { get; set; } = SearchModes.Auto;

    /// <summary>How many pages a search asks for (1-20).</summary>
    public int ResultCount { get; set; } = 6;

    /// <summary>The search APIs in the order they are asked (drag order). A plugin's has the vendor id <c>plugin:id</c>.</summary>
    public List<SearchApiConfig> Apis { get; set; } = [];
}

public sealed class SearchApiConfig
{
    /// <summary>One of <see cref="SearchVendors"/>, or a plugin's <c>VendorId</c>.</summary>
    public string Vendor { get; set; } = "";

    /// <summary>Off keeps the keys but leaves the API out of every search.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><see cref="SearchPicks"/>.</summary>
    public string Pick { get; set; } = SearchPicks.Turns;

    /// <summary>The API's address when it is the user's own (a SearXNG's, a self-hosted Firecrawl); empty is the vendor's.</summary>
    public string? Url { get; set; }

    /// <summary>The keys in their order; each can be switched off on its own.</summary>
    public List<SearchKeyConfig> Keys { get; set; } = [];
}

public sealed class SearchKeyConfig
{
    /// <summary>A short random id the admin API names the key by (the key itself never leaves the server).</summary>
    public string Id { get; set; } = "";

    /// <summary>The key, protected.</summary>
    public string KeyEnc { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

public sealed class WebFetchSettings
{
    /// <summary>At most this many bytes of a page are read.</summary>
    public int MaxBytes { get; set; } = 5 * 1024 * 1024;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>The default number of characters one call returns.</summary>
    public int MaxChars { get; set; } = 20000;

    /// <summary>Allow loopback, private and link-local addresses. Off: a model can not make Astra read a service only this machine reaches.</summary>
    public bool AllowPrivate { get; set; }
}

public sealed class ImageToolSettings
{
    /// <summary>The model the <c>generate_image</c> tool asks the gateway's images route for (empty: the tool is not offered).</summary>
    public string Model { get; set; } = "";

    public string Size { get; set; } = "";
}

/// <summary>A search API Astra can ask itself.</summary>
public sealed record SearchVendorInfo(string Id, string Name, string? BaseUrl, string? KeysUrl, bool KeyOptional, bool NeedsUrl);

/// <summary>The built-in search APIs.</summary>
public static class SearchVendors
{
    public const string Tavily = "tavily";
    public const string Brave = "brave";
    public const string Exa = "exa";
    public const string Firecrawl = "firecrawl";
    public const string You = "you";
    public const string DeepSeek = "deepseek";
    public const string Volcengine = "volcengine";
    public const string Wsa = "wsa";
    public const string TinyFish = "tinyfish";
    public const string SearxNg = "searxng";

    /// <summary>The built-in APIs in the order they are offered (addresses and key pages from magpie, MIT).</summary>
    public static readonly IReadOnlyList<SearchVendorInfo> All =
    [
        new(Tavily, "Tavily", "https://api.tavily.com", "https://app.tavily.com/home", false, false),
        new(Brave, "Brave Search", "https://api.search.brave.com", "https://api-dashboard.search.brave.com/app/keys", false, false),
        new(Exa, "Exa", "https://api.exa.ai", "https://dashboard.exa.ai/api-keys", false, false),
        new(Firecrawl, "Firecrawl", "https://api.firecrawl.dev", "https://www.firecrawl.dev/app/api-keys", false, false),
        new(You, "You.com", "https://ydc-index.io", "https://you.com/platform", true, false),
        new(DeepSeek, "DeepSeek", "https://api.deepseek.com", "https://platform.deepseek.com/api_keys", false, false),
        new(Volcengine, "Doubao Search (Volcengine)", "https://open.feedcoopapi.com", "https://console.volcengine.com/search-infinity/api-key", false, false),
        new(Wsa, "Yuanbao Search (Tencent Cloud)", "https://api.wsa.cloud.tencent.com", "https://console.cloud.tencent.com/wsapi/index", false, false),
        new(TinyFish, "TinyFish", "https://api.search.tinyfish.ai", "https://agent.tinyfish.ai/api-keys", false, false),
        new(SearxNg, "SearXNG", null, null, true, true),
    ];

    public static SearchVendorInfo? Find(string id) => All.FirstOrDefault(v => v.Id == id);
}
