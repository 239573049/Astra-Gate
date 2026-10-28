using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tray;

/// <summary>The non-drawing decisions of TrayPanelView (web/src/test/tray.test.tsx "TrayPanelView").</summary>
[Collection("Loc")]
public sealed class TrayPanelLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public TrayPanelLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    private static readonly TrayPanelState Running = new(true, 18317, null, false, null);

    private static string T(string key, (string Name, object? Value)[] args) => Loc.T(key, args);

    internal static T Json<T>(string json) => System.Text.Json.JsonSerializer.Deserialize(json, Astra.Core.JsonContexts.Info<T>(Astra.Core.Json.Api))!;

    internal static ClientInfoDto Client(string kind, string name, bool enabled, string? providerId)
        => Json<ClientInfoDto>(
            $$"""{"kind":"{{kind}}","name":"{{name}}","protocol":"openai-chat","availability":"available","mode":"config","detection":{"installed":true},"status":"applied","enabled":{{(enabled ? "true" : "false")}},"providerId":{{(providerId is null ? "null" : "\"" + providerId + "\"")}},"extras":{},"warnings":[],"requiresRestart":false}""");

    private static TrayData Data(IReadOnlyList<TrayQuotaPlan>? plans = null, bool subs = true, IReadOnlyList<ClientInfoDto>? clients = null)
        => TrayData.Empty with
        {
            Summary = TrayLogicTests.Summary(),
            Top = [new TopModelDto("glm-5.3-flash", 0, 7, 68_800, 60_000, 0)],
            Plans = plans ?? TrayMath.QuotaPlans([(TrayLogicTests.Glm, TrayLogicTests.Account())]),
            HasSubscriptions = subs,
            Clients = clients ?? [],
        };

    [Fact]
    public void Status_address_and_copy_text()
    {
        Assert.Equal("tray.status.running", TrayPanelLogic.StatusKey(Running));
        Assert.Equal("tray.status.stopped", TrayPanelLogic.StatusKey(Running with { Running = false }));
        Assert.Equal("tray.status.restarting", TrayPanelLogic.StatusKey(Running with { Activity = TrayActivity.Restarting }));
        var settings = Json<SettingsDto>("""{"gatewayBaseUrl":"http://localhost:18317"}""");
        Assert.Equal("localhost:18317", TrayPanelLogic.Address(settings, 1));
        Assert.Equal("127.0.0.1:18317", TrayPanelLogic.Address(null, 18317));
        Assert.Equal("", TrayPanelLogic.Address(null, null));
        Assert.Equal("http://localhost:18317/v1", TrayPanelLogic.CopyText(settings));
        Assert.Null(TrayPanelLogic.CopyText(null));
    }

    [Fact]
    public void Failure_and_quota_alerts()
    {
        var alerts = TrayPanelLogic.Alerts(TrayPrefs.Default, Data(), T);
        Assert.Equal([new TrayAlert(true, "1 failed · 14.3% of today", false)], alerts);
        var low = TrayMath.QuotaPlans([(TrayLogicTests.Glm, TrayLogicTests.Account(quota: JsonNode.Parse("""{"weekly":{"usedPercent":92,"windowMinutes":10080}}""")))]);
        var both = TrayPanelLogic.Alerts(TrayPrefs.Default, Data(low) with { Summary = TrayLogicTests.Summary(success: 1) }, T);
        Assert.Equal([new TrayAlert(true, "GLM Coding Plan 7d window: only 8% left", true)], both);
        var orange = TrayMath.QuotaPlans([(TrayLogicTests.Glm, TrayLogicTests.Account(quota: JsonNode.Parse("""{"weekly":{"usedPercent":80,"windowMinutes":10080}}""")))]);
        Assert.False(TrayPanelLogic.Alerts(TrayPrefs.Default, Data(orange) with { Summary = TrayLogicTests.Summary(success: 1) }, T)[0].Critical);
        // Switched-off sections raise nothing; no summary yet, nothing.
        var off = TrayLogic.WithSection(TrayLogic.WithSection(TrayPrefs.Default, TraySection.Overview, false), TraySection.Quota, false);
        Assert.Empty(TrayPanelLogic.Alerts(off, Data(low), T));
        Assert.Empty(TrayPanelLogic.Alerts(TrayPrefs.Default, Data(low) with { Summary = null }, T));
    }

    [Fact]
    public void Tiles_and_chips_follow_the_sections()
    {
        var tiles = TrayPanelLogic.Tiles(TrayPrefs.Default, TrayLogicTests.Summary());
        Assert.Equal([("tray.requests", "7"), ("tray.tokens", "68.8K"), ("tray.cost", "$<0.01")], tiles);
        var noCost = TrayLogic.WithSection(TrayPrefs.Default, TraySection.Cost, false);
        Assert.Equal(2, TrayPanelLogic.Tiles(noCost, TrayLogicTests.Summary()).Count);

        var all = new TrayPrefs(TrayTitleMode.None, TrayPrefsJson.Sections.ToDictionary(s => s, _ => true));
        var now = LocalTime.At(Fakes.Noon, TimeSpan.Zero);
        var daily = new List<TimeseriesPointDto> { new("2026-10-25", "p", 0, 1, 34_400) };
        var chips = TrayPanelLogic.Chips(all, Data() with { Daily = daily }, now, k => Loc.T(k));
        Assert.Equal(new TrayChip("Top model", "glm-5.3-flash", "glm-5.3-flash", Share: "100%"), chips[0]);
        Assert.Equal(new TrayChip("Cache hits", "50.0%"), chips[1]);
        Assert.Equal(new TrayChip("vs yesterday", "100%", Up: true), chips[2]);
        Assert.Equal(new TrayChip("vs yesterday", "—"), TrayPanelLogic.Chips(all, Data(), now, k => Loc.T(k))[2]);
        Assert.False(TrayPanelLogic.HasChips(TrayLogic.WithSection(TrayPrefs.Default, TraySection.TopModel, false)));
        var nothing = new TrayPrefs(TrayTitleMode.None, TrayPrefsJson.Sections.ToDictionary(s => s, _ => false));
        Assert.False(TrayPanelLogic.HasUsage(nothing));
    }

    [Fact]
    public void Only_tabs_with_content()
    {
        var prefs = TrayLogic.WithSection(TrayPrefs.Default, TraySection.Clients, true);
        var tabs = TrayPanelLogic.Tabs(prefs, Data(clients: [Client("codex", "Codex", true, null)]));
        Assert.Equal([TrayTab.Overview, TrayTab.Quota, TrayTab.Clients], tabs);
        Assert.True(TrayPanelLogic.ShowTabs(Running, tabs));
        Assert.False(TrayPanelLogic.ShowTabs(Running with { Running = false }, tabs));
        Assert.True(TrayPanelLogic.ShowTabs(new TrayPanelState(false, null, TrayActivity.Starting, false, null), tabs));
        var only = TrayPanelLogic.Tabs(TrayPrefs.Default, Data([], subs: false));
        Assert.Equal([TrayTab.Overview], only);
        Assert.False(TrayPanelLogic.ShowTabs(Running, only));
        // A pending subscription still gets the quota tab; a disabled client no clients tab.
        Assert.Contains(TrayTab.Quota, TrayPanelLogic.Tabs(TrayPrefs.Default, Data([], subs: true)));
        Assert.DoesNotContain(TrayTab.Clients, TrayPanelLogic.Tabs(prefs, Data(clients: [Client("codex", "Codex", false, null)])));
        Assert.Equal(TrayTab.Overview, TrayPanelLogic.Active(TrayTab.Clients, only));
        Assert.Equal("tray.clients", TrayPanelLogic.TabKey(TrayTab.Clients));
    }

    [Fact]
    public void Chart_quota_and_clients_helpers()
    {
        Assert.Equal(64, TrayPanelLogic.BarHeight(10, 10));
        Assert.Equal(4, TrayPanelLogic.BarHeight(1, 1000));
        Assert.Equal(2, TrayPanelLogic.BarHeight(0, 10));
        var plans = TrayMath.QuotaPlans([(TrayLogicTests.Glm, TrayLogicTests.Account()), (TrayLogicTests.Glm, TrayLogicTests.Account("acc-2"))]);
        Assert.Equal(4, TrayPanelLogic.RowCount(plans));
        Assert.True(TrayPanelLogic.ShowAccount(plans, plans[0]));
        Assert.False(TrayPanelLogic.ShowAccount(plans[..1], plans[0]));
        var providers = new List<ProviderDto> { Dtos.Provider("p1", "OpenAI"), Dtos.Provider("p2", "Kimi", enabled: false) };
        Assert.Equal("OpenAI", TrayPanelLogic.ProviderName(providers, "p1", "Not bound"));
        Assert.Equal("Not bound", TrayPanelLogic.ProviderName(providers, null, "Not bound"));
        var menu = TrayPanelLogic.SwitchMenu(providers, Client("codex", "Codex", true, "p2"), n => Loc.T("tray.disabledProvider", ("name", n)));
        Assert.Equal([("p1", "OpenAI", false), ("p2", "Kimi (disabled)", true)], menu.Select(m => (m.Id!, m.Label!, m.Checked)));
        Assert.All(menu, m => Assert.True(m.Checkbox));
    }
}
