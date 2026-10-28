using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.ProviderDetail;

/// <summary>
/// One provider's page (web <c>ProviderDetailPage</c>, web/src/pages/ProviderDetailPage.tsx, route <c>/providers/:id</c>): the models tab (with the
/// model mapping, adding, the remote list and the per-model edit sheet), the accounts / API-key tab (usage and the balance query for API-key
/// providers) and the settings tab. The header carries the back button, "test connection" and the "more" menu (duplicate, template update,
/// template switch, delete). The route parameter is the provider id.
/// </summary>
public sealed class ProviderDetailPage : PageViewModel
{
    private readonly PageContext _ctx;
    private ProviderTab _tab = ProviderTab.Models;

    /// <summary>Builds the page for the provider id in <paramref name="param"/>.</summary>
    public ProviderDetailPage(PageContext context, string? param) : base(AppRoute.ProviderDetail, param, context)
    {
        _ctx = context;
        Id = param ?? "";
        Api = new ProviderDetailApi(context.Queries, context.Api, Id);
        Observe(Api.Provider);
        Observe(Api.Models);
        Subscribe(Api.Provider, OnProvider);
        BuildHeader();
    }

    /// <summary>The provider id.</summary>
    public string Id { get; }

    /// <summary>Set by the view: whether its settings tab holds edits (the form lives in the tab).</summary>
    internal Func<bool>? UnsavedSettings { get; set; }

    /// <inheritdoc />
    public override bool HoldsUnsavedWork => UnsavedSettings?.Invoke() == true;

    /// <summary>The page's queries and writes.</summary>
    public ProviderDetailApi Api { get; }

    /// <summary>The app context (views reach toasts, navigation and the clock through it).</summary>
    public PageContext Ctx => _ctx;

    /// <summary>The selected tab.</summary>
    public ProviderTab Tab
    {
        get => _tab;
        set => SetProperty(ref _tab, value);
    }

    /// <summary>The shown view (scenes and tests drive its sheets).</summary>
    internal ProviderDetailView? View { get; set; }

    /// <summary>The provider once loaded.</summary>
    public ProviderDto? Provider => Api.Provider.Data;

    /// <summary>Raised when a header action asks for a sheet (the view owns the sheets).</summary>
    public event Action<ProviderSheetKind>? SheetRequested;

    /// <inheritdoc />
    /// <remarks>The web shows an empty title while loading; the list's title stands in so the header (and the smoke check) always has one.</remarks>
    public override string Title => Api.Provider.HasData && !Api.Provider.IsError ? Api.Provider.Data!.Name : Loc.T("nav.providers");

    /// <inheritdoc />
    public override string? Subtitle => Api.Provider.HasData && !Api.Provider.IsError ? ProviderDetailLogic.Subtitle(Api.Provider.Data!) : null;

    /// <summary>web <c>navigate('/providers')</c>.</summary>
    public void Back() => _ctx.Navigator.Navigate("Providers");

    /// <summary>Opens a header sheet.</summary>
    public void Request(ProviderSheetKind kind) => SheetRequested?.Invoke(kind);

    /// <summary>Duplicates the provider and opens the copy (web <c>duplicate.mutate</c>).</summary>
    public async Task DuplicateAsync()
    {
        try
        {
            if (await Api.DuplicateAsync() is { } copy) _ctx.Navigator.Navigate("ProviderDetail", copy.Id);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            _ctx.Feedback.Error(e.Message);
        }
    }

    /// <summary>Asks, then deletes the provider and returns to the list (web <c>remove</c>); a 409 names the bound clients.</summary>
    public async Task DeleteAsync()
    {
        if (Provider is not { } p) return;
        var ok = await _ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("providers.deleteConfirm", ("name", p.Name)), Loc.T("providers.deleteDetail"), Loc.T("common.delete"), null, true));
        if (!ok) return;
        try
        {
            await Api.DeleteAsync();
            _ctx.Feedback.Success(Loc.T("providers.deleted"));
            _ctx.Navigator.Navigate("Providers");
        }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            _ctx.Feedback.Error(ProviderDetailLogic.DeleteError(e));
        }
    }

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        // tabs and sheets start their own queries once they attach: give them a few rounds
        for (var i = 0; i < 4; i++)
        {
            await _ctx.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Leading();
        BuildHeader();
    }

    /// <inheritdoc />
    protected override void OnActivated() => Leading();

    /// <summary>
    /// web <c>BackButton</c> (to the list). When the page was pushed over the list the shell already draws its back button, which goes to the same
    /// place, so the page adds its own only when there is nothing to go back to (opened by <c>Navigate</c>, e.g. after "duplicate").
    /// </summary>
    private void Leading()
        => SetHeaderLeading(_ctx.Navigator.CanGoBack ? null : PdUi.Button(null, PdUi.Look.Glass, LucideIconKind.ArrowLeft, click: Back, name: Loc.T("common.back")));

    private void OnProvider(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Provider));
        BuildHeader();
    }

    private bool _headerFor;
    private string? _headerSig;

    /// <summary>"Test connection" and the "more" menu, once the provider is loaded (web <c>actions={p && …}</c>).</summary>
    private void BuildHeader()
    {
        var p = Api.Provider.IsError ? null : Provider;
        var sig = p is null ? null : $"{p.TemplateId}|{p.AuthScheme}|{Loc.Language}";
        if (_headerFor == (p is not null) && sig == _headerSig && p is not null) return;
        _headerFor = p is not null;
        _headerSig = sig;
        if (p is null)
        {
            SetHeaderActions(null);
            return;
        }
        var test = PdUi.Button(Loc.T("providers.test"), PdUi.Look.Glass, LucideIconKind.FlaskConical, click: () => Request(ProviderSheetKind.Test));
        var menu = new DropdownMenu
        {
            Label = Loc.T("common.more"), Icon = Lucide.Geometry(LucideIconKind.Ellipsis), Alignment = MenuAlignment.End,
        };
        menu.Items.Add(Entry(Loc.T("providers.duplicate"), LucideIconKind.Copy, () => _ = DuplicateAsync()));
        menu.Items.Add(Entry(Loc.T("providers.templateUpdate"), LucideIconKind.Sparkles, () => Request(ProviderSheetKind.TemplateUpdate), enabled: !string.IsNullOrEmpty(p.TemplateId)));
        menu.Items.Add(Entry(Loc.T("providers.templateSwitch"), LucideIconKind.ArrowLeftRight, () => Request(ProviderSheetKind.TemplateSwitch), enabled: TemplateSwitchLogic.CanSwitch(p)));
        menu.Items.Add(Entry(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = DeleteAsync(), destructive: true, separator: true));
        SetHeaderActions(new ActionGroup(8, test, menu));
    }

    private static MenuEntry Entry(string label, LucideIconKind icon, Action select, bool enabled = true, bool destructive = false, bool separator = false)
    {
        var entry = new MenuEntry { Header = label, Icon = Lucide.Geometry(icon), IsEnabled = enabled, IsDestructive = destructive, HasSeparatorBefore = separator };
        entry.Click += (_, _) => select();
        return entry;
    }
}

/// <summary>The sheets the header opens.</summary>
public enum ProviderSheetKind
{
    /// <summary>The connection test dialog.</summary>
    Test,

    /// <summary>Template update.</summary>
    TemplateUpdate,

    /// <summary>Template switch.</summary>
    TemplateSwitch,
}
