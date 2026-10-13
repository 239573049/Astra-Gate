using Astra.Plugins.Market;

namespace Astra.Plugins.Tests;

public sealed class PluginMarketTests
{
    private const string DocsRegistry = "https://astra-gate.si/api/plugin-market/registry.json";

    private static PluginMarket Market(TestPlugins plugins, StubHttp http, string? env = null, Func<string?>? configured = null) =>
        new(plugins.Paths, http.Client, name => name == "ASTRA_PLUGIN_MARKET" ? env : null, configured);

    private static string Registry(string extra = "") =>
        $$"""{"version":2,"plugins":[{"package":"@astra/plugin-a","name":"A","kind":"middleware","origin":"astra","latest":"1.2.0","description":"d","license":"MIT","author":"me","homepage":"https://a.example","repository":"https://github.com/a/a","tags":["x","y"],"minAstraVersion":"0.7.0","summary":{"en":"En","zh":"中"} {{extra}} },{"package":"Not A Package","name":"bad"},{"package":"@magpie-community/opencode-zed-auth","community":true,"providers":["zed"],"replaces":"zed"}]}""";

    [Fact]
    public void BuiltInIcon_names_the_listings_icon_for_each_provider_it_serves()
    {
        Assert.Equal("workbuddy-color", PluginMarket.BuiltInIcon("workbuddy"));
        Assert.Equal("workbuddy-color", PluginMarket.BuiltInIcon("workbuddy-ai"));
        const string qoder = "https://unpkg.com/@lobehub/icons-static-svg@1.95.1/icons/qoder-color.svg";
        Assert.Equal(qoder, PluginMarket.BuiltInIcon("qoder"));
        // a provider the listing doesn't name is matched by the package its plugin was installed as
        Assert.Equal(qoder, PluginMarket.BuiltInIcon("qoder-cn", "@astragate-community/qoder-auth@latest"));
        // and by the name the same plugin had before the community repository took it over
        Assert.Equal(qoder, PluginMarket.BuiltInIcon("qoder-cn", "@magpie-community/opencode-qoder-auth@latest"));
        Assert.Null(PluginMarket.BuiltInIcon("qoder-cn", "@astragate-community/qoder-authx"));
        Assert.Null(PluginMarket.BuiltInIcon("qoder-cn", "@magpie-community/opencode-qoder-authx"));
        Assert.Null(PluginMarket.BuiltInIcon("no-such-provider"));
    }

    [Fact]
    public async Task Reads_the_docs_registry_by_default_and_never_asks_npm_or_github()
    {
        var plugins = new TestPlugins();
        var http = new StubHttp(_ => StubHttp.Ok(Registry()));
        var list = await Market(plugins, http).ListAsync();

        Assert.Equal([DocsRegistry], http.Requests);
        Assert.Equal(["@astra/plugin-a", "@magpie-community/opencode-zed-auth"], list.Select(l => l.Package));
        var a = list[0];
        Assert.Equal("astra", a.Origin);
        Assert.Equal("1.2.0", a.Latest);
        Assert.Equal("MIT", a.License);
        Assert.Equal("0.7.0", a.MinAstraVersion);
        Assert.Equal(["x", "y"], a.Tags);
        Assert.Equal("中", a.Summary["zh"]);
        Assert.Equal("upstream", list[1].Origin); // no community flag needed, and none of the entries had to be community
        Assert.Equal("provider", list[1].Kind);
    }

    [Fact]
    public void Chooses_the_source_by_environment_then_setting_then_default()
    {
        var plugins = new TestPlugins();
        var none = new StubHttp(_ => StubHttp.NotFound());
        Assert.Equal(PluginMarket.DefaultUrl, Market(plugins, none).Source);
        Assert.Equal(PluginMarket.DefaultUrl, Market(plugins, none, configured: () => null).Source);
        Assert.Equal("https://m.example/r.json", Market(plugins, none, configured: () => " https://m.example/r.json ").Source);
        Assert.Equal("https://env.example/r.json", Market(plugins, none, env: "https://env.example/r.json", configured: () => "https://m.example/r.json").Source);
        Assert.Null(Market(plugins, none, env: "off", configured: () => "https://m.example/r.json").Source);
        Assert.Null(Market(plugins, none, configured: () => "").Source);
        // an address that isn't http(s) is no address
        Assert.Equal("https://m.example/r.json", Market(plugins, none, env: "ftp://x", configured: () => "https://m.example/r.json").Source);
        Assert.Equal(PluginMarket.DefaultUrl, Market(plugins, none, configured: () => "not a url").Source);
    }

