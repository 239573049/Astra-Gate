using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Providers;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Providers;

/// <summary>
/// The Providers page (ProvidersPage.tsx): watches the shared provider list, the last-week usage series, the templates and price keys
/// (the wizard's) and the import sources (only while that sheet is open). Opens the wizard for the route parameter <c>new</c> (the app
/// menu's "New provider", the web's <c>?new=1</c>) and for the page command of the same name.
/// </summary>
public sealed class ProvidersViewModel : PageViewModel, IProviderSheetHost
{
    private readonly Action<PageContext, ProviderDto>? _openTest;
    private List<ProviderDto> _items = [];
    private string _range;
    private IDisposable? _rangedToken;
    private bool _active;

    /// <summary>Creates the page.</summary>
    public ProvidersViewModel(PageContext context, string? param, Action<PageContext, ProviderDto>? openTest = null)
        : base(AppRoute.Providers, param, context)
    {
        _openTest = openTest;
        Queries = new ProviderQueries(context.Queries, context.Api, () => context.Clock.Now);
        ProviderList = context.Providers.Providers;
        ImportEnabled(false);
        Observe(ProviderList);
        Observe(Queries.Series);
        WeekStats = Queries.Stats("7d");
        Observe(WeekStats);
        _range = "7d";
        RangedStats = WeekStats;
        OnDispose(() => _rangedToken?.Dispose());
        Observe(Queries.Templates);
        Observe(Queries.PriceKeys);
        Observe(Queries.ImportSources);
        OpenAddOnShow = param == MenuCommandRouter.NewProviderParam;
        BuildHeader();
        Reparse();
        Subscribe(ProviderList, (_, _) => Reparse());
    }

    /// <summary>The page's own queries and writes.</summary>
    public ProviderQueries Queries { get; }

    /// <summary>The ranges the overview offers (web <c>OVERVIEW_RANGES</c>).</summary>
    public static readonly IReadOnlyList<string> Ranges = ["today", "7d", "30d"];

    /// <summary>Per-provider stats over the cards' week (success rate, TTFT, tokens; health).</summary>
    public Query<List<ProviderStatDto>> WeekStats { get; }

    /// <summary>Per-provider stats over <see cref="Range"/> (the overview's figures and charts).</summary>
    public Query<List<ProviderStatDto>> RangedStats { get; private set; }

    /// <summary>The overview's range, one of <see cref="Ranges"/>.</summary>
    public string Range => _range;

    /// <summary>Raised after <see cref="Range"/>, <see cref="View"/> or <see cref="ChartsOpen"/> changed.</summary>
    public event Action? LayoutChanged;

    /// <summary>"grid" or "table", remembered in the app's prefs (web localStorage <c>astra.providers.view</c>).</summary>
    public string View => ProvidersPrefs.View;

    /// <summary>Whether the overview's charts show, remembered (web <c>astra.providers.charts</c>).</summary>
    public bool ChartsOpen => ProvidersPrefs.ChartsOpen;

    /// <summary>Switches the overview's range; the ranged query is watched while the page is active.</summary>
    public void SetRange(string range)
    {
        if (range == _range || !Ranges.Contains(range)) return;
        _range = range;
        _rangedToken?.Dispose();
        _rangedToken = null;
        RangedStats = range == "7d" ? WeekStats : Queries.Stats(range);
        if (_active && range != "7d") _rangedToken = RangedStats.Activate();
        LayoutChanged?.Invoke();
    }

    /// <summary>Shows the cards or the table and remembers it.</summary>
    public void SetView(string view)
    {
        if (view == ProvidersPrefs.View) return;
        ProvidersPrefs.View = view;
        LayoutChanged?.Invoke();
    }

    /// <summary>Opens or closes the overview's charts and remembers it.</summary>
    public void SetChartsOpen(bool open)
    {
        if (open == ProvidersPrefs.ChartsOpen) return;
        ProvidersPrefs.ChartsOpen = open;
        LayoutChanged?.Invoke();
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        base.OnActivated();
        _active = true;
        if (_range != "7d") _rangedToken ??= RangedStats.Activate();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        _active = false;
        _rangedToken?.Dispose();
        _rangedToken = null;
    }

    /// <summary>The shared providers list (key ['providers']).</summary>
    public Query<JsonArray> ProviderList { get; }

    /// <summary>The list decoded to DTOs (empty until loaded).</summary>
    public IReadOnlyList<ProviderDto> Items => _items;

    /// <summary>The wizard and the import sheet of the shown view (set by the view; the visual tests drive them).</summary>
    internal AddProviderSheet? AddSheet { get; set; }

    /// <summary>The import sheet of the shown view.</summary>
    internal ImportProvidersSheet? ImportSheet { get; set; }

    /// <summary>The backup and restore sheet of the shown view.</summary>
    internal ProviderBackupSheet? BackupSheet { get; set; }

    /// <summary>True when the page was opened for the new-provider flow: the view opens the wizard once it is shown.</summary>
    public bool OpenAddOnShow { get; set; }

    /// <summary>Raised when the wizard should open (header button, empty state, menu command).</summary>
    public event Action? AddRequested;

    /// <summary>Raised when the import sheet should open.</summary>
    public event Action? ImportRequested;

    /// <summary>Raised when the backup and restore sheet should open.</summary>
    public event Action? BackupRequested;

    /// <summary>The today's date for the usage columns: the page's clock, not the machine's (tests freeze it).</summary>
    public DateTimeOffset Now => Context!.Clock.Now;

    /// <summary>Feedback (toasts).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <inheritdoc />
    public override string? Subtitle => ProviderList.HasData ? Loc.T("providers.count", ("count", _items.Count)) : null;

    /// <summary>Enables or stops the import-source query (the web's <c>useImportSources(open)</c>).</summary>
    public void ImportEnabled(bool open) => Queries.ImportSources.Enabled = open;

    /// <summary>Asks the view to open the add wizard.</summary>
    public void RequestAdd() => AddRequested?.Invoke();

    /// <summary>Asks the view to open the import sheet.</summary>
    public void RequestImport() => ImportRequested?.Invoke();

    /// <summary>Asks the view to open the backup and restore sheet.</summary>
    public void RequestBackup() => BackupRequested?.Invoke();

    /// <summary>Opens the provider's detail page (web <c>navigate('/providers/:id')</c>), keeping the list below it so Back returns here.</summary>
    public void Open(ProviderDto provider) => Context!.Navigator.Push("ProviderDetail", provider.Id);

    /// <summary>The card's test button.</summary>
    public void Test(ProviderDto provider)
    {
        if (_openTest is not null) _openTest(Context!, provider);
        else Open(provider);
    }

    /// <summary>Navigates to another page (the wizard's "browse the plugin market").</summary>
    public void NavigateTo(string route, string? param) => Context!.Navigator.Navigate(route, param);

    /// <inheritdoc />
    public override bool OnMenuCommand(string command)
    {
        if (command != MenuCommands.NewProvider) return false;
        RequestAdd();
        return true;
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        BuildHeader();
    }

    private void BuildHeader()
    {
        SetHeaderActions(new ActionGroup(8,
            ProviderUi.Button(Loc.T("providers.backup"), LucideIconKind.Archive, onClick: RequestBackup),
            ProviderUi.Button(Loc.T("providers.import"), LucideIconKind.Download, onClick: RequestImport),
            ProviderUi.Button(Loc.T("providers.add"), LucideIconKind.Plus, ButtonVariant.Primary, onClick: RequestAdd)));
    }

    private void Reparse()
    {
        _items = ProviderQueries.ParseProviders(ProviderList.Data);
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Subtitle));
    }
}
