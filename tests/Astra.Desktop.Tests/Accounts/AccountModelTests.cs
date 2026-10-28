using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Tests.Api;
using Astra.Desktop.Tests.Plugins;

namespace Astra.Desktop.Tests.Accounts;

[Collection("Loc")]
public sealed class AccountModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _saved = Loc.Language;
    private readonly ManualTime _time = new();
    private readonly StubHttp _http = new();
    private readonly QueryClient _queries;
    private readonly ApiClient _api;
    private readonly Feedback _feedback = new();
    private readonly List<string> _opened = [];

    public AccountModelTests()
    {
        Loc.Language = "en";
        _queries = new QueryClient(null, _time);
        _api = new ApiClient("http://stub", _http);
        _http.On("GET /api/providers/p1/usage", """{"day":"2026-10-26","timeZone":"UTC","accounts":[]}""")
            .On("GET /api/providers/p1/subscription-policy", """{"clientPolicy":"claude-code-only","switchMode":"failover","claudeSubscription":true,"mimicClaudeCode":false}""")
            .On("GET /api/providers/p1/accounts/import-info", """{"supported":true,"apiKey":false,"formats":[],"maxBatch":10}""");
    }

    public void Dispose()
    {
        Loc.Language = _saved;
        _queries.Dispose();
        _api.Dispose();
    }

    private sealed class Feedback : IFeedback
    {
        public List<string> Errors { get; } = [];
        public List<string> Successes { get; } = [];
        public List<string> Infos { get; } = [];
        public List<ConfirmRequest> Asked { get; } = [];
        public bool Answer { get; set; } = true;
        public void Success(string title, string? description = null) => Successes.Add(title);
        public void Error(string title, string? description = null) => Errors.Add(title);
        public void Info(string title, string? description = null) => Infos.Add(title);
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default)
        {
            Asked.Add(request);
            return Task.FromResult(Answer);
        }
    }

    private static ProviderDto Provider(string template = "anthropic-subscription", string scheme = "oauth-subscription", JsonObject? settings = null)
        => new("p1", template, 1, false, "Claude", null, "subscription", [], [], scheme, false, null, [], null, 1, null, null, settings ?? [], true, 0, null, null, 0, [],
            Now, Now, new ProviderQuotaConfigDto(false, null, null, null, null, 0, 0, null, [], [], null, null), null, null);

    private static string Accounts(params ProviderAccountDto[] list) => "[" + string.Join(',', list.Select(Json.SerializeApi)) + "]";

    private static ProviderAccountDto Fresh(string id, string name = "x", string status = "active", bool enabled = true)
        => AccountLogicTests.Account(id, name, status, enabled, quota: JsonNode.Parse("""{"fetchedAtUtc":"2026-10-26T11:59:00Z"}"""));

    private SubscriptionAccountsModel Model(ProviderDto? provider = null)
        => new(new AccountsApi(_api, _queries), provider ?? Provider(), _feedback, new FixedClock(Now), url => { _opened.Add(url); return Task.CompletedTask; });

    private async Task Settle() => await _queries.WhenIdleAsync(TimeSpan.FromSeconds(2));

    // Waits for work a fired timer started (it runs on the thread pool); gives up after 2 s and lets the assertion say what is wrong.
    private static async Task Until(Func<bool> ready)
    {
        for (var i = 0; i < 200 && !ready(); i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Activation_loads_and_fetches_stale_quotas_once()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a"), AccountLogicTests.Account("b"), AccountLogicTests.Account("c", status: "expired")))
            .On("POST /api/provider-accounts/b/quota", "{}", 501);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        Assert.Equal(3, m.List.Count);
        Assert.Equal(1, _http.Count("POST", "/api/provider-accounts/b/quota"));
        Assert.Equal(0, _http.Count("POST", "/api/provider-accounts/a/quota"));
        Assert.Equal(0, _http.Count("POST", "/api/provider-accounts/c/quota"));
        Assert.Empty(_feedback.Errors); // the automatic fetch is quiet
        await m.Accounts.RefetchAsync();
        Assert.Equal(1, _http.Count("POST", "/api/provider-accounts/b/quota"));
    }

    [Fact]
    public async Task Not_a_subscription_activates_nothing()
    {
        var m = Model(Provider(scheme: "bearer"));
        using var _ = m.Activate();
        await Settle();
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Selection_follows_the_list_and_bulk_reports()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a"), Fresh("b")))
            .On("PATCH /api/provider-accounts/a", Accounts(Fresh("a", enabled: false), Fresh("b")))
            .On("PATCH /api/provider-accounts/b", """{"error":"boom"}""", 500);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        m.ToggleSelect("a");
        m.ToggleSelect("b");
        m.ToggleSelect("zzz");
        Assert.True(m.Selecting);
        await m.BulkEnableAsync(false);
        Assert.False(m.Selecting);
        Assert.Equal(["x: boom"], _feedback.Errors);
        Assert.Equal([Loc.T("providers.accounts.bulkDone", ("n", 1))], _feedback.Successes);
        Assert.Contains("\"enabled\":false", _http.Requests.First(r => r.Method == "PATCH").Body);
    }

    [Fact]
    public async Task Removed_accounts_drop_out_of_selection_and_detail()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a"), Fresh("b")));
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        m.ToggleSelect("b");
        m.DetailId = "b";
        Assert.Equal(1, m.DetailIndex);
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")));
        await m.Accounts.RefetchAsync();
        Assert.False(m.Selecting);
        Assert.Null(m.DetailId);
    }

    [Fact]
    public async Task Card_actions_hit_the_web_endpoints()
    {
        var list = Accounts(Fresh("a"), Fresh("b"));
        _http.On("GET /api/providers/p1/accounts", list)
            .On("PUT /api/providers/p1/accounts/order", Accounts(Fresh("b"), Fresh("a")))
            .On("POST /api/provider-accounts/a/activate", list)
            .On("DELETE /api/provider-accounts/a", "")
            .On("POST /api/provider-accounts/a/quota", """{"error":"no probe"}""", 501)
            .On("PUT /api/providers/p1/subscription-policy", """{"clientPolicy":"any","switchMode":"balanced","claudeSubscription":true,"mimicClaudeCode":false}""");
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        var a = m.List[0];

        await m.RunAsync("down", a, 0);
        Assert.Contains("\"ids\":[\"b\",\"a\"]", _http.Requests.Single(r => r.Method == "PUT").Body);
        Assert.Equal("b", m.List[0].Id); // the answer replaced the cached list

        await m.RunAsync("activate", a);
        Assert.Contains(Loc.T("providers.subscription.activated", ("name", "x")), _feedback.Successes);

        await m.RunAsync("quota", a);
        Assert.Contains(Loc.T("providers.subscription.quotaUnsupported"), _feedback.Errors);

        _feedback.Answer = false;
        await m.RunAsync("remove", a);
        Assert.Equal(0, _http.Count("DELETE", "/api/provider-accounts/a"));
        _feedback.Answer = true;
        await m.RunAsync("remove", a);
        Assert.Equal(1, _http.Count("DELETE", "/api/provider-accounts/a"));
        Assert.True(_feedback.Asked.Last().Destructive);

        await m.SetSwitchModeAsync("balanced");
        Assert.Equal("balanced", m.Policy.Data!.SwitchMode);
    }

    [Fact]
    public async Task Device_login_polls_until_done()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")))
            .On("POST /api/providers/p1/accounts/login", """{"mode":"device","state":"st","userCode":"AB","verificationUrl":"https://v","interval":5}""")
            .On("POST /api/providers/p1/accounts/login/st/poll", """{"status":"pending"}""");
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync();
        Assert.True(m.LoginOpen);
        Assert.Empty(_opened); // a device login waits for the user to open the page

        _time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => _http.Count("POST", "/api/providers/p1/accounts/login/st/poll") == 1);
        await Task.Delay(20);
        Assert.Equal(1, _http.Count("POST", "/api/providers/p1/accounts/login/st/poll"));
        Assert.True(m.LoginOpen);

        _http.On("POST /api/providers/p1/accounts/login/st/poll", """{"status":"done","account":""" + Json.SerializeApi(Fresh("n", "newbie")) + "}");
        _time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => !m.LoginOpen);
        Assert.False(m.LoginOpen);
        Assert.Contains(Loc.T("providers.subscription.loginOk", ("name", "newbie")), _feedback.Successes);

        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(20);
        Assert.Equal(2, _http.Count("POST", "/api/providers/p1/accounts/login/st/poll")); // the loop stopped
    }

    [Fact]
    public async Task A_missing_session_ends_the_poll_with_its_error()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")))
            .On("POST /api/providers/p1/accounts/login", """{"mode":"cli","state":"st","authorizeUrl":"https://auth","interval":3}""")
            .On("POST /api/providers/p1/accounts/login/st/poll", """{"error":"session gone"}""", 404);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync();
        Assert.Equal(["https://auth"], _opened);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Until(() => m.LoginError is not null);
        Assert.Equal("session gone", m.LoginError);
        Assert.True(m.LoginOpen);
        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(20);
        Assert.Equal(1, _http.Count("POST", "/api/providers/p1/accounts/login/st/poll"));
    }

    [Fact]
    public async Task Check_now_counts_a_new_account_after_a_404()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")))
            .On("POST /api/providers/p1/accounts/login", """{"mode":"device","state":"st","userCode":"AB","interval":5}""")
            .On("POST /api/providers/p1/accounts/login/st/poll", """{"error":"gone"}""", 404);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync();
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a"), Fresh("n", "added")));
        await m.CheckNowAsync();
        Assert.False(m.LoginOpen);
        Assert.Contains(Loc.T("providers.subscription.loginOk", ("name", "added")), _feedback.Successes);
    }

    [Fact]
    public async Task Pkce_login_finishes_when_the_new_account_appears()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")))
            .On("POST /api/providers/p1/accounts/login", """{"mode":"pkce","state":"st","authorizeUrl":"https://auth"}""");
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync(null, " socks5://p ");
        Assert.Contains("\"httpProxy\":\"socks5://p\"", _http.Requests.Single(r => r.PathAndQuery.EndsWith("/accounts/login", StringComparison.Ordinal)).Body);
        Assert.Equal(["https://auth"], _opened);
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a"), Fresh("n", "fresh")));
        _time.Advance(TimeSpan.FromSeconds(3));
        await Until(() => !m.LoginOpen);
        Assert.False(m.LoginOpen);
        Assert.Contains(Loc.T("providers.subscription.loginOk", ("name", "fresh")), _feedback.Successes);
    }

    [Fact]
    public async Task Pasted_code_completes_or_shows_why_not()
    {
        _http.On("GET /api/providers/p1/accounts", Accounts(Fresh("a")))
            .On("POST /api/providers/p1/accounts/login", """{"mode":"paste","state":"st","authorizeUrl":"https://claude"}""")
            .On("POST /api/providers/p1/accounts/login/st/complete", """{"status":"error","error":"bad code"}""", 400);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync();
        m.Pasted = "  code#state ";
        await m.SubmitPastedAsync();
        Assert.Contains("\"code\":\"code#state\"", _http.Requests.Last(r => r.PathAndQuery.EndsWith("/complete", StringComparison.Ordinal)).Body);
        Assert.Equal("bad code", m.LoginError);
        Assert.True(m.LoginOpen);
        m.CloseLogin();
        Assert.False(m.LoginOpen);
        Assert.Null(m.LoginError);
    }

    [Fact]
    public async Task An_unverified_flow_has_its_own_message()
    {
        _http.On("GET /api/providers/p1/accounts", "[]")
            .On("POST /api/providers/p1/accounts/login", """{"error":"x","details":{"needsVerification":true}}""", 400);
        var m = Model();
        using var _ = m.Activate();
        await Settle();
        await m.StartLoginAsync();
        Assert.False(m.LoginOpen);
        Assert.Equal([Loc.T("providers.subscription.needsVerification")], _feedback.Errors);
    }

    [Fact]
    public async Task Add_menu_and_local_note_follow_the_template()
    {
        _http.On("GET /api/providers/p1/accounts", "[]")
            .On("GET /api/subscription/codex/local-login", """{"available":true,"accountEmail":"me@x","detail":"auth.json"}""")
            .On("POST /api/providers/p1/accounts/import-codex", """{"account":""" + Json.SerializeApi(Fresh("c", "codexer")) + ""","warning":"check failed"}""");
        var m = Model(Provider("openai-subscription"));
        using var _ = m.Activate();
        await Settle();
        Assert.Equal(["login", "login-proxy", "codex", "token"], m.AddMenu.Select(i => i.Id));
        Assert.False(m.AddMenu.Single(i => i.Id == "token").SeparatorBefore);
        Assert.Equal(Loc.T("providers.subscription.localCodexFound", ("who", "me@x")) + " · auth.json", m.LocalNote);
        await m.AddAsync("codex");
        Assert.Equal([Loc.T("providers.subscription.importOk", ("name", "codexer"))], _feedback.Infos);
        Assert.Equal(["check failed"], _feedback.Errors);
        await m.AddAsync("token");
        Assert.Equal(ImportMode.Token, m.ImportMode);
    }

    // ---- plugin accounts ----

    private PluginAccountsModel PluginModel() => new(new AccountsApi(_api, _queries), Provider("plugin-x", "plugin", new JsonObject { ["plugin"] = new JsonObject { ["usage"] = true } }),
        _feedback, url => { _opened.Add(url); return Task.CompletedTask; });

    [Fact]
    public async Task Plugin_prompts_then_key_sign_in()
    {
        var asked = 0;
        _http.On("POST /api/plugin-providers/p1/prompt", _ =>
            {
                asked++;
                var body = _http.Requests.Last().Body;
                return (200, body.Contains("\"region\"") ? """{"prompt":null}""" : """{"prompt":{"type":"select","key":"region","message":"Region?","options":[{"label":"CN","value":"cn"},{"label":"Intl","value":"intl"}]}}""");
            })
            .On("POST /api/plugin-providers/p1/validate", _ => (200, _http.Requests.Last().Body.Contains("\"intl\"") ? """{"error":"not here"}""" : """{"error":null}"""))
            .On("POST /api/plugin-providers/p1/api-key", """{"account":"acc"}""");
        var owner = PluginModel();
        Assert.True(owner.UsageTold);
        using var sign = new PluginSignInModel(owner, new PluginAuthMethodDto(1, "api", "", null));
        var closed = false;
        sign.Closed += () => closed = true;
        await Until(() => sign.PromptLoaded);
        Assert.Equal("cn", sign.Answer);
        sign.Answer = "intl";
        await sign.ContinueAsync();
        Assert.Equal("not here", sign.AnswerError);
        sign.Answer = "cn";
        await sign.ContinueAsync();
        Assert.Null(sign.AnswerError);
        Assert.True(sign.WantsKey);
        Assert.Equal(2, asked);
        sign.Key = "sk-1";
        await sign.UseKeyAsync();
        Assert.Contains("\"region\":\"cn\"", _http.Requests.Last().Body);
        Assert.True(closed);
        Assert.Contains(Loc.T("pluginAccounts.signedIn"), _feedback.Successes);
    }

    [Fact]
    public async Task Plugin_browser_sign_in_follows_the_flow()
    {
        _http.On("POST /api/plugin-providers/p1/prompt", """{"prompt":null}""")
            .On("POST /api/plugin-providers/p1/sign-in", """{"id":"f1","state":"waiting","url":"https://page","pasteCode":true}""")
            .On("GET /api/plugin-providers/p1/sign-in/f1", """{"id":"f1","state":"waiting","url":"https://page","pasteCode":true}""")
            .On("POST /api/plugin-providers/p1/sign-in/f1/code", """{"id":"f1","state":"done","pasteCode":true}""");
        var owner = PluginModel();
        using var sign = new PluginSignInModel(owner, new PluginAuthMethodDto(0, "oauth", "Browser", null));
        var closed = false;
        sign.Closed += () => closed = true;
        await Until(() => sign.PromptLoaded);
        Assert.True(sign.WantsStart);
        await sign.BeginAsync();
        await Settle();
        Assert.Equal(["https://page"], _opened);
        Assert.Equal("waiting", sign.State!.State);
        sign.Code = " 1234 ";
        await sign.SubmitCodeAsync();
        await Until(() => closed);
        Assert.True(closed);
        Assert.Contains("\"code\":\"1234\"", _http.Requests.Last(r => r.Method == "POST").Body);
    }

    [Fact]
    public async Task Plugin_checkin_and_sign_out()
    {
        _http.On("GET /api/plugin-providers/p1/checkin", """{"supported":true,"on":false,"accounts":[]}""")
            .On("PUT /api/plugin-providers/p1/checkin", """{"supported":true,"on":true,"accounts":[]}""")
            .On("POST /api/plugin-providers/p1/accounts/a/sign-out", """{"removed":true}""");
        var m = PluginModel();
        using var token = m.Checkin.Activate();
        await Settle();
        await m.CheckinAsync(true);
        Assert.True(m.Checkin.Data!.On);
        Assert.Contains("\"on\":true", _http.Requests.Single(r => r.Method == "PUT").Body);
        await m.SignOutAsync(AccountLogicTests.Account("a"));
        Assert.Equal(1, _http.Count("POST", "/api/plugin-providers/p1/accounts/a/sign-out"));
        Assert.Equal("green", PluginAccountsModel.OutcomeTone("claimed"));
        Assert.Equal(Loc.T("pluginAccounts.credit", ("credit", "50")) + " · " + Loc.T("pluginAccounts.streak", ("days", 7)),
            PluginAccountsModel.CheckinDetail(new PluginCheckinDto("a", "a", null, "claimed", null, 50, 7, null)));
    }
}
