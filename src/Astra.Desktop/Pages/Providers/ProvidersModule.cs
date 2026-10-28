using Astra.Api.Contracts;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Providers;

/// <summary>
/// The Providers list route (web <c>/providers</c>, ProvidersPage.tsx): provider cards, the add-provider wizard and the import sheet.
/// The detail route (<see cref="AppRoute.ProviderDetail"/>) is another package.
/// </summary>
public sealed class ProvidersModule : IPageModule
{
    private readonly Action<PageContext, ProviderDto>? _openTest;

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
        => pages.Add(AppRoute.Providers, (ctx, param) => new ProvidersViewModel(ctx, param, _openTest), vm => new ProvidersView(vm));
}
