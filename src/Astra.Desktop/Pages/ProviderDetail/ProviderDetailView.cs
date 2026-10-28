using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Accounts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TabItem = TKCode.Arc.TabItem;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.ProviderDetail;

/// <summary>
/// The body of the provider detail page (web <c>ProviderDetailPage</c> render): the error alert, the loading skeleton, or the three tabs in a
/// centered column at most 860 px wide (web <c>mx-auto max-w-[860px]</c>), plus the header's sheets. A tab is rebuilt when the provider object
/// changes (a save, a refetch) — the web's forms reset on the same signal — and keeps the selected tab.
/// </summary>
public sealed class ProviderDetailView : UserControl
{
    private const double MaxWidthPx = 860;

    private readonly ProviderDetailPage _page;
    private readonly Border _host = new() { MaxWidth = MaxWidthPx, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TemplateUpdateSheet _updateSheet;
    private readonly TemplateSwitchSheet _switchSheet;
    private ProviderTestDialog? _testDialog;
    private Tabs? _tabs;
    private ProviderDto? _shown;
    private bool _quiet;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public ProviderDetailView(ProviderDetailPage page)
    {
        _page = page;
        page.View = this;
        _updateSheet = new TemplateUpdateSheet(page.Ctx, page.Api);
        _switchSheet = new TemplateSwitchSheet(page.Ctx, page.Api);
        Content = new Panel { Children = { _host, _updateSheet, _switchSheet } };
        Rebuild(force: true);
    }

    /// <summary>The models tab of the shown provider (scenes drive its sheets).</summary>
    internal ModelsTabView? ModelsTab { get; private set; }

    /// <summary>The API-key tab (scenes open the quota test result).</summary>
    internal ApiKeyTabView? ApiKeyTab { get; private set; }

    /// <summary>The accounts tab's view (subscription / plugin accounts, or the API-key tab).</summary>
    internal Control? AccountsView => _accountsHost.Content as Control;

    /// <summary>The settings tab.</summary>
    internal SettingsTabView? SettingsTab { get; private set; }

    /// <summary>The connection test dialog once opened.</summary>
    internal ProviderTestDialog? TestDialog => _testDialog;

    /// <summary>The template switch sheet.</summary>
    internal TemplateSwitchSheet SwitchSheet => _switchSheet;

    /// <summary>The template update sheet.</summary>
    internal TemplateUpdateSheet UpdateSheet => _updateSheet;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page.Api.Provider.PropertyChanged += OnChanged;
        _page.SheetRequested += OnSheet;
        _page.PropertyChanged += OnPage;
        _page.UnsavedSettings = () => SettingsTab?.IsDirty == true;
        Loc.LanguageChanged += OnLanguage;
        Rebuild(force: false);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _page.Api.Provider.PropertyChanged -= OnChanged;
        _page.SheetRequested -= OnSheet;
        _page.PropertyChanged -= OnPage;
        _page.UnsavedSettings = null;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Dispatcher.UIThread.Post(() => Rebuild(force: true));

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Rebuild(force: false);

    private void OnPage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderDetailPage.Tab)) SelectTab(_page.Tab);
    }

    /// <summary>Opens a header sheet.</summary>
    public void OnSheet(ProviderSheetKind kind)
    {
        if (_page.Provider is not { } p) return;
        switch (kind)
        {
            case ProviderSheetKind.Test:
                _testDialog ??= new ProviderTestDialog(_page.Ctx, p, _page.Api);
                if (_testDialog.Parent is null) ((Panel)Content!).Children.Add(_testDialog);
                _testDialog.Provider = p;
                _testDialog.Open();
                break;
            case ProviderSheetKind.TemplateUpdate:
                _updateSheet.Show(p);
                break;
            case ProviderSheetKind.TemplateSwitch:
                _switchSheet.Show(p);
                break;
        }
    }

    private void Rebuild(bool force)
    {
        var q = _page.Api.Provider;
        // web: `if (provider.isError)` replaces the whole page with the alert, also when an older copy is cached
        if (q.IsError)
        {
            _shown = null;
            _shownSig = null;
            _tabs = null;
            // web: the alert alone in the page, not in the 860 px column
            _host.MaxWidth = double.PositiveInfinity;
            _host.Child = new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message, VerticalAlignment = VerticalAlignment.Top };
            return;
        }
        _host.MaxWidth = MaxWidthPx;
        if (q.Data is not { } p)
        {
            _host.Child = new LoadingState(4) { Margin = new Thickness(0, 24, 0, 0) };
            return;
        }
        if (!force && ReferenceEquals(p, _shown)) return;
        // react-query's structural sharing: a refetch that brings the same provider keeps the same object, so nothing re-renders and no form
        // resets. Compare the JSON to get the same effect.
        var sig = Astra.Core.Json.SerializeApi(p);
        if (!force && _tabs is not null && sig == _shownSig)
        {
            _shown = p;
            return;
        }
        var prev = _shown;
        _shown = p;
        _shownSig = sig;
        if (!force && _tabs is not null && prev is not null && prev.Id == p.Id && prev.AuthScheme == p.AuthScheme) UpdateTabs(p);
        else BuildTabs(p);
    }

    // Each tab's content sits in a host that stays with its Tabs: Arc Tabs copies a tab's content when it is selected, so replacing a TabItem's
    // content later would not show; replacing the host's does. A new Tabs gets new hosts (the old ones still have a parent).
    private ContentControl _modelsHost = Host();
    private ContentControl _accountsHost = Host();
    private ContentControl _settingsHost = Host();

    private static ContentControl Host() => new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private string? _shownSig;

    private void BuildTabs(ProviderDto p)
    {
        _modelsHost = Host();
        _accountsHost = Host();
        _settingsHost = Host();
        // Web: arc tabs .content margin-top 16 plus the page's TabsContent pt-4 (ProviderDetailPage.tsx) = 32 below the tab list.
        _tabs = new Tabs { ContentMargin = new Thickness(0, 32, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(_tabs, p.Name);
        _modelsHost.Content = ModelsTab = new ModelsTabView(_page.Ctx, _page.Api, p);
        Control accounts;
        ApiKeyTab = null;
        if (p.AuthScheme == "plugin") accounts = new PluginAccountsView(_page.Ctx, p);
        else if (p.AuthScheme == "oauth-subscription") accounts = new SubscriptionAccountsView(_page.Ctx, p);
        else accounts = ApiKeyTab = new ApiKeyTabView(_page.Ctx, _page.Api, p);
        _accountsHost.Content = accounts;
        _settingsHost.Content = SettingsTab = new SettingsTabView(_page.Ctx, _page.Api, p);
        _tabs.Items.Add(new TabItem { Tag = ProviderTab.Models, Header = Loc.T("providers.tab.models"), Content = _modelsHost });
        _tabs.Items.Add(new TabItem { Tag = ProviderTab.Accounts, Header = ProviderDetailLogic.AccountsTabLabel(p), Content = _accountsHost });
        _tabs.Items.Add(new TabItem { Tag = ProviderTab.Settings, Header = Loc.T("providers.tab.settings"), Content = _settingsHost });
        SelectTab(_page.Tab);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_quiet || _tabs.SelectedItem is not TabItem { Tag: ProviderTab tab }) return;
            _page.Tab = tab;
        };
        _host.Child = _tabs;
    }

    /// <summary>
    /// A newer copy of the shown provider (web: the page re-renders with the new object). The models tab and the accounts / API-key tab keep their
    /// state and take the copy; the settings tab starts over from it, like the web's form (<c>useEffect([base])</c>).
    /// </summary>
    private void UpdateTabs(ProviderDto p)
    {
        if (_tabs is not null) Avalonia.Automation.AutomationProperties.SetName(_tabs, p.Name);
        ModelsTab?.ProviderChanged();
        switch (_accountsHost.Content)
        {
            case SubscriptionAccountsView s: s.UpdateProvider(p); break;
            case PluginAccountsView pl: pl.UpdateProvider(p); break;
            case ApiKeyTabView k: k.UpdateProvider(p); break;
        }
        _settingsHost.Content = SettingsTab = new SettingsTabView(_page.Ctx, _page.Api, p);
    }

    private void SelectTab(ProviderTab tab)
    {
        if (_tabs is null) return;
        var index = (int)tab;
        if (_tabs.SelectedIndex == index) return;
        _quiet = true;
        try { _tabs.SelectTab(index); }
        finally { _quiet = false; }
    }
}
