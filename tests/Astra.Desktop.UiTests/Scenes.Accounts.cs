using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Components.Accounts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Scenes of the provider account components (WP3: Components/Accounts). The provider detail page that embeds them is another package's,
/// so each scene hosts the view directly in a page-like column (the shell's content width and padding) over the fixture set plus the
/// account answers of <see cref="AccountsData"/>, served by <see cref="AccountsRoutesHandler"/> in front of the fixture handler.
/// </summary>
public static partial class Scenes
{
    static partial void AddAccounts(Dictionary<string, Func<Scene>> d)
    {
        // Subscription accounts (Claude subscription: client policy group; codex: reset cards in the detail).
        d["accounts__list"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude));
        d["accounts__empty"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude, accounts: "[]"));
        d["accounts__loading"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), hang: "/api/providers/prov_claude/accounts", settle: false);
        d["accounts__error"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude, accountsStatus: 500, usageStatus: 500));
        d["accounts__no-match"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view => view.Model.Search = "nobody");
        d["accounts__filter-cooling"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view => view.Model.Filter = AccountFilter.Cooling);
        d["accounts__selection"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view =>
        {
            view.Model.ToggleSelect("acc_max");
            view.Model.ToggleSelect("acc_pro");
        });
        d["accounts__policy"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude, accounts: AccountsData.FewAccounts(), policy: AccountsData.PolicyAny));
        d["accounts__detail"] = () => AccountsScene(AccountsData.Codex, AccountsData.Routes(AccountsData.Codex), view => view.Model.DetailId = "acc_max");
        d["accounts__detail-dead"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view => view.Model.DetailId = "acc_dead");
        d["accounts__detail-key"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view => view.Model.DetailId = "acc_key");
        d["accounts__codex-local"] = () => AccountsScene(AccountsData.Codex, AccountsData.Routes(AccountsData.Codex));
        foreach (var mode in new[] { "device", "pkce", "paste", "cli" })
            d["accounts__login-" + mode] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude, login: AccountsData.Login(mode)),
                async view => await view.Model.StartLoginAsync());
        d["accounts__login-error"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude, login: AccountsData.Login("device"), poll: ("""{"error":"授权已过期，请重新登录"}""", 400)),
            async view =>
            {
                await view.Model.StartLoginAsync();
                await view.Model.CheckNowAsync();
            });
        d["accounts__proxy-login"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view =>
        {
            view.Model.LoginProxy = "socks5://127.0.0.1:1080";
            view.Model.ProxyLoginOpen = true;
        });
        d["accounts__import"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), async view =>
        {
            view.Model.ImportMode = ImportMode.Batch;
            await AccountsUntil(() => view.ImportSheet.IsOpen);
            view.ImportSheet.Text = "sk-ant-ort01-AAAA…\nsk-ant-oat01-BBBB…\n# a comment\n{\"claudeAiOauth\":{}}";
        });
        d["accounts__import-result"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), async view =>
        {
            view.Model.ImportMode = ImportMode.Batch;
            await AccountsUntil(() => view.ImportSheet.IsOpen);
            view.ImportSheet.Text = "sk-ant-ort01-AAAA\nsk-ant-ort01-BBBB\nsk-ant-ort01-CCCC\nbad-token";
            await view.ImportSheet.SubmitAsync(true);
        });
        d["accounts__import-apikey"] = () => AccountsScene(AccountsData.Claude, AccountsData.Routes(AccountsData.Claude), view => view.Model.ImportMode = ImportMode.ApiKey);

        // Plugin accounts.
        d["plugin-accounts__signed-in"] = () => PluginAccountsScene(AccountsData.PluginRoutes());
        d["plugin-accounts__signed-out"] = () => PluginAccountsScene(AccountsData.PluginRoutes(accounts: "[]"));
        d["plugin-accounts__usage"] = () => PluginAccountsScene(AccountsData.PluginRoutes(), view => view.Model.ToggleUsage("pacc_1"));
        d["plugin-accounts__prompt"] = () => PluginAccountsScene(AccountsData.PluginRoutes(), view => view.Model.Method = AccountsData.BrowserMethod);
        d["plugin-accounts__key"] = () => PluginAccountsScene(AccountsData.PluginRoutes(prompt: """{"prompt":null}"""), view => view.Model.Method = AccountsData.KeyMethod);
        d["plugin-accounts__waiting"] = () => PluginAccountsScene(AccountsData.PluginRoutes(prompt: """{"prompt":null}"""), async view =>
        {
            view.Model.Method = AccountsData.BrowserMethod;
            await AccountsUntil(() => view.SignInSheet.Current?.PromptLoaded == true);
            view.SignInSheet.Current!.Follow("flow1");
        });

        // The usage table on its own.
        d["account-usage__normal"] = () => AccountsHost(new AccountUsageStatsView(AccountsData.UsageRow(), compact: false) { Width = 420 });
        d["account-usage__compact"] = () => AccountsHost(new AccountUsageStatsView(AccountsData.UsageRow(), compact: true) { Width = 200 });
    }

    // Waits (pumping) for a condition the view reaches on a later turn of the UI thread; gives up after 3 s.
    private static async Task AccountsUntil(Func<bool> ready)
    {
        for (var i = 0; i < 300 && !ready(); i++) await Task.Delay(10);
    }

    // A page-like column: the shell's content area (sidebar width off the left, the page padding) on the shell's canvas colour (web
    // steps/accounts-models.mjs iso(): the tab panel cut out at left 232 / top 52 over a flat background, which renders as the wallpaper's
    // --canvas). The scroller is the shell's own ScrollArea (Views/ShellView.axaml PageScroll), not a ScrollViewer: AtomUI's ScrollViewer
    // hosts a scoped overlay layer, so a sheet opened inside it dimmed and centred on the column instead of the whole window.
    private static Control AccountsHost(Control content)
    {
        var scroller = new ScrollArea { FadeLength = 0, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new Border { Padding = new Thickness(20, 16, 40, 40), Child = content } };
        var page = new Border { Margin = new Thickness(232, 52, 0, 0), Child = scroller };
        var dark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        return new Border { Child = page, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(WallpaperPalette.Canvas(dark))) };
    }

    private static Scene AccountsScene(string providerJson, Dictionary<string, (string Body, int Status)> routes, Func<SubscriptionAccountsView, Task>? after = null,
        string? hang = null, bool settle = true)
    {
        var (ctx, fixtures) = AccountsContext(routes, hang);
        var provider = Json.DeserializeApi<ProviderDto>(JsonNode.Parse(providerJson))!;
        var view = new SubscriptionAccountsView(ctx, provider);
        view.Model.OpenUrl = _ => Task.CompletedTask;
        return new Scene(AccountsHost(view), async () =>
        {
            if (!settle)
            {
                await Task.Delay(200);
                return;
            }
            await ctx.Queries.WhenIdleAsync();
            if (after is not null) await after(view);
            await ctx.Queries.WhenIdleAsync();
            await Task.Delay(100);
            _ = fixtures;
        });
    }

    private static Scene AccountsScene(string providerJson, Dictionary<string, (string Body, int Status)> routes, Action<SubscriptionAccountsView> after)
        => AccountsScene(providerJson, routes, view =>
        {
            after(view);
            return Task.CompletedTask;
        });

    private static Scene PluginAccountsScene(Dictionary<string, (string Body, int Status)> routes, Func<PluginAccountsView, Task>? after = null)
    {
        var (ctx, _) = AccountsContext(routes, null);
        var provider = Json.DeserializeApi<ProviderDto>(JsonNode.Parse(AccountsData.Plugin))!;
        var view = new PluginAccountsView(ctx, provider);
        view.Model.OpenUrl = _ => Task.CompletedTask;
        return new Scene(AccountsHost(view), async () =>
        {
            await ctx.Queries.WhenIdleAsync();
            if (after is not null) await after(view);
            await ctx.Queries.WhenIdleAsync();
            await Task.Delay(150);
            await ctx.Queries.WhenIdleAsync();
        });
    }

    private static Scene PluginAccountsScene(Dictionary<string, (string Body, int Status)> routes, Action<PluginAccountsView> after)
        => PluginAccountsScene(routes, view =>
        {
            after(view);
            return Task.CompletedTask;
        });

    private static (PageContext Context, FixtureSet Fixtures) AccountsContext(Dictionary<string, (string Body, int Status)> routes, string? hang)
    {
        // Dates in UTC like the web capture (capture-web.mjs timezoneId: index.timeZone ?? 'UTC').
        AccountRules.DisplayZone = TimeZoneInfo.Utc;
        var fixtures = Fixtures();
        HttpMessageHandler handler = new AccountsRoutesHandler(fixtures.Handler(), routes);
        if (hang is not null) handler = new PathHangingHandler(handler, hang);
        var api = FixtureApi(handler);
        return (PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now)), fixtures);
    }

    /// <summary>Answers "METHOD /path" from a table (query ignored), everything else from the fixture handler.</summary>
    private sealed class AccountsRoutesHandler(HttpMessageHandler inner, Dictionary<string, (string Body, int Status)> routes) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (routes.TryGetValue($"{request.Method.Method} {request.RequestUri!.AbsolutePath}", out var hit))
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)hit.Status) { Content = new StringContent(hit.Body, Encoding.UTF8, "application/json") });
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>The account answers of the WP3 scenes, shaped like the admin API (camelCase).</summary>
internal static class AccountsData
{
    private const string Now = "2026-10-26T12:00:00Z";

