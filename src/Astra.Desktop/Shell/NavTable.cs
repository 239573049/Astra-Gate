using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Shell;

/// <summary>One sidebar entry (web/src/components/layout/nav.tsx <c>NavItem</c>).</summary>
public sealed record NavEntry(AppRoute Route, string Key, string LabelKey, LucideIconKind Icon);

/// <summary>A sidebar group; <see cref="TitleKey"/> is null for the untitled first group.</summary>
public sealed record NavGroup(string? TitleKey, IReadOnlyList<NavEntry> Items);

/// <summary>The sidebar content, in the same order, grouping and icons as <c>NAV_GROUPS</c> in the web shell.</summary>
public static class NavTable
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new(null,
        [
            new(AppRoute.Overview, "overview", "nav.overview", LucideIconKind.LayoutDashboard),
            new(AppRoute.Requests, "requests", "nav.requests", LucideIconKind.ScrollText),
            new(AppRoute.Traces, "traces", "nav.traces", LucideIconKind.Waypoints),
        ]),
        new("nav.group.config",
        [
            new(AppRoute.Tokens, "tokens", "nav.tokens", LucideIconKind.KeyRound),
            new(AppRoute.Clients, "clients", "nav.clients", LucideIconKind.AppWindow),
            new(AppRoute.Providers, "providers", "nav.providers", LucideIconKind.Server),
            new(AppRoute.Models, "models", "nav.models", LucideIconKind.Boxes),
            new(AppRoute.Plugins, "plugins", "nav.plugins", LucideIconKind.Puzzle),
            new(AppRoute.Privacy, "privacy", "nav.privacy", LucideIconKind.ShieldCheck),
            new(AppRoute.SmartRouting, "smartRouting", "nav.smartRouting", LucideIconKind.Route),
        ]),
        new("nav.group.system",
        [
            new(AppRoute.Settings, "settings", "nav.settings", LucideIconKind.Settings),
        ]),
    ];

    public static NavEntry? ForRoute(AppRoute route)
    {
        foreach (var g in Groups)
            foreach (var i in g.Items)
                if (i.Route == route) return i;
        return null;
    }
}
