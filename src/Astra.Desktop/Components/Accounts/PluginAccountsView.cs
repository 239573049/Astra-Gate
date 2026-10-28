using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using LiveView = Astra.Desktop.Components.Settings.LiveView;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The accounts of a plugin's provider (web PluginAccounts.tsx), embedded by the provider detail page: one button per sign-in method of the
/// plugin, the sign-ins (status, gateway usage, the plugin's allowance on request, sign-out), the switching mode when there are several,
/// the usage hint, the plugin's daily check-in, and the sign-in sheet (<see cref="PluginSignInSheet"/>). It owns its queries while it is
/// on screen.
/// </summary>
public sealed class PluginAccountsView : LiveView
{
    private readonly PluginAccountsModel _model;
    private readonly PluginSignInSheet _sheet;
    private IDisposable? _activation;
    private string _listSignature = "";

    private WrapPanel _methods = null!;
    private ContentControl _list = null!;
    private ContentControl _policy = null!;
    private ContentControl _usageError = null!;
    private TextBlock _hint = null!;
    private ContentControl _checkin = null!;
    private ControlledSegmented _mode = null!;

    /// <summary>Creates the view for <paramref name="provider"/> over the app context.</summary>
    public PluginAccountsView(PageContext ctx, ProviderDto provider) : base(ctx)
    {
        _model = new PluginAccountsModel(new AccountsApi(ctx.Api, ctx.Queries), provider, ctx.Feedback, url => SystemActions.OpenUrlAsync(this, url));
        _sheet = new PluginSignInSheet(_model);
    }

    /// <summary>The state and actions behind the view (scenes and tests drive it).</summary>
    public PluginAccountsModel Model => _model;

    /// <summary>The sign-in sheet (scenes drive it).</summary>
    internal PluginSignInSheet SignInSheet => _sheet;

    /// <summary>A newer copy of the same provider: the view keeps its state and redraws (see <see cref="PluginAccountsModel.UpdateProvider"/>).</summary>
    public void UpdateProvider(ProviderDto provider) => _model.UpdateProvider(provider);

    /// <inheritdoc />
    protected override void Build()
    {
        _methods = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        _list = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _policy = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _usageError = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _hint = AccountsUi.Note("");
        _hint.LineHeight = 17.875;
        _checkin = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _mode = new ControlledSegmented(
            [("manual", Loc.T("providers.subscription.policy.manual")), ("failover", Loc.T("providers.subscription.policy.failover")), ("balanced", Loc.T("providers.subscription.policy.balanced"))],
            null);
        Avalonia.Automation.AutomationProperties.SetName(_mode, Loc.T("providers.subscription.policy.switchMode"));
        _mode.Chose += mode => _ = _model.SetSwitchModeAsync(mode);
        _listSignature = "";
        // web: flex flex-col gap-4; empty slots take no space.
        var stack = new StackPanel { Spacing = 16 };
        foreach (var c in new Control[] { _methods, _policy, _list, _usageError, _hint, _checkin }) stack.Children.Add(c);
        Content = new Panel { Children = { stack, _sheet } };
    }

    /// <inheritdoc />
    protected override void OnShown()
    {
        _activation = _model.Activate();
        _model.PropertyChanged += OnChanged;
        foreach (var q in new INotifyPropertyChanged[] { _model.Accounts, _model.Usage, _model.Methods, _model.Policy, _model.Checkin }) q.PropertyChanged += OnChanged;
    }