    private static string ProviderJson(string id, string name, string templateId, string authScheme, JsonObject? settings = null)
    {
        var p = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "providers", "prov_anthropic.json")))!.AsObject();
        p["id"] = id;
        p["name"] = name;
        p["templateId"] = templateId;
        p["authScheme"] = authScheme;
        p["httpProxy"] = null;
        p["settings"] = settings ?? new JsonObject();
        return p.ToJsonString();
    }

    public static readonly string Claude = ProviderJson("prov_claude", "Claude Max", "anthropic-subscription", "oauth-subscription");
    public static readonly string Codex = ProviderJson("prov_claude", "ChatGPT", "openai-subscription", "oauth-subscription");
    public static readonly string Plugin = ProviderJson("plugin-kimi", "Kimi (plugin)", "plugin-kimi", "plugin", new JsonObject { ["plugin"] = new JsonObject { ["usage"] = true } });

    private static JsonObject Window(double used, int minutes, string resets) => new() { ["usedPercent"] = used, ["windowMinutes"] = minutes, ["resetsAtUtc"] = resets };

    private static JsonObject Account(string id, string name, string? plan, string status = "active", bool enabled = true, bool current = false, int order = 0,
        JsonObject? quota = null, string? cooldown = null, string? error = null, string kind = "oauth", string? source = "login", string? proxy = null, string? keyMasked = null,
        string? email = null) => new()
    {
        ["id"] = id, ["providerId"] = "prov_claude", ["displayName"] = name, ["accountEmail"] = email, ["plan"] = plan, ["status"] = status,
        ["expiresAtUtc"] = kind == "oauth" ? "2026-11-02T09:30:00Z" : null, ["lastRefreshAtUtc"] = "2026-10-26T08:10:00Z", ["quota"] = quota,
        ["createdAt"] = "2026-09-20T10:00:00Z", ["enabled"] = enabled, ["isCurrent"] = current, ["sortOrder"] = order, ["cooldownUntilUtc"] = cooldown,
        ["lastError"] = error, ["kind"] = kind, ["source"] = source, ["httpProxy"] = proxy, ["keyMasked"] = keyMasked,
    };

    private static JsonObject Quota(double session, double weekly, string? planLabel = null) => new()
    {
        ["fetchedAtUtc"] = "2026-10-26T11:58:00Z",
        ["session"] = Window(session, 300, "2026-10-26T15:00:00Z"),
        ["weekly"] = Window(weekly, 10080, "2026-10-30T00:00:00Z"),
        ["planLabel"] = planLabel,
    };

    public static string Accounts() => new JsonArray(
        Account("acc_max", "alice@example.com", "Max 20x", current: true, quota: Quota(42, 18, "Claude Max 20x"), email: "alice@example.com"),
        Account("acc_pro", "bob@example.com", "Pro", order: 1, quota: Quota(96, 74), proxy: "socks5://127.0.0.1:1080"),
        Account("acc_cool", "carol@example.com", "Team", order: 2, cooldown: "2026-10-26T13:20:00Z", quota: Quota(100, 62)),
        Account("acc_off", "dave@example.com", "Pro", enabled: false, order: 3, quota: Quota(12, 5), source: "token"),
        Account("acc_dead", "erin@example.com", "Max 5x", status: "expired", order: 4, error: "refresh_token 已失效：invalid_grant", source: "batch"),
        Account("acc_key", "", null, order: 5, kind: "api_key", source: "token", keyMasked: "sk-ant-api…9f2c")).ToJsonString();

    public static string FewAccounts() => new JsonArray(
        Account("acc_max", "alice@example.com", "Max 20x", current: true, quota: Quota(42, 18))).ToJsonString();

    public static string Usage() => new JsonObject
    {
        ["day"] = "2026-10-26",
        ["timeZone"] = "Asia/Shanghai",
        ["accounts"] = new JsonArray(
            UsageJson("acc_max", 128, 2_412_300, 1_843_200_000, 4_812, 98_120_400, 152_330_000_000),
            UsageJson("acc_pro", 37, 412_800, 380_500_000, 1_204, 21_300_100, 30_120_000_000),
            UsageJson("acc_cool", 9, 88_100, 41_000_000, 812, 9_120_000, 8_830_000_000)),
    }.ToJsonString();

    private static JsonObject UsageJson(string id, long reqToday, long tokToday, long costToday, long reqTotal, long tokTotal, long costTotal) => new()
    {
        ["accountId"] = id,
        ["today"] = new JsonObject { ["requests"] = reqToday, ["inputTokens"] = tokToday * 3 / 4, ["outputTokens"] = tokToday - tokToday * 3 / 4, ["costNanoUsd"] = costToday },
        ["total"] = new JsonObject { ["requests"] = reqTotal, ["inputTokens"] = tokTotal * 3 / 4, ["outputTokens"] = tokTotal - tokTotal * 3 / 4, ["costNanoUsd"] = costTotal },
    };

    public static ProviderAccountUsageDto UsageRow() => Json.DeserializeApi<ProviderAccountUsageDto>(UsageJson("acc_max", 128, 2_412_300, 1_843_200_000, 48_120, 98_120_400, 1_523_300_000_000))!;

    public const string PolicyClaude = """{"clientPolicy":"claude-code-only","switchMode":"failover","claudeSubscription":true,"mimicClaudeCode":false}""";
    public const string PolicyAny = """{"clientPolicy":"any","switchMode":"balanced","claudeSubscription":true,"mimicClaudeCode":false}""";

    private const string ImportInfo = """{"supported":true,"apiKey":true,"formats":["claude-refresh-token","claude-setup-token","claude-credentials-json"],"maxBatch":200}""";

    private const string ResetCredits = """
        {"credits":[
          {"id":"rc1","reset_type":"weekly","title":"每周额度重置","description":"立即清空本周的用量","status":"available","expires_at":"2026-11-15T00:00:00Z","is_supported_by_plan":true},
          {"id":"rc2","reset_type":"session","title":"5 小时窗口重置","status":"redeemed","redeemed_at":"2026-10-20T09:12:00Z"},
          {"id":"rc3","reset_type":"weekly","title":"每周额度重置","status":"expired","expires_at":"2026-10-01T00:00:00Z","is_supported_by_plan":false}
        ],"available_count":1}
        """;

    private const string ImportPreview = """
        {"items":[
          {"index":1,"source":"sk-a…AAAA","status":"ready","kind":"refresh_token"},
          {"index":2,"source":"sk-a…BBBB","status":"ready","kind":"refresh_token"},
          {"index":3,"source":"sk-a…CCCC","status":"duplicate","kind":"refresh_token","message":"已存在：alice@example.com"},
          {"index":4,"source":"bad-…oken","status":"invalid_format","message":"无法识别的凭据格式"}
        ],"imported":2,"duplicates":1,"failed":1}
        """;

    /// <summary>A login start answer of <paramref name="mode"/>.</summary>
    public static string Login(string mode) => mode switch
    {
        "device" => """{"mode":"device","state":"st1","userCode":"WDJB-MJHT","verificationUrl":"https://github.com/login/device","interval":5}""",
        "cli" => """{"mode":"cli","state":"st1","authorizeUrl":"https://z.ai/cli/authorize?state=st1","interval":5}""",
        "paste" => """{"mode":"paste","state":"st1","authorizeUrl":"https://claude.ai/oauth/authorize?state=st1"}""",
        _ => """{"mode":"pkce","state":"st1","authorizeUrl":"https://auth.openai.com/oauth/authorize?state=st1"}""",
    };

    public static Dictionary<string, (string Body, int Status)> Routes(string providerJson, string? accounts = null, int accountsStatus = 200, int usageStatus = 200,
        string policy = PolicyClaude, string? login = null, (string Body, int Status)? poll = null)
    {
        var id = JsonNode.Parse(providerJson)!["id"]!.GetValue<string>();
        var p = "/api/providers/" + id;
        var routes = new Dictionary<string, (string Body, int Status)>(StringComparer.Ordinal)
        {
            ["GET " + p + "/accounts"] = accountsStatus == 200 ? (accounts ?? Accounts(), 200) : ("""{"error":"读取账户失败"}""", accountsStatus),
            ["GET " + p + "/usage"] = usageStatus == 200 ? (Usage(), 200) : ("""{"error":"用量统计不可用"}""", usageStatus),
            ["GET " + p + "/subscription-policy"] = (policy, 200),
            ["GET " + p + "/accounts/import-info"] = (ImportInfo, 200),
            ["POST " + p + "/accounts/import"] = (ImportPreview, 200),
            ["GET /api/subscription/codex/local-login"] = ("""{"available":true,"accountEmail":"me@example.com","plan":"plus","detail":"~/.codex/auth.json"}""", 200),
            ["GET /api/provider-accounts/acc_max/reset-credits"] = (ResetCredits, 200),
            ["POST " + p + "/accounts/login/st1/poll"] = poll ?? ("""{"status":"pending"}""", 200),
        };
        if (login is not null) routes["POST " + p + "/accounts/login"] = (login, 200);
        return routes;
    }

    // ---- plugin provider ----

    public static readonly PluginAuthMethodDto BrowserMethod = new(0, "oauth", "Kimi 账号登录", null);
    public static readonly PluginAuthMethodDto KeyMethod = new(1, "api", "", "sk-…");

    public static Dictionary<string, (string Body, int Status)> PluginRoutes(string? accounts = null, string? prompt = null)
    {
        var p = "/api/plugin-providers/plugin-kimi";
        var list = new JsonArray(
            PluginAccount("pacc_1", "kimi-user-01", true, "active"),
            PluginAccount("pacc_2", "kimi-user-02", false, "expired"));
        return new Dictionary<string, (string Body, int Status)>(StringComparer.Ordinal)
        {
            ["GET /api/providers/plugin-kimi/accounts"] = (accounts ?? list.ToJsonString(), 200),
            ["GET /api/providers/plugin-kimi/usage"] = ("""{"day":"2026-10-26","timeZone":"Asia/Shanghai","accounts":[{"accountId":"pacc_1","today":{"requests":21,"inputTokens":120000,"outputTokens":30000,"costNanoUsd":0},"total":{"requests":640,"inputTokens":4100000,"outputTokens":900000,"costNanoUsd":0}}]}""", 200),
            ["GET /api/providers/plugin-kimi/subscription-policy"] = ("""{"clientPolicy":"any","switchMode":"failover","claudeSubscription":false,"mimicClaudeCode":false}""", 200),
            ["GET " + p + "/methods"] = (new JsonArray(
                new JsonObject { ["index"] = 0, ["type"] = "oauth", ["label"] = "Kimi 账号登录" },
                new JsonObject { ["index"] = 1, ["type"] = "api", ["label"] = "", ["placeholder"] = "sk-…" }).ToJsonString(), 200),
            ["GET " + p + "/checkin"] = ("""{"supported":true,"on":true,"accounts":[{"accountId":"pacc_1","account":"kimi-user-01","day":"2026-10-26","outcome":"claimed","credit":50,"streak":7,"at":"2026-10-26T08:00:00Z"},{"accountId":"pacc_2","account":"kimi-user-02","outcome":null}]}""", 200),
            ["GET " + p + "/accounts/pacc_1/usage"] = ("""{"plan":"Kimi Pro","until":"2026-12-01T00:00:00Z","windows":[{"name":"每日额度","used":34,"limit":0},{"name":"本月请求","amount":1200,"limit":5000,"unit":"次","resetsAt":1764547200}]}""", 200),
            ["POST " + p + "/prompt"] = (prompt ?? """{"prompt":{"type":"select","key":"region","message":"选择账号所在区域","options":[{"label":"中国大陆","value":"cn","hint":"kimi.moonshot.cn"},{"label":"国际","value":"intl"}]}}""", 200),
            ["POST " + p + "/validate"] = ("""{"error":null}""", 200),
            ["GET " + p + "/sign-in/flow1"] = ("""{"id":"flow1","state":"waiting","url":"https://kimi.moonshot.cn/device","instructions":"在打开的页面登录后，复制显示的授权码粘贴到下方。","pasteCode":true,"deviceCode":"KIMI-4821"}""", 200),
        };
    }

    private static JsonObject PluginAccount(string id, string name, bool current, string status) => new()
    {
        ["id"] = id, ["providerId"] = "plugin-kimi", ["displayName"] = name, ["status"] = status, ["createdAt"] = Now, ["enabled"] = true,
        ["isCurrent"] = current, ["sortOrder"] = 0, ["kind"] = "plugin", ["source"] = "plugin",
    };
}
