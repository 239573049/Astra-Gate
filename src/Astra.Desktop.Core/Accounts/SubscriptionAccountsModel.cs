using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Accounts;

/// <summary>An entry of the "add account" menu (web <c>addItems</c>).</summary>
/// <param name="Id">login, login-proxy, codex, copilot, token, apikey.</param>
/// <param name="Label">The text.</param>
/// <param name="SeparatorBefore">A line above it.</param>
public sealed record AddMenuItem(string Id, string Label, bool SeparatorBefore = false);

/// <summary>
/// The state and actions of a subscription provider's "accounts" tab (web SubscriptionAccounts.tsx, plan §5.4): the account pool, its
/// filter / search / selection, the detail sheet, the import sheet, the proxy-login sheet, and the login wizard with its three ways to
/// finish — PKCE (the browser callback completes on the server; the new account is detected by refetching every 3 s), device code / CLI
/// (polled at the login's interval until done or expired) and a pasted authorization code (Claude). It is the component's React state and
/// handlers moved out of the view so they can be tested; the view only draws it.
/// </summary>
public sealed class SubscriptionAccountsModel : ModelBase
{
    /// <summary>Quota snapshots older than this are refreshed when the list shows (web <c>QUOTA_FRESH_MS</c>).</summary>
    public static readonly TimeSpan QuotaFresh = TimeSpan.FromMinutes(5);

    /// <summary>The PKCE login's account refetch period.</summary>
    public static readonly TimeSpan PkceRefetch = TimeSpan.FromSeconds(3);

    private readonly AccountsApi _api;
    private readonly IFeedback _feedback;
    private readonly IClock _clock;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quotaAttempted = new(StringComparer.Ordinal);
    private HashSet<string> _baseline = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loop;
    private int _failures;
    private int _active;

    private AccountFilter _filter;
    private string _search = "";
    private string? _detailId;
    private ImportMode? _importMode;
    private bool _proxyLoginOpen;
    private string _loginProxy = "";
    private bool _bulkBusy;
    private bool _loginOpen;
    private LoginStart? _login;
    private string? _loginError;
    private bool _checking;
    private string _pasted = "";
    private bool _starting;
    private string? _quotaPending;

    /// <summary>Creates the model of <paramref name="provider"/>'s accounts. <paramref name="openUrl"/> opens a page in the system browser.</summary>
    public SubscriptionAccountsModel(AccountsApi api, ProviderDto provider, IFeedback feedback, IClock clock, Func<string, Task>? openUrl = null)
    {
        _api = api;
        _feedback = feedback;
        _clock = clock;
        OpenUrl = openUrl ?? (_ => Task.CompletedTask);
        Provider = provider;
        Accounts = api.Accounts(provider.Id);
        Usage = api.Usage(provider.Id);
        Policy = api.Policy(provider.Id);
        ImportInfo = api.ImportInfo(provider.Id);
        // Only a ChatGPT subscription has a local codex login to adopt; only Copilot a machine GitHub authorization.
        if (IsCodex) LocalCodex = api.LocalCodex();
        if (IsCopilot) LocalCopilot = api.LocalCopilot();
    }

    /// <summary>The API layer.</summary>
    public AccountsApi Api => _api;

    /// <summary>Toasts and confirmations.</summary>
    public IFeedback Feedback => _feedback;

    /// <summary>Opens an address in the system browser (the web's <c>window.open(url, '_blank')</c>).</summary>
    public Func<string, Task> OpenUrl { get; set; }

    /// <summary>The provider (replaced in place by <see cref="UpdateProvider"/> when the server sends a newer copy).</summary>
    public ProviderDto Provider { get; private set; }

    /// <summary>
    /// A newer copy of the same provider (its proxy or settings changed): the web component re-renders with the new prop and keeps its state
    /// (filters, selection, open sheets, polling), so the model takes the copy instead of being recreated.
    /// </summary>
    public void UpdateProvider(ProviderDto provider)
    {
        if (provider.Id != Provider.Id) throw new ArgumentException("another provider", nameof(provider));
        Provider = provider;
        Raise(nameof(Provider));
    }

