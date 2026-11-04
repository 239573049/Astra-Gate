using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;

namespace Astra.Desktop.Pages.Providers;

/// <summary>
/// The Providers list route (web <c>/providers</c>, ProvidersPage.tsx): the overview, provider cards or table, the add-provider wizard and the import sheet.
/// The detail route (<see cref="AppRoute.ProviderDetail"/>) is another package.
/// </summary>
public sealed class ProvidersModule : IPageModule
{
    private readonly Action<PageContext, ProviderDto>? _openTest;
    private IProvidersPrefsStore? _prefs;

    /// <summary>The standard module.</summary>
    public ProvidersModule() : this(null)
    {
    }

    /// <summary>
    /// A module whose card "test" button calls <paramref name="openTest"/> (the web opens <c>ProviderTestDialog</c> over the list; that dialog
    /// belongs to the provider detail package, which can hand its opener in here). Null: open the provider's detail page, where testing lives.
    /// </summary>
    public ProvidersModule(Action<PageContext, ProviderDto>? openTest) => _openTest = openTest;

    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Providers, (ctx, param) =>
        {
            // The list view and the charts switch are remembered in desktop-prefs.json (web: localStorage), like the trace canvases.
            ProvidersPrefs.Use(_prefs ??= new DesktopProvidersPrefs(AppServices.Current.Prefs));
            return new ProvidersViewModel(ctx, param, _openTest);
        }, vm => new ProvidersView(vm));

    /// <summary>The <see cref="ProvidersPrefs.Key"/> entry of the shell's prefs file.</summary>
    private sealed class DesktopProvidersPrefs(DesktopPrefs prefs) : IProvidersPrefsStore
    {
        public JsonNode? Get(string key) => prefs.GetExtra(key);

        public void Set(string key, JsonNode? value) => prefs.SetExtra(key, value);
    }
}
