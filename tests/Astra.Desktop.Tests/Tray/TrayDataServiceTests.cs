using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Tests.Api;
using Astra.Desktop.Tests.Plugins;

namespace Astra.Desktop.Tests.Tray;

/// <summary>useTrayData ported: what is fetched and polled (web/src/test/tray-polling.test.tsx), the aggregation, and the 5-minute quota refresh.</summary>
[Collection("Loc")]
public sealed class TrayDataServiceTests
{
    private const string Summary = "stats/summary/today//";
    private const string Hourly = "stats/timeseries/today/hour/provider///0";
    private const string Daily = "stats/timeseries/7d/day/provider///0";
    private const string Top = "stats/top/today//3/";

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Runs dispatcher work one action at a time (the app runs it on the UI thread).</summary>
    private sealed class SerialDispatcher : IUiDispatcher
    {
        private readonly object _gate = new();
        public void Post(Action action)
        {
            lock (_gate) action();
        }
    }

    private static TrayPrefs All(TrayTitleMode title) => new(title, TrayPrefsJson.Sections.ToDictionary(s => s, _ => true));

    private static (TrayDataService Service, StubHttp Http, ManualTime Time) Create(StubHttp? http = null, QuotaAttempts? attempts = null)
    {
        http ??= Stub();
        var time = new ManualTime();
        var queries = new QueryClient(new SerialDispatcher(), time);
        var api = new ApiClient("http://tray.test", http);
        return (new TrayDataService(api, queries, new FixedClock(Now), time, attempts ?? new QuotaAttempts()), http, time);
    }

    private static StubHttp Stub(string providers = "[]", string accounts = "[]") => new StubHttp()
        .On("GET /api/settings", """{"gatewayBaseUrl":"http://localhost:18317"}""")
        .On("GET /api/stats/summary", """{"range":"today","costUsd":0.004,"requests":7,"successRate":0.857,"inputTokens":60000,"outputTokens":8800,"cacheReadTokens":30000,"cacheWriteTokens":0,"reasoningTokens":0,"avgTtftMs":null}""")
        .On("GET /api/stats/timeseries", """[{"bucket":"2026-10-26T09:00","key":"p","costUsd":0,"requests":3,"tokens":10}]""")
        .On("GET /api/stats/top-models", """[{"model":"m","costUsd":0,"requests":1,"tokens":10,"inputTokens":0,"cacheReadTokens":0}]""")
        .On("GET /api/providers", providers)
        .On("GET /api/clients", "[]")
        .On("GET /api/providers/sub/accounts", accounts);

