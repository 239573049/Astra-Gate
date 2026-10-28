using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;

namespace Astra.Desktop.Tests.Plugins;

// Loc.Language is process-wide state: these tests pin it and must not race the other Loc users.
[Collection("Loc")]
public sealed class PluginLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public PluginLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    private static PluginDto Plugin(
        string spec = "@astragate-community/middleware-word-guard", string[]? kinds = null, string? version = "0.1.4", bool isPath = false,
        string? error = null, MiddlewareStateDto? mw = null, PluginAgentDto? agent = null, string? icon = null, JsonObject? options = null)
        => new(spec, spec, spec.Split('/').Last(), version, true, [.. kinds ?? ["middleware"]], "market", options, null, icon, isPath, error, mw, agent);

    // ---- route ----

    [Theory]
    [InlineData(null, PluginTab.Installed, null)]
    [InlineData("", PluginTab.Installed, null)]
    [InlineData("market", PluginTab.Market, null)]
    [InlineData("runtime", PluginTab.Runtime, null)]
    [InlineData("bogus", PluginTab.Installed, null)]
    [InlineData("market?install=%40astragate-community%2Fcline-auth", PluginTab.Market, "@astragate-community/cline-auth")]
    [InlineData("market?install=github:owner/repo&x=1", PluginTab.Market, "github:owner/repo")]
    public void Route_parses_the_tab_and_the_install_link(string? param, PluginTab tab, string? install)
        => Assert.Equal(new PluginRoute(tab, install), PluginRoute.Parse(param));

    [Fact]
    public void Route_param_is_the_web_path_segment() => Assert.Equal(
        [null, "market", "runtime"], new[] { PluginTab.Installed, PluginTab.Market, PluginTab.Runtime }.Select(PluginRoute.ParamOf));

    [Theory]
    [InlineData("left-pad", true)]
    [InlineData("@scope/pkg.name", true)]
    [InlineData("github:owner/repo", true)]
    [InlineData("github:owner/repo/extra", false)]
    [InlineData("./local/path", false)]
    [InlineData("/abs/path", false)]
    [InlineData("C:\\plugin", false)]
    [InlineData("Upper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_packages_and_github_repositories_may_be_asked_for_by_a_link(string? spec, bool ok) => Assert.Equal(ok, PluginSpecs.IsLinkable(spec));

    // ---- kinds and icons ----

    [Fact]
    public void Kinds_have_the_web_labels_and_tones()
    {
        Assert.Equal("Provider", PluginKinds.Label("provider"));
        Assert.Equal("weird", PluginKinds.Label("weird"));
        Assert.Equal(PluginTone.Accent, PluginKinds.Tone("middleware"));
        Assert.Equal(PluginTone.Green, PluginKinds.Tone("agent"));
        Assert.Equal(PluginTone.Orange, PluginKinds.Tone("pi"));
        Assert.Equal(PluginTone.Neutral, PluginKinds.Tone("provider"));
        Assert.True(PluginKinds.RunsInHost("pi"));
        Assert.False(PluginKinds.RunsInHost("agent"));
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA", "provider", PluginIconKind.Picture)]
    [InlineData("https://x/y.svg", "middleware", PluginIconKind.MiddlewareGlyph)]
    [InlineData(null, "middleware", PluginIconKind.MiddlewareGlyph)]
    [InlineData(null, "agent", PluginIconKind.AgentGlyph)]
    [InlineData("https://x/y.svg", "provider", PluginIconKind.Brand)]
    [InlineData(null, "pi", PluginIconKind.Brand)]
    public void Icons_follow_the_picture_then_the_kind(string? icon, string kind, PluginIconKind expected) => Assert.Equal(expected, PluginIcons.Resolve(icon, kind));

    [Fact]
    public void Picture_urls_are_https_or_image_data() => Assert.Equal([true, true, false, false],
        new[] { "https://a/b.png", "DATA:image/svg+xml;utf8,<svg/>", "http://a/b.png", null }.Select(PluginIcons.IsPictureUrl));

    // ---- rows ----

    [Fact]
    public void A_row_with_middleware_shows_its_counters_and_last_failure()
    {
        var mw = new MiddlewareStateDto(["onRequest", "onResponse"], null, null, 120, 12.345, 2, "boom");
        var row = PluginRows.Build(Plugin(mw: mw), null, new Dictionary<string, string>());
        var detail = Assert.IsType<MiddlewareDetail>(Assert.Single(row.Details));
        Assert.Equal("Middleware onRequest, onResponse · 120 calls · 12.3 µs avg · 2 failures", detail.Stats);
        Assert.Equal("Last failure: boom", detail.LastError);
        Assert.Equal("v0.1.4", row.VersionText);
        Assert.True(row.HasMiddleware);
        Assert.Equal(["Middleware"], row.Badges.Select(b => b.Text));
        Assert.Equal(PluginTone.Accent, row.Badges[0].Tone);
        Assert.Empty(row.Alerts);
    }

    [Fact]
    public void A_middleware_that_failed_to_load_is_an_alert_and_not_a_detail()
    {
        var mw = new MiddlewareStateDto([], null, "bad hook", 0, 0, 0, null);
        var row = PluginRows.Build(Plugin(mw: mw, error: "missing"), null, new Dictionary<string, string>());
        Assert.Empty(row.Details);
        Assert.Equal(["missing"], row.Alerts.Select(a => a.Text).Take(1));
        Assert.Contains(row.Alerts, a => a.Text.Contains("bad hook", StringComparison.Ordinal));
    }

    [Fact]
    public void A_local_plugin_has_the_local_badge_and_no_update()
    {
        var row = PluginRows.Build(Plugin(kinds: ["provider", "agent"], isPath: true), null, new Dictionary<string, string>());
        Assert.False(row.CanUpdate);
        Assert.Equal(["Provider", "Client", "Local"], row.Badges.Select(b => b.Text));
        Assert.Equal("provider", row.Kind);
    }

    [Fact]
    public void An_agent_links_to_its_client()
    {
        var row = PluginRows.Build(Plugin(kinds: ["agent"], agent: new PluginAgentDto("aider", "plugin-aider", "Aider", null)), null, new Dictionary<string, string>());
        var detail = Assert.IsType<AgentDetail>(Assert.Single(row.Details));
        Assert.Equal("plugin-aider", detail.ClientKind);
        Assert.Equal("Adds the client \"Aider\": enable it on the Clients page", detail.Text);
    }

    [Fact]
    public void A_provider_plugin_lists_what_it_provides_who_else_serves_it_and_whether_it_was_picked()
    {
        var host = new PluginHostStatusDto(true, [new PluginHostLoadedDto("p", null, ["cursor", "kiro"], new Dictionary<string, string> { ["kiro"] = "other" })], null, null, []);
        var row = PluginRows.Build(Plugin(spec: "p", kinds: ["provider"]), host, new Dictionary<string, string> { ["cursor"] = "p" });
        var detail = Assert.IsType<ProvidesDetail>(Assert.Single(row.Details));
        Assert.Equal("Signs in to: cursor, kiro", detail.Text);
        var served = Assert.Single(detail.ServedBy);
        Assert.Equal("kiro", served.Provider);
        Assert.Equal("kiro is served by another plugin, other", served.Text);
        Assert.Equal("Use for kiro", served.ButtonText);
        Assert.Equal("Picked to serve these providers.", detail.PickedText);
    }

    [Fact]
    public void A_host_error_is_an_alert_and_nothing_provided_is_no_detail()
    {
        var host = new PluginHostStatusDto(true, [new PluginHostLoadedDto("p", "crashed", [], [])], null, null, []);
        var row = PluginRows.Build(Plugin(spec: "p", kinds: ["provider"]), host, new Dictionary<string, string>());
        Assert.Empty(row.Details);
        Assert.Equal("crashed", Assert.Single(row.Alerts).Text);
    }

    [Fact]
    public void Move_swaps_with_the_neighbour_and_ignores_the_ends()
    {
        string[] order = ["a", "b", "c"];
        Assert.Equal(["a", "c", "b"], PluginRows.Move(order, 1, 1));
        Assert.Equal(["b", "a", "c"], PluginRows.Move(order, 1, -1));
        Assert.Equal(order, PluginRows.Move(order, 0, -1));
        Assert.Equal(order, PluginRows.Move(order, 2, 1));
        Assert.Equal(order, PluginRows.Move(order, 9, 1));
    }

    [Fact]
    public void The_order_hint_shows_only_with_middleware()
    {
        Assert.False(PluginRows.ShowsOrderHint([Plugin(kinds: ["provider"])]));
        Assert.True(PluginRows.ShowsOrderHint([Plugin(mw: new MiddlewareStateDto([], null, null, 0, 0, 0, null))]));
    }

    [Fact]
    public void The_import_banner_counts_what_astra_does_not_have()
    {
        Assert.Null(PluginRows.ImportableCount(null));
        Assert.Null(PluginRows.ImportableCount(new MagpieImportPreviewDto(false, "", [], 0, null)));
        Assert.Null(PluginRows.ImportableCount(new MagpieImportPreviewDto(true, "", [new MagpiePluginDto("a", false, false, true)], 0, null)));
        Assert.Equal(1, PluginRows.ImportableCount(new MagpieImportPreviewDto(true, "", [new MagpiePluginDto("a", false, false, true), new MagpiePluginDto("b", false, true, false)], 1, null)));
    }

    // ---- market ----

    private static MarketListingDto Listing(string package, string kind = "provider", string? latest = "1.0.0", string origin = "upstream", string? min = null, string? desc = "d", Dictionary<string, string>? summary = null)
        => new(package, package, null, kind, [], null, summary ?? [], latest, desc, null, null, origin, null, null, null, [], min, []);

    [Fact]
    public void Filters_count_and_filter_the_registry()
    {
        MarketListingDto[] all = [Listing("a"), Listing("b", "middleware"), Listing("c", "agent"), Listing("d", "middleware")];
        Assert.Equal([4, 1, 2, 1], PluginMarketLogic.Filters.Select(f => PluginMarketLogic.Count(all, f)));
        Assert.Equal(["b", "d"], PluginMarketLogic.Filter(all, KindFilter.Middleware).Select(l => l.Package));
        Assert.Equal("All 4", PluginMarketLogic.FilterText(all, KindFilter.All));
        Assert.Equal("Middleware 2", PluginMarketLogic.FilterText(all, KindFilter.Middleware));
        Assert.Equal(0, PluginMarketLogic.Count(null, KindFilter.All));
    }

    [Fact]
    public void A_search_needs_two_characters() => Assert.Equal([false, false, true], new[] { "", "a", "ab" }.Select(PluginMarketLogic.IsSearching));

    [Fact]
    public void Installed_means_the_same_name_or_the_same_spec()
    {
        PluginDto[] installed = [Plugin(spec: "github:o/r"), Plugin(spec: "pkg")];
        Assert.True(PluginMarketLogic.IsInstalled(installed, "pkg", "x"));
        Assert.True(PluginMarketLogic.IsInstalled(installed, "x", "github:o/r"));
        Assert.False(PluginMarketLogic.IsInstalled(installed, "other", "other"));
    }

    [Fact]
    public void A_registry_card_picks_its_summary_by_language_and_collects_its_tags()
    {
        var l = Listing("p", "middleware", "0.2.0", "astra", "0.12.0", "fallback", new Dictionary<string, string> { ["en"] = "english", ["zh"] = "中文" });
        var card = PluginMarketLogic.CardOf(l, "zh", []);
        Assert.Equal("中文", card.Detail);
        Assert.Equal(["Middleware", "v0.2.0", "Astra", "Astra 0.12.0+"], card.Tags);
        Assert.Equal(("p", "market"), (card.InstallSpec, card.InstallSource));
        Assert.Equal("english", PluginMarketLogic.CardOf(l, "fr", []).Detail);
        Assert.Equal("fallback", PluginMarketLogic.CardOf(Listing("p", desc: "fallback"), "en", []).Detail);
        Assert.Null(PluginMarketLogic.CardOf(Listing("p", desc: null), "en", []).Detail);
        Assert.True(PluginMarketLogic.CardOf(l, "en", [Plugin(spec: "p")]).Installed);
    }

    [Theory]
    [InlineData("github", 12, null, "github", new[] { "Provider", "★ 12" })]
    [InlineData("npm", 0, "1.2.3", "npm", new[] { "Provider", "v1.2.3" })]
    [InlineData("astra", 0, "0.1.0", "market", new[] { "Provider", "v0.1.0", "Astra" })]
    [InlineData("npm", 0, null, "npm", new[] { "Provider" })]
    public void A_search_card_drops_empty_tags_and_maps_its_source(string source, int stars, string? version, string installSource, string[] tags)
    {
        var card = PluginMarketLogic.CardOf(new MarketSearchResultDto("spec", "name", "desc", version, source, "provider", stars), []);
        Assert.Equal(tags, card.Tags);
        Assert.Equal(installSource, card.InstallSource);
        Assert.Equal("spec", card.InstallSpec);
    }

    // ---- jobs ----

    private static PluginJobDto Job(string state, string[]? log = null, string? error = null)
        => new("j1", "add", "word-guard", state, DateTimeOffset.UnixEpoch, null, [.. log ?? []], error, null);

    [Fact]
    public void Job_texts_follow_the_state()
    {
        Assert.Null(PluginJobTexts.Description(null));
        Assert.Equal("Working on word-guard…", PluginJobTexts.Description(Job("running")));
        Assert.Equal("word-guard done", PluginJobTexts.Description(Job("succeeded")));
        Assert.Equal("word-guard failed", PluginJobTexts.Description(Job("failed")));
        Assert.Equal("…", PluginJobTexts.LogText(Job("running")));
        Assert.Equal("a\nb", PluginJobTexts.LogText(Job("running", ["a", "b"])));
        Assert.True(PluginJobTexts.IsRunning(null));
        Assert.True(PluginJobTexts.IsRunning(Job("running")));
        Assert.False(PluginJobTexts.IsRunning(Job("failed")));
        Assert.Equal("nope", PluginJobTexts.FailureText(Job("failed", error: "nope")));
        Assert.Null(PluginJobTexts.FailureText(Job("failed")));
        Assert.Null(PluginJobTexts.FailureText(Job("succeeded", error: "x")));
    }

    // ---- options ----

    [Fact]
    public void The_options_editor_starts_from_the_options_then_the_example_then_an_empty_object()
    {
        var set = new JsonObject { ["a"] = 1 };
        var example = new JsonObject { ["b"] = new JsonArray("x", "y") };
        Assert.Equal("{\n  \"a\": 1\n}", PluginOptions.InitialText(set, example).Replace("\r\n", "\n"));
        Assert.Contains("\"b\": [", PluginOptions.InitialText(null, example), StringComparison.Ordinal);
        Assert.Equal("{}", PluginOptions.InitialText(null, null));
        Assert.Null(PluginOptions.Description(set, example));
        Assert.Equal("Not set yet: this is the example the plugin gives.", PluginOptions.Description(null, example));
        Assert.StartsWith("Not set.", PluginOptions.Description(null, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_text_must_be_a_json_object()
    {
        Assert.True(PluginOptions.TryParse("", out var empty, out _));
        Assert.Empty(empty!);
        Assert.True(PluginOptions.TryParse("{\"words\":[\"a\"]}", out var o, out _));
        Assert.Equal("a", (string?)o!["words"]![0]);
        Assert.False(PluginOptions.TryParse("[1]", out _, out var notObject));
        Assert.Equal("Options must be a JSON object", notObject);
        Assert.False(PluginOptions.TryParse("{nope", out _, out var syntax));
        Assert.False(string.IsNullOrEmpty(syntax));
        Assert.False(PluginOptions.TryParse("null", out _, out var nul));
        Assert.Equal("Options must be a JSON object", nul);
    }

    // ---- format ----

    [Fact]
    public void Dates_are_24_hour_in_the_shape_of_the_language()
    {
        var at = new DateTimeOffset(2026, 10, 26, 9, 5, 7, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 26, 9, 5, 7)));
        Assert.Equal("2026/10/26 09:05", PluginFormat.DateTime(at, "zh"));
        Assert.Equal("10/26/2026, 09:05", PluginFormat.DateTime(at, "en"));
        Assert.Equal("—", PluginFormat.DateTime(null, "en"));
        Assert.Equal("09:05:07", PluginFormat.Time(at, "en", at.AddHours(1)));
        Assert.Equal("10/26 09:05", PluginFormat.Time(at, "zh", at.AddDays(2)));
        Assert.Equal("10/26, 09:05", PluginFormat.Time(at, "en", at.AddDays(2)));
    }
}
