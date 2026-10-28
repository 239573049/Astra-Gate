using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Plugins;

/// <summary>
/// Everything the Plugins page does apart from drawing (web <c>PluginsPage</c>, <c>InstalledList</c>, <c>PluginRow</c>, <c>MarketTab</c>,
/// <c>RuntimeTab</c> and the sheets): which tab is shown and which queries that keeps alive, the debounced market search, the open
/// sheets and followed job, and every action with its confirmation, error toast and follow-up job. The Avalonia view model forwards to it
/// and the views listen to <see cref="NotifyingModel.PropertyChanged"/> and to the queries.
/// </summary>
public sealed class PluginsPageModel : NotifyingModel, IDisposable
{
    private readonly IFeedback _feedback;
    private readonly INavigator _navigator;
    private readonly Func<string, Task<bool>>? _openExternal;
    private readonly Debouncer _debounce;
    private readonly List<IDisposable> _tabTokens = [];
    private IDisposable? _searchToken;
    private IDisposable? _jobToken;
    private bool _active;
    private PluginTab _tab;
    private string _searchText = "";
    private string _debounced = "";
    private KindFilter _kind = KindFilter.All;
    private string? _jobId;
    private bool _adding;
    private bool _importing;
    private PluginDto? _editing;
    private string? _askedLink;
    private string? _link;

    /// <summary>Creates the model.</summary>
    /// <param name="api">The plugins API.</param>
    /// <param name="feedback">Toasts and confirmations.</param>
    /// <param name="navigator">Where "open provider" / "open client" go.</param>
    /// <param name="queries">The shared cache (its clock and dispatcher drive the search debounce).</param>
    /// <param name="route">The route parameter the page was opened with.</param>
    /// <param name="openExternal">Opens an address in the user's browser (the desktop host's <c>OpenPathAsync</c>); null does nothing.</param>
    public PluginsPageModel(PluginsApi api, IFeedback feedback, INavigator navigator, QueryClient queries, PluginRoute route, Func<string, Task<bool>>? openExternal = null)
    {
        _openExternal = openExternal;
        Api = api;
        _feedback = feedback;
        _navigator = navigator;
        _tab = route.Tab;
        _link = route.Install;
        _debounce = new Debouncer(queries.Time, queries.Dispatcher, PluginMarketLogic.SearchDebounce, text => DebouncedQuery = text.Trim());
        api.Plugins.PropertyChanged += OnPlugins;
    }

