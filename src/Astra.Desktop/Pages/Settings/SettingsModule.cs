using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Settings;

/// <summary>The Settings area (web SettingsPage, route /settings[/:tab]); one line in <see cref="AppModules"/>.</summary>
public sealed class SettingsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Settings,
            (ctx, param) => new SettingsPageViewModel(ctx, param, new AppAppearance(ctx.Host, AppServices.Current.Theme, AppServices.Current.Prefs)),
            vm => new SettingsView(vm));
}
