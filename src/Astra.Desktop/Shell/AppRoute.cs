namespace Astra.Desktop.Shell;

/// <summary>Every page of the admin shell: the routes of web/src/App.tsx (KeyPage and LoginPage are not replicated, plan avalonia-client §3 D8).</summary>
public enum AppRoute
{
    Overview,
    Requests,
    Traces,
    Tokens,
    TokenDetail,
    Clients,
    Providers,
    ProviderDetail,
    Models,
    ModelDetail,
    Plugins,
    Privacy,
    SmartRouting,
    Settings,
}

/// <summary>
/// Static facts about a route: the web path it mirrors, the i18n key of its title and the sidebar entry that stays
/// highlighted while it is shown (a detail page highlights its list).
/// </summary>
public sealed record RouteInfo(AppRoute Route, string WebPath, string TitleKey, AppRoute NavRoute)
{
    private static readonly RouteInfo[] All =
    [
        new(AppRoute.Overview, "/", "nav.overview", AppRoute.Overview),
        new(AppRoute.Requests, "/requests", "nav.requests", AppRoute.Requests),
        // traces/sessions[/:key] and traces/requests[/:id] are views of the one Traces page: the route's Param carries them.
        new(AppRoute.Traces, "/traces", "nav.traces", AppRoute.Traces),
        new(AppRoute.Tokens, "/tokens", "nav.tokens", AppRoute.Tokens),
        new(AppRoute.TokenDetail, "/tokens/:id", "nav.tokens", AppRoute.Tokens),
        new(AppRoute.Clients, "/clients[/:kind]", "nav.clients", AppRoute.Clients),
        new(AppRoute.Providers, "/providers", "nav.providers", AppRoute.Providers),
        new(AppRoute.ProviderDetail, "/providers/:id", "nav.providers", AppRoute.Providers),
        new(AppRoute.Models, "/models", "nav.models", AppRoute.Models),
        new(AppRoute.ModelDetail, "/models/:id", "nav.models", AppRoute.Models),
        new(AppRoute.Plugins, "/plugins[/:tab]", "nav.plugins", AppRoute.Plugins),
        new(AppRoute.Privacy, "/privacy[/:tab]", "nav.privacy", AppRoute.Privacy),
        new(AppRoute.SmartRouting, "/smart-routing", "nav.smartRouting", AppRoute.SmartRouting),
        new(AppRoute.Settings, "/settings[/:tab]", "nav.settings", AppRoute.Settings),
    ];

    public static IReadOnlyList<RouteInfo> Routes => All;

    public static RouteInfo Of(AppRoute route) => All[(int)route];
}
