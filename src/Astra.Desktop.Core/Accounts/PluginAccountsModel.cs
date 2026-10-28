using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Accounts;

/// <summary>
/// The accounts of a plugin's provider (auth scheme "plugin", web PluginAccounts.tsx): the sign-ins its plugin keeps, the plugin's
/// sign-in methods, the switching mode when there are several, each account's allowance on request, and the plugin's daily check-in.
/// </summary>
public sealed class PluginAccountsModel : ModelBase
{
    private readonly AccountsApi _api;
    private readonly IFeedback _feedback;
    private readonly HashSet<string> _showUsage = new(StringComparer.Ordinal);
    private PluginAuthMethodDto? _method;
    private bool _checkinPending;

    /// <summary>Creates the model of <paramref name="provider"/>'s plugin sign-ins.</summary>
    public PluginAccountsModel(AccountsApi api, ProviderDto provider, IFeedback feedback, Func<string, Task>? openUrl = null)
    {
        _api = api;
        _feedback = feedback;
        Provider = provider;
        OpenUrl = openUrl ?? (_ => Task.CompletedTask);
        Accounts = api.Accounts(provider.Id);
        Usage = api.Usage(provider.Id);
        Methods = api.PluginMethods(provider.Id);
        Policy = api.Policy(provider.Id);
        Checkin = api.PluginCheckin(provider.Id);
    }

    /// <summary>The API layer (the sign-in sheet's model uses it too).</summary>
    public AccountsApi Api => _api;

    /// <summary>Toasts and confirmations.</summary>
    public IFeedback Feedback => _feedback;

    /// <summary>Opens an address in the system browser.</summary>
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

    /// <summary>The sign-ins.</summary>
    public Query<List<ProviderAccountDto>> Accounts { get; }

    /// <summary>Gateway usage per sign-in (polled).</summary>
    public Query<ProviderUsageDto> Usage { get; }

    /// <summary>The plugin's sign-in methods.</summary>
    public Query<List<PluginAuthMethodDto>> Methods { get; }

    /// <summary>How requests pick between sign-ins (the subscription switch modes).</summary>
    public Query<SubscriptionPolicyDto> Policy { get; }

    /// <summary>The daily check-in.</summary>
    public Query<PluginCheckinStateDto> Checkin { get; }

    /// <summary>The plugin tells each account's allowance (<c>settings.plugin.usage</c>): rows offer a "usage" button.</summary>
    public bool UsageTold => Provider.Settings["plugin"] is JsonObject p && AccountQuota.Bool(p["usage"]) == true;

    /// <summary>The method whose sign-in sheet is open.</summary>
    public PluginAuthMethodDto? Method { get => _method; set => Set(ref _method, value); }

    /// <summary>A check-in switch or run is in flight.</summary>
    public bool CheckinPending { get => _checkinPending; private set => Set(ref _checkinPending, value); }

    /// <summary>Observes the queries while the view is shown; dispose to stop.</summary>
    public IDisposable Activate()
    {
        var tokens = new[] { Accounts.Activate(), Usage.Activate(), Methods.Activate(), Policy.Activate(), Checkin.Activate() };
        return Disposer(() =>
        {
            foreach (var t in tokens) t.Dispose();
        });
    }

    /// <summary>A method's button label: its own, else "API key" / "browser sign-in".</summary>
    public static string MethodLabel(PluginAuthMethodDto m)
        => !string.IsNullOrEmpty(m.Label) ? m.Label : Loc.T(m.Type == "api" ? "pluginAccounts.apiKey" : "pluginAccounts.browser");

    /// <summary>The usage row of one sign-in.</summary>
    public ProviderAccountUsageDto? UsageOf(string accountId) => Usage.Data?.Accounts.FirstOrDefault(u => u.AccountId == accountId);

    /// <summary>Whether a row's allowance panel is open.</summary>
    public bool ShowsUsage(string accountId) => _showUsage.Contains(accountId);

    /// <summary>The allowance of a sign-in (fetched while its panel is open).</summary>
    public Query<JsonObject> AllowanceOf(string accountId) => _api.PluginUsage(Provider.Id, accountId);

    /// <summary>Opens or closes a row's allowance panel.</summary>
    public void ToggleUsage(string accountId)
    {
        if (!_showUsage.Remove(accountId)) _showUsage.Add(accountId);
        Raise(nameof(ShowsUsage));
    }

    /// <summary>Changes the switching mode.</summary>
    public async Task SetSwitchModeAsync(string mode)
    {
        try { await _api.UpdatePolicyAsync(Provider.Id, new SubscriptionPolicyPatch(null, mode, null)); }
        catch (Exception e) when (e is not OperationCanceledException) { _feedback.Error(AccountRules.ErrorText(e)); }
    }

