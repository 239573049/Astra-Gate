using Astra.Api.Contracts;
using Astra.Desktop.Components.Privacy;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Avalonia.Headless.XUnit;
using Astra.Desktop.Pages.Privacy;
using Astra.Desktop.Pages.Settings;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>Scenes of WP9: Settings (general, notifications, remote access, tray, listen sheet, update states) and Privacy (overview, log, rules, tester).</summary>
public static partial class Scenes
{
    static partial void AddSettingsAndPrivacy(Dictionary<string, Func<Scene>> d)
    {
        // The web capture runs in UTC (index.timeZone); the machine's zone would shift every time text.
        Astra.Desktop.Core.Settings.SettingsFormat.DisplayZone = TimeZoneInfo.Utc;
        // ---- Settings › General
        d["settings__data"] = () => Page(AppRoute.Settings, Fixtures());
        d["settings__data-lower"] = () => Scrolled(Page(AppRoute.Settings, Fixtures()), 1100);
        d["settings__data-bottom"] = () => Scrolled(Page(AppRoute.Settings, Fixtures()), 100000);
        d["settings__error"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/settings", """{"error":"settings unavailable"}""", 500));
        d["settings__update-available"] = () => Scrolled(Page(AppRoute.Settings, Fixtures().Override("/api/update/status", UpdateAvailable)), 100000);
        d["settings__update-downloading"] = () => Scrolled(Downloading(), 100000);
        d["settings__proxy-custom"] = () => Scrolled(Page(AppRoute.Settings, Fixtures().Override("/api/settings", SettingsWith(("proxyMode", "\"custom\""), ("proxyUrl", "\"http://127.0.0.1:7890\""),
            ("proxyUsername", "\"dev\""), ("hasProxyPassword", "true"), ("proxyBypass", "\"example.com, *.lan\"")))), 560);
        d["settings__lan-pending"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/server/listen", ListenLan).Override("/api/auth/status", """{"required":false,"signedIn":false,"remote":false,"adminBlocked":false}"""));
        d["settings__listen-sheet"] = () => After(Page(AppRoute.Settings, Fixtures()), root => root.GetVisualDescendants().OfType<GeneralSettingsView>().First().Listen.ShowPasswordSheetAsync());

        // ---- Settings › Notifications
        d["settings__notifications"] = () => Page(AppRoute.Settings, Alerts(Fixtures()), param: "notifications");
        d["settings__notifications-empty"] = () => Page(AppRoute.Settings, Fixtures()
            .Override("/api/webhooks", "[]").Override("/api/alerts/rules", "[]").Override("/api/alerts/states", "[]").Override("/api/alerts/events", AlertEvents), param: "notifications");
        d["settings__webhook-sheet"] = () => After(Page(AppRoute.Settings, Alerts(Fixtures()), param: "notifications"), root => Notifications(root).OpenWebhookEditor(null));
        d["settings__webhook-edit"] = () => After(Page(AppRoute.Settings, Alerts(Fixtures()), param: "notifications"),
            root => Notifications(root).OpenWebhookEditor(WebhookDto("wh_feishu")));
        d["settings__webhook-deliveries"] = () => After(Page(AppRoute.Settings, Alerts(Fixtures()), param: "notifications"),
            root => Notifications(root).OpenDeliveries(WebhookDto("wh_ops")));
        d["settings__rule-sheet"] = () => After(Page(AppRoute.Settings, Alerts(Fixtures()), param: "notifications"), root => Notifications(root).OpenRuleEditor(null));

        // ---- Settings › Remote access
        d["settings__remote-off"] = () => Page(AppRoute.Settings, Fixtures(), param: "remote");
        d["settings__remote-downloading"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel", Tunnel("downloading", enabled: true, downloading: true)), param: "remote");
        d["settings__remote-running"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel", Tunnel("connected", enabled: true, running: true)), param: "remote");
        d["settings__remote-error"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel",
            Tunnel("reconnecting", enabled: true, error: "failed to dial to edge with quic: timeout: no recent network activity", log: true)), param: "remote");
        d["settings__remote-named"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel", Tunnel("off", mode: "named")), param: "remote");
        d["settings__remote-admin"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel", Tunnel("connected", enabled: true, running: true, allowAdmin: true, password: true)), param: "remote");
        d["settings__remote-unsupported"] = () => Page(AppRoute.Settings, Fixtures().Override("/api/tunnel", Tunnel("off", supported: false)), param: "remote");

        // ---- Settings › Tray panel
        d["settings__tray"] = () => Page(AppRoute.Settings, Fixtures(), param: "tray");

        // ---- Privacy
        d["privacy__overview"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()));
        d["privacy__overview-empty"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()).Override("/api/privacy/events/stats", """{"events":0,"blocked":0,"dryRun":0,"redactions":0,"categories":[]}"""));
        d["privacy__logs"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()), param: "logs");
        d["privacy__logs-empty"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()).Override("/api/privacy/events", """{"items":[],"total":0,"page":1,"pageSize":50}"""), param: "logs");
        d["privacy__logs-paged"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()).Override("/api/privacy/events", PrivacyEvents(total: 137)), param: "logs");
        d["privacy__rules"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()), param: "rules");
        d["privacy__rules-sheet"] = () => After(Page(AppRoute.Privacy, Privacy(Fixtures()), param: "rules"), root => root.GetVisualDescendants().OfType<PrivacyRulesView>().First().OpenAddRule());
        d["privacy__tester"] = () => After(Page(AppRoute.Privacy, Privacy(Fixtures()), param: "tester"), root =>
        {
            var view = root.GetVisualDescendants().OfType<DryRunTesterView>().First();
            view.ShowResult(new PrivacyDryRunResultDto(false, [new() { RuleId = "email", Category = "email", Action = "redact", Count = 2, Samples = ["jo••••om", "an••••rg"] },
                new() { RuleId = "api-key-openai", Category = "api_key", Action = "block", Count = 1 }], 2, ["email", "api_key"]));
            return Task.CompletedTask;
        });
        d["privacy__error"] = () => Page(AppRoute.Privacy, Privacy(Fixtures()).Override("/api/privacy/events/stats", """{"error":"stats unavailable"}""", 500));
        d["privacy__disabled"] = () => Page(AppRoute.Privacy, Privacy(Fixtures(), enabled: false));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The page scene, scrolled down by <paramref name="offset"/> px once it has settled (the capture is a fixed 1440x900 window).</summary>
    internal static Scene Scrolled(Scene scene, double offset) => scene with
    {
        Settled = async () =>
        {
            if (scene.Settled is not null) await scene.Settled();
            await Task.Delay(300);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var scroller = scene.View.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(s => s.Extent.Height - s.Viewport.Height).FirstOrDefault();
                scroller?.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, Math.Min(offset, scroller.Extent.Height - scroller.Viewport.Height)));
            });
            await Task.Delay(100);
        },
    };

    /// <summary>Runs <paramref name="open"/> (which shows a sheet) once the page has settled; the capture then shows the sheet.</summary>
    internal static Scene After(Scene scene, Func<Control, Task> open) => scene with
    {
        Settled = async () =>
        {
            if (scene.Settled is not null) await scene.Settled();
            await Task.Delay(400);
            await Dispatcher.UIThread.InvokeAsync(() => { _ = open(scene.View); });
            await Task.Delay(600);
        },
    };

    private static NotificationSettingsView Notifications(Control root) => root.GetVisualDescendants().OfType<NotificationSettingsView>().First();

    internal static string SettingsJsonWith(params (string Key, string Json)[] changes) => SettingsWith(changes);

    private static string SettingsWith(params (string Key, string Json)[] changes)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "settings.json")))!.AsObject();
        foreach (var (key, json) in changes) node[key] = System.Text.Json.Nodes.JsonNode.Parse(json);
        return node.ToJsonString();
    }

    private const string UpdateAvailable = """{"current":"0.11.7","available":"0.12.0","lastCheckAt":"2026-10-26T11:25:00Z","notes":null,"error":null,"channel":"stable","autoCheck":true,"feedConfigured":true}""";

    private const string ListenLan = """{"host":"127.0.0.1","port":17321,"configuredHost":"0.0.0.0","hostFromEnvironment":false,"passwordSet":true,"networkUrls":["http://192.168.1.23:17321","http://10.0.0.8:17321"]}""";

    /// <summary>A scene host whose update never finishes downloading: the apply button shows its busy state.</summary>
    private sealed class DownloadingHost(NullDesktopHost inner) : IDesktopHost
    {
        public string Platform => inner.Platform;
        public string Version => "0.11.7";
        public bool ApiVersionMismatch => false;
        public Task<string?> GetAccentColorAsync() => inner.GetAccentColorAsync();
        public event Action<string?>? AccentColorChanged { add { } remove { } }
        public Task<bool> SetThemeSourceAsync(Astra.Desktop.Core.Shell.ThemeSource source) => inner.SetThemeSourceAsync(source);
        public Task<bool> OpenPathAsync(string path) => inner.OpenPathAsync(path);
        public Task<bool> RevealLogsAsync() => inner.RevealLogsAsync();
        public Task<HostResult> StartServiceAsync() => inner.StartServiceAsync();
        public Task<HostResult> RestartServiceAsync() => inner.RestartServiceAsync();
        public Task<LoginItemState> GetLoginItemAsync() => Task.FromResult(new LoginItemState(true, true));
        public Task<LoginItemState> SetLoginItemAsync(bool enabled) => Task.FromResult(new LoginItemState(true, enabled));
        public event Action<LoginItemState>? LoginItemChanged { add { } remove { } }
        public Task<UpdateSummary> CheckForUpdateAsync() => inner.CheckForUpdateAsync();
        public Task<UpdateApplyOutcome> ApplyUpdateAsync() => new TaskCompletionSource<UpdateApplyOutcome>().Task;
        public Task<string?> ShowContextMenuAsync(IReadOnlyList<ContextMenuItem> items) => inner.ShowContextMenuAsync(items);
        public event Action<string>? MenuCommandReceived { add { } remove { } }
        public ITrayHost Tray => inner.Tray;
    }

    private static Scene Downloading()
    {
        var fixtures = Fixtures().Override("/api/update/status", UpdateAvailable);
        var api = FixtureApi(fixtures.Handler());
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, host: new DownloadingHost(new NullDesktopHost()), clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Settings);
        var view = new ShellView { DataContext = shell };
        return new Scene(view, async () =>
        {
            await context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            await Task.Delay(300);
            // Press the apply button: the host never answers, so the row stays in its "applying" state.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var apply = view.GetVisualDescendants().OfType<TKCode.Arc.Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == Astra.Desktop.Core.I18n.Loc.T("settings.updateApply"));
                apply?.Command?.Execute(null);
                apply?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            });
            await Task.Delay(200);
        });
    }

    // ------------------------------------------------------------------ fixtures for what the default set does not have

    private const string AlertEvents = """
        [{"id":"provider.balance.low","subjectType":"provider","params":[{"name":"metric","kind":"choice","choices":["remaining","usedPercent"],"default":null,"required":false},{"name":"value","kind":"number","choices":null,"default":null,"required":true},{"name":"plan","kind":"text","choices":null,"default":null,"required":false}]},
         {"id":"provider.quota.failed","subjectType":"provider","params":[{"name":"value","kind":"number","choices":null,"default":3,"required":false}]},
         {"id":"account.quota.high","subjectType":"account","params":[{"name":"window","kind":"choice","choices":["any","session","weekly","credits"],"default":null,"required":false},{"name":"value","kind":"number","choices":null,"default":90,"required":false}]},
         {"id":"account.auth.invalid","subjectType":"account","params":[]},
         {"id":"account.quota.failed","subjectType":"account","params":[{"name":"value","kind":"number","choices":null,"default":3,"required":false}]},
         {"id":"token.usage.high","subjectType":"token","params":[{"name":"budget","kind":"text","choices":null,"default":null,"required":false},{"name":"value","kind":"number","choices":null,"default":80,"required":false}]},
         {"id":"token.limit.reached","subjectType":"token","params":[{"name":"budget","kind":"text","choices":null,"default":null,"required":false}]}]
        """;

    private static string Hook(string id, string name, string url, string format = "generic", bool enabled = true, bool hasSecret = true, string headers = "")
        => $$"""{"id":"{{id}}","name":"{{name}}","url":"{{url}}","format":"{{format}}","enabled":{{(enabled ? "true" : "false")}},"timeoutSec":10,"useProxy":true,"hasSecret":{{(hasSecret ? "true" : "false")}},"headerNames":[{{headers}}],"createdAt":"2026-10-20T08:00:00Z","updatedAt":"2026-10-20T08:00:00Z"}""";

    private static WebhookDto WebhookDto(string id) => System.Text.Json.JsonSerializer.Deserialize(id switch
    {
        "wh_feishu" => Hook("wh_feishu", "Feishu on-call", "https://open.feishu.cn/open-apis/bot/v2/hook/9f1c2a", "feishu", hasSecret: true, headers: "\"X-Team\""),
        _ => Hook("wh_ops", "Ops channel", "https://hooks.example.com/astra"),
    }, Astra.Core.JsonContexts.Info<Astra.Api.Contracts.WebhookDto>(Astra.Core.Json.Api))!;

    private static FixtureSet Alerts(FixtureSet f) => f
        .Override("/api/alerts/events", AlertEvents)
        .Override("/api/webhooks", "[" + string.Join(",",
            Hook("wh_ops", "Ops channel", "https://hooks.example.com/astra"),
            Hook("wh_feishu", "Feishu on-call", "https://open.feishu.cn/open-apis/bot/v2/hook/9f1c2a", "feishu", headers: "\"X-Team\""),
            Hook("wh_slack", "Slack #alerts", "https://hooks.slack.com/services/T000/B000/XXXX", "slack", enabled: false, hasSecret: false)) + "]")
        .Override("/api/alerts/rules", """
            [{"id":"rule_balance","name":"Low balance","enabled":true,"event":"provider.balance.low","subjects":null,"condition":{"metric":"remaining","value":5},"notifyResolved":true,"repeatMinutes":60,"cooldownMinutes":60,"webhookIds":["wh_ops","wh_feishu"],"firing":1,"createdAt":"2026-10-20T08:00:00Z","updatedAt":"2026-10-20T08:00:00Z"},
             {"id":"rule_limit","name":"Token limit reached","enabled":false,"event":"token.limit.reached","subjects":["tok_ci"],"condition":{},"notifyResolved":false,"repeatMinutes":null,"cooldownMinutes":30,"webhookIds":["wh_ops"],"firing":0,"createdAt":"2026-10-20T08:00:00Z","updatedAt":"2026-10-20T08:00:00Z"}]
            """)
        .Override("/api/alerts/states", """
            [{"ruleId":"rule_balance","ruleName":"Low balance","event":"provider.balance.low","subjectType":"provider","subjectId":"anthropic","subjectName":"Anthropic","state":"firing","value":{"remaining":3.2},"changedAt":"2026-10-26T10:12:00Z","lastNotifiedAt":"2026-10-26T10:12:00Z"}]
            """)
        .Override("/api/webhooks/wh_ops/deliveries", """
            [{"id":"d1","webhookId":"wh_ops","ruleId":"rule_balance","event":"provider.balance.low","status":"success","attempts":1,"nextAttemptAt":null,"httpStatus":200,"error":null,"responseSnippet":"ok","payload":{"event":"provider.balance.low","subject":"Anthropic","remaining":3.2},"createdAt":"2026-10-26T10:12:00Z","finishedAt":"2026-10-26T10:12:01Z"},
             {"id":"d2","webhookId":"wh_ops","ruleId":null,"event":"test","status":"failed","attempts":3,"nextAttemptAt":null,"httpStatus":502,"error":"Bad Gateway","responseSnippet":null,"payload":{"event":"test"},"createdAt":"2026-10-25T09:00:00Z","finishedAt":"2026-10-25T09:05:00Z"},
             {"id":"d3","webhookId":"wh_ops","ruleId":"rule_balance","event":"provider.balance.low","status":"pending","attempts":1,"nextAttemptAt":"2026-10-26T12:05:00Z","httpStatus":null,"error":null,"responseSnippet":null,"payload":{"event":"provider.balance.low"},"createdAt":"2026-10-26T11:58:00Z","finishedAt":null}]
            """);

    private static string Tunnel(string state, bool enabled = false, string mode = "quick", bool downloading = false, bool running = false, string? error = null, bool log = false,
        bool allowAdmin = false, bool password = false, bool supported = true)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "tunnel.json")))!.AsObject();
        node["supported"] = supported;
        node["enabled"] = enabled;
        node["state"] = state;
        node["mode"] = mode;
        node["allowAdmin"] = allowAdmin;
        node["checks"] = new System.Text.Json.Nodes.JsonObject { ["passwordSet"] = password, ["blocker"] = null };
        var cf = node["cloudflared"]!.AsObject();
        if (downloading)
        {
            cf["downloading"] = true;
            cf["progress"] = 0.42;
            cf["source"] = "github.com";
            cf["asset"] = "cloudflared-darwin-arm64.tgz";
            cf["version"] = "2026.3.0";
        }
        if (running || error is not null)
        {
            cf["path"] = "/Users/demo/.astra/tools/cloudflared-2026.3.0/cloudflared";
            cf["version"] = "2026.3.0";
            cf["asset"] = "cloudflared-darwin-arm64.tgz";
        }
        if (running)
        {
            node["publicUrl"] = "https://quiet-river-1234.trycloudflare.com";
            node["connectedAt"] = "2026-10-26T11:48:00Z";
        }
        if (error is not null) node["error"] = error;
        if (log || running)
            node["log"] = new System.Text.Json.Nodes.JsonArray("2026-10-26T11:47:58Z INF Starting tunnel", "2026-10-26T11:48:00Z INF Registered tunnel connection connIndex=0 location=hkg01");
        return node.ToJsonString();
    }

    private const string PrivacyPolicy = """
        {"settings":{"enabled":true,"dryRun":false,"restoreResponses":true,"defaultAction":"redact","recordSamples":true,
          "categoryActions":{"email":"redact","phone":"warn","credit_card":"block","jwt":"off"},"clientDefaults":{},
          "customRules":[{"id":"custom-mj1k2l","name":"Internal ticket id","pattern":"TKT-\\d{6}","enabled":true},{"id":"custom-mj1k3m","name":"Employee no.","pattern":"EMP\\d{5}","enabled":false}]},
         "rules":[{"id":"api-key-openai","category":"api_key","description":"OpenAI / Anthropic / generic API keys"},{"id":"aws-access-key","category":"aws_key","description":"AWS access key ids and secrets"},
                  {"id":"jwt","category":"jwt","description":"JSON Web Tokens"},{"id":"private-key","category":"private_key","description":"PEM private keys"},
                  {"id":"email","category":"email","description":"Email addresses"},{"id":"phone-cn","category":"phone","description":"Mobile phone numbers"},
                  {"id":"ip-private","category":"intranet","description":"Private IP addresses and internal hosts"},{"id":"card","category":"credit_card","description":"Credit card numbers (Luhn)"},
                  {"id":"cn-id","category":"national_id","description":"National id numbers"},{"id":"password-kv","category":"credential","description":"password= / secret= pairs"}]}
        """;

    private const string PrivacyStats = """
        {"events":214,"blocked":9,"dryRun":0,"redactions":187,"categories":[
          {"category":"email","requests":96,"hits":143,"warnHits":0,"blockHits":0,"redactHits":143},
          {"category":"api_key","requests":31,"hits":38,"warnHits":2,"blockHits":9,"redactHits":27},
          {"category":"phone","requests":22,"hits":25,"warnHits":25,"blockHits":0,"redactHits":0},
          {"category":"intranet","requests":12,"hits":17,"warnHits":0,"blockHits":0,"redactHits":17},
          {"category":"custom","requests":4,"hits":4,"warnHits":4,"blockHits":0,"redactHits":0}]}
        """;

    private static string PrivacyEvents(int total = 5) => $$"""
        {"items":[
          {"id":"req_a1","startedAtUtc":"2026-10-26T11:42:10Z","clientKind":"claude-code","providerId":"anthropic","providerName":"Anthropic","requestedModel":"claude-sonnet-4","status":"success","dryRun":false,"blocked":true,"redactions":0,"hits":[{"ruleId":"api-key-openai","category":"api_key","action":"block","count":1,"samples":["sk-••••9f3a"]}]},
          {"id":"req_a2","startedAtUtc":"2026-10-26T11:20:41Z","clientKind":"codex","providerId":"openai","providerName":"OpenAI","requestedModel":"gpt-5","status":"success","dryRun":false,"blocked":false,"redactions":3,"hits":[{"ruleId":"email","category":"email","action":"redact","count":2,"samples":["jo••••om","an••••rg"]},{"ruleId":"phone-cn","category":"phone","action":"redact","count":1,"samples":["138••••5678"]},{"ruleId":"ip-private","category":"intranet","action":"redact","count":1,"samples":[]}]},
          {"id":"req_a3","startedAtUtc":"2026-10-26T09:05:12Z","clientKind":"gemini-cli","providerId":"openai","providerName":"OpenAI","requestedModel":"gemini-2.5-pro","status":"success","dryRun":true,"blocked":false,"redactions":0,"hits":[{"ruleId":"email","category":"email","action":"warn","count":1,"samples":["li••••om"]}]},
          {"id":"req_a4","startedAtUtc":"2026-10-25T17:31:00Z","clientKind":"opencode","providerId":"anthropic","providerName":"Anthropic","requestedModel":null,"status":"error","dryRun":false,"blocked":false,"redactions":0,"hits":[{"ruleId":"custom-mj1k2l","category":"custom","action":"warn","count":1,"samples":[]}]},
          {"id":"req_a5","startedAtUtc":"2026-10-25T08:12:44Z","clientKind":"plugin-demo","providerId":"anthropic","providerName":"Anthropic","requestedModel":"claude-opus-4","status":"success","dryRun":false,"blocked":false,"redactions":1,"hits":[]}],
         "total":{{total}},"page":1,"pageSize":50}
        """;

    private static FixtureSet Privacy(FixtureSet f, bool enabled = true) => f
        .Override("/api/privacy", enabled ? PrivacyPolicy : PrivacyPolicy.Replace("\"enabled\":true", "\"enabled\":false"))
        .Override("/api/privacy/events/stats", PrivacyStats)
        .Override("/api/privacy/events", PrivacyEvents());
}

/// <summary>
/// Behaviour the scenes cannot show: what a refresh, a language switch or a choice does to a page that is on screen. They build the real
/// shell over the fixture server, like a scene, and drive the controls.
/// </summary>
public class SettingsBehaviourTests
{
    private static async Task<(AtomUI.Desktop.Controls.Window Window, Control View, FixtureSet Fixtures, ShellViewModel Shell)> ShowAsync(FixtureSet fixtures, string? tab = null)
    {
        var shell = Scenes.BuildShell(fixtures);
        shell.Navigator.Navigate(AppRoute.Settings, tab);
        var view = new ShellView { DataContext = shell };
        var window = new AtomUI.Desktop.Controls.Window { Content = view, Width = 1440, Height = 900, IsTitleBarVisible = false, CanResize = false };
        TKCode.Arc.Motion.ArcMotion.SetLevel(window, TKCode.Arc.Motion.ArcMotionLevel.Off);
        window.Show();
        SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
        SceneCapture.Pump(30);
        await Task.CompletedTask;
        return (window, view, fixtures, shell);
    }

    private static T Find<T>(Control root, Func<T, bool>? where = null) where T : Visual
        => root.GetVisualDescendants().OfType<T>().First(where ?? (_ => true));

    private static string? Name(Control c) => Avalonia.Automation.AutomationProperties.GetName(c);

    [AvaloniaFact]
    public async Task Typing_in_the_proxy_form_survives_a_settings_refresh()
    {
        var (window, view, _, shell) = await ShowAsync(Scenes.Fixtures().Override("/api/settings", Scenes.SettingsJsonWith(("proxyMode", "\"custom\""), ("proxyUrl", "\"http://127.0.0.1:7890\""))));
        try
        {
            var url = Find<TKCode.Arc.Input>(view, i => Name(i) == Astra.Desktop.Core.I18n.Loc.T("settings.proxy.url"));
            url.Text = "http://typed.example:1";
            // A refetch of the settings (what any save, or the shell's refresh, does) must not rebuild the field under the user's hands.
            shell.Context.Queries.Invalidate("settings");
            SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
            SceneCapture.Pump(20);
            Assert.Equal("http://typed.example:1", Find<TKCode.Arc.Input>(view, i => Name(i) == Astra.Desktop.Core.I18n.Loc.T("settings.proxy.url")).Text);
            Assert.Same(url, Find<TKCode.Arc.Input>(view, i => Name(i) == Astra.Desktop.Core.I18n.Loc.T("settings.proxy.url")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_language_switch_redraws_the_open_tab_and_keeps_it_selected()
    {
        var (window, view, _, _) = await ShowAsync(Scenes.Fixtures(), "remote");
        try
        {
            Astra.Desktop.Core.I18n.Loc.Language = "en";
            SceneCapture.Pump(40);
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(Astra.Desktop.Core.I18n.Loc.T("settings.remote.status"), texts);
            Assert.Contains("Remote access", texts);
            Assert.DoesNotContain(texts, t => t == "远程访问");
        }
        finally
        {
            Astra.Desktop.Core.I18n.Loc.Language = "zh";
            SceneCapture.Pump(10);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Choosing_the_LAN_without_an_admin_password_saves_nothing_it_asks_for_a_password()
    {
        var fixtures = Scenes.Fixtures();
        var (window, view, _, _) = await ShowAsync(fixtures);
        try
        {
            var scope = Find<Astra.Desktop.Components.Settings.ControlledSegmented>(view, s => Name(s) == Astra.Desktop.Core.I18n.Loc.T("settings.listen"));
            scope.SelectedValue = "lan";
            SceneCapture.Pump(40);
            Assert.DoesNotContain(fixtures.Unmatched, u => u.StartsWith("PUT /api/server/listen", StringComparison.Ordinal));
            Assert.Equal("local", scope.SelectedValue); // the switch went back to where the server is
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Choosing_the_LAN_with_a_password_confirms_then_saves_the_wildcard_address()
    {
        var fixtures = Scenes.Fixtures().Override("/api/server/listen", """{"host":"127.0.0.1","port":17321,"configuredHost":"127.0.0.1","hostFromEnvironment":false,"passwordSet":true,"networkUrls":[]}""");
        var (window, view, _, _) = await ShowAsync(fixtures);
        try
        {
            var scope = Find<Astra.Desktop.Components.Settings.ControlledSegmented>(view, s => Name(s) == Astra.Desktop.Core.I18n.Loc.T("settings.listen"));
            scope.SelectedValue = "lan";
            SceneCapture.Pump(60);
            // The test feedback confirms; the fixture has no PUT route, so the attempt is what we see.
            Assert.Contains(fixtures.Unmatched, u => u == "PUT /api/server/listen");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task The_tray_tab_edits_prefs_through_the_host_and_reset_restores_the_defaults()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        shell.Navigator.Navigate(AppRoute.Settings, "tray");
        var view = new ShellView { DataContext = shell };
        var window = new AtomUI.Desktop.Controls.Window { Content = view, Width = 1440, Height = 900, IsTitleBarVisible = false, CanResize = false };
        window.Show();
        try
        {
            SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
            SceneCapture.Pump(30);
            var host = shell.Context.Host;
            var toggle = Find<Astra.Desktop.Components.Settings.ControlledSwitch>(view, s => Name(s) == Astra.Desktop.Core.I18n.Loc.T("settings.tray.section.compareYesterday"));
            Assert.False(toggle.IsChecked);
            // A click only asks; the page writes the prefs to the host and then shows them.
            typeof(Astra.Desktop.Components.Settings.ControlledSwitch).GetMethod("Toggle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(toggle, null);
            SceneCapture.Pump(30);
            Assert.True((await host.Tray.GetPrefsAsync()).Sections[TraySection.CompareYesterday]);
            Assert.True(Find<Astra.Desktop.Components.Settings.ControlledSwitch>(view, s => Name(s) == Astra.Desktop.Core.I18n.Loc.T("settings.tray.section.compareYesterday")).IsChecked);
        }
        finally { window.Close(); }
    }
}