    /// <summary>The tab exists only on an <c>oauth-subscription</c> provider; elsewhere the view shows nothing.</summary>
    public bool IsSubscription => Provider.AuthScheme == "oauth-subscription";

    /// <summary>A ChatGPT (codex) subscription.</summary>
    public bool IsCodex => Provider.TemplateId == "openai-subscription";

    /// <summary>A GitHub Copilot subscription.</summary>
    public bool IsCopilot => Provider.TemplateId == "github-copilot-subscription";

    /// <summary>The accounts (failover order).</summary>
    public Query<List<ProviderAccountDto>> Accounts { get; }

    /// <summary>Per-account request usage (polled).</summary>
    public Query<ProviderUsageDto> Usage { get; }

    /// <summary>The client / switching policy.</summary>
    public Query<SubscriptionPolicyDto> Policy { get; }

    /// <summary>What the import sheet accepts.</summary>
    public Query<AccountImportInfoDto> ImportInfo { get; }

    /// <summary>The machine's codex login (codex only).</summary>
    public Query<LocalCodexLoginDto>? LocalCodex { get; }

    /// <summary>The machine's GitHub authorization (Copilot only).</summary>
    public Query<LocalCopilotLoginDto>? LocalCopilot { get; }

    /// <summary>The time the views judge states by.</summary>
    public DateTimeOffset Now => _clock.Now;

    /// <summary>The account list (empty until it loads).</summary>
    public IReadOnlyList<ProviderAccountDto> List => Accounts.Data ?? [];

    /// <summary>
    /// Observes the queries while the view is shown (web: the hooks are mounted) and follows the list: selections and the open detail drop
    /// accounts that went away, stale quotas are fetched once, and a PKCE login finishes when its new account appears. Dispose to stop.
    /// </summary>
    public IDisposable Activate()
    {
        if (!IsSubscription) return Disposer(() => { });
        _active++;
        var tokens = new List<IDisposable> { Accounts.Activate(), Usage.Activate(), Policy.Activate(), ImportInfo.Activate() };
        if (LocalCodex is not null) tokens.Add(LocalCodex.Activate());
        if (LocalCopilot is not null) tokens.Add(LocalCopilot.Activate());
        Accounts.PropertyChanged += OnAccounts;
        OnList();
        return Disposer(() =>
        {
            Accounts.PropertyChanged -= OnAccounts;
            foreach (var t in tokens) t.Dispose();
            if (--_active == 0) StopLoop();
        });
    }