    /// <summary>Signs a sign-in out (after a confirmation).</summary>
    public async Task SignOutAsync(ProviderAccountDto a)
    {
        if (!await _feedback.ConfirmAsync(new ConfirmRequest(Loc.T("pluginAccounts.signOutConfirm", ("name", a.DisplayName)), null, Loc.T("pluginAccounts.signOut"), null, true))) return;
        try { await _api.PluginSignOutAsync(Provider.Id, a.Id); }
        catch (Exception e) when (e is not OperationCanceledException) { _feedback.Error(AccountRules.ErrorText(e)); }
    }

    /// <summary>Switches the daily check-in (<paramref name="on"/>), or checks every sign-in in now (null).</summary>
    public async Task CheckinAsync(bool? on)
    {
        CheckinPending = true;
        try { await _api.PluginCheckinActionAsync(Provider.Id, on); }
        catch (Exception e) when (e is not OperationCanceledException) { _feedback.Error(AccountRules.ErrorText(e)); }
        finally { CheckinPending = false; }
    }

    /// <summary>A check-in outcome's badge tone.</summary>
    public static string OutcomeTone(string? outcome) => outcome switch
    {
        "claimed" or "done" => "green",
        "captcha" => "orange",
        "failed" => "red",
        _ => "neutral",
    };

    /// <summary>The detail of a check-in row: credit, streak, message, time.</summary>
    public static string CheckinDetail(PluginCheckinDto a, TimeZoneInfo? zone = null)
    {
        var parts = new List<string>();
        if (a.Credit is { } c && c != 0) parts.Add(Loc.T("pluginAccounts.credit", ("credit", QuotaViews.JsNumber(c))));
        if (a.Streak is { } s && s != 0) parts.Add(Loc.T("pluginAccounts.streak", ("days", s)));
        if (!string.IsNullOrEmpty(a.Message)) parts.Add(a.Message);
        if (a.At is { } at) parts.Add(AccountRules.When(at, zone));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The value of an allowance window: the plugin's own text, else "amount / limit unit", else "used%"; plus " · resets …" when it says
    /// (a reset time in seconds or milliseconds).
    /// </summary>
    public static string WindowText(PluginUsageWindow w, TimeZoneInfo? zone = null)
    {
        var text = w.Display ?? (w.Limit is { } limit && limit != 0
            ? $"{QuotaViews.JsNumber(w.Amount ?? w.Used ?? 0)} / {QuotaViews.JsNumber(limit)} {w.Unit ?? ""}"
            : $"{QuotaViews.JsNumber(w.Used ?? 0)}%");
        if (w.ResetsAt is { } r && r != 0)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(r > 1e12 ? r : r * 1000));
            text += " · " + Loc.T("pluginAccounts.resets", ("at", AccountRules.When(at, zone)));
        }
        return text;
    }
}

/// <summary>
/// One sign-in with a plugin's method (web <c>SignInSheet</c>): its questions one by one (the plugin says which comes next and checks each
/// answer), then a key, or a browser page whose code is pasted back or which the plugin notices on its own.
/// </summary>
public sealed class PluginSignInModel : ModelBase, IDisposable
{
    private readonly PluginAccountsModel _owner;
    private readonly Dictionary<string, string> _inputs = new(StringComparer.Ordinal);
    private bool _promptLoaded;
    private PluginPrompt? _prompt;
    private string _answer = "";
    private string? _answerError;
    private string _key = "";
    private string _code = "";
    private Query<PluginSignInDto>? _signIn;
    private IDisposable? _signInToken;
    private bool _busy;
    private bool _closed;
    private int _promptVersion;

    /// <summary>Creates the sheet's model and asks for the method's first question.</summary>
    public PluginSignInModel(PluginAccountsModel owner, PluginAuthMethodDto method)
    {
        _owner = owner;
        Method = method;
        _ = LoadPromptAsync();
    }

    /// <summary>The method.</summary>
    public PluginAuthMethodDto Method { get; }

    /// <summary>Raised once when the sign-in finished (the sheet closes).</summary>
    public event Action? Closed;

    /// <summary>The answers so far.</summary>
    public IReadOnlyDictionary<string, string> Inputs => _inputs;

    /// <summary>The prompt endpoint answered (web <c>prompt !== undefined</c>).</summary>
    public bool PromptLoaded { get => _promptLoaded; private set => Set(ref _promptLoaded, value); }

    /// <summary>The current question; null once none is left.</summary>
    public PluginPrompt? Prompt { get => _prompt; private set => Set(ref _prompt, value); }

    /// <summary>The answer being typed / chosen.</summary>
    public string Answer { get => _answer; set => Set(ref _answer, value ?? ""); }

