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

    /// <summary>The shared providers list (key ['providers']).</summary>
    public Query<JsonArray> ProviderList { get; }

    /// <summary>The list decoded to DTOs (empty until loaded).</summary>
    public IReadOnlyList<ProviderDto> Items => _items;

    /// <summary>The wizard and the import sheet of the shown view (set by the view; the visual tests drive them).</summary>
    internal AddProviderSheet? AddSheet { get; set; }

    /// <summary>The import sheet of the shown view.</summary>
    internal ImportProvidersSheet? ImportSheet { get; set; }

    /// <summary>True when the page was opened for the new-provider flow: the view opens the wizard once it is shown.</summary>
    public bool OpenAddOnShow { get; set; }

    /// <summary>Raised when the wizard should open (header button, empty state, menu command).</summary>
    public event Action? AddRequested;

    /// <summary>Raised when the import sheet should open.</summary>
    public event Action? ImportRequested;

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
