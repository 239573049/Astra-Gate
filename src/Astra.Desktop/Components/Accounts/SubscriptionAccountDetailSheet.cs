using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The account detail sheet (web SubscriptionAccountDetail.tsx), opened by a card: the card menu's actions as buttons, the failure of a
/// lapsed login, the usage table, the name and the account's own proxy (each saved on its own), the full quota card, codex's reset cards,
/// and the credential facts. New data refreshes the sheet in place; the two text boxes keep what is typed and are only reset when the
/// account's saved name / proxy change (web: the effect on <c>account.displayName</c> / <c>account.httpProxy</c>).
/// </summary>
internal sealed class SubscriptionAccountDetailSheet : Sheet
{
    private readonly SubscriptionAccountsModel _model;
    private readonly StackPanel _top = new() { Spacing = 16 };
    private readonly StackPanel _bottom = new() { Spacing = 16 };
    private readonly AccountUsageStatsView _usage = new(null);
    private readonly TextBlock _usageHint = AccountsUi.Note("");
    private readonly Input _name = new();
    private readonly Input _proxy = new();
    private readonly ArcButton _saveName;
    private readonly ArcButton _saveProxy;
    private readonly TextBlock _proxyHint;
    private readonly Border _usageCard;
    private readonly TextBlock _usageTitle;
    private readonly TextBlock _identityTitle;
    private string? _shownId;
    private string? _savedName;
    private string? _savedProxy;
    private string? _saving;
    private string _signature = "";

    /// <summary>Creates the (closed) sheet over the accounts model.</summary>
    public SubscriptionAccountDetailSheet(SubscriptionAccountsModel model)
    {
        _model = model;
        SheetWidth = 520;
        _name.Label = Loc.T("providers.accounts.detail.name");
        _proxy.Label = Loc.T("providers.accounts.detail.proxy");
        _proxy.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _saveName = AccountsUi.Button(Loc.T("common.save"), () => _ = SaveNameAsync(), size: ControlSize.Medium);
        _saveProxy = AccountsUi.Button(Loc.T("common.save"), () => _ = SaveProxyAsync(), size: ControlSize.Medium);
        _saveName.VerticalAlignment = _saveProxy.VerticalAlignment = VerticalAlignment.Bottom;
        _name.TextChanged += (_, _) => Buttons();
        _proxy.TextChanged += (_, _) => Buttons();
        _proxyHint = AccountsUi.Note("");

        _usageTitle = AccountsUi.Heading("");
        _usageCard = AccountsUi.Card(_usage, new Thickness(12, 8));
        _identityTitle = AccountsUi.Heading("");
        var identity = new StackPanel
        {
            Spacing = 8,
            Children = { _identityTitle, InputRow(_name, _saveName), InputRow(_proxy, _saveProxy), _proxyHint },
        };
        var usage = new StackPanel { Spacing = 8, Children = { _usageTitle, _usageCard, _usageHint } };
        Content = new StackPanel { Spacing = 16, Children = { _top, usage, identity, _bottom } };
    }