    [Fact]
    public async Task Kept_to_cached_and_built_in_copies_when_switched_off_or_emptied()
    {
        var plugins = new TestPlugins();
        var http = new StubHttp(_ => StubHttp.Ok(Registry()));
        var list = await Market(plugins, http, configured: () => "").ListAsync();

        Assert.Empty(http.Requests);
        Assert.Contains(list, l => l.Package == "@astragate-community/middleware-model-map"); // the built-in copy
        Assert.Empty(await Market(plugins, http, env: "off").SearchAsync("zed"));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Falls_back_to_the_copy_fetched_last_and_then_the_built_in_one()
    {
        var plugins = new TestPlugins();
        var online = true;
        var http = new StubHttp(_ => online ? StubHttp.Ok(Registry()) : StubHttp.NotFound());

        Assert.Contains((await Market(plugins, http).ListAsync()), l => l.Package == "@astra/plugin-a");
        online = false;
        var cached = await Market(plugins, http).ListAsync(); // a new market: no memory, so the file kept last is read
        Assert.Contains(cached, l => l.Package == "@astra/plugin-a");

        File.Delete(Path.Combine(plugins.Paths.PluginMarketDir, "registry.json"));
        var builtIn = await Market(plugins, http).ListAsync();
        Assert.DoesNotContain(builtIn, l => l.Package == "@astra/plugin-a");
        Assert.NotEmpty(builtIn);
    }

    [Fact]
    public async Task Hosted_tarballs_are_resolved_against_the_registry_and_need_a_hash_and_a_safe_address()
    {
        var sha = new string('a', 64);
        var tarballs = $$"""
            ,"tarballs":[
              {"version":"1.0.0","url":"/api/plugin-market/packages/p/1.0.0.tgz","sha256":"{{sha}}","size":10},
              {"version":"1.2.0","url":"https://cdn.example/p/1.2.0.tgz","sha256":"{{sha.ToUpperInvariant()}}","sha512":"abc=","size":20},
              {"version":"1.1.0","url":"http://cdn.example/p/1.1.0.tgz","sha256":"{{sha}}"},
              {"version":"0.9.0","url":"/p/0.9.0.tgz","sha256":"short"},
              {"version":"0.8.0","url":"http://127.0.0.1:9/p/0.8.0.tgz","sha256":"{{sha}}"}]
            """;
        var plugins = new TestPlugins();
        var market = Market(plugins, new StubHttp(_ => StubHttp.Ok(Registry(tarballs))));

        var a = (await market.FindAsync("@astra/plugin-a"))!;
        // newest first; plain http to another machine and a short hash are dropped; http on this machine is fine
        Assert.Equal(["1.2.0", "1.0.0", "0.8.0"], a.Tarballs.Select(t => t.Version));
        Assert.Equal("https://astra-gate.si/api/plugin-market/packages/p/1.0.0.tgz", a.Tarballs[1].Url);
        Assert.Equal(sha, a.Tarballs[0].Sha256); // lower-cased
        Assert.Equal("abc=", a.Tarballs[0].Sha512);

        var newest = (await market.FindHostedAsync("@astra/plugin-a"))!.Value;
        Assert.Equal("1.2.0", newest.Tarball.Version);
        Assert.Equal("1.0.0", (await market.FindHostedAsync("@astra/plugin-a", "1.0.0"))!.Value.Tarball.Version);
        Assert.Null(await market.FindHostedAsync("@astra/plugin-a", "9.9.9"));
        Assert.Null(await market.FindHostedAsync("@magpie-community/opencode-zed-auth"));
        Assert.Null(await market.FindHostedAsync("nope"));
    }

    [Fact]
    public async Task Searches_through_the_registrys_host()
    {
        var plugins = new TestPlugins();
        var http = new StubHttp(_ => StubHttp.Ok("""
            [{"spec":"@astra/plugin-a","name":"A","description":"d","version":"1.0.0","source":"astra","kind":"middleware","stars":0},
             {"spec":"github:o/r","name":"r","source":"github","kind":"agent","stars":7},
             {"spec":"x","source":"weird","kind":"weird"},
             {"name":"no spec"}]
            """));
        var market = Market(plugins, http);

        var results = await market.SearchAsync(" zed & co ");
        Assert.Equal(["https://astra-gate.si/api/plugin-market/search?q=zed%20%26%20co"], http.Requests);
        Assert.Equal(["astra", "github", "npm"], results.Select(r => r.Source));
        Assert.Equal(["middleware", "agent", "provider"], results.Select(r => r.Kind));
        Assert.Equal(7, results[1].Stars);

        await market.SearchAsync("zed & co"); // cached
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Search_finds_nothing_when_the_registry_has_no_search_beside_it_or_cant_be_reached()
    {
        var plugins = new TestPlugins();
        var http = new StubHttp(_ => StubHttp.NotFound());
        Assert.Empty(await Market(plugins, http, env: "https://example.com/plugins.json").SearchAsync("zed"));
        Assert.Empty(http.Requests);
        Assert.Empty(await Market(plugins, http).SearchAsync("zed")); // a 404 is no result, not an error
    }

    [Theory]
    [InlineData(null, "0.1.0", true)]
    [InlineData("0.7.0", "0.7.1", true)]
    [InlineData("0.7.1", "0.7.1", true)]
    [InlineData("0.8.0", "0.7.1", false)]
    [InlineData("1", "0.9.9", false)]
    public void Knows_whether_this_astra_is_new_enough(string? min, string current, bool expected) =>
        Assert.Equal(expected, PluginMarket.MeetsMinVersion(min, current));
}
