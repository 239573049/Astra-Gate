using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using LiveView = Astra.Desktop.Components.Settings.LiveView;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The "accounts" tab of a subscription provider (web SubscriptionAccounts.tsx, plan §5.4), embedded by the provider detail page. A pool
/// line (counts, the provider's proxy, the switching mode) and its explanation; a toolbar (search, status chips, batch import, "add
/// account" menu); a note about a local codex / Copilot login; the card grid (or the loading / empty / no-match states); the usage hint
/// and footnote; the bulk bar while cards are selected; the client policy group; and the detail, import, proxy-login and login sheets.
/// It owns its queries: they are observed while the view is on screen and released when it leaves (<see cref="SubscriptionAccountsModel.Activate"/>).
/// On a provider that is not an <c>oauth-subscription</c> it shows nothing, like the web component.
/// </summary>
public sealed class SubscriptionAccountsView : LiveView
{
    private readonly SubscriptionAccountsModel _model;
    private readonly SubscriptionAccountDetailSheet _detail;
    private readonly AccountImportSheet _import;
    private readonly LoginSheet _login;
    private readonly ProxyLoginSheet _proxyLogin;
    private readonly Dictionary<string, SubscriptionAccountCardView> _cards = new(StringComparer.Ordinal);
    private IDisposable? _activation;
    private ImportMode? _shownImport;

    private SearchField _search = null!;
    private ControlledSegmented _mode = null!;
    private Border _poolCard = null!;
    private WrapPanel _stats = null!;
    private TextBlock _modeDetail = null!;
    private WrapPanel _chips = null!;
    private Control _batchImport = null!;
    private ContentControl _addMenu = null!;
    private TextBlock _localNote = null!;
    private ContentControl _body = null!;
    private AutoFillGrid _grid = null!;
    private ContentControl _usageError = null!;
    private TextBlock _hint = null!;
    private TextBlock _footer = null!;
    private ContentControl _bulk = null!;

    /// <summary>Creates the tab for <paramref name="provider"/> over the app context.</summary>
    public SubscriptionAccountsView(PageContext ctx, ProviderDto provider) : base(ctx)
    {
        _model = new SubscriptionAccountsModel(new AccountsApi(ctx.Api, ctx.Queries), provider, ctx.Feedback, ctx.Clock,
            url => SystemActions.OpenUrlAsync(this, url));
        _detail = new SubscriptionAccountDetailSheet(_model);
        _detail.Closed += (_, _) => { if (_model.DetailId is not null && !_detail.IsOpen) _model.DetailId = null; };
        _import = new AccountImportSheet(_model);
        _import.Closed += (_, _) => { if (!_import.IsOpen) _model.ImportMode = null; };
        _login = new LoginSheet(_model);
        _login.Closed += (_, _) => { if (_model.LoginOpen && !_login.IsOpen) _model.CloseLogin(); };
        _proxyLogin = new ProxyLoginSheet(_model);
        _proxyLogin.Closed += (_, _) => { if (!_proxyLogin.IsOpen) _model.ProxyLoginOpen = false; };
    }

    /// <summary>The state and actions behind the view (scenes and tests drive it).</summary>
    public SubscriptionAccountsModel Model => _model;

    /// <summary>A newer copy of the same provider: the view keeps its state and redraws (see <see cref="SubscriptionAccountsModel.UpdateProvider"/>).</summary>
    public void UpdateProvider(ProviderDto provider) => _model.UpdateProvider(provider);

    /// <summary>The import sheet (scenes fill it).</summary>
    internal AccountImportSheet ImportSheet => _import;

    /// <summary>Completes when the view's queries answered (for the visual tests).</summary>
    public Task WhenSettledAsync() => Context.Queries.WhenIdleAsync();