    private void OnAccounts(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Query<int>.Data)) OnList();
    }

    private void OnList()
    {
        var list = List;
        var ids = list.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (_selected.RemoveWhere(id => !ids.Contains(id)) > 0) Raise(nameof(Selected));
        if (_detailId is not null && !ids.Contains(_detailId)) DetailId = null;
        else if (_detailId is not null) Raise(nameof(Detail));

        // Fetch the quota of active accounts once when it is missing or older than five minutes; failures stay quiet (manual retry).
        if (Accounts.HasData)
            foreach (var a in list)
            {
                if (a.Status != "active" || _quotaAttempted.Contains(a.Id)) continue;
                var at = AccountQuota.Parse(a.Quota)?.FetchedAt;
                if (at is { } fetched && Now - fetched < QuotaFresh) continue;
                _quotaAttempted.Add(a.Id);
                _ = FetchQuotaAsync(a, quiet: true);
            }

        // PKCE: the browser callback completed the exchange on the server; the new account is the sign.
        if (LoginOpen && Login?.Mode == "pkce" && list.FirstOrDefault(a => !_baseline.Contains(a.Id)) is { } fresh)
            Finish(true, AccountRules.NameOf(fresh));
    }

    // ---- view state ----

    /// <summary>The status filter.</summary>
    public AccountFilter Filter { get => _filter; set => Set(ref _filter, value); }

    /// <summary>The search text.</summary>
    public string Search { get => _search; set => Set(ref _search, value ?? ""); }

    /// <summary>The selected account ids (bulk actions).</summary>
    public IReadOnlySet<string> Selected => _selected;

    /// <summary>Some card is selected: check boxes stay visible and a click toggles the selection.</summary>
    public bool Selecting => _selected.Count > 0;

    /// <summary>The account whose detail sheet is open.</summary>
    public string? DetailId
    {
        get => _detailId;
        set
        {
            if (Set(ref _detailId, value)) Raise(nameof(Detail));
        }
    }

    /// <summary>The open detail's account (null when none or it went away).</summary>
    public ProviderAccountDto? Detail => _detailId is null ? null : List.FirstOrDefault(a => a.Id == _detailId);

    /// <summary>The open detail's place in the failover order.</summary>
    public int DetailIndex => Detail is { } d ? List.ToList().IndexOf(d) : 0;

    /// <summary>The open import sheet's mode (null = closed).</summary>
    public ImportMode? ImportMode { get => _importMode; set => Set(ref _importMode, value); }

    /// <summary>The "log in through a proxy" sheet is open.</summary>
    public bool ProxyLoginOpen { get => _proxyLoginOpen; set => Set(ref _proxyLoginOpen, value); }

    /// <summary>The proxy typed in that sheet.</summary>
    public string LoginProxy { get => _loginProxy; set => Set(ref _loginProxy, value ?? ""); }

    /// <summary>A bulk action is running.</summary>
    public bool BulkBusy { get => _bulkBusy; private set => Set(ref _bulkBusy, value); }

    /// <summary>The login wizard is open.</summary>
    public bool LoginOpen { get => _loginOpen; private set => Set(ref _loginOpen, value); }

    /// <summary>The login under way.</summary>
    public LoginStart? Login { get => _login; private set => Set(ref _login, value); }

    /// <summary>The login's last error, shown in the wizard so a finished authorization never looks like it is still waiting.</summary>
    public string? LoginError { get => _loginError; private set => Set(ref _loginError, value); }

    /// <summary>A manual "check now" / paste submit is running.</summary>
    public bool Checking { get => _checking; private set => Set(ref _checking, value); }

    /// <summary>The authorization code the user pasted (Claude).</summary>
    public string Pasted { get => _pasted; set => Set(ref _pasted, value ?? ""); }

    /// <summary>A login start is running.</summary>
    public bool Starting { get => _starting; private set => Set(ref _starting, value); }

    /// <summary>The account whose quota fetch runs (its strip fades).</summary>
    public string? QuotaPending { get => _quotaPending; private set => Set(ref _quotaPending, value); }

    // ---- derived ----

    /// <summary>The pool line's counts: total, usable, cooling, lapsed.</summary>
    public (int Total, int Usable, int Cooling, int Dead) Stats
    {
        get
        {
            var list = List;
            return (list.Count, AccountCards.CountOf(list, AccountFilter.Usable, Now), AccountCards.CountOf(list, AccountFilter.Cooling, Now),
                AccountCards.CountOf(list, AccountFilter.Dead, Now));
        }
    }

    /// <summary>The cards shown (filter + search), each with its place in the whole list.</summary>
    public IReadOnlyList<AccountCardModel> Cards(TimeZoneInfo? zone = null)
    {
        var list = List;
        return AccountCards.Visible(list, Filter, Search, Now).Select(v => AccountCards.Card(v.Account, v.Index, list.Count, Now, zone)).ToList();
    }

    /// <summary>The usage row of one account.</summary>
    public ProviderAccountUsageDto? UsageOf(string? accountId) => accountId is null ? null : Usage.Data?.Accounts.FirstOrDefault(u => u.AccountId == accountId);

    /// <summary>The "add account" menu: browser login (plain / through a proxy), local imports, token and key imports.</summary>
    public IReadOnlyList<AddMenuItem> AddMenu
    {
        get
        {
            var items = new List<AddMenuItem>
            {
                new("login", Loc.T("providers.accounts.addLogin")),
                new("login-proxy", Loc.T("providers.accounts.addLoginProxy")),
            };
            var codexLocal = IsCodex && LocalCodex?.Data?.Available == true;
            if (codexLocal) items.Add(new("codex", Loc.T("providers.subscription.importCodex"), true));
            if (IsCopilot) items.Add(new("copilot", Loc.T("providers.subscription.importCopilot"), true));
            var info = ImportInfo.Data;
            if (info?.Supported == true) items.Add(new("token", Loc.T("providers.accounts.addToken"), !codexLocal && !IsCopilot));
            if (info?.ApiKey == true) items.Add(new("apikey", Loc.T("providers.accounts.addApiKey")));
            return items;
        }
    }

    /// <summary>The detail of the switching mode under the pool line.</summary>
    public static string SwitchModeDetail(string mode) => Loc.T(mode switch
    {
        "balanced" => "providers.subscription.policy.balancedDetail",
        "failover" => "providers.subscription.policy.failoverDetail",
        _ => "providers.subscription.policy.manualDetail",
    });

    /// <summary>The proxy the provider's requests take (the pool line's last item).</summary>
    public string ProviderProxyText => Loc.T("providers.accounts.providerProxy",
        ("proxy", string.IsNullOrEmpty(Provider.HttpProxy) ? Loc.T("providers.accounts.proxyFallbackSystem") : Provider.HttpProxy));

    /// <summary>The note under the toolbar about a local codex login / Copilot authorization; null when there is none.</summary>
    public string? LocalNote
    {
        get
        {
            if (IsCodex && LocalCodex?.Data is { Available: true } codex)
            {
                var who = codex.AccountEmail ?? codex.Plan ?? Loc.T("providers.subscription.unknownAccount");
                return Loc.T("providers.subscription.localCodexFound", ("who", who)) + (string.IsNullOrEmpty(codex.Detail) ? "" : " · " + codex.Detail);
            }
            if (IsCopilot)
            {
                var c = LocalCopilot?.Data;
                var text = c?.Available == true
                    ? Loc.T("providers.subscription.localCopilotFound", ("source", c.Source ?? ""))
                    : Loc.T("providers.subscription.localCopilotHint");
                return text + (string.IsNullOrEmpty(c?.Detail) ? "" : " · " + c.Detail);
            }
            return null;
        }
    }

    // ---- selection ----

    /// <summary>Adds or removes an account from the selection.</summary>
    public void ToggleSelect(string id)
    {
        if (!_selected.Remove(id)) _selected.Add(id);
        Raise(nameof(Selected));
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Raise(nameof(Selected));
    }

    /// <summary>A click on a card: toggles it while selecting, else opens its detail.</summary>
    public void Open(ProviderAccountDto a)
    {
        if (Selecting) ToggleSelect(a.Id);
        else DetailId = a.Id;
    }

    // ---- per-account actions (card menu and detail sheet) ----

    /// <summary>Runs a menu entry (<see cref="AccountMenuItem.Id"/>) on an account.</summary>
    public Task RunAsync(string action, ProviderAccountDto a, int index = -1) => action switch
    {
        "activate" => ActivateAsync(a),
        "toggle" => ToggleEnabledAsync(a),
        "up" => MoveAsync(index, -1),
        "down" => MoveAsync(index, 1),
        "quota" => FetchQuotaAsync(a),
        "refresh" => RefreshTokenAsync(a),
        "relogin" => StartLoginAsync(a.Id),
        "remove" => RemoveAsync(a),
        _ => Task.CompletedTask,
    };

    /// <summary>Makes it the provider's current account.</summary>
    public async Task ActivateAsync(ProviderAccountDto a)
    {
        if (await Try(() => _api.ActivateAsync(Provider.Id, a.Id)))
            _feedback.Success(Loc.T("providers.subscription.activated", ("name", AccountRules.NameOf(a))));
    }

    /// <summary>Switches the account on or off.</summary>
    public Task ToggleEnabledAsync(ProviderAccountDto a) => Try(() => _api.UpdateAsync(Provider.Id, a.Id, new AccountPatch(!a.Enabled, null)));

    /// <summary>Fetches the account's quota now (501: the family has no quota probe); <paramref name="quiet"/> swallows failures.</summary>
    public async Task FetchQuotaAsync(ProviderAccountDto a, bool quiet = false)
    {
        QuotaPending = a.Id;
        try
        {
            await _api.FetchQuotaAsync(Provider.Id, a.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!quiet) _feedback.Error(e is ApiException { Status: 501 } ? Loc.T("providers.subscription.quotaUnsupported") : AccountRules.ErrorText(e));
        }
        finally
        {
            if (QuotaPending == a.Id) QuotaPending = null;
        }
    }

    /// <summary>Refreshes the login's token.</summary>
    public async Task RefreshTokenAsync(ProviderAccountDto a)
    {
        if (await Try(() => _api.RefreshTokenAsync(Provider.Id, a.Id))) _feedback.Success(Loc.T("providers.subscription.tokenRefreshed"));
    }

    /// <summary>Logs the account out (after a confirmation).</summary>
    public async Task RemoveAsync(ProviderAccountDto a)
    {
        var name = AccountRules.NameOf(a);
        if (!await _feedback.ConfirmAsync(new ConfirmRequest(Loc.T("providers.subscription.logoutConfirm", ("name", name)), null, Loc.T("providers.subscription.logout"), null, true))) return;
        await Try(() => _api.DeleteAsync(Provider.Id, a.Id));
    }

    /// <summary>Swaps the account at <paramref name="index"/> with its neighbour in the failover order.</summary>
    public async Task MoveAsync(int index, int delta)
    {
        if (AccountCards.Swap(List, index, delta) is { } ids) await Try(() => _api.ReorderAsync(Provider.Id, ids));
    }

    /// <summary>Saves the account's display name; a toast says so.</summary>
    public async Task<bool> RenameAsync(ProviderAccountDto a, string name)
    {
        var ok = await Try(() => _api.UpdateAsync(Provider.Id, a.Id, new AccountPatch(null, name.Trim())));
        if (ok) _feedback.Success(Loc.T("providers.accounts.renamed"));
        return ok;
    }

    /// <summary>Saves the account's own proxy ("" = follow the provider); a toast says so.</summary>
    public async Task<bool> SaveProxyAsync(ProviderAccountDto a, string proxy)
    {
        var ok = await Try(() => _api.UpdateAsync(Provider.Id, a.Id, new AccountPatch(null, null, proxy.Trim())));
        if (ok) _feedback.Success(Loc.T("providers.accounts.proxySaved"));
        return ok;
    }

    /// <summary>Changes how requests pick between accounts.</summary>
    public Task SetSwitchModeAsync(string mode) => Try(() => _api.UpdatePolicyAsync(Provider.Id, new SubscriptionPolicyPatch(null, mode, null)));

    // ---- bulk ----

    /// <summary>
    /// Runs <paramref name="run"/> for each selected account in list order; a failure is reported with the account's name, then the selection
    /// clears, the list is refetched and the successes are counted in a toast.
    /// </summary>
    public async Task BulkAsync(Func<ProviderAccountDto, Task> run)
    {
        var targets = List.Where(a => _selected.Contains(a.Id)).ToList();
        BulkBusy = true;
        var done = 0;
        foreach (var a in targets)
        {
            try
            {
                await run(a);
                done++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _feedback.Error($"{AccountRules.NameOf(a)}: {AccountRules.ErrorText(e)}");
            }
        }
        BulkBusy = false;
        ClearSelection();
        _api.Queries.Invalidate(AccountsApi.AccountsKey(Provider.Id));
        if (done > 0) _feedback.Success(Loc.T("providers.accounts.bulkDone", ("n", done)));
    }

    /// <summary>Switches the selected accounts on (true) or off.</summary>
    public Task BulkEnableAsync(bool on) => BulkAsync(a => _api.UpdateAsync(Provider.Id, a.Id, new AccountPatch(on, null)));

    /// <summary>Fetches the selected accounts' quotas.</summary>
    public Task BulkQuotaAsync() => BulkAsync(a => _api.FetchQuotaAsync(Provider.Id, a.Id));

    /// <summary>Deletes the selected accounts (after a confirmation).</summary>
    public async Task BulkDeleteAsync()
    {
        var ok = await _feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("providers.accounts.bulkDeleteConfirm", ("n", _selected.Count)), Loc.T("providers.accounts.bulkDeleteDetail"), Loc.T("common.delete"), null, true));
        if (ok) await BulkAsync(a => _api.DeleteAsync(Provider.Id, a.Id));
    }

    // ---- local imports ----

    /// <summary>Adopts the machine's codex login.</summary>
    public async Task ImportCodexAsync()
    {
        try
        {
            if (await _api.ImportCodexAsync(Provider.Id) is { } r) Imported(r);
            if (LocalCodex is not null) _ = LocalCodex.RefetchAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _feedback.Error(AccountRules.ErrorText(e));
        }
    }

    /// <summary>Adopts the machine's GitHub authorization as a Copilot account.</summary>
    public async Task ImportCopilotAsync()
    {
        try
        {
            if (await _api.ImportCopilotAsync(Provider.Id) is { } r) Imported(r);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _feedback.Error(AccountRules.ErrorText(e));
        }
    }

    private void Imported(ImportedAccountDto r)
    {
        var text = Loc.T("providers.subscription.importOk", ("name", AccountRules.NameOf(r.Account)));
        if (string.IsNullOrEmpty(r.Warning)) _feedback.Success(text);
        else
        {
            _feedback.Info(text);
            _feedback.Error(r.Warning);
        }
    }

    /// <summary>Runs an "add account" menu entry.</summary>
    public Task AddAsync(string id)
    {
        switch (id)
        {
            case "login": return StartLoginAsync();
            case "login-proxy": ProxyLoginOpen = true; return Task.CompletedTask;
            case "codex": return ImportCodexAsync();
            case "copilot": return ImportCopilotAsync();
            case "token": ImportMode = global::Astra.Desktop.Core.Accounts.ImportMode.Token; return Task.CompletedTask;
            case "apikey": ImportMode = global::Astra.Desktop.Core.Accounts.ImportMode.ApiKey; return Task.CompletedTask;
            default: return Task.CompletedTask;
        }
    }

    // ---- login ----

    /// <summary>
    /// Starts a login (<paramref name="accountId"/> logs that account in again, <paramref name="proxy"/> is the new account's own proxy) and
    /// opens the wizard; PKCE, CLI and paste logins open the authorization page right away (a device login waits for the user).
    /// </summary>
    public async Task StartLoginAsync(string? accountId = null, string? proxy = null)
    {
        _baseline = List.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        LoginError = null;
        Starting = true;
        try
        {
            var res = await _api.StartLoginAsync(Provider.Id, accountId, proxy);
            ProxyLoginOpen = false;
            Pasted = "";
            Login = res;
            LoginOpen = true;
            StartLoop(res);
            if (res.Mode != "device" && !string.IsNullOrEmpty(res.AuthorizeUrl)) await OpenUrl(res.AuthorizeUrl);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is ApiException { Details: System.Text.Json.Nodes.JsonObject d } && AccountQuota.Bool(d["needsVerification"]) == true)
                _feedback.Error(Loc.T("providers.subscription.needsVerification"));
            else _feedback.Error(AccountRules.ErrorText(e));
        }
        finally
        {
            Starting = false;
        }
    }

    /// <summary>Closes the wizard (and stops following the login).</summary>
    public void CloseLogin()
    {
        StopLoop();
        LoginOpen = false;
        Login = null;
        LoginError = null;
    }

    private void Finish(bool ok, string? name = null, string? error = null)
    {
        _api.Queries.Invalidate(AccountsApi.AccountsKey(Provider.Id));
        if (ok)
        {
            StopLoop();
            Login = null;
            LoginError = null;
            LoginOpen = false;
            _feedback.Success(Loc.T("providers.subscription.loginOk", ("name", name ?? "")));
            return;
        }
        // On failure the wizard stays open with the reason: the user needs a place to retry or close.
        if (error is not null) LoginError = error;
    }

    private void StartLoop(LoginStart login)
    {
        StopLoop();
        _failures = 0;
        var cts = new CancellationTokenSource();
        _loop = cts;
        if (login.Mode == "pkce") _ = PkceLoopAsync(cts.Token);
        else if (login.Polls) _ = PollLoopAsync(login, cts.Token);
    }

    private void StopLoop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private async Task PkceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PkceRefetch, _api.Queries.Time, ct);
                await Accounts.RefetchAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollLoopAsync(LoginStart login, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(login.PollEvery, _api.Queries.Time, ct);
                if (await TickAsync(login, ct)) return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// One poll of a device / CLI login; true when the login came to an end (done, or a failure that polling cannot fix). A missing session
    /// (404) or a failed exchange (400) ends it at once; network trouble only after five failures in a row.
    /// </summary>
    public async Task<bool> TickAsync(LoginStart login, CancellationToken ct = default)
    {
        try
        {
            var r = await _api.PollLoginAsync(Provider.Id, login.State);
            if (ct.IsCancellationRequested) return true;
            _failures = 0;
            if (r.Done)
            {
                Finish(true, AccountRules.NameOf(r.Account!));
                return true;
            }
            if (r.Pending) return false;
            Finish(false, error: r.FailureText);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (ct.IsCancellationRequested) return true;
            if (e is ApiException { Status: 404 or 400 })
            {
                Finish(false, error: AccountRules.ErrorText(e));
                return true;
            }
            if (++_failures >= 5) Finish(false, error: AccountRules.ErrorText(e));
            return false;
        }
    }

    /// <summary>
    /// "I authorized — check now" (device / CLI): asks the server at once instead of waiting for the next poll. When the background poll
    /// already consumed the session (404), a new account in the list still counts as success.
    /// </summary>
    public async Task CheckNowAsync()
    {
        if (Login is not { } login) return;
        Checking = true;
        try
        {
            var r = await _api.PollLoginAsync(Provider.Id, login.State);
            if (r.Done) Finish(true, AccountRules.NameOf(r.Account!));
            else if (r.Pending) _feedback.Info(Loc.T("providers.subscription.stillWaiting"));
            else Finish(false, error: r.FailureText);
        }
        catch (ApiException e) when (e.Status == 404)
        {
            await Accounts.RefetchAsync();
            if (List.FirstOrDefault(a => !_baseline.Contains(a.Id)) is { } added) Finish(true, AccountRules.NameOf(added));
            else Finish(false, error: AccountRules.ErrorText(e));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Finish(false, error: AccountRules.ErrorText(e));
        }
        finally
        {
            Checking = false;
        }
    }

    /// <summary>Hands the code the authorization page showed to the server (Claude); the whole "code#state" text is accepted.</summary>
    public async Task SubmitPastedAsync()
    {
        if (Login is not { Mode: "paste" } login || Pasted.Trim().Length == 0) return;
        Checking = true;
        try
        {
            var r = await _api.CompleteLoginAsync(Provider.Id, login.State, Pasted.Trim());
            if (r.Done)
            {
                Pasted = "";
                Finish(true, AccountRules.NameOf(r.Account!));
            }
            else Finish(false, error: r.FailureText);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Finish(false, error: AccountRules.ErrorText(e));
        }
        finally
        {
            Checking = false;
        }
    }

    /// <summary>"I authorized" of a PKCE login: refetch the list now (the new account finishes the login).</summary>
    public Task RefreshForLoginAsync()
    {
        _api.Queries.Invalidate(AccountsApi.AccountsKey(Provider.Id));
        return Accounts.RefetchAsync();
    }

    private async Task<bool> Try(Func<Task> write)
    {
        try
        {
            await write();
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _feedback.Error(AccountRules.ErrorText(e));
            return false;
        }
    }
}
