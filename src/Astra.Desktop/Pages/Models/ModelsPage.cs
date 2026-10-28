using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using SearchField = TKCode.Arc.SearchField;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// The model catalog page (web <c>ModelsPage</c>, web/src/pages/ModelsPage.tsx): a searchable, vendor-filterable table of the system
/// models, "sync model catalog" (models.dev preview and apply) and "new model". The list is a server-side filtered query whose search
/// text is debounced by 250 ms; while a new filter loads the previous rows stay on screen (react-query <c>placeholderData: prev</c>).
/// </summary>
public sealed class ModelsPage : PageViewModel
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly PageContext _ctx;
    private readonly Query<List<ModelDto>> _all;
    private Query<List<ModelDto>> _list;
    private IDisposable? _listToken;
    private bool _active;
    private string _search = "";
    private string _debounced = "";
    private string _vendor = "";
    private ITimer? _timer;
    private List<ModelDto>? _models;
    private bool _isCreating;
    private bool _isSyncOpen;
    private readonly SearchField _searchBox;
    private readonly Select _vendorBox;

    /// <summary>Builds the page and its header controls over the app context.</summary>
    public ModelsPage(PageContext context, string? param) : base(AppRoute.Models, param, context)
    {
        _ctx = context;
        _all = ModelQueries.ListQuery(context.Queries, context.Api, null, null);
        _list = _all;
        _models = _all.Data;
        Observe(_all);

        _searchBox = new SearchField { Placeholder = Loc.T("models.search"), Width = 208, ClearText = Loc.T("common.clearSearch"), Label = Loc.T("models.search"), IsLabelVisible = false, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_searchBox, Loc.T("models.search"));
        _searchBox.TextChanged += (_, _) => Search = _searchBox.Text ?? "";
        _vendorBox = new Select { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 108 };
        Avalonia.Automation.AutomationProperties.SetName(_vendorBox, Loc.T("models.allVendors"));
        _vendorBox.SelectionChanged += (_, _) =>
        {
            if (_vendorBox.SelectedValue is string v) Vendor = v;
        };
        FillVendors();
        SetHeaderActions(BuildActions());
    }

    /// <summary>The search box's text (not yet debounced).</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (_search == value) return;
            _search = value;
            OnPropertyChanged();
            _timer?.Dispose();
            // 250 ms after the last keystroke, on the UI thread (web useDebounced)
            _timer = _ctx.Queries.Time.CreateTimer(_ => _ctx.Queries.Dispatcher.Post(() => ApplyFilter(_search, _vendor)), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The vendor filter ("" = all).</summary>
    public string Vendor
    {
        get => _vendor;
        set
        {
            if (_vendor == value) return;
            _vendor = value;
            OnPropertyChanged();
            ApplyFilter(_debounced, value);
        }
    }

    /// <summary>The catalog rows to show: the current filter's, or the previous filter's while the new one loads; null before the first answer.</summary>
    public List<ModelDto>? Models => _models;

    /// <summary>The first load is running (nothing to show yet).</summary>
    public bool IsLoading => _models is null && !IsError;

    /// <summary>The list query failed and there is nothing to show.</summary>
    public bool IsError => _models is null && _list.IsError;

    /// <summary>The failure's message.</summary>
    public string? ErrorText => _list.Error?.Message;

    /// <summary>The "new model" sheet is open.</summary>
    public bool IsCreating
    {
        get => _isCreating;
        set => SetProperty(ref _isCreating, value);
    }

    /// <summary>The sync sheet is open.</summary>
    public bool IsSyncOpen
    {
        get => _isSyncOpen;
        set => SetProperty(ref _isSyncOpen, value);
    }

    /// <summary>The search box in the header (for the visual harness, which types into it).</summary>
    internal SearchField SearchBox => _searchBox;

    /// <summary>The vendor options (all vendors of the unfiltered catalog).</summary>
    public List<string> Vendors => ModelCatalog.Vendors(_all.Data);

    /// <inheritdoc />
    public override string? Subtitle => _models is { } m ? Loc.T("models.count", ("count", m.Count)) : null;

    /// <summary>Refetches after a failure.</summary>
    public void Retry() => _ = _list.RefetchAsync();

    /// <summary>The detail page of a model.</summary>
    public void Open(string id) => _ctx.Navigator.Navigate("ModelDetail", id);

    /// <summary>Focuses the search box (menu command <c>find</c>).</summary>
    public override bool OnMenuCommand(string command)
    {
        if (command != MenuCommands.Find) return false;
        _searchBox.Focus();
        return true;
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _active = true;
        // the shared queries outlive the page: listen only while it is shown, and catch up with what arrived meanwhile
        _all.PropertyChanged += OnAllChanged;
        _list.PropertyChanged += OnQueryChanged;
        _listToken ??= _list.Activate();
        if (_list.HasData && !ReferenceEquals(_models, _list.Data))
        {
            _models = _list.Data;
            RaiseState();
        }
        FillVendors();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _active = false;
        _all.PropertyChanged -= OnAllChanged;
        _list.PropertyChanged -= OnQueryChanged;
        _listToken?.Dispose();
        _listToken = null;
        _timer?.Dispose();
        _timer = null;
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _searchBox.Placeholder = Loc.T("models.search");
        FillVendors();
        SetHeaderActions(BuildActions());
        OnPropertyChanged(nameof(Models));
    }

    private void ApplyFilter(string search, string vendor)
    {
        _debounced = search;
        var next = ModelQueries.ListQuery(_ctx.Queries, _ctx.Api, search, vendor);
        if (ReferenceEquals(next, _list)) return;
        _list.PropertyChanged -= OnQueryChanged;
        _listToken?.Dispose();
        _listToken = null;
        _list = next;
        if (_active) _list.PropertyChanged += OnQueryChanged;
        // placeholderData: previous rows stay until the new query has data of its own
        if (_list.HasData) _models = _list.Data;
        if (_active) _listToken = _list.Activate();
        RaiseState();
    }

    private void OnQueryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _list)) return;
        if (e.PropertyName is nameof(Query<List<ModelDto>>.Data) or nameof(Query<List<ModelDto>>.HasData)) _models = _list.HasData ? _list.Data : _models;
        RaiseState();
    }

    private void OnAllChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Query<List<ModelDto>>.Data)) Dispatcher.UIThread.Post(FillVendors);
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(Subtitle));
    }

    private void FillVendors()
    {
        var options = new List<SelectOption> { new("", Loc.T("models.allVendors")) };
        options.AddRange(Vendors.Select(v => new SelectOption(v, v)));
        _vendorBox.ItemsSource = options;
        _vendorBox.SelectedValue = options.Any(o => Equals(o.Value, _vendor)) ? _vendor : "";
    }

    private Control BuildActions()
    {
        var sync = PricingUi.Button(Loc.T("models.sync"), PricingUi.Look.Glass, LucideIconKind.CloudDownload, click: () => IsSyncOpen = true);
        var create = PricingUi.Button(Loc.T("models.new"), PricingUi.Look.Primary, LucideIconKind.Plus, click: () => IsCreating = true);
        // web: the page's actions are a flex row with gap-2; the search box and vendor select are separate items
        return new ActionGroup(8, DetachedSearch(), DetachedVendor(), sync, create);
    }

    // the controls are created once (focus, text) and re-parented when the actions are rebuilt for a language change
    private Control DetachedSearch()
    {
        if (_searchBox.Parent is Panel p) p.Children.Remove(_searchBox);
        return _searchBox;
    }

    private Control DetachedVendor()
    {
        if (_vendorBox.Parent is Panel p) p.Children.Remove(_vendorBox);
        return _vendorBox;
    }
}