    private static Control InputRow(Input input, ArcButton save)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(input);
        Grid.SetColumn(save, 1);
        grid.Children.Add(save);
        return grid;
    }

    /// <summary>Shows the model's current detail (call when it changed); opens / closes the sheet to match.</summary>
    public void Refresh()
    {
        if (_model.Detail is not { } a)
        {
            _shownId = null;
            _signature = "";
            if (IsOpen) IsOpen = false;
            return;
        }
        // A poll that changed nothing about the account only moves the usage numbers.
        var signature = string.Join('|', Astra.Core.Json.SerializeApi(a), _model.DetailIndex, Loc.Language);
        if (signature == _signature && IsOpen)
        {
            RefreshUsage();
            return;
        }
        _signature = signature;
        var fresh = a.Id != _shownId;
        if (fresh || a.DisplayName != _savedName) _name.Text = a.DisplayName ?? "";
        if (fresh || a.HttpProxy != _savedProxy) _proxy.Text = a.HttpProxy ?? "";
        _shownId = a.Id;
        _savedName = a.DisplayName;
        _savedProxy = a.HttpProxy;
        Fill(a);
        if (!IsOpen) IsOpen = true;
    }

    /// <summary>Moves the usage poll's answer into the table.</summary>
    public void RefreshUsage()
    {
        if (_model.Detail is { } a) _usage.Usage = _model.UsageOf(a.Id);
        _usageHint.Text = UsageHintText();
    }

    private string UsageHintText()
    {
        var zone = _model.Usage.Data?.TimeZone;
        return Loc.T("providers.usage.hint") + (string.IsNullOrEmpty(zone) ? "" : " " + Loc.T("providers.usage.dayHint", ("zone", zone)));
    }

    private void Fill(ProviderAccountDto a)
    {
        var state = AccountRules.StateOf(a, _model.Now);
        var isKey = AccountRules.IsKey(a);
        var isCodex = _model.IsCodex;
        var fallback = string.IsNullOrEmpty(_model.Provider.HttpProxy) ? Loc.T("providers.accounts.proxyFallbackSystem") : _model.Provider.HttpProxy!;

        Title = AccountRules.TitleOf(a);
        Description = string.Join(" · ", new[] { a.Plan, AccountRules.KindText(a) }.Where(s => !string.IsNullOrEmpty(s)));
        _usageTitle.Text = Loc.T("providers.usage.title");
        _identityTitle.Text = Loc.T("providers.accounts.detail.identity");
        _name.Label = Loc.T("providers.accounts.detail.name");
        _proxy.Label = Loc.T("providers.accounts.detail.proxy");
        _proxy.Placeholder = fallback;
        _proxyHint.Text = Loc.T("providers.accounts.detail.proxyHint", ("fallback", fallback));
        _saveName.Content = _saveProxy.Content = Loc.T("common.save");
        RefreshUsage();
        Buttons();

        // Actions (web: flex flex-wrap gap-1.5; log out pushed to the right).
        _top.Children.Clear();
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        var left = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        left.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.activate"), () => _ = _model.ActivateAsync(a), icon: LucideIconKind.ArrowRightLeft,
            enabled: !a.IsCurrent && state != AccountState.Dead));
        left.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.quotaRefresh"), () => _ = _model.FetchQuotaAsync(a), icon: LucideIconKind.Gauge,
            enabled: state != AccountState.Dead));
        if (!isKey)
            left.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.refreshToken"), () => _ = _model.RefreshTokenAsync(a), icon: LucideIconKind.RotateCcw,
                enabled: a.Status != "revoked"));
        if (!isKey && a.Status != "active")
            left.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.relogin"), () => _ = _model.StartLoginAsync(a.Id), ButtonVariant.Primary));
        actions.Children.Add(left);
        var logout = AccountsUi.Button(Loc.T("providers.subscription.logout"), () => _ = _model.RemoveAsync(a), ButtonVariant.Ghost, LucideIconKind.Trash2,
            foreground: ArcDesignTokenKind.Danger);
        logout.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(logout, 1);
        actions.Children.Add(logout);
        _top.Children.Add(actions);
        if (state == AccountState.Dead && !string.IsNullOrEmpty(a.LastError))
            _top.Children.Add(new Alert { Tone = AlertTone.Danger, Title = a.LastError });

        _bottom.Children.Clear();
        if (AccountQuota.Parse(a.Quota) is { } quota)
        {
            var heading = AccountsUi.Heading(Loc.T("providers.accounts.detail.quota"));
            var section = new StackPanel { Spacing = 4, Margin = new Thickness(-16, 0), Children = { new Border { Padding = new Thickness(16, 0), Child = heading }, new SubscriptionQuotaCardView(quota) } };
            _bottom.Children.Add(section);
        }
        if (isCodex && state != AccountState.Dead)
            _bottom.Children.Add(new Border { Margin = new Thickness(-16, 0), Padding = new Thickness(16, 0), Child = new SubscriptionResetCreditsView(_model.Api, _model.Feedback, a, _model.Provider.Id) });

        var rows = new List<Control>
        {
            AccountsUi.KeyValue(Loc.T("providers.accounts.detail.status"), AccountsUi.Badge(AccountRules.StatusText(a, state), AccountRules.BadgeTone(state))),
        };
        if (AccountRules.SourceText(a) is { } source) rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.source"), source));
        if (isKey) rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.key"), a.KeyMasked ?? "••••", mono: true));
        else
        {
            rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.expires"), AccountRules.When(a.ExpiresAtUtc)));
            rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.lastRefresh"), AccountRules.When(a.LastRefreshAtUtc)));
        }
        if (state == AccountState.Cooling) rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.filter.cooling"), AccountRules.When(a.CooldownUntilUtc)));
        rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.order"), Loc.T("providers.accounts.order", ("n", _model.DetailIndex + 1))));
        rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.created"), AccountRules.When(a.CreatedAt)));
        if (!string.IsNullOrEmpty(a.LastError) && state != AccountState.Dead) rows.Add(AccountsUi.KeyValue(Loc.T("providers.accounts.detail.lastError"), a.LastError));
        var credentials = new StackPanel
        {
            Spacing = 4,
            Children = { AccountsUi.Heading(Loc.T("providers.accounts.detail.credentials")), AccountsUi.Card(AccountsUi.Divided(rows, ArcDesignTokenKind.Border), new Thickness(12, 0)) },
        };
        _bottom.Children.Add(credentials);
    }

    private void Buttons()
    {
        var a = _model.Detail;
        var name = (_name.Text ?? "").Trim();
        _saveName.IsEnabled = a is not null && name.Length > 0 && name != a.DisplayName && _saving is null;
        _saveName.IsLoading = _saving == "name";
        _saveProxy.IsEnabled = a is not null && (_proxy.Text ?? "").Trim() != (a.HttpProxy ?? "") && _saving is null;
        _saveProxy.IsLoading = _saving == "proxy";
    }

    private async Task SaveNameAsync()
    {
        if (_model.Detail is not { } a) return;
        _saving = "name";
        Buttons();
        await _model.RenameAsync(a, _name.Text ?? "");
        _saving = null;
        Buttons();
    }

    private async Task SaveProxyAsync()
    {
        if (_model.Detail is not { } a) return;
        _saving = "proxy";
        Buttons();
        await _model.SaveProxyAsync(a, _proxy.Text ?? "");
        _saving = null;
        Buttons();
    }
}