    /// <inheritdoc />
    protected override void Build()
    {
        if (!_model.IsSubscription)
        {
            Content = null;
            return;
        }
        // Pool line: counts + provider proxy on the left, the switching mode on the right (web card mb-3 px-3 py-2).
        _stats = new WrapPanel { ItemSpacing = 12, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        _mode = new ControlledSegmented(
            [("manual", Loc.T("providers.subscription.policy.manual")), ("failover", Loc.T("providers.subscription.policy.failover")), ("balanced", Loc.T("providers.subscription.policy.balanced"))],
            null);
        Avalonia.Automation.AutomationProperties.SetName(_mode, Loc.T("providers.subscription.policy.switchMode"));
        _mode.Chose += mode => _ = _model.SetSwitchModeAsync(mode);
        _mode.VerticalAlignment = VerticalAlignment.Center;
        var pool = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        pool.Children.Add(_stats);
        Grid.SetColumn(_mode, 1);
        pool.Children.Add(_mode);
        _poolCard = AccountsUi.Card(pool, new Thickness(12, 8));
        _poolCard.Margin = new Thickness(0, 0, 0, 12);
        _modeDetail = AccountsUi.Note("");
        _modeDetail.Margin = new Thickness(4, -4, 4, 12);

        // Toolbar: search, status chips, batch import, add menu (web mb-2 flex-wrap gap-2).
        _search = new SearchField { Width = 208, IsLabelVisible = false, Placeholder = Loc.T("providers.accounts.search"), Label = Loc.T("providers.accounts.search"), Text = _model.Search, VerticalAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) => _model.Search = _search.Text ?? "";
        _chips = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_chips, Loc.T("providers.accounts.filterLabel"));
        _batchImport = AccountsUi.Button(Loc.T("providers.accounts.batchImport"), () => OpenImport(ImportMode.Batch), icon: LucideIconKind.ListPlus);
        _addMenu = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(_search);
        Grid.SetColumn(_chips, 1);
        toolbar.Children.Add(_chips);
        Grid.SetColumn(_batchImport, 2);
        toolbar.Children.Add(_batchImport);
        Grid.SetColumn(_addMenu, 3);
        toolbar.Children.Add(_addMenu);

        _localNote = AccountsUi.Note("");
        _localNote.Margin = new Thickness(4, 0, 4, 8);
        _body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _grid = new AutoFillGrid { MinColumnWidth = 190, ColumnSpacing = 12, RowSpacing = 12 };
        _cards.Clear();
        _usageError = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _hint = AccountsUi.Note("");
        _hint.LineHeight = 17.875;
        _footer = AccountsUi.Note(Loc.T("providers.subscription.footer"));
        _footer.Margin = new Thickness(4, 8, 4, 0);
        _bulk = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        // web: card sticky bottom-3 z-10 — stays at the viewport's bottom while the list runs past it
        var bulkHost = new Astra.Desktop.Components.ProviderDetail.StickyBottom { Offset = 12, Child = _bulk };
        var policy = new SubscriptionPolicyView(_model.Api, _model.Feedback, _model.Provider) { Margin = new Thickness(0, 20, 0, 0) };

        var sheets = new Panel { Children = { _detail, _import, _proxyLogin, _login } };
        Content = new StackPanel
        {
            Children = { _poolCard, _modeDetail, toolbar, _localNote, _body, _usageError, _hint, _footer, bulkHost, policy, sheets },
        };
    }

    /// <inheritdoc />
    protected override void OnShown()
    {
        _activation = _model.Activate();
        _model.PropertyChanged += OnModel;
        foreach (var q in Queries()) q.PropertyChanged += OnQuery;
    }

    /// <inheritdoc />
    protected override void OnHidden()
    {
        _model.PropertyChanged -= OnModel;
        foreach (var q in Queries()) q.PropertyChanged -= OnQuery;
        _activation?.Dispose();
        _activation = null;
    }

    private IEnumerable<INotifyPropertyChanged> Queries()
    {
        yield return _model.Accounts;
        yield return _model.Usage;
        yield return _model.Policy;
        yield return _model.ImportInfo;
        if (_model.LocalCodex is { } c) yield return c;
        if (_model.LocalCopilot is { } p) yield return p;
    }

    private void OnModel(object? sender, PropertyChangedEventArgs e) => RequestRefresh();

    private void OnQuery(object? sender, PropertyChangedEventArgs e) => RequestRefresh();