    /// <inheritdoc />
    protected override void OnHidden()
    {
        _model.PropertyChanged -= OnChanged;
        foreach (var q in new INotifyPropertyChanged[] { _model.Accounts, _model.Usage, _model.Methods, _model.Policy, _model.Checkin }) q.PropertyChanged -= OnChanged;
        _activation?.Dispose();
        _activation = null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => RequestRefresh();

    /// <inheritdoc />
    protected override void Refresh()
    {
        Methods();
        var accounts = _model.Accounts;
        var list = accounts.Data ?? [];

        if (accounts.Data is { Count: > 1 } && _model.Policy.Data is { } policy)
        {
            _mode.SetValue(policy.SwitchMode);
            (_mode.Parent as Panel)?.Children.Remove(_mode);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(4, 0) };
            var detail = AccountsUi.Text(SubscriptionAccountsModel.SwitchModeDetail(policy.SwitchMode), 12, ArcDesignTokenKind.TextSecondary, wrap: true);
            row.Children.Add(detail);
            Grid.SetColumn(_mode, 1);
            row.Children.Add(_mode);
            _policy.Content = row;
        }
        else _policy.Content = null;
        _policy.IsVisible = _policy.Content is not null;

        // The list redraws only when what it shows changed (the usage poll answers every 5 s).
        var signature = string.Join('|', accounts.IsLoading, accounts.HasData, list.Count == 0 ? "" : Json.SerializeApi(list),
            _model.Usage.Data is { } u ? Json.SerializeApi(u) : "", string.Join(',', list.Where(a => _model.ShowsUsage(a.Id)).Select(a => a.Id)), Loc.Language);
        if (signature != _listSignature)
        {
            _listSignature = signature;
            _list.Content = accounts.IsLoading || (!accounts.HasData && !accounts.IsError)
                ? new LoadingState(3)
                : list.Count == 0
                    ? AccountsUi.Card(new EmptyStateView(Loc.T("pluginAccounts.empty"), Loc.T("pluginAccounts.emptyDetail"), AccountsUi.Icon(LucideIconKind.Plug, 24)))
                    : AccountsUi.Card(AccountsUi.Divided(list.Select(Row)));
        }
        _list.IsVisible = _list.Content is not null;

        _usageError.Content = _model.Usage.IsError
            ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("providers.usage.error"), Content = AccountsUi.Text(AccountRules.ErrorText(_model.Usage.Error), 13, wrap: true) }
            : null;
        _usageError.IsVisible = _usageError.Content is not null;
        var zone = _model.Usage.Data?.TimeZone;
        _hint.Text = Loc.T("providers.usage.hint") + (string.IsNullOrEmpty(zone) ? "" : " " + Loc.T("providers.usage.dayHint", ("zone", zone)));
        _hint.IsVisible = list.Count > 0;

