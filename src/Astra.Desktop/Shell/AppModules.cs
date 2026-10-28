using Astra.Desktop.Pages.Clients;
using Astra.Desktop.Pages.Models;
using Astra.Desktop.Pages.Overview;
using Astra.Desktop.Pages.Plugins;
using Astra.Desktop.Pages.Privacy;
using Astra.Desktop.Pages.ProviderDetail;
using Astra.Desktop.Pages.Providers;
using Astra.Desktop.Pages.Requests;
using Astra.Desktop.Pages.Settings;
using Astra.Desktop.Pages.SmartRouting;
using Astra.Desktop.Pages.Tokens;
using Astra.Desktop.Pages.Traces;

namespace Astra.Desktop.Shell;

/// <summary>
/// The one place that lists the app's page modules, one line each, in route-area order. Adding an area's pages = adding its module here
/// (an explicit list instead of reflection or assembly scanning: AOT, and a visible diff). Routes without a module keep the placeholder page.
/// </summary>
public static class AppModules
{
    /// <summary>Every module the app registers at startup.</summary>
    public static IReadOnlyList<IPageModule> All { get; } =
    [
        new ClientsModule(),
        new ModelsModule(),
        new OverviewModule(),
        new PluginsModule(),
        new PrivacyModule(),
        new ProviderDetailModule(),
        new ProvidersModule(Astra.Desktop.Components.ProviderDetail.ProviderTestDialog.Opener),
        new RequestsModule(),
        new SettingsModule(),
        new SmartRoutingModule(),
        new TokensModule(),
        new TracesModule(),
    ];
}