    /// <inheritdoc />
    protected override void Refresh()
    {
        if (!_model.IsSubscription) return;
        var list = _model.List;
        var now = _model.Now;

        // Pool line.
        var (total, usable, cooling, dead) = _model.Stats;
        _stats.Children.Clear();
        _stats.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.statTotal", ("n", total)), 12, AccountsUi.Inline, FontWeight.Medium));
        _stats.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.statUsable", ("n", usable)), 12, ArcDesignTokenKind.Success));
        if (cooling > 0) _stats.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.statCooling", ("n", cooling)), 12, ArcDesignTokenKind.Warning));
        if (dead > 0) _stats.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.statDead", ("n", dead)), 12, ArcDesignTokenKind.Danger));
        _stats.Children.Add(AccountsUi.Text(_model.ProviderProxyText, 11, ArcDesignTokenKind.TextMuted));
        var policy = _model.Policy.Data;
        _mode.IsVisible = policy is not null;
        _mode.SetValue(policy?.SwitchMode);
        _modeDetail.IsVisible = policy is not null;
        if (policy is not null) _modeDetail.Text = SubscriptionAccountsModel.SwitchModeDetail(policy.SwitchMode);

        // Toolbar.
        _chips.Children.Clear();
        foreach (var (filter, label, count) in AccountCards.Chips(list, now)) _chips.Children.Add(Chip(filter, label, count));
        var info = _model.ImportInfo.Data;
        _batchImport.IsVisible = info?.Supported == true;
        _addMenu.Content = AddMenu();

        var note = _model.LocalNote;
        _localNote.IsVisible = note is not null;
        _localNote.Text = note;

        // Body.
        if (_model.Accounts.IsLoading || (!_model.Accounts.HasData && !_model.Accounts.IsError))
            _body.Content = new Border { Margin = new Thickness(0, 16), Child = new LoadingState(2) };
        else if (list.Count == 0) _body.Content = EmptyButton();
        else
        {
            var cards = _model.Cards();
            if (cards.Count == 0)
            {
                var none = AccountsUi.Text(Loc.T("providers.accounts.noMatch"), 12, ArcDesignTokenKind.TextSecondary);
                none.HorizontalAlignment = HorizontalAlignment.Center;
                _body.Content = new Border { Padding = new Thickness(16, 20), Child = none };
            }
            else
            {
                FillGrid(cards);
                _body.Content = _grid;
            }
        }

        _usageError.Content = _model.Usage.IsError
            ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("providers.usage.error"), Content = AccountsUi.Text(AccountRules.ErrorText(_model.Usage.Error), 13, wrap: true), Margin = new Thickness(0, 12, 0, 0) }
            : null;
        _hint.IsVisible = list.Count > 0;
        _hint.Margin = new Thickness(0, list.Count > 0 ? 12 : 0, 0, 0);
        var zone = _model.Usage.Data?.TimeZone;
        _hint.Text = Loc.T("providers.usage.hint") + (string.IsNullOrEmpty(zone) ? "" : " " + Loc.T("providers.usage.dayHint", ("zone", zone)));
        _bulk.Content = _model.Selecting ? BulkBar() : null;

