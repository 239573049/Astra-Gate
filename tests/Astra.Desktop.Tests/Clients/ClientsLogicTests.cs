using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.Overview;
using static Astra.Desktop.Tests.Clients.ClientsTestData;

namespace Astra.Desktop.Tests.Clients;

public sealed class ClientsLogicTests
{
    private static string T(string key, params (string Name, object? Value)[] args)
        => key + (args.Length == 0 ? "" : "(" + string.Join(",", args.Select(a => a.Name + "=" + a.Value)) + ")");

    [Fact]
    public void Order_is_the_catalog_then_plugins_by_name()
    {
        var clients = new[] { Client("plugin-b", o => o["name"] = "Zeta"), Client("codex"), Client("plugin-a", o => o["name"] = "Alpha") };
        var order = ClientsLogic.OrderOf(clients);
        Assert.Equal(ClientCatalog.Order.Count + 2, order.Count);
        Assert.Equal(["plugin-a", "plugin-b"], order.TakeLast(2));
    }

    [Fact]
    public void Groups_follow_install_state_and_skip_empty_ones()
    {
        var clients = new[]
        {
            Client("claude-code"),
            Client("codex", o => { o["detection"]!["installed"] = false; o["enabled"] = true; }),
            Client("gemini-cli", o => o["detection"]!["installed"] = false),
            Client("zed", o => o["availability"] = "coming_soon"),
        };
        var groups = ClientsLogic.Groups(clients);
        Assert.Equal([ClientGroup.Installed, ClientGroup.NotInstalled, ClientGroup.ComingSoon], groups.Select(g => g.Group));
        // Catalog order: codex before claude-code; an enabled client counts as installed even when detection missed it.
        Assert.Equal(["codex", "claude-code"], groups[0].Items.Select(c => c.Kind));
        Assert.Equal("clients.group.notInstalled", groups[1].TitleKey);
        Assert.Empty(ClientsLogic.Groups([]));
    }

    [Fact]
    public void Selected_kind_falls_back_to_codex()
    {
        var clients = new[] { Client("codex"), Client("zed") };
        Assert.Equal("zed", ClientsLogic.SelectedKind("zed", clients));
        Assert.Equal("codex", ClientsLogic.SelectedKind("nope", clients));
        Assert.Equal("codex", ClientsLogic.SelectedKind(null, clients));
        // A built-in kind is always in the order, even before the list loads.
        Assert.Equal("zed", ClientsLogic.SelectedKind("zed", null));
    }

    [Fact]
    public void Version_update_and_shadowed()
    {
        var c = Client(patch: o => o["install"] = Install(version: "2.0.0", latest: "2.1.0", update: true));
        Assert.Equal("2.0.0", ClientsLogic.VersionOf(c));
        Assert.Equal("2.1.0", ClientsLogic.UpdateOf(c));
        var shadowed = Client(patch: o => o["install"] = Install(latest: "2.1.0", update: true, blocker: "shadowed"));
        Assert.True(ClientsLogic.IsShadowed(shadowed));
        Assert.Null(ClientsLogic.UpdateOf(shadowed));
        Assert.Equal(1, ClientsLogic.UpdatesFound([c, shadowed]));
        Assert.Equal("1.0.0", ClientsLogic.VersionOf(Client()));
    }

    [Fact]
    public void Status_badge_order()
    {
        Assert.Equal(ClientStatusBadge.ComingSoon, ClientsLogic.StatusOf(Client(patch: o => { o["availability"] = "coming_soon"; o["status"] = "drifted"; })));
        Assert.Equal(ClientStatusBadge.Drifted, ClientsLogic.StatusOf(Client(patch: o => { o["status"] = "drifted"; o["enabled"] = true; })));
        Assert.Equal(ClientStatusBadge.Enabled, ClientsLogic.StatusOf(Client(patch: o => o["enabled"] = true)));
        Assert.Equal(ClientStatusBadge.Disabled, ClientsLogic.StatusOf(Client()));
    }

    [Fact]
    public void Header_detail()
    {
        Assert.Equal("clients.detected(version=1.0.0) · /home/u/.codex/config.json", ClientsLogic.HeaderDetail(Client(), false, T));
        Assert.Equal("clients.detected(version=1.0.0)", ClientsLogic.HeaderDetail(Client(), true, T));
        Assert.Equal("clients.notDetected", ClientsLogic.HeaderDetail(Client(patch: o => { o["detection"]!["installed"] = false; o["detection"]!["configPaths"] = new JsonArray(); }), false, T));
        Assert.Equal("why", ClientsLogic.HeaderDetail(Client(patch: o => { o["availability"] = "coming_soon"; o["availabilityReason"] = "why"; }), false, T));
        Assert.Equal("clients.comingSoonDetail", ClientsLogic.HeaderDetail(Client(patch: o => o["availability"] = "coming_soon"), false, T));
    }