        _checkin.Content = _model.Checkin.Data is { Supported: true } state ? Checkin(state) : null;
        _checkin.IsVisible = _checkin.Content is not null;
        _sheet.Sync();
    }

    private void Methods()
    {
        _methods.Children.Clear();
        var q = _model.Methods;
        if (q.IsLoading) _methods.Children.Add(new LoadingState(1) { Width = 200 });
        if (q.IsError && !q.HasData)
            _methods.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("pluginAccounts.noPlugin"), Content = AccountsUi.Text(AccountRules.ErrorText(q.Error), 13, wrap: true) });
        foreach (var m in q.Data ?? [])
        {
            var method = m;
            _methods.Children.Add(AccountsUi.Button(PluginAccountsModel.MethodLabel(m), () => _model.Method = method,
                m.Index == 0 ? ButtonVariant.Primary : ButtonVariant.Secondary, m.Type == "api" ? LucideIconKind.KeyRound : LucideIconKind.LogIn, size: ControlSize.Medium));
        }
        _methods.IsVisible = _methods.Children.Count > 0;
    }

    private Control Row(ProviderAccountDto a)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(16, 12) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(AccountsUi.Text(a.DisplayName, 13, AccountsUi.Inline, FontWeight.Medium));
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (a.IsCurrent) right.Children.Add(AccountsUi.Badge(Loc.T("pluginAccounts.current"), "accent"));
        right.Children.Add(AccountsUi.Badge(Loc.T(a.Status == "active" ? "pluginAccounts.active" : "pluginAccounts.lapsed"), a.Status == "active" ? "green" : "red"));
        if (_model.UsageTold) right.Children.Add(AccountsUi.Button(Loc.T("pluginAccounts.usage"), () => _model.ToggleUsage(a.Id)));
        right.Children.Add(AccountsUi.Button(Loc.T("pluginAccounts.signOut"), () => _ = _model.SignOutAsync(a), ButtonVariant.Danger, LucideIconKind.LogOut));
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        stack.Children.Add(head);
        if (a.Status != "active") stack.Children.Add(AccountsUi.Text(Loc.T("pluginAccounts.lapsedDetail"), 12, ArcDesignTokenKind.Danger, wrap: true));
        stack.Children.Add(new AccountUsageStatsView(_model.UsageOf(a.Id), valueColor: AccountsUi.Inline));
        if (_model.ShowsUsage(a.Id)) stack.Children.Add(Allowance(a));
        return stack;
    }

    // The plugin's word on the account's allowance (web: rounded-md bg-[var(--surface-muted)] px-3 py-2), fetched while open.
    private Control Allowance(ProviderAccountDto a)
    {
        var host = new QueryHost<JsonObject>(_model.AllowanceOf(a.Id), q =>
        {
            var lines = new StackPanel { Spacing = 4 };
            if (q.IsLoading || (!q.HasData && !q.IsError)) lines.Children.Add(new LoadingState(2));
            if (q.IsError && !q.HasData) lines.Children.Add(AccountsUi.Text(AccountRules.ErrorText(q.Error), 12, ArcDesignTokenKind.Danger, wrap: true));
            if (q.HasData)
            {
                var usage = PluginUsage.Parse(q.Data);
                if (usage.Error is { } err) lines.Children.Add(AccountsUi.Text(err, 12, ArcDesignTokenKind.Danger, wrap: true));
                if (usage.Plan is { } plan) lines.Children.Add(AccountsUi.KeyValue(Loc.T("pluginAccounts.plan"), plan));
                if (usage.Until is { } until) lines.Children.Add(AccountsUi.KeyValue(Loc.T("pluginAccounts.until"), AccountRules.When(until)));
                foreach (var w in usage.Windows) lines.Children.Add(AccountsUi.KeyValue(w.Name, PluginAccountsModel.WindowText(w)));
            }
            return lines;
        });
        var box = new Border { Padding = new Thickness(12, 8), Child = host };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        return box;
    }

    private Control Checkin(PluginCheckinStateDto state)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 16, Margin = new Thickness(16, 12) };
        var title = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AccountsUi.Text(Loc.T("pluginAccounts.checkin"), 13, AccountsUi.Inline, FontWeight.Medium),
                AccountsUi.Text(Loc.T("pluginAccounts.checkinDetail"), 12, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        };
        head.Children.Add(title);
        var toggle = new ControlledSwitch { IsChecked = state.On, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T("pluginAccounts.checkinOn"));
        toggle.ToggleRequested += on => _ = _model.CheckinAsync(on);
        Grid.SetColumn(toggle, 1);
        head.Children.Add(toggle);
        var now = AccountsUi.Button(Loc.T("pluginAccounts.checkinNow"), () => _ = _model.CheckinAsync(null), loading: _model.CheckinPending);
        Grid.SetColumn(now, 2);
        head.Children.Add(now);

        var rows = new List<Control> { head };
        foreach (var a in state.Accounts)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 10) };
            row.Children.Add(AccountsUi.Text(a.Account, 13, AccountsUi.Inline));
            var detail = PluginAccountsModel.CheckinDetail(a);
            if (detail.Length > 0)
            {
                var d = AccountsUi.Text(detail, 12, ArcDesignTokenKind.TextSecondary);
                Grid.SetColumn(d, 1);
                row.Children.Add(d);
            }
            var badge = string.IsNullOrEmpty(a.Outcome)
                ? AccountsUi.Badge(Loc.T("pluginAccounts.checkinNever"), "neutral")
                : AccountsUi.Badge(Loc.T("pluginAccounts.outcome." + a.Outcome), PluginAccountsModel.OutcomeTone(a.Outcome));
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
            rows.Add(row);
        }
        return AccountsUi.Card(AccountsUi.Divided(rows));
    }
}

/// <summary>
/// The sheet of one sign-in with a plugin's method (web <c>SignInSheet</c>): the method's questions one by one, then a key, or the page
/// to open — after which the code it shows is pasted here, or the plugin notices the sign-in itself while the sheet waits.
/// </summary>
internal sealed class PluginSignInSheet : Sheet
{
    private readonly PluginAccountsModel _owner;
    private readonly Input _answer = new();
    private readonly PasswordField _key = new();
    private readonly Input _code = new();
    private PluginSignInModel? _model;
    private string? _promptKey;
    private string? _focusToken;
    private Control? _select;
    private TKCode.Arc.Button? _keyButton;
    private TKCode.Arc.Button? _codeButton;

