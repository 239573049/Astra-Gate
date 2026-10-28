using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The tray panel (WP10; plan inventory §5.4 "TrayPanel"): all sections on, overview only, stopped, starting, update available, API version
/// mismatch, quota missing, plus the quota and clients tabs. The panel is the popup's view (close button shown) over the fixture API with
/// the clock frozen, drawn on the muted surface it sits on in the Settings preview. The web has no tray capture, so these are checked
/// against TrayPanelView.tsx by eye.
/// </summary>
public static partial class Scenes
{
    static partial void AddTray(Dictionary<string, Func<Scene>> d)
    {
        // The Settings › Tray tab (settings__tray, WP9) shows the real panel through this seam.
        TrayPreviewControl.Install();

        var running = new TrayPanelState(true, 17321, null, false, null);
        var all = new TrayPrefs(TrayTitleMode.Requests, TrayPrefsJson.Sections.ToDictionary(s => s, _ => true));
        var overviewOnly = new TrayPrefs(TrayTitleMode.Requests, TrayPrefsJson.Sections.ToDictionary(s => s, s => s == TraySection.Overview));

        d["tray__all"] = () => TrayScene(TraySubscription(Fixtures()), all, running);
        d["tray__default"] = () => TrayScene(TraySubscription(Fixtures()), TrayPrefs.Default, running);
        d["tray__overview-only"] = () => TrayScene(Fixtures(), overviewOnly, running);
        d["tray__stopped"] = () => TrayScene(Fixtures(), TrayPrefs.Default, running with { Running = false, Port = null });
        d["tray__starting"] = () => TrayScene(Fixtures(), TrayPrefs.Default, new TrayPanelState(false, null, TrayActivity.Starting, false, null));
        d["tray__update-available"] = () => TrayScene(Fixtures(), TrayPrefs.Default, running with { UpdateAvailable = "yes" });
        d["tray__mismatch"] = () => TrayScene(Fixtures(), TrayPrefs.Default, running with { ApiVersionMismatch = true });
        d["tray__quota-missing"] = () => TrayScene(TrayPendingQuota(Fixtures()), TrayPrefs.Default, running);
        d["tray__quota-tab"] = () => TrayScene(TraySubscription(Fixtures()), all, running, TrayTab.Quota);
        d["tray__clients-tab"] = () => TrayScene(TraySubscription(Fixtures()), all, running, TrayTab.Clients);
    }

    /// <summary>A clock that stands still at the fixtures' now (so "updated just now" never depends on when the test runs); timers stay real.</summary>
    private sealed class TrayFrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Scene TrayScene(FixtureSet fixtures, TrayPrefs prefs, TrayPanelState state, TrayTab tab = TrayTab.Overview)
    {
        var api = FixtureApi(fixtures.Handler());
        var queries = new QueryClient(AvaloniaUiDispatcher.Instance, new TrayFrozenTime(fixtures.Now));
        var clock = new FixedClock(fixtures.Now);
        var data = new TrayDataService(api, queries, clock, attempts: new QuotaAttempts());
        var view = new TrayPanelView { Width = TrayPanelLayout.Width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Tab = tab };
        _ = TrayPanelPresenter.ForPreview(view, data, clock, prefs, state);
        var surface = new Border { Padding = new Thickness(0, 40), Child = view };
        surface.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        return new Scene(surface, async () =>
        {
            await queries.WhenIdleAsync();
            // A second pass: provider data brings account queries.
            await Task.Delay(50);
            await queries.WhenIdleAsync();
        });
    }

    private const string TraySubProvider = """{"id":"prov_claude_max","templateId":"claude-subscription","name":"Claude Max","icon":"claude","category":"subscription","endpoints":[],"preferredUpstreamProtocols":[],"authScheme":"oauth-subscription","hasApiKey":false,"extraHeaders":{},"priceMultiplier":1,"settings":{},"enabled":true,"sortOrder":2,"modelCount":3,"boundClients":[],"createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00Z","templateUpdateAvailable":false,"quotaConfig":{"enabled":false,"effectiveIntervalMinutes":30,"timeoutSec":15,"params":{},"secrets":[]}}""";

    /// <summary>The default providers plus a Claude subscription whose account has a 5-hour and a weekly window.</summary>
    private static FixtureSet TraySubscription(FixtureSet f) => TrayWithProvider(f)
        .Override("/api/providers/prov_claude_max/accounts", """[{"id":"acc_max","providerId":"prov_claude_max","displayName":"dev@example.com","status":"active","createdAt":"2026-10-01T00:00:00Z","enabled":true,"isCurrent":true,"sortOrder":0,"kind":"oauth","quota":{"fetchedAtUtc":"2026-10-26T11:58:00Z","session":{"usedPercent":78,"windowMinutes":300,"resetsAtUtc":"2026-10-26T14:00:00Z"},"weekly":{"usedPercent":36,"windowMinutes":10080,"resetsAtUtc":"2026-10-30T00:00:00Z"}}}]""");

    /// <summary>Only a subscription whose account has no snapshot yet: the "fetching quota" line.</summary>
    private static FixtureSet TrayPendingQuota(FixtureSet f) => f
        .Override("/api/providers", "[" + TraySubProvider + "]")
        .Override("/api/providers/prov_claude_max/accounts", """[{"id":"acc_max","providerId":"prov_claude_max","displayName":"dev@example.com","status":"active","createdAt":"2026-10-01T00:00:00Z","enabled":true,"isCurrent":true,"sortOrder":0,"kind":"oauth","quota":null}]""")
        .Override("/api/provider-accounts/acc_max/quota", """{"error":"pending"}""", 502);

    private static FixtureSet TrayWithProvider(FixtureSet f)
    {
        var providers = File.ReadAllText(Path.Combine(TrayFixtureDir(), "providers.json")).TrimEnd();
        return f.Override("/api/providers", providers[..providers.LastIndexOf(']')].TrimEnd() + "," + TraySubProvider + "]");
    }

    private static string TrayFixtureDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "ui", "default");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("tests/fixtures/ui/default");
    }
}