        // Sheets follow the model.
        _detail.Refresh();
        _proxyLogin.Refresh();
        _login.Refresh();
        if (_model.ImportMode != _shownImport)
        {
            _shownImport = _model.ImportMode;
            if (_model.ImportMode is { } mode && info is not null) _import.Open(mode, info);
            else if (_import.IsOpen) _import.IsOpen = false;
        }
    }

    private void OpenImport(ImportMode mode) => _model.ImportMode = mode;

    private void FillGrid(IReadOnlyList<AccountCardModel> cards)
    {
        // Cards are kept by account id and updated in place, so a poll that changes nothing redraws nothing.
        var ids = cards.Select(c => c.Account.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _cards.Keys.Where(k => !ids.Contains(k)).ToList()) _cards.Remove(gone);
        var ordered = new List<Control>();
        foreach (var card in cards)
        {
            if (!_cards.TryGetValue(card.Account.Id, out var view)) _cards[card.Account.Id] = view = new SubscriptionAccountCardView(_model);
            view.Update(card, _model.UsageOf(card.Account.Id), _model.Selected.Contains(card.Account.Id), _model.Selecting, _model.QuotaPending == card.Account.Id);
            ordered.Add(view);
        }
        if (!_grid.Children.SequenceEqual(ordered))
        {
            _grid.Children.Clear();
            foreach (var c in ordered) _grid.Children.Add(c);
        }
    }

    // A status chip (web: rounded-full px-2.5 py-0.5 text-[11.5px], pressed = fill + primary text, the count muted).
    private Control Chip(AccountFilter filter, string label, int count)
    {
        var pressed = _model.Filter == filter;
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        text.Children.Add(AccountsUi.Text(label, 11.5, pressed ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextSecondary));
        text.Children.Add(AccountsUi.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5, ArcDesignTokenKind.TextMuted));
        var chip = new Border { Padding = new Thickness(10, 2), Child = text, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
        chip.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPill);
        void Paint(bool hover)
        {
            if (pressed || hover) chip.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            else chip.Background = Brushes.Transparent;
        }
        Paint(false);
        chip.PointerEntered += (_, _) => Paint(true);
        chip.PointerExited += (_, _) => Paint(false);
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _model.Filter = filter;
        };
        chip.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            _model.Filter = filter;
        };
        Avalonia.Automation.AutomationProperties.SetName(chip, label);
        return chip;
    }

    private DropdownMenu AddMenu()
    {
        var menu = new DropdownMenu { Label = Loc.T("providers.accounts.add"), Icon = Lucide.Geometry(LucideIconKind.Plus), Alignment = MenuAlignment.End };
        foreach (var item in _model.AddMenu)
        {
            var entry = new MenuEntry { Header = item.Label, HasSeparatorBefore = item.SeparatorBefore };
            LucideIconKind? icon = item.Id switch
            {
                "login" => LucideIconKind.LogIn,
                "codex" or "copilot" => LucideIconKind.Download,
                "apikey" => LucideIconKind.KeyRound,
                _ => null,
            };
            if (icon is { } kind) entry.Icon = Lucide.Geometry(kind);
            var id = item.Id;
            entry.Click += (_, _) => _ = _model.AddAsync(id);
            menu.Items.Add(entry);
        }
        return menu;
    }

    // No account yet: a dashed button that starts a login (web: border-dashed, "none" + "+ log in" in the accent).
    private Control EmptyButton()
    {
        var plus = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { AccountsUi.Icon(LucideIconKind.Plus, 14, ArcDesignTokenKind.Accent), AccountsUi.Text(Loc.T("providers.subscription.login"), 12, ArcDesignTokenKind.Accent) },
        };
        var none = AccountsUi.Text(Loc.T("providers.subscription.none"), 12, ArcDesignTokenKind.TextSecondary);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        // Avalonia's Border draws no dashes: the dashed edge is a rectangle behind the content.
        var edge = new Avalonia.Controls.Shapes.Rectangle { StrokeThickness = 1, StrokeDashArray = [4, 3], RadiusX = 14, RadiusY = 14 };
        edge.Token(Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.Border);
        var box = new Panel
        {
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Children = { edge, new Border { Padding = new Thickness(16, 24), Child = new StackPanel { Spacing = 4, Children = { none, plus } } } },
        };
        box.PointerEntered += (_, _) => edge.Token(Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.Accent);
        box.PointerExited += (_, _) => edge.Token(Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.Border);
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _ = _model.StartLoginAsync();
        };
        Avalonia.Automation.AutomationProperties.SetName(box, Loc.T("providers.subscription.login"));
        return box;
    }

    // The bulk bar (web: a sticky card at the bottom with a shadow); its host keeps it in view (StickyBottom).
    private Control BulkBar()
    {
        var busy = _model.BulkBusy;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.selected", ("n", _model.Selected.Count)), 12, ArcDesignTokenKind.TextSecondary));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.enable"), () => _ = _model.BulkEnableAsync(true), icon: LucideIconKind.Power, enabled: !busy));
        buttons.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.disable"), () => _ = _model.BulkEnableAsync(false), enabled: !busy));
        buttons.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.quotaRefresh"), () => _ = _model.BulkQuotaAsync(), enabled: !busy));
        buttons.Children.Add(AccountsUi.Button(Loc.T("common.delete"), () => _ = _model.BulkDeleteAsync(), icon: LucideIconKind.Trash2, enabled: !busy, foreground: ArcDesignTokenKind.Danger));
        buttons.Children.Add(AccountsUi.Button(Loc.T("providers.accounts.clearSelection"), _model.ClearSelection, ButtonVariant.Ghost));
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        var card = AccountsUi.Card(row, new Thickness(12, 6));
        card.Margin = new Thickness(0, 12, 0, 0);
        card.BoxShadow = BoxShadows.Parse("0 10 15 -3 #1A000000, 0 4 6 -4 #1A000000");
        return card;
    }
}