    [Fact]
    public void Provider_detail_shows_key_count_and_multiplier()
    {
        Assert.Equal("openai · providers.modelCount(count=3)", ClientsLogic.ProviderDetail(Provider("p1"), T));
        Assert.Equal("tpl-p2 · providers.modelCount(count=1) · ×1.5", ClientsLogic.ProviderDetail(Provider("p2", priceKey: null, multiplier: 1.5m, models: 1), T));
    }

    [Fact]
    public void Bindings_split_and_move()
    {
        var saved = ClientsLogic.SavedBindings(Client(patch: o => o["bindings"] = null));
        Assert.Equal([new ClientBindingDto("p1", null)], saved);
        Assert.Empty(ClientsLogic.SavedBindings(Client(patch: o => o["providerId"] = null)));

        var providers = new[] { Provider("p1"), Provider("p2"), Provider("p3", enabled: false), Provider("p4") };
        var order = new List<ClientBindingDto> { new("p2", null), new("gone", null), new("p3", null) };
        var (bound, available) = ClientsLogic.Split(order, providers);
        Assert.Equal(["p2", "p3"], bound.Select(b => b.Provider.Id));
        Assert.Equal(["p1", "p4"], available.Select(p => p.Id));

        Assert.Equal(["c", "a", "b"], ClientsLogic.Move(["a", "b", "c"], 2, 0));
        Assert.Equal(["b", "a", "c"], ClientsLogic.Move(["a", "b", "c"], 0, 1));
        Assert.Equal(["a", "b"], ClientsLogic.Move(["a", "b"], 0, 5));
    }

    [Fact]
    public void Token_options_keep_the_current_disabled_token()
    {
        var tokens = new[] { Token("t1", "one", true), Token("t2", "two", false), Token("t3", "three", false) };
        var options = ClientsLogic.TokenOptions(tokens, "t2", T);
        Assert.Equal([("t1", "one", false), ("t2", "clients.tokenDisabled(name=two)", true)], options);
    }

    [Fact]
    public void Account_binding_and_options()
    {
        Assert.True(ClientsLogic.IsAccountBinding(Provider("s", authScheme: "oauth-subscription")));
        Assert.True(ClientsLogic.IsAccountBinding(Provider("s", authScheme: "plugin")));
        Assert.False(ClientsLogic.IsAccountBinding(Provider("s")));
        Assert.False(ClientsLogic.IsAccountBinding(null));
        var accounts = new[]
        {
            Read<ProviderAccountDto>("""{"id":"a1","providerId":"s","displayName":"Work","status":"active","createdAt":"2026-01-01T00:00:00Z"}"""),
            Read<ProviderAccountDto>("""{"id":"a2","providerId":"s","displayName":"","status":"expired","createdAt":"2026-01-01T00:00:00Z"}"""),
        };
        Assert.Equal([("", "clients.accountDefault"), ("a1", "Work"), ("a2", "a2 · providers.subscription.status.expired")], ClientsLogic.AccountOptions(accounts, T));
    }

    [Theory]
    [InlineData("/home/u/.codex/config.toml", "/home/u/.codex")]
    [InlineData(@"C:\Users\u\.codex\config.toml", @"C:\Users\u\.codex")]
    [InlineData("/config.json", "/config.json")]
    [InlineData("config.json", "config.json")]
    public void Dirname_matches_the_web(string path, string expected) => Assert.Equal(expected, ClientsLogic.DirnameOf(path));

    private static JsonObject Install(string? version = null, string? latest = null, bool update = false, string? blocker = null) => new()
    {
        ["method"] = "npm", ["package"] = "x", ["homepageUrl"] = "https://x", ["installed"] = true, ["executable"] = "/bin/x", ["version"] = version,
        ["latestVersion"] = latest, ["latestCheckedAt"] = null, ["latestError"] = null, ["updateAvailable"] = update, ["installCommand"] = null,
        ["updateCommand"] = null, ["updateVia"] = null, ["blocker"] = blocker, ["busy"] = false,
    };

    private static TokenDto Token(string id, string name, bool enabled) => Read<TokenDto>(new JsonObject
    {
        ["id"] = id, ["name"] = name, ["isDefault"] = false, ["enabled"] = enabled, ["keyMasked"] = "sk-…", ["providerId"] = null, ["accountId"] = null,
        ["createdAt"] = "2026-01-01T00:00:00Z", ["updatedAt"] = "2026-01-01T00:00:00Z", ["lastUsedAt"] = null, ["clients"] = new JsonArray(),
        ["today"] = new JsonObject(), ["total"] = new JsonObject(), ["limitUsage"] = new JsonArray(),
    }.ToJsonString());
}
