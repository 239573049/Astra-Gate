using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Astra.Plugins.Market;
using Astra.Plugins.Packages;
using Astra.Plugins.Runtime;

namespace Astra.Plugins.Tests;

/// <summary>Plugins the market hosts: fetched from the market, checked against its hash, kept, updated and removed.</summary>
public sealed class PluginInstallerHostedTests
{
    private const string Pkg = "@t/middleware-x";
    private const string RegistryUrl = "https://market.test/api/plugin-market/registry.json";

    /// <summary>A fake market: the registry (rebuilt from <see cref="Versions"/>) and the tarballs it lists.</summary>
    private sealed class Fixture
    {
        public Fixture(Func<string, byte[]>? make = null, string? botBun = null)
        {
            Plugins = new TestPlugins();
            Make = make ?? (v => TestTarballs.Middleware(Pkg, v));
            Http = new StubHttp(Answer);
            var bun = new BunRuntime(Plugins.Paths, Http.Client, name => name == "ASTRA_BUN" ? botBun : null);
            var market = new PluginMarket(Plugins.Paths, Http.Client, name => name == "ASTRA_PLUGIN_MARKET" ? RegistryUrl : null);
            Market = market;
            Installer = new PluginInstaller(Plugins.Catalog, bun, new NpmTarballFetcher(Http.Client), market);
        }

        public TestPlugins Plugins { get; }
        public StubHttp Http { get; }
        public PluginMarket Market { get; }
        public PluginInstaller Installer { get; }
        public Func<string, byte[]> Make { get; }

        /// <summary>Newest first; a version in <see cref="Corrupt"/> is served with other bytes than the hash is of.</summary>
        public List<string> Versions { get; } = [];
        public HashSet<string> Corrupt { get; } = [];
        public bool Offline { get; set; }

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            if (Offline) throw new HttpRequestException("offline");
            var url = request.RequestUri!.AbsoluteUri;
            if (url == RegistryUrl) return StubHttp.Ok(TestTarballs.Registry(Pkg, Versions.Select(v => (v, Make(v))).ToArray()));
            foreach (var v in Versions)
                if (url == $"https://market.test/api/plugin-market/packages/x/{v}.tgz")
                    return StubHttp.Ok(Corrupt.Contains(v) ? TestTarballs.Make(("package/other.txt", "tampered")) : Make(v));
            return StubHttp.NotFound();
        }

        public async Task<PluginEntry> AddAsync(string? version = null, bool pinned = false)
        {
            Market.ListAsync(refresh: true).GetAwaiter().GetResult();
            var found = (await Market.FindHostedAsync(Pkg, version))!.Value;
            return await Installer.AddHostedAsync(Pkg, found.Tarball, pinned, _ => { });
        }

        public string Package(string file) => Path.Combine(Plugins.Paths.PluginsDir, "packages", "node_modules", "@t", "middleware-x", file);

