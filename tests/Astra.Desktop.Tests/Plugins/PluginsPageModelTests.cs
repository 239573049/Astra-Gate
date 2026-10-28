using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Tests.Api;

namespace Astra.Desktop.Tests.Plugins;

[Collection("Loc")]
public sealed class PluginsPageModelTests : IDisposable
{
    private readonly string _saved = Loc.Language;
    private readonly ManualTime _time = new();
    private readonly StubHttp _http = new();
    private readonly QueryClient _queries;
    private readonly ApiClient _api;
    private readonly RecordingFeedback _feedback = new();
    private readonly RecordingNavigator _nav = new();

    public PluginsPageModelTests()
    {
        Loc.Language = "en";
        _queries = new QueryClient(null, _time);
        _api = new ApiClient("http://stub", _http);
        _http.On("GET /api/plugins", """{"plugins":[{"spec":"p1","name":"p1","shortName":"p1","enabled":true,"kinds":["provider"],"isPath":false},{"spec":"m1","name":"m1","shortName":"m1","enabled":true,"kinds":["middleware"],"isPath":false}],"preferences":{},"runtime":{"downloading":false}}""")
             .On("GET /api/plugins/host", """{"running":true,"plugins":[],"recent":[]}""")
             .On("GET /api/plugins/import-magpie", """{"found":false,"path":"","plugins":[],"signIns":0}""")
             .On("GET /api/plugins/market", "[]")
             .On("GET /api/plugins/search", "[]");
    }

    public void Dispose()
    {
        Loc.Language = _saved;
        _queries.Dispose();
        _api.Dispose();
    }