    // The model properties that change what the sheet shows (the typed texts do not).
    private static readonly HashSet<string> Shown = [nameof(PluginSignInModel.PromptLoaded), nameof(PluginSignInModel.Prompt), nameof(PluginSignInModel.AnswerError),
        nameof(PluginSignInModel.Busy), nameof(PluginSignInModel.SignIn), nameof(PluginSignInModel.State)];

    /// <summary>Creates the (closed) sheet.</summary>
    public PluginSignInSheet(PluginAccountsModel owner)
    {
        _owner = owner;
        SheetWidth = 520;
        _key.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _code.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _answer.TextChanged += (_, _) => { if (_model is not null) _model.Answer = _answer.Text ?? ""; };
        // Typing only enables the sign-in buttons: redrawing the sheet would move the focused box.
        _key.TextChanged += (_, _) =>
        {
            if (_model is null) return;
            _model.Key = _key.Text ?? "";
            if (_keyButton is not null) _keyButton.IsEnabled = _model.Key.Trim().Length > 0 && !_model.Busy;
        };
        _code.TextChanged += (_, _) =>
        {
            if (_model is null) return;
            _model.Code = _code.Text ?? "";
            if (_codeButton is not null) _codeButton.IsEnabled = _model.Code.Trim().Length > 0 && !_model.Busy;
        };
        Closed += (_, _) =>
        {
            if (IsOpen) return;
            _model?.Dispose();
            _model = null;
            _owner.Method = null;
        };
    }

    /// <summary>The sign-in under way (scenes drive it).</summary>
    public PluginSignInModel? Current => _model;