        public string Kept(string version) => Path.Combine(Plugins.Paths.PluginsDir, "market-packages", "@t_middleware-x", version + ".tgz");
    }

    [Fact]
    public async Task Installs_a_hosted_middleware_without_bun_and_keeps_its_tarball()
    {
        var f = new Fixture();
        f.Versions.Add("1.0.0");

        var entry = await f.AddAsync();

        Assert.Equal($"{Pkg}@latest", entry.Spec);
        Assert.Equal(PluginSources.MarketPackage, entry.Source);
        Assert.Equal("1.0.0", entry.InstalledVersion);
        Assert.True(File.Exists(f.Package("mw.js")));
        Assert.True(File.Exists(f.Kept("1.0.0")));
        // the packages folder's package.json points at the kept tarball, so a later bun add finds it where it expects it
        var deps = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Plugins.Paths.PluginsDir, "packages", "package.json")))!["dependencies"]!;
        Assert.Equal(f.Kept("1.0.0"), deps[Pkg]!.GetValue<string>());
        Assert.Equal(entry.Spec, (await f.Plugins.Store.ListAsync()).Single().Spec);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Plugins.Paths.PluginsDir, ".staging")));
    }

    [Fact]
    public async Task A_pinned_install_keeps_its_version_in_the_spec()
    {
        var f = new Fixture();
        f.Versions.AddRange(["1.1.0", "1.0.0"]);

        var entry = await f.AddAsync("1.0.0", pinned: true);

        Assert.Equal($"{Pkg}@1.0.0", entry.Spec);
        Assert.Equal("1.0.0", entry.InstalledVersion);
    }

    [Fact]
    public async Task Refuses_a_tarball_that_doesnt_match_the_markets_hash_and_leaves_nothing_behind()
    {
        var f = new Fixture();
        f.Versions.Add("1.0.0");
        f.Corrupt.Add("1.0.0");

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => f.AddAsync());

        Assert.Contains("sha256", e.Message);
        Assert.Empty(await f.Plugins.Store.ListAsync());
        Assert.False(Directory.Exists(f.Package("")) && File.Exists(f.Package("mw.js")));
        Assert.False(File.Exists(f.Kept("1.0.0")));
    }

    [Fact]
    public async Task Refuses_a_tarball_of_another_package_or_version_than_the_market_says()
    {
        var other = new Fixture(v => TestTarballs.Middleware("@t/somebody-else", v));
        other.Versions.Add("1.0.0");
        var e1 = await Assert.ThrowsAsync<InvalidDataException>(() => other.AddAsync());
        Assert.Contains("somebody-else", e1.Message);

        var stale = new Fixture(_ => TestTarballs.Middleware(Pkg, "0.0.1"));
        stale.Versions.Add("1.0.0");
        var e2 = await Assert.ThrowsAsync<InvalidDataException>(() => stale.AddAsync());
        Assert.Contains("0.0.1", e2.Message);
        Assert.Empty(await stale.Plugins.Store.ListAsync());
    }

    [Fact]
    public async Task Refuses_a_tarball_that_writes_outside_its_package()
    {
        var f = new Fixture(v => TestTarballs.Make(
            ("package/package.json", $$"""{"name":"{{Pkg}}","version":"{{v}}","magpie":{"middleware":"./mw.js"} }"""),
            ("package/mw.js", "export default {};"),
            ("package/../../escaped.txt", "x")));
        f.Versions.Add("1.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => f.AddAsync());

        Assert.False(File.Exists(Path.Combine(f.Plugins.Paths.PluginsDir, "escaped.txt")));
        Assert.Empty(await f.Plugins.Store.ListAsync());
    }

    [Fact]
    public async Task Updates_ask_the_market_and_keep_options_and_only_the_newest_tarball()
    {
        var f = new Fixture();
        f.Versions.Add("1.0.0");
        var entry = await f.AddAsync();
        await f.Plugins.Store.SetOptionsAsync(entry.Spec, """{"a":1}""");
        f.Plugins.Catalog.Invalidate();

        // published after the registry was cached: an explicit update asks the market again
        f.Versions.Insert(0, "1.1.0");
        var log = new List<string>();
        await f.Installer.UpdateAsync(null, log.Add);

        var now = (await f.Plugins.Store.ListAsync()).Single();
        Assert.Equal("1.1.0", now.InstalledVersion);
        Assert.Equal("""{"a":1}""", now.OptionsJson);
        Assert.Equal(PluginSources.MarketPackage, now.Source);
        Assert.Contains("1.1.0", File.ReadAllText(f.Package("package.json")));
        Assert.True(File.Exists(f.Kept("1.1.0")));
        Assert.False(File.Exists(f.Kept("1.0.0")));

        log.Clear();
        await f.Installer.UpdateAsync(null, log.Add);
        Assert.Contains(log, l => l.Contains("up to date"));
    }

    [Fact]
    public async Task An_update_the_market_cant_give_leaves_the_install_as_it_was()
    {
        var f = new Fixture();
        f.Versions.Add("1.0.0");
        await f.AddAsync();

        f.Versions.Insert(0, "1.1.0");
        f.Offline = true;
        // not "nothing newer": the cached registry must not stand in for a market that can't be asked
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.UpdateAsync(null, _ => { }));
        Assert.Contains("offline", e.Message);

        var now = (await f.Plugins.Store.ListAsync()).Single();
        Assert.Equal("1.0.0", now.InstalledVersion);
        Assert.Contains("1.0.0", File.ReadAllText(f.Package("package.json")));
        Assert.True(File.Exists(f.Kept("1.0.0")));
        Assert.False(File.Exists(f.Kept("1.1.0")));
    }

    [Fact]
    public async Task Removing_takes_out_the_package_and_its_kept_tarballs()
    {
        var f = new Fixture();
        f.Versions.Add("1.0.0");
        var entry = await f.AddAsync();

        await f.Installer.RemoveAsync(entry.Spec, _ => { });

        Assert.Empty(await f.Plugins.Store.ListAsync());
        Assert.False(File.Exists(f.Package("package.json")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Kept("1.0.0"))));
    }

    [BunFact]
    public async Task A_hosted_provider_goes_through_bun_add_from_the_kept_tarball()
    {
        var f = new Fixture(v => TestTarballs.Provider(Pkg, v), BunFactAttribute.BunPath);
        f.Versions.Add("1.0.0");

        var entry = await f.AddAsync();

        Assert.Equal("1.0.0", entry.InstalledVersion);
        Assert.True(File.Exists(f.Package("index.js")));
        var deps = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Plugins.Paths.PluginsDir, "packages", "package.json")))!["dependencies"]!;
        Assert.Equal(f.Kept("1.0.0"), deps[Pkg]!.GetValue<string>());

        await f.Installer.RemoveAsync(entry.Spec, _ => { });
        Assert.False(File.Exists(f.Package("index.js")));
        Assert.False(File.Exists(f.Kept("1.0.0")));
    }
}