    /// <summary>The plugin's objection to the last answer.</summary>
    public string? AnswerError { get => _answerError; private set => Set(ref _answerError, value); }

    /// <summary>The key typed (api methods).</summary>
    public string Key { get => _key; set => Set(ref _key, value ?? ""); }

    /// <summary>The code pasted from the page.</summary>
    public string Code { get => _code; set => Set(ref _code, value ?? ""); }

    /// <summary>A start / key / code request is running.</summary>
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>The browser sign-in under way (null before it starts).</summary>
    public Query<PluginSignInDto>? SignIn { get => _signIn; private set => Set(ref _signIn, value); }

    /// <summary>The followed sign-in's state (web <c>s</c>).</summary>
    public PluginSignInDto? State => SignIn?.Data;

    /// <summary>The method asks for a key (once no question is left).</summary>
    public bool WantsKey => PromptLoaded && Prompt is null && Method.Type == "api";

    /// <summary>The method opens a page and nothing started yet.</summary>
    public bool WantsStart => PromptLoaded && Prompt is null && Method.Type != "api" && State is null;

    private async Task LoadPromptAsync()
    {
        var version = ++_promptVersion;
        try
        {
            var p = await _owner.Api.PluginPromptAsync(_owner.Provider.Id, Method.Index, _inputs);
            if (version != _promptVersion || _closed) return;
            Prompt = p;
            Answer = p?.InitialAnswer ?? "";
            PromptLoaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (version == _promptVersion && !_closed) _owner.Feedback.Error(AccountRules.ErrorText(e));
        }
    }

    /// <summary>Checks the answer with the plugin; a refusal is shown under the field, an accepted answer brings the next question.</summary>
    public async Task ContinueAsync()
    {
        if (Prompt is not { } prompt) return;
        string? error;
        try { error = await _owner.Api.PluginValidateAsync(_owner.Provider.Id, Method.Index, prompt.Key, Answer); }
        catch (Exception e) when (e is not OperationCanceledException) { error = null; }
        if (error is not null)
        {
            AnswerError = error;
            return;
        }
        AnswerError = null;
        _inputs[prompt.Key] = Answer;
        // Like the web, the answered question stays on screen until the next one arrives.
        await LoadPromptAsync();
    }

    /// <summary>Starts the browser sign-in and opens its page.</summary>
    public async Task BeginAsync()
    {
        Busy = true;
        try
        {
            var r = await _owner.Api.StartPluginSignInAsync(_owner.Provider.Id, Method.Index, _inputs);
            Follow(r.Id);
            if (!string.IsNullOrEmpty(r.Url)) await _owner.OpenUrl(r.Url);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _owner.Feedback.Error(AccountRules.ErrorText(e));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Signs in with the typed key.</summary>
    public async Task UseKeyAsync()
    {
        if (Key.Trim().Length == 0) return;
        Busy = true;
        try
        {
            await _owner.Api.PluginApiKeyAsync(_owner.Provider.Id, Method.Index, _inputs, Key);
            Done();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _owner.Feedback.Error(AccountRules.ErrorText(e));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Hands the page's code to the plugin.</summary>
    public async Task SubmitCodeAsync()
    {
        if (State is not { } s || Code.Trim().Length == 0) return;
        Busy = true;
        try { await _owner.Api.SubmitPluginCodeAsync(_owner.Provider.Id, s.Id, Code.Trim()); }
        catch (Exception e) when (e is not OperationCanceledException) { _owner.Feedback.Error(AccountRules.ErrorText(e)); }
        finally { Busy = false; }
    }

    /// <summary>Follows a started flow (also used by tests and scenes that start from a known flow id).</summary>
    public void Follow(string flow)
    {
        _signInToken?.Dispose();
        if (SignIn is not null) SignIn.PropertyChanged -= OnSignIn;
        var q = _owner.Api.PluginSignIn(_owner.Provider.Id, flow);
        q.PropertyChanged += OnSignIn;
        SignIn = q;
        _signInToken = q.Activate();
        Raise(nameof(State));
    }

    private void OnSignIn(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Query<int>.Data)) return;
        Raise(nameof(State));
        if (State?.State == "done") Done();
    }

    private void Done()
    {
        if (_closed) return;
        _owner.Feedback.Success(Loc.T("pluginAccounts.signedIn"));
        Dispose();
        Closed?.Invoke();
    }

    /// <summary>Stops following the sign-in.</summary>
    public void Dispose()
    {
        _closed = true;
        if (SignIn is not null) SignIn.PropertyChanged -= OnSignIn;
        _signInToken?.Dispose();
        _signInToken = null;
    }
}
