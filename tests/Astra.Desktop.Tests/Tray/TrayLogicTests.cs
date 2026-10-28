using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tray;

/// <summary>Ports of web/src/test/tray.test.tsx ("tray helpers") and desktop/tests/tray.test.ts, plus the C#-only pieces.</summary>
[Collection("Loc")]
public sealed class TrayLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public TrayLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    internal static StatsSummaryDto Summary(long requests = 7, double success = 6.0 / 7, decimal cost = 0.004m)
        => new("today", cost, requests, success, 60_000, 8_800, 30_000, 0, 0, null);

    internal static ProviderAccountDto Account(string id = "acc-1", string status = "active", JsonNode? quota = null, string providerId = "p1", bool noQuota = false)
        => new(id, providerId, "me@example.com", null, null, status, null, null,
            noQuota ? null : quota ?? JsonNode.Parse("""{"session":{"usedPercent":1,"windowMinutes":300,"resetsAtUtc":"2026-10-08T14:00:00Z"},"weekly":{"usedPercent":36,"windowMinutes":10080,"resetsAtUtc":"2026-10-12T00:00:00Z"}}"""),
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"));

    internal static ProviderDto Glm => Dtos.Provider("p1", "GLM Coding Plan", auth: "oauth-subscription") with { Icon = "zhipu" };

    [Fact]
    public void Failures_and_short_costs()
    {
        Assert.Equal(1, TrayMath.FailedRequests(Summary()));
        Assert.Equal(0, TrayMath.FailedRequests(Summary(0, 0)));
        Assert.Equal("$0", TrayMath.FormatCostShort(0));
        Assert.Equal("$<0.01", TrayMath.FormatCostShort(0.004m));
        Assert.Equal("$0.37", TrayMath.FormatCostShort(0.371m));
        Assert.Equal("$12.40", TrayMath.FormatCostShort(12.4m));
        Assert.Equal("$1.2K", TrayMath.FormatCostShort(1234m));
    }

    [Fact]
    public void Hourly_buckets_and_yesterday()
    {
        var now = LocalTime.At(new DateTimeOffset(2026, 10, 8, 15, 30, 0, TimeSpan.Zero), TimeSpan.Zero);
        TimeseriesPointDto P(string b, string k, long req, long tok) => new(b, k, 0, req, tok);
        var bars = TrayMath.HourlyRequests([P("2026-10-08T09:00", "a", 3, 10), P("2026-10-08T09:00", "b", 2, 10)], now);
        Assert.Equal(24, bars.Count);
        Assert.Equal(new HourBar("2026-10-08T09:00", 5), bars[9]);
        Assert.Equal(500, TrayMath.YesterdayTokens([P("2026-10-07", "a", 1, 400), P("2026-10-07", "b", 1, 100), P("2026-10-06", "a", 1, 900)], now));
        Assert.Equal(0.5, TrayMath.ChangeRatio(750, 500));
        Assert.Null(TrayMath.ChangeRatio(10, 0));
    }

    [Fact]
    public void Top_model_by_tokens()
    {
        var top = new List<TopModelDto> { new("a", 0, 9, 10, 0, 0), new("b", 0, 1, 30, 0, 0) };
        Assert.Equal(("b", 0.75), TrayMath.TopModelShare(top, 40));
        Assert.Null(TrayMath.TopModelShare([], 0));
        Assert.Equal(("b", 1.0), TrayMath.TopModelShare(top, 0));
    }

    [Fact]
    public void Subscription_quotas_become_remaining_share_rows()
    {
        var grok = Dtos.Provider("p2", "Grok", auth: "oauth-subscription");
        var plans = TrayMath.QuotaPlans([
            (Glm, Account()),
            (Glm, Account("acc-2", "expired")),
            (Glm, Account("acc-3", noQuota: true)),
            (grok, Account("acc-4", quota: JsonNode.Parse("""{"credits":{"usedPercent":95}}"""), providerId: "p2")),
        ]);
        Assert.Equal(["acc-1", "acc-4"], plans.Select(p => p.AccountId));
        Assert.Equal([(TrayQuotaKind.Session, 99.0), (TrayQuotaKind.Weekly, 64.0)], plans[0].Rows.Select(r => (r.Kind, r.Remaining!.Value)));
        Assert.Equal("me@example.com", plans[0].AccountName);
        Assert.Equal("zhipu", plans[0].ProviderIcon);
        var tight = TrayMath.TightestQuota(plans)!;
        Assert.Equal((TrayQuotaKind.Credits, 5.0), (tight.Row.Kind, tight.Remaining));
        Assert.Equal([(TrayQuotaKind.Credits, 5.0), (TrayQuotaKind.Weekly, 64.0)], TrayMath.TightestRows(plans, 2).Select(r => (r.Row.Kind, r.Remaining)));
        Assert.Equal(QuotaTone.Green, TrayMath.Tone(64));
        Assert.Equal(QuotaTone.Orange, TrayMath.Tone(20));
        Assert.Equal(QuotaTone.Red, TrayMath.Tone(5));
    }

    [Fact]
    public void Plan_label_and_default_window_lengths()
    {
        var plans = TrayMath.QuotaPlans([(Glm, Account(quota: JsonNode.Parse("""{"planLabel":"Max","session":{"usedPercent":10}}""")))]);
        Assert.Equal("Max", plans[0].ProviderName);
        Assert.Equal(300, plans[0].Rows[0].WindowMinutes);
    }

    [Fact]
    public void Api_key_provider_balances_and_windows()
    {
        var on = new ProviderQuotaConfigDto(true, null, null, null, null, 0, 0, null, [], [], null, null);
        var p = Dtos.Provider("k1", "DeepSeek") with
        {
            QuotaConfig = on,
            Quota = JsonNode.Parse("""{"isValid":true,"plans":[{"unit":"CNY","remaining":110,"name":"Top-up"},{"windowMinutes":300,"usedPercent":20,"resetsAtUtc":"2026-10-26T13:00:00Z"},{"unit":"USD","remaining":5,"total":10,"used":5}]}""")!.AsObject(),
        };
        var off = Dtos.Provider("k2", "Off") with { Quota = p.Quota };
        var invalid = p with { Id = "k3", Quota = JsonNode.Parse("""{"isValid":false,"plans":[{"remaining":1}]}""")!.AsObject() };
        var plans = TrayMath.ProviderQuotaPlans([p, off, invalid]);
        var plan = Assert.Single(plans);
        Assert.Equal("provider:k1", plan.AccountId);
        Assert.Equal("", plan.AccountName);
        Assert.Equal(TrayQuotaKind.Balance, plan.Rows[0].Kind);
        Assert.Null(plan.Rows[0].Remaining);
        Assert.Equal(new TrayAmount(110, "CNY"), plan.Rows[0].Amount);
        Assert.Equal("Top-up", TrayMath.RowName(plan.Rows[0]));
        Assert.Equal((TrayQuotaKind.Window, 80.0), (plan.Rows[1].Kind, plan.Rows[1].Remaining!.Value));
        Assert.Equal(50, plan.Rows[2].Remaining);
        Assert.Equal("Balance", TrayMath.RowName(plan.Rows[2]));
        // The balance without a share is never the tightest.
        Assert.Equal(50, TrayMath.TightestQuota(plans)!.Remaining);
    }

    [Fact]
    public void Window_lengths_and_relative_times()
    {
        Assert.Equal("5h", TrayMath.WindowLength(300));
        Assert.Equal("7d", TrayMath.WindowLength(10_080));
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        Assert.Equal("in 1 hour", TrayMath.ResetsIn("2026-10-08T13:00:00Z", now));
        Assert.Null(TrayMath.ResetsIn("2026-10-08T11:00:00Z", now));
        Assert.Null(TrayMath.ResetsIn(null, now));
        Assert.Equal("in 3 days", TrayMath.ResetsIn("2026-10-11T12:00:00Z", now));
        Assert.Equal("in 5 minutes", TrayMath.ResetsIn("2026-10-08T12:05:00Z", now));
        Assert.Equal("just now", TrayMath.UpdatedAgo(now.AddSeconds(-5), now, "just now"));
        Assert.Equal("3 minutes ago", TrayMath.UpdatedAgo(now.AddMinutes(-3), now, "just now"));
        Assert.Equal("1 hour ago", TrayMath.UpdatedAgo(now.AddMinutes(-70), now, "just now"));
        Assert.Null(TrayMath.UpdatedAgo(null, now, "just now"));

        Loc.Language = "zh";
        Assert.Equal("5小时", TrayMath.WindowLength(300));
        Assert.Equal("1小时后", TrayMath.ResetsIn("2026-10-08T13:00:00Z", now));
        Assert.Equal("后天", TrayMath.ResetsIn("2026-10-10T12:00:00Z", now));
        Assert.Equal("3天后", TrayMath.ResetsIn("2026-10-11T12:00:00Z", now));
        Assert.Equal("3分钟前", TrayMath.UpdatedAgo(now.AddMinutes(-3), now, "刚刚"));
    }

    [Fact]
    public void Menu_bar_title_for_every_mode()
    {
        var plans = TrayMath.QuotaPlans([(Glm, Account())]);
        var s = Summary();
        Assert.Equal(TrayTitleText.None, TrayTitle.Compute(TrayTitleMode.None, true, s, plans));
        Assert.Equal(new TrayTitleText("7", "7 calls today"), TrayTitle.Compute(TrayTitleMode.Requests, true, s, plans));
        Assert.Equal(new TrayTitleText("68.8K", "68.8K tokens today"), TrayTitle.Compute(TrayTitleMode.Tokens, true, s, plans));
        Assert.Equal(new TrayTitleText("$<0.01", "$<0.01 estimated today"), TrayTitle.Compute(TrayTitleMode.Cost, true, s, plans));
        Assert.Equal(new TrayTitleText("64%", "GLM Coding Plan 7d window: 64% left"), TrayTitle.Compute(TrayTitleMode.Quota, true, s, plans));
        Assert.Equal(new TrayTitleText("⚠ 1", "1 failed today"), TrayTitle.Compute(TrayTitleMode.Alerts, true, s, plans));
        Assert.Equal(TrayTitleText.None, TrayTitle.Compute(TrayTitleMode.Alerts, true, Summary(success: 1), plans));
        Assert.Equal(new TrayTitleText("!", "Gateway not running"), TrayTitle.Compute(TrayTitleMode.Alerts, false, s, plans));
        // No data yet / gateway down: nothing rather than a stale or zero number.
        Assert.Equal("", TrayTitle.Compute(TrayTitleMode.Requests, true, null, plans).Title);
        Assert.Equal("", TrayTitle.Compute(TrayTitleMode.Requests, false, s, plans).Title);
        Assert.Equal("", TrayTitle.Compute(TrayTitleMode.Quota, true, s, []).Title);
        // A critical quota is an alert too.
        var low = TrayMath.QuotaPlans([(Glm, Account(quota: JsonNode.Parse("""{"weekly":{"usedPercent":95,"windowMinutes":10080}}""")))]);
        Assert.Equal("⚠ 5%", TrayTitle.Compute(TrayTitleMode.Alerts, true, Summary(success: 1), low).Title);
    }

    [Fact]
    public void Title_input_is_sanitized()
    {
        Assert.Equal(new TrayTitleText("7", "Today 7 calls"), TrayTitle.Sanitize(" 7\n", "Today 7 calls"));
        Assert.Equal(TrayTitleText.None, TrayTitle.Sanitize(null, null));
        Assert.Equal(24, TrayTitle.Sanitize(new string('x', 40), "").Title.Length);
        Assert.Equal("a b", TrayTitle.Sanitize("a\r\n\tb", "").Title);
    }

    [Fact]
    public void Prefs_parse_tolerantly_one_field_at_a_time()
    {
        var tray = TrayPrefsJson.Parse(JsonNode.Parse("""{"title":"cost","sections":{"overview":false,"quota":"yes","bogus":true}}"""));
        Assert.Equal(TrayTitleMode.Cost, tray.Title);
        Assert.False(tray.Sections[TraySection.Overview]);
        Assert.Equal(TrayPrefs.Default.Sections[TraySection.Quota], tray.Sections[TraySection.Quota]);
        Assert.Equal(8, tray.Sections.Count);
        Assert.Equal(TrayPrefs.Default.Title, TrayPrefsJson.Parse(JsonNode.Parse("""{"title":"everything"}""")).Title);
        Assert.True(TrayPrefsJson.Same(TrayPrefs.Default, TrayPrefsJson.Parse(null)));
        Assert.True(TrayPrefsJson.Same(TrayPrefs.Default, TrayPrefsJson.Parse(JsonNode.Parse("[1]"))));
        // Parsing never hands out the shared default dictionary.
        Assert.NotSame(TrayPrefs.Default.Sections, TrayPrefsJson.Parse(null).Sections);
    }

    [Fact]
    public void Prefs_are_written_in_electrons_shape()
    {
        // JSON.stringify(prefs.tray): title, then all eight sections in TRAY_SECTIONS order.
        Assert.Equal(
            """{"title":"requests","sections":{"overview":true,"cost":true,"chart":true,"topModel":true,"compareYesterday":false,"cacheHitRate":false,"quota":true,"clients":false}}""",
            TrayPrefsJson.ToJson(TrayPrefs.Default).ToJsonString());
        var partial = new TrayPrefs(TrayTitleMode.Alerts, new Dictionary<TraySection, bool> { [TraySection.Clients] = true });
        var round = TrayPrefsJson.Parse(TrayPrefsJson.ToJson(partial));
        Assert.Equal(TrayTitleMode.Alerts, round.Title);
        Assert.True(round.Sections[TraySection.Clients]);
        Assert.True(round.Sections[TraySection.Overview]);
    }

    [Fact]
    public void Panel_drops_down_from_a_macos_menu_bar_icon()
        => Assert.Equal(new ScreenRect(1035, 31, 360, 500), TrayPanelLayout.Bounds(new(1200, 0, 30, 24), new(0, 25, 1512, 920), 360, 500));

    [Fact]
    public void Panel_opens_upward_from_a_bottom_taskbar()
        => Assert.Equal(new ScreenRect(1554, 534, 360, 500), TrayPanelLayout.Bounds(new(1880, 1044, 24, 32), new(0, 0, 1920, 1040), 360, 500));

    [Fact]
    public void Panel_opens_sideways_from_a_left_taskbar()
        => Assert.Equal(new ScreenRect(54, 574, 360, 500), TrayPanelLayout.Bounds(new(8, 1000, 32, 32), new(48, 0, 1872, 1080), 360, 500));

    [Fact]
    public void Panel_below_a_cursor_or_above_without_room()
    {
        var work = new ScreenRect(0, 0, 1920, 1080);
        Assert.Equal(106, TrayPanelLayout.Bounds(new(500, 100, 0, 0), work, 360, 500).Y);
        Assert.Equal(494, TrayPanelLayout.Bounds(new(500, 1000, 0, 0), work, 360, 500).Y);
        var tall = TrayPanelLayout.Bounds(new(1200, 0, 30, 24), new(0, 25, 1440, 400), 360, 900);
        Assert.Equal((388.0, 31.0), (tall.Height, tall.Y));
        Assert.Equal(2000, TrayPanelLayout.ContentHeight(5000));
        Assert.Equal(301, TrayPanelLayout.ContentHeight(300.2));
        Assert.Null(TrayPanelLayout.ContentHeight(double.NaN));
        Assert.Null(TrayPanelLayout.ContentHeight(0));
    }

    private static TrayState State(bool running = true, int? port = 17321, TrayActivity? activity = null, bool? openAtLogin = false,
        IReadOnlyList<TrayClient>? clients = null, IReadOnlyList<TrayProvider>? providers = null, string? update = null, bool mismatch = false)
        => new(running, running ? port : null, mismatch, clients ?? [], providers ?? [], update, activity, openAtLogin);

    private static List<TrayMenuItem> Menu(TrayState s, string platform, string lang)
        => TrayMenuModel.Build(s, TrayMenuLabels.For(lang), platform, "1.2.3", MenuLabels.For(lang));

    private static TrayMenuItem Find(IEnumerable<TrayMenuItem> items, string label)
        => items.FirstOrDefault(i => i.Label == label) ?? throw new Xunit.Sdk.XunitException($"no menu item \"{label}\" in {string.Join(", ", items.Select(i => i.Label))}");

    [Fact]
    public void Menu_shows_version_status_and_ends_with_quit()
    {
        var items = Menu(State(), "darwin", "en");
        Assert.Equal(("Astra 1.2.3", false), (items[0].Label, items[0].Enabled));
        Assert.Equal(("Service running on port 17321", false), (items[1].Label, items[1].Enabled));
        Assert.Equal(("Quit Astra", true), (items[^1].Label, items[^1].QuitAccelerator));
        var zh = Menu(State(running: false), "win32", "zh");
        Assert.Equal("服务未运行", zh[1].Label);
        Find(zh, "启动服务");
        Assert.Equal(("退出 Astra", false), (zh[^1].Label, zh[^1].QuitAccelerator));
    }

    [Fact]
    public void Menu_service_items_follow_the_state()
    {
        var running = Menu(State(), "darwin", "en");
        Assert.Equal(TrayMenuVerb.Stop, Find(running, "Stop Service").Action!.Verb);
        Assert.Equal(TrayMenuVerb.Restart, Find(running, "Restart Service").Action!.Verb);
        Assert.DoesNotContain(running, i => i.Label == "Start Service");
        var stopped = Menu(State(running: false), "darwin", "en");
        Assert.DoesNotContain(stopped, i => i.Label == "Stop Service");
        Assert.False(Find(stopped, "Copy API Address").Enabled);
        Assert.False(Find(stopped, "Open in Browser").Enabled);
        var busy = Menu(State(activity: TrayActivity.Restarting), "darwin", "en");
        Assert.Equal("Restarting service…", busy[1].Label);
        Assert.False(Find(busy, "Stop Service").Enabled);
        Assert.False(Find(busy, "Restart Service").Enabled);
    }

    [Fact]
    public void Menu_panel_entry_only_on_linux_and_go_to_every_page()
    {
        Assert.Equal(TrayMenuVerb.ShowPanel, Find(Menu(State(), "linux", "en"), "Show Tray Panel").Action!.Verb);
        Assert.DoesNotContain(Menu(State(), "darwin", "en"), i => i.Label == "Show Tray Panel");
        Assert.DoesNotContain(Menu(State(), "win32", "zh"), i => i.Label == "显示托盘面板");
        var sub = Find(Menu(State(), "darwin", "en"), "Go To").Submenu!;
        Assert.Equal(ShortcutMap.NavOrder.Count, sub.Count);
        Assert.Equal(new TrayMenuAction(TrayMenuVerb.Navigate, "requests"), sub[1].Action);
    }

    [Fact]
    public void Menu_lists_enabled_clients_with_their_provider()
    {
        var s = State(
            clients: [new("claude-code", "Claude Code", true, "p2"), new("codex", "Codex", false, null)],
            providers: [new("p1", "OpenAI", true), new("p2", "DeepSeek", false)]);
        var mac = Menu(s, "darwin", "en");
        var client = Find(mac, "Claude Code");
        Assert.Equal("DeepSeek", client.Sublabel);
        Assert.True(client.Enabled);
        Assert.DoesNotContain(mac, i => i.Label == "Codex");
        Assert.Equal([("OpenAI", false), ("DeepSeek (disabled)", true)], client.Submenu!.Select(i => (i.Label, i.Checked)));
        Assert.Equal(new TrayMenuAction(TrayMenuVerb.SwitchProvider, "claude-code", "p1"), client.Submenu![0].Action);
        Find(Menu(s, "win32", "zh"), "Claude Code · DeepSeek");
        Assert.False(Find(Menu(s with { Running = false }, "darwin", "en"), "Claude Code").Enabled);
        var none = Menu(State(clients: [new("codex", "Codex", true, null)]), "darwin", "en");
        Assert.Equal("Not bound", Find(none, "Codex").Sublabel);
        Assert.Equal(("No providers configured", false), (Find(none, "Codex").Submenu![0].Label, Find(none, "Codex").Submenu![0].Enabled));
    }

    [Fact]
    public void Menu_login_item_update_and_mismatch()
    {
        var item = Find(Menu(State(openAtLogin: true), "darwin", "en"), "Launch at Login");
        Assert.Equal((TrayMenuItemKind.Checkbox, true), (item.Kind, item.Checked));
        Assert.Equal(new TrayMenuAction(TrayMenuVerb.ToggleOpenAtLogin, Flag: false), item.Action);
        Assert.DoesNotContain(Menu(State(openAtLogin: null), "darwin", "en"), i => i.Label == "Launch at Login");
        var items = Menu(State(update: "yes", mismatch: true), "darwin", "en");
        Find(items, "API version mismatch — run \"astra update\"");
        Assert.Equal(TrayMenuVerb.CheckUpdates, Find(items, "Update Available…").Action!.Verb);
    }

    [Fact]
    public void Tooltip_and_change_key()
    {
        var en = TrayMenuLabels.For("en");
        Assert.Equal("Astra — running on port 17321", TrayMenuModel.Tooltip(State(), en));
        Assert.Equal("Astra — 服务未运行", TrayMenuModel.Tooltip(State(running: false), TrayMenuLabels.For("zh-CN")));
        Assert.Equal("Astra — Starting service…", TrayMenuModel.Tooltip(State(activity: TrayActivity.Starting), en));
        Assert.Equal("Astra — API version mismatch", TrayMenuModel.Tooltip(State(mismatch: true), en));
        Assert.Equal(State().Key(), State().Key());
        Assert.NotEqual(State().Key(), State(openAtLogin: true).Key());
        Assert.NotEqual(State(clients: [new("a", "A", true, null)]).Key(), State(clients: [new("a", "A", true, "p")]).Key());
    }
}
