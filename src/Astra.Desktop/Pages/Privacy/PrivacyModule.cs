using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Privacy;

/// <summary>The Privacy area (web PrivacyPage, route /privacy[/:tab]); one line in <see cref="AppModules"/>.</summary>
public sealed class PrivacyModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Privacy, (ctx, param) => new PrivacyPageViewModel(ctx, param), vm => new PrivacyView(vm));
}