    private void OnPlugins(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Query<PluginsDto>.Data)) SyncHost();
    }

    /// <summary>The plugins API (queries and mutations).</summary>
    public PluginsApi Api { get; }

    /// <summary>The tab shown.</summary>
    public PluginTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            if (_active) ApplyTab();
        }
    }

    /// <summary>What the search box holds; the registry is asked for it after it rested (<see cref="DebouncedQuery"/>).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _debounce.Change(value);
        }
    }

    /// <summary>The search text once it rested for 400 ms, trimmed.</summary>
    public string DebouncedQuery
    {
        get => _debounced;
        private set
        {
            if (!Set(ref _debounced, value)) return;
            if (_active) ApplySearch();
            Raise(nameof(Searching));
        }
    }

    /// <summary>True while the market shows search results instead of the registry list.</summary>
    public bool Searching => PluginMarketLogic.IsSearching(_debounced);

    /// <summary>The market's kind filter.</summary>
    public KindFilter Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    /// <summary>The job whose sheet is open, null for none.</summary>
    public string? JobId
    {
        get => _jobId;
        set
        {
            if (!Set(ref _jobId, value)) return;
            if (_active) ApplyJob();
        }
    }

    /// <summary>The "install a plugin" sheet is open.</summary>
    public bool Adding
    {
        get => _adding;
        set => Set(ref _adding, value);
    }

    /// <summary>The "import from magpie" sheet is open.</summary>
    public bool Importing
    {
        get => _importing;
        set => Set(ref _importing, value);
    }

    /// <summary>The plugin whose options are being edited, null for none.</summary>
    public PluginDto? Editing
    {
        get => _editing;
        set => Set(ref _editing, value);
    }

    /// <summary>The installed plugins from the last answer (empty before it).</summary>
    public IReadOnlyList<PluginDto> Installed => Api.Plugins.Data?.Plugins ?? [];

    /// <summary>The page became the visible one: watch the queries of the current tab.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        _tabTokens.Add(Api.Plugins.Activate());
        ApplyTab();
        ApplyJob();
        TryAskLink();
    }

    /// <summary>The page was covered or left: stop polling.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        ReleaseTab();
        _searchToken?.Dispose();
        _searchToken = null;
        _jobToken?.Dispose();
        _jobToken = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Deactivate();
        // The plugins query is shared and outlives this model.
        Api.Plugins.PropertyChanged -= OnPlugins;
        _debounce.Dispose();
    }

    // ---- which queries a tab keeps alive (web: each tab's hooks mount with it) ----

    private void ReleaseTab()
    {
        foreach (var t in _tabTokens) t.Dispose();
        _tabTokens.Clear();
    }

    private void ApplyTab()
    {
        ReleaseTab();
        _tabTokens.Add(Api.Plugins.Activate());
        switch (_tab)
        {
            case PluginTab.Installed:
                _tabTokens.Add(Api.Host.Activate());
                _tabTokens.Add(Api.MagpiePreview.Activate());
                break;
            case PluginTab.Market:
                _tabTokens.Add(Api.Market.Activate());
                break;
            case PluginTab.Runtime:
                _tabTokens.Add(Api.Host.Activate());
                break;
        }
        SyncHost();
        ApplySearch();
        TryAskLink();
    }

    // The installed tab asks for the host only when a provider / pi plugin is installed; the runtime tab always does.
    private void SyncHost() => Api.Host.Enabled = _tab == PluginTab.Runtime || Installed.Any(p => p.Kinds.Any(PluginKinds.RunsInHost));

    private void ApplySearch()
    {
        _searchToken?.Dispose();
        _searchToken = null;
        if (_tab == PluginTab.Market && Searching) _searchToken = Api.Search(_debounced).Activate();
    }

    private void ApplyJob()
    {
        _jobToken?.Dispose();
        _jobToken = _jobId is null ? null : Api.Job(_jobId).Activate();
    }

    // A link from the website asks the same question as the Install button; nothing is installed without the answer.
    private void TryAskLink()
    {
        if (_link is null || _tab != PluginTab.Market || !_active || _askedLink == _link) return;
        _askedLink = _link;
        var link = _link;
        _link = null;
        if (PluginSpecs.IsLinkable(link)) _ = InstallAsync(link, "market");
        else _feedback.Error(Loc.T("plugins.linkInvalid"));
    }

    // ---- actions ----

    /// <summary>The header's "update all".</summary>
    public async Task UpdateAllAsync()
    {
        try { JobId = (await Api.UpdateAsync(null).ConfigureAwait(true)).Id; }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>The row menu's "update".</summary>
    public async Task UpdateAsync(PluginDto plugin)
    {
        try { JobId = (await Api.UpdateAsync(plugin.Spec).ConfigureAwait(true)).Id; }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>The row menu's "remove": asks first.</summary>
    public async Task RemoveAsync(PluginDto plugin)
    {
        var ok = await _feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("plugins.removeConfirm", ("name", plugin.ShortName)), Loc.T("plugins.removeDetail"), Loc.T("common.delete"), Destructive: true)).ConfigureAwait(true);
        if (!ok) return;
        try { JobId = (await Api.RemoveAsync(plugin.Spec).ConfigureAwait(true)).Id; }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>The row's switch.</summary>
    /// <returns>False when the change failed (the switch snaps back, like the web's controlled <c>Switch</c>).</returns>
    public async Task<bool> SetEnabledAsync(PluginDto plugin, bool enabled)
    {
        try
        {
            await Api.SetEnabledAsync(plugin.Spec, enabled).ConfigureAwait(true);
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return false;
        }
    }

    /// <summary>Moves a plugin up or down (the middleware chain's order).</summary>
    public async Task MoveAsync(int index, int by)
    {
        try { await Api.ReorderAsync(PluginRows.Move(Installed.Select(p => p.Spec).ToList(), index, by)).ConfigureAwait(true); }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>"Use this plugin for provider X".</summary>
    public async Task PreferAsync(PluginDto plugin, string provider)
    {
        try { await Api.PreferAsync(plugin.Spec, provider).ConfigureAwait(true); }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>Saves the options sheet; true when saved (the sheet closes). A null <paramref name="options"/> clears them.</summary>
    public async Task<bool> SaveOptionsAsync(PluginDto plugin, JsonObject? options)
    {
        try
        {
            await Api.SetOptionsAsync(plugin.Spec, options).ConfigureAwait(true);
            // web: toast(t('plugins.optionsSaved')) — the default (info) tone.
            _feedback.Info(Loc.T("plugins.optionsSaved"));
            Editing = null;
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return false;
        }
    }

    /// <summary>The sheet's "install": a typed spec.</summary>
    public async Task AddAsync(string spec)
    {
        try
        {
            var job = await Api.AddAsync(spec.Trim()).ConfigureAwait(true);
            JobId = job.Id;
            Adding = false;
        }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>The market's Install button: asks first (the question the website link also asks).</summary>
    public async Task InstallAsync(string spec, string source)
    {
        var ok = await _feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("plugins.installConfirm", ("name", spec)), Loc.T("plugins.trustDetail"), Loc.T("plugins.install"))).ConfigureAwait(true);
        if (!ok) return;
        try { JobId = (await Api.AddAsync(spec, source).ConfigureAwait(true)).Id; }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>The runtime tab's Bun download button.</summary>
    public async Task InstallBunAsync()
    {
        // web: the button is disabled while the mutation is pending (installBun.isPending), not only while the server says downloading.
        if (BunPending) return;
        BunPending = true;
        try { JobId = (await Api.InstallBunAsync().ConfigureAwait(true)).Id; }
        catch (Exception e) { Fail(e); }
        finally { BunPending = false; }
    }

    private bool _bunPending;

    /// <summary>The Bun download request is in flight.</summary>
    public bool BunPending
    {
        get => _bunPending;
        private set => Set(ref _bunPending, value);
    }

    /// <summary>The magpie sheet's import button.</summary>
    public async Task ImportMagpieAsync(bool signIns)
    {
        try
        {
            var job = await Api.ImportMagpieAsync(signIns).ConfigureAwait(true);
            JobId = job.Id;
            Importing = false;
        }
        catch (Exception e) { Fail(e); }
    }

    /// <summary>Opens a client's page (an agent plugin's client).</summary>
    public void OpenClient(string kind) => _navigator.Navigate("Clients", kind);

    /// <summary>Opens the Providers page.</summary>
    public void OpenProviders() => _navigator.Navigate("Providers");

    /// <summary>Opens an address in the browser (the community repository link of the runtime tab).</summary>
    public void OpenExternal(string url)
    {
        if (_openExternal is { } open) _ = open(url);
    }

    /// <summary>Opens the market tab (the empty list's call to action).</summary>
    public void BrowseMarket() => Tab = PluginTab.Market;

    private void Fail(Exception e) => _feedback.Error(ErrorText(e));

    /// <summary>The text of an error for a toast or an alert (web <c>errorText</c>).</summary>
    public static string ErrorText(Exception? e) => e?.Message ?? "";
}