    private sealed class RecordingFeedback : IFeedback
    {
        public List<string> Errors { get; } = [];
        public List<string> Successes { get; } = [];
        public List<ConfirmRequest> Asked { get; } = [];
        public bool Answer { get; set; } = true;
        public void Success(string title, string? description = null) => Successes.Add(title);
        public void Error(string title, string? description = null) => Errors.Add(title);
        public List<string> Infos { get; } = [];
        public void Info(string title, string? description = null) => Infos.Add(title);
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default)
        {
            Asked.Add(request);
            return Task.FromResult(Answer);
        }
    }

    private PluginsPageModel Model(PluginRoute? route = null)
        => new(new PluginsApi(_api, _queries), _feedback, _nav, _queries, route ?? new PluginRoute(PluginTab.Installed));

    private static async Task Settle(PluginsPageModel m)
    {
        await m.Api.Plugins.RefetchAsync();
    }

    [Fact]
    public async Task The_installed_tab_reads_plugins_host_and_magpie_but_not_the_market()
    {
        var m = Model();
        m.Activate();
        await Settle(m);
        await m.Api.Host.RefetchAsync();
        Assert.True(_http.Count("GET", "/api/plugins") >= 1);
        Assert.True(_http.Count("GET", "/api/plugins/host") >= 1);
        Assert.True(_http.Count("GET", "/api/plugins/import-magpie") >= 1);
        Assert.Equal(0, _http.Count("GET", "/api/plugins/market"));
        m.Dispose();
    }

    [Fact]
    public async Task The_host_is_only_asked_for_when_a_host_plugin_is_installed_or_on_the_runtime_tab()
    {
        _http.On("GET /api/plugins", """{"plugins":[{"spec":"m1","name":"m1","shortName":"m1","enabled":true,"kinds":["middleware"],"isPath":false}],"preferences":{},"runtime":{"downloading":false}}""");
        var m = Model();
        m.Activate();
        await Settle(m);
        Assert.False(m.Api.Host.Enabled);
        Assert.Equal(0, _http.Count("GET", "/api/plugins/host"));
        m.Tab = PluginTab.Runtime;
        Assert.True(m.Api.Host.Enabled);
        await m.Api.Host.RefetchAsync();
        Assert.True(_http.Count("GET", "/api/plugins/host") >= 1);
        m.Dispose();
    }

    [Fact]
    public async Task The_market_loads_when_its_tab_opens_and_search_waits_for_the_text_to_rest()
    {
        var m = Model();
        m.Activate();
        m.Tab = PluginTab.Market;
        await m.Api.Market.RefetchAsync();
        Assert.True(_http.Count("GET", "/api/plugins/market") >= 1);

        m.SearchText = "wo";
        _time.Advance(TimeSpan.FromMilliseconds(300));
        m.SearchText = "word";
        _time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("", m.DebouncedQuery);
        Assert.False(m.Searching);
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal("word", m.DebouncedQuery);
        Assert.True(m.Searching);
        await m.Api.Search("word").RefetchAsync();
        Assert.Contains(_http.Requests, r => r.Method == "GET" && r.PathAndQuery == "/api/plugins/search?q=word");
        m.Dispose();
    }

    [Fact]
    public void A_search_of_one_character_does_not_search()
    {
        var m = Model();
        m.Activate();
        m.Tab = PluginTab.Market;
        m.SearchText = " a ";
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("a", m.DebouncedQuery);
        Assert.False(m.Searching);
        Assert.Equal(0, _http.Count("GET", "/api/plugins/search"));
        m.Dispose();
    }

    [Fact]
    public async Task Installing_from_the_market_asks_first_and_follows_the_job()
    {
        _http.On("POST /api/plugins", """{"id":"j9","action":"add","target":"pkg","state":"running","startedAt":"2026-10-26T00:00:00Z","log":[]}""");
        var m = Model();
        _feedback.Answer = false;
        await m.InstallAsync("pkg", "market");
        Assert.Equal(0, _http.Count("POST", "/api/plugins"));
        Assert.Equal("Install pkg?", _feedback.Asked[0].Title);
        Assert.Equal(Loc.T("plugins.trustDetail"), _feedback.Asked[0].Message);
        Assert.Null(m.JobId);

        _feedback.Answer = true;
        await m.InstallAsync("pkg", "market");
        var post = Assert.Single(_http.Requests, r => r.Method == "POST" && r.PathAndQuery == "/api/plugins");
        Assert.Equal("1", post.Admin);
        Assert.Equal("""{"spec":"pkg","source":"market"}""", post.Body);
        Assert.Equal("j9", m.JobId);
    }

    [Fact]
    public async Task A_failed_install_is_an_error_toast_and_no_job()
    {
        _http.On("POST /api/plugins", """{"error":"plugins are off"}""", 409);
        var m = Model();
        await m.AddAsync(" pkg ");
        Assert.Equal(["plugins are off"], _feedback.Errors);
        Assert.Null(m.JobId);
        Assert.False(m.Adding);
        Assert.Equal("""{"spec":"pkg"}""", _http.Requests.Last(r => r.Method == "POST").Body);
    }

    [Fact]
    public async Task Removing_asks_with_a_destructive_confirm_and_sends_the_spec_in_the_query()
    {
        _http.On("DELETE /api/plugins", """{"id":"r1","action":"remove","target":"@s/p","state":"running","startedAt":"2026-10-26T00:00:00Z","log":[]}""");
        var m = Model();
        var plugin = new PluginDto("@s/p", "@s/p", "p", null, true, ["provider"], null, null, null, null, false, null, null, null);
        await m.RemoveAsync(plugin);
        var ask = Assert.Single(_feedback.Asked);
        Assert.Equal("Remove plugin p?", ask.Title);
        Assert.True(ask.Destructive);
        Assert.Equal(Loc.T("common.delete"), ask.ConfirmText);
        Assert.Contains(_http.Requests, r => r.Method == "DELETE" && r.PathAndQuery == "/api/plugins?spec=%40s%2Fp");
        Assert.Equal("r1", m.JobId);
    }

    [Fact]
    public async Task Update_all_sends_no_spec_and_a_single_update_sends_its_own()
    {
        _http.On("POST /api/plugins/update", """{"id":"u1","action":"update","target":"*","state":"running","startedAt":"2026-10-26T00:00:00Z","log":[]}""");
        var m = Model();
        await m.UpdateAllAsync();
        // The web sends {spec: undefined}, which JSON.stringify drops: an empty object, the server's "every plugin".
        Assert.Equal("{}", Assert.Single(_http.Requests, r => r.PathAndQuery == "/api/plugins/update").Body);
        Assert.Equal("u1", m.JobId);
        await m.UpdateAsync(new PluginDto("p", "p", "p", null, true, ["provider"], null, null, null, null, false, null, null, null));
        Assert.Equal("""{"spec":"p"}""", _http.Requests.Last(r => r.PathAndQuery == "/api/plugins/update").Body);
    }

    [Fact]
    public async Task Toggling_and_saving_options_patch_the_plugin_and_clearing_sends_a_null()
    {
        _http.On("PATCH /api/plugins", """{"plugins":[],"preferences":{},"runtime":{"downloading":false}}""");
        var m = Model();
        var plugin = new PluginDto("p", "p", "p", null, true, ["middleware"], null, null, null, null, false, null, null, null);
        await m.SetEnabledAsync(plugin, false);
        Assert.True(await m.SaveOptionsAsync(plugin, new JsonObject { ["words"] = new JsonArray("a") }));
        Assert.True(await m.SaveOptionsAsync(plugin, null));
        var patches = _http.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.All(patches, p => Assert.Equal("/api/plugins?spec=p", p.PathAndQuery));
        Assert.Equal("""{"enabled":false}""", patches[0].Body);
        Assert.Equal("""{"options":{"words":["a"]}}""", patches[1].Body);
        Assert.Equal("""{"options":null}""", patches[2].Body);
        // web: toast(t('plugins.optionsSaved')) is the default (info) tone.
        Assert.Equal([Loc.T("plugins.optionsSaved"), Loc.T("plugins.optionsSaved")], _feedback.Infos);
    }

    [Fact]
    public async Task Reordering_puts_the_moved_spec_in_place()
    {
        _http.On("PUT /api/plugins/order", """{"plugins":[],"preferences":{},"runtime":{"downloading":false}}""");
        var m = Model();
        m.Activate();
        await Settle(m);
        await m.MoveAsync(1, -1);
        Assert.Equal("""{"specs":["m1","p1"]}""", _http.Requests.Single(r => r.Method == "PUT").Body);
        m.Dispose();
    }

    [Fact]
    public async Task A_mutation_refreshes_plugins_host_clients_and_providers()
    {
        _http.On("POST /api/plugins/prefer", """{"plugins":[],"preferences":{},"runtime":{"downloading":false}}""");
        var fetched = new Dictionary<string, int> { ["clients"] = 0, ["providers"] = 0 };
        var clients = _queries.Query<int>("clients", _ => Task.FromResult(++fetched["clients"]));
        var providers = _queries.Query<int>("providers", _ => Task.FromResult(++fetched["providers"]));
        using var t1 = clients.Activate();
        using var t2 = providers.Activate();
        await clients.RefetchAsync();
        var before = (fetched["clients"], fetched["providers"]);
        var m = Model();
        m.Activate();
        await m.Api.Plugins.RefetchAsync();
        var plugins = _http.Count("GET", "/api/plugins");
        await m.PreferAsync(new PluginDto("p", "p", "p", null, true, ["provider"], null, null, null, null, false, null, null, null), "kiro");
        await clients.RefetchAsync();
        Assert.Equal("""{"spec":"p","provider":"kiro"}""", _http.Requests.Single(r => r.PathAndQuery == "/api/plugins/prefer").Body);
        Assert.True(fetched["clients"] > before.Item1, "clients were read again");
        Assert.True(fetched["providers"] > before.Item2, "providers were read again");
        Assert.True(_http.Count("GET", "/api/plugins") > plugins, "plugins were read again");
        m.Dispose();
    }

    [Fact]
    public async Task A_website_link_asks_before_installing_and_a_bad_one_is_refused()
    {
        _http.On("POST /api/plugins", """{"id":"j1","action":"add","target":"x","state":"running","startedAt":"2026-10-26T00:00:00Z","log":[]}""");
        var good = Model(PluginRoute.Parse("market?install=%40astragate-community%2Fcline-auth"));
        good.Activate();
        await Task.Delay(50);
        Assert.Equal("Install @astragate-community/cline-auth?", Assert.Single(_feedback.Asked).Title);
        good.Dispose();

        _feedback.Asked.Clear();
        var bad = Model(PluginRoute.Parse("market?install=..%2F..%2Fevil"));
        bad.Activate();
        Assert.Empty(_feedback.Asked);
        Assert.Equal([Loc.T("plugins.linkInvalid")], _feedback.Errors);
        bad.Dispose();
    }

    [Fact]
    public async Task Following_a_job_polls_each_second_until_it_ends()
    {
        var calls = 0;
        _http.On("GET /api/plugins/jobs/j1", _ => (200, ++calls < 3
            ? """{"id":"j1","action":"add","target":"x","state":"running","startedAt":"2026-10-26T00:00:00Z","log":["a"]}"""
            : """{"id":"j1","action":"add","target":"x","state":"succeeded","startedAt":"2026-10-26T00:00:00Z","log":["a","b"]}"""));
        var m = Model();
        m.Activate();
        m.JobId = "j1";
        var job = m.Api.Job("j1");
        await job.RefetchAsync();
        Assert.Equal("running", job.Data!.State);
        // A running job is read again after a second, and again until it has ended; then it is left alone.
        for (var i = 0; i < 6 && job.Data!.State == "running"; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(40);
        }
        Assert.Equal("succeeded", job.Data!.State);
        var seen = calls;
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(40);
        Assert.Equal(seen, calls);
        m.Dispose();
    }
}