    /// <summary>Opens / closes to match the owner's chosen method.</summary>
    public void Sync()
    {
        if (_owner.Method is { } method)
        {
            if (_model is null || _model.Method != method)
            {
                _model?.Dispose();
                _model = new PluginSignInModel(_owner, method);
                _model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is { } name && Shown.Contains(name)) Avalonia.Threading.Dispatcher.UIThread.Post(Fill);
                };
                _model.Closed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsOpen = false);
                _answer.Text = _key.Text = _code.Text = "";
                _promptKey = null;
                _focusToken = null;
            }
            Fill();
            if (!IsOpen) IsOpen = true;
        }
        else if (IsOpen) IsOpen = false;
    }

    private void Fill()
    {
        if (_model is not { } m) return;
        Title = Loc.T("pluginAccounts.signInTitle", ("name", _owner.Provider.Name));
        Description = m.Method.Label;
        var stack = new StackPanel { Spacing = 12 };
        // Fill re-parents the three boxes, which drops the keyboard focus: remember who had it to give it back.
        Control? hadFocus = _answer.IsKeyboardFocusWithin ? _answer : _key.IsKeyboardFocusWithin ? _key : _code.IsKeyboardFocusWithin ? _code : null;
        Detach(_answer, _key, _code);
        if (_select is not null) Detach(_select);

        if (!m.PromptLoaded) stack.Children.Add(new LoadingState(2));
        if (m.PromptLoaded && m.Prompt is { } prompt)
        {
            if (prompt.IsSelect)
            {
                if (_promptKey != prompt.Key || _select is null)
                {
                    var options = prompt.Options.Select(o => (o.Value, o.Label)).ToList();
                    var select = new ControlledSelect(options, m.Answer, double.NaN) { HorizontalAlignment = HorizontalAlignment.Stretch };
                    // web: the option's hint is its description. ARC-GAP: Arc's SelectOption has no description line; its Meta (shown at the
                    // option's right) carries the hint instead.
                    if (prompt.Options.Any(o => !string.IsNullOrEmpty(o.Hint)))
                    {
                        select.ItemsSource = prompt.Options.Select(o => new SelectOption(o.Value, o.Label) { Meta = o.Hint }).ToList();
                        select.SetValue(m.Answer);
                    }
                    select.Chose += v => { m.Answer = v; select.SetValue(v); };
                    _select = FormField.Wrap(prompt.Message, select);
                }
                ((Field)_select).ErrorMessage = m.AnswerError;
                stack.Children.Add(_select);
            }
            else
            {
                if (_promptKey != prompt.Key) _answer.Text = m.Answer;
                _answer.Label = prompt.Message;
                _answer.Placeholder = prompt.Placeholder;
                _answer.ErrorMessage = m.AnswerError;
                stack.Children.Add(_answer);
                AutoFocus(_answer, "answer:" + prompt.Key);
            }
            _promptKey = prompt.Key;
            stack.Children.Add(Right(AccountsUi.Button(Loc.T("pluginAccounts.continue"), () => _ = m.ContinueAsync(), ButtonVariant.Primary, size: ControlSize.Medium)));
        }
        if (m.WantsKey)
        {
            _key.Label = Loc.T("pluginAccounts.key");
            _key.PlaceholderText = m.Method.Placeholder;
            stack.Children.Add(_key);
            AutoFocus(_key, "key");
            _keyButton = AccountsUi.Button(Loc.T("pluginAccounts.signIn"), () => _ = m.UseKeyAsync(), ButtonVariant.Primary,
                enabled: m.Key.Trim().Length > 0 && !m.Busy, size: ControlSize.Medium);
            stack.Children.Add(Right(_keyButton));
        }
        if (m.WantsStart)
            stack.Children.Add(Right(AccountsUi.Button(Loc.T("pluginAccounts.openPage"), () => _ = m.BeginAsync(), ButtonVariant.Primary, LucideIconKind.ExternalLink,
                enabled: !m.Busy, size: ControlSize.Medium)));
        if (m.State is { } s && s.State != "done")
        {
            if (!string.IsNullOrEmpty(s.Instructions)) stack.Children.Add(AccountsUi.Text(s.Instructions, 12, ArcDesignTokenKind.TextSecondary, wrap: true));
            if (!string.IsNullOrEmpty(s.DeviceCode)) stack.Children.Add(AccountsUi.KeyValue(Loc.T("pluginAccounts.deviceCode"), s.DeviceCode, mono: true));
            if (!string.IsNullOrEmpty(s.Url))
            {
                var url = s.Url;
                var again = AccountsUi.Button(Loc.T("pluginAccounts.openAgain"), () => _ = _owner.OpenUrl(url), icon: LucideIconKind.ExternalLink, size: ControlSize.Medium);
                again.HorizontalAlignment = HorizontalAlignment.Stretch;
                again.HorizontalContentAlignment = HorizontalAlignment.Center;
                stack.Children.Add(again);
            }
            if (s.State == "waiting" && s.PasteCode)
            {
                _code.Label = Loc.T("pluginAccounts.code");
                stack.Children.Add(_code);
                AutoFocus(_code, "code");
                _codeButton = AccountsUi.Button(Loc.T("pluginAccounts.signIn"), () => _ = m.SubmitCodeAsync(), ButtonVariant.Primary,
                    enabled: m.Code.Trim().Length > 0 && !m.Busy, size: ControlSize.Medium);
                stack.Children.Add(Right(_codeButton));
            }
            if (s.State == "waiting" && !s.PasteCode) stack.Children.Add(AccountsUi.Text(Loc.T("pluginAccounts.waiting"), 12, ArcDesignTokenKind.TextMuted, wrap: true));
            if (s.State == "failed")
                stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("pluginAccounts.failed"), Content = string.IsNullOrEmpty(s.Error) ? null : AccountsUi.Text(s.Error, 13, wrap: true) });
        }
        Content = stack;
        if (hadFocus is not null && hadFocus.Parent == stack) Astra.Desktop.Components.ProviderDetail.PdSheet.FocusWhenShown(hadFocus);
    }

    /// <summary>web <c>autoFocus</c>: the first time a field (per prompt) shows up in the sheet it takes the caret.</summary>
    private void AutoFocus(Control field, string token)
    {
        if (_focusToken == token) return;
        _focusToken = token;
        Astra.Desktop.Components.ProviderDetail.PdSheet.FocusWhenShown(field);
    }

    private static Control Right(Control button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Right;
        return button;
    }

    private static void Detach(params Control[] controls)
    {
        foreach (var c in controls) (c.Parent as Panel)?.Children.Remove(c);
    }
}