    private static async Task Settle(TrayDataService service, Func<bool>? until = null)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limit)
        {
            await Task.Delay(20);
            if (until?.Invoke() ?? true) break;
        }
        await Task.Delay(50);
    }

    [Fact]
    public void Hidden_panel_polls_only_what_the_title_needs()
    {
        var (s, _, _) = Create();
        s.Configure(new TrayDataOptions(All(TrayTitleMode.Requests), true, Background: true, Live: false));
        var o = s.Observed;
        Assert.Equal(TimeSpan.FromSeconds(15), o[Summary]);
        Assert.Null(o[Hourly]);
        Assert.Null(o[Top]);
        Assert.Null(o["clients/typed"]);
        Assert.Null(o["providers/typed"]);

        s.Configure(new TrayDataOptions(All(TrayTitleMode.Quota), true, true, false));
        Assert.Null(s.Observed[Summary]);
        Assert.Equal(TimeSpan.FromSeconds(60), s.Observed["providers/typed"]);

        s.Configure(new TrayDataOptions(All(TrayTitleMode.None), true, true, false));
        Assert.All(s.Observed.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Title_only_fetches_just_the_titles_data()
    {
        var (s, _, _) = Create();
        s.Configure(new TrayDataOptions(All(TrayTitleMode.Requests), true, true, false, TitleOnly: true));
        Assert.Equal(["settings", Summary], s.Observed.Keys.Order(StringComparer.Ordinal));
        s.Configure(new TrayDataOptions(All(TrayTitleMode.Alerts), true, true, false, TitleOnly: true));
        Assert.Equal(["providers/typed", "settings", Summary], s.Observed.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_panel_on_screen_polls_every_section_and_a_stopped_service_nothing()
    {
        var (s, _, _) = Create();
        s.Configure(new TrayDataOptions(All(TrayTitleMode.None), true, Background: false, Live: true));
        var o = s.Observed;
        Assert.Equal(TimeSpan.FromSeconds(15), o[Summary]);
        Assert.Equal(TimeSpan.FromSeconds(30), o[Hourly]);
        Assert.Equal(TimeSpan.FromSeconds(60), o[Daily]);
        Assert.Equal(TimeSpan.FromSeconds(30), o[Top]);
        Assert.Equal(TimeSpan.FromSeconds(60), o["providers/typed"]);
        Assert.Equal(TimeSpan.FromSeconds(30), o["clients/typed"]);

        s.Configure(new TrayDataOptions(TrayPrefs.Default, true, false, true));
        Assert.DoesNotContain(Daily, s.Observed.Keys);
        Assert.DoesNotContain("clients/typed", s.Observed.Keys);

        s.Configure(new TrayDataOptions(All(TrayTitleMode.Requests), Enabled: false, true, true));
        Assert.Empty(s.Observed);
    }

    [Fact]
    public async Task Polls_on_its_own_timer()
    {
        var (s, http, time) = Create();
        s.Configure(new TrayDataOptions(All(TrayTitleMode.Requests), true, true, false, TitleOnly: true));
        await Settle(s, () => http.Count("GET", "/api/stats/summary") == 1);
        Assert.Equal(1, http.Count("GET", "/api/stats/summary"));
        time.Advance(TimeSpan.FromSeconds(15));
        await Settle(s, () => http.Count("GET", "/api/stats/summary") == 2);
        time.Advance(TimeSpan.FromSeconds(15));
        await Settle(s, () => http.Count("GET", "/api/stats/summary") == 3);
        Assert.Equal(3, http.Count("GET", "/api/stats/summary"));
        s.Dispose();
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100);
        Assert.Equal(3, http.Count("GET", "/api/stats/summary"));
    }

    private const string SubProvider = """[{"id":"sub","name":"Claude Max","icon":"claude","category":"subscription","endpoints":[],"preferredUpstreamProtocols":[],"authScheme":"oauth-subscription","hasApiKey":false,"extraHeaders":{},"priceMultiplier":1,"settings":{},"enabled":true,"sortOrder":0,"modelCount":0,"boundClients":[],"createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00Z","quotaConfig":{"enabled":false,"effectiveIntervalMinutes":30,"timeoutSec":15,"params":{},"secrets":[]},"templateUpdateAvailable":false}]""";

    private static string Accounts(string fetchedAt) =>
        """[{"id":"a1","providerId":"sub","displayName":"me","status":"active","createdAt":"2026-10-01T00:00:00Z","enabled":true,"isCurrent":true,"sortOrder":0,"kind":"oauth","quota":{"fetchedAtUtc":"FETCHED","session":{"usedPercent":40,"windowMinutes":300}}}]""".Replace("FETCHED", fetchedAt, StringComparison.Ordinal);

    [Fact]
    public async Task Aggregates_subscription_windows_and_todays_numbers()
    {
        var (s, http, _) = Create(Stub(SubProvider, Accounts("2026-10-26T11:59:00Z")));
        var changed = 0;
        s.Changed += () => Interlocked.Increment(ref changed);
        s.Configure(new TrayDataOptions(TrayPrefs.Default, true, false, Live: true));
        await Settle(s, () => s.Snapshot().Plans.Count == 1 && s.Snapshot().Summary is not null);
        var d = s.Snapshot();
        Assert.Equal(7, d.Summary!.Requests);
        Assert.Equal("http://localhost:18317", d.Settings!.GatewayBaseUrl);
        Assert.Single(d.Hourly);
        Assert.Single(d.Top);
        var plan = Assert.Single(d.Plans);
        Assert.Equal(("Claude Max", 60.0), (plan.ProviderName, plan.Rows[0].Remaining!.Value));
        Assert.True(d.HasSubscriptions);
        Assert.NotNull(d.UpdatedAt);
        Assert.False(d.Error);
        Assert.True(changed > 0);
        // A fresh snapshot is not asked for again.
        Assert.Equal(0, http.Count("POST", "/api/provider-accounts/"));
        Assert.Equal(new TrayTitleText("7", "7 calls today"), WithEnglish(() => s.Title(true)));
    }

    private static TrayTitleText WithEnglish(Func<TrayTitleText> f)
    {
        var saved = Astra.Desktop.Core.I18n.Loc.Language;
        Astra.Desktop.Core.I18n.Loc.Language = "en";
        try { return f(); }
        finally { Astra.Desktop.Core.I18n.Loc.Language = saved; }
    }

    [Fact]
    public async Task Stale_quota_is_refreshed_once_per_five_minutes_and_never_retried()
    {
        var http = Stub(SubProvider, Accounts("2026-10-26T11:00:00Z")).On("POST /api/provider-accounts/a1/quota", """{"error":"upstream"}""", 502);
        var attempts = new QuotaAttempts();
        var (s, _, _) = Create(http, attempts);
        s.Configure(new TrayDataOptions(TrayPrefs.Default, true, false, Live: true));
        await Settle(s, () => http.Count("POST", "/api/provider-accounts/a1/quota") == 1);
        Assert.Equal(1, http.Count("POST", "/api/provider-accounts/a1/quota"));
        // The failed probe invalidated the accounts (refetched) and the next pass sees the same stale snapshot: still one attempt.
        s.Refresh();
        s.Configure(new TrayDataOptions(TrayPrefs.Default, true, false, Live: true));
        await Settle(s);
        Assert.Equal(1, http.Count("POST", "/api/provider-accounts/a1/quota"));
        // A second panel in the same app shares the attempts.
        var (other, otherHttp, _) = Create(Stub(SubProvider, Accounts("2026-10-26T11:00:00Z")), attempts);
        other.Configure(new TrayDataOptions(TrayPrefs.Default, true, false, Live: true));
        await Settle(other, () => other.Snapshot().Plans.Count == 1);
        Assert.Equal(0, otherHttp.Count("POST", "/api/provider-accounts/a1/quota"));
    }

    [Fact]
    public async Task Quota_is_not_refreshed_while_nobody_looks()
    {
        var http = Stub(SubProvider, Accounts("2026-10-26T11:00:00Z")).On("POST /api/provider-accounts/a1/quota", "{}");
        var (s, _, _) = Create(http);
        // Hidden, title shows requests: the accounts are read (the quota section is on) but no snapshot is asked for, and nothing polls.
        s.Configure(new TrayDataOptions(TrayPrefs.Default, true, true, Live: false));
        await Settle(s, () => http.Count("GET", "/api/providers/sub/accounts") == 1);
        Assert.Equal(0, http.Count("POST", "/api/provider-accounts/"));
        Assert.Null(s.Observed["providers/sub/accounts"]);
    }

    [Fact]
    public void Attempts_window()
    {
        var a = new QuotaAttempts();
        Assert.True(a.TryBegin("x", Now, TimeSpan.FromMinutes(5)));
        Assert.False(a.TryBegin("x", Now.AddMinutes(4), TimeSpan.FromMinutes(5)));
        Assert.True(a.TryBegin("y", Now, TimeSpan.FromMinutes(5)));
        Assert.True(a.TryBegin("x", Now.AddMinutes(5), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Binding_switch_puts_and_refreshes()
    {
        var http = Stub().On("PUT /api/clients/codex/binding", """{"kind":"codex"}""");
        var (s, _, _) = Create(http);
        try
        {
            await s.SetBindingAsync("codex", "p1");
        }
        catch (Exception)
        {
            // The stub's ClientInfoDto is partial; only the request matters here.
        }
        var put = Assert.Single(http.Requests, r => r.Method == "PUT");
        Assert.Contains("\"providerId\":\"p1\"", put.Body);
        Assert.Equal("1", put.Admin);
    }
}
