using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The categories of the Settings page's side navigation (the URL segment is the lower-case name). The web keeps General as one long tab;
/// the desktop splits it into General, Gateway, Models, Network, Usage, Logging and About so each page is short (compact layout).
/// </summary>
public enum SettingsTab { General, Gateway, Models, Network, Usage, Logging, Notifications, Remote, Tray, About }

/// <summary>
/// Settings (web/src/pages/SettingsPage.tsx): the web's General tab split into categories, Notifications and Remote access, plus Tray panel
/// in the desktop app (<c>/settings/:tab</c>). The tab is local state, started from the route parameter; the shell's <c>settings:tray</c> command
/// selects the tray tab on a page that is already open. Each tab's view observes its own queries while it is shown; this model
/// watches the General tab's (the page opens there).
/// </summary>
public sealed class SettingsPageViewModel : PageViewModel
{
    private SettingsTab _tab;

    /// <summary>Creates the page over the app context.</summary>
    /// <param name="context">The app context.</param>
    /// <param name="param">The tab segment ("gateway", "models", "network", "usage", "logging", "notifications", "remote", "tray", "about"; anything else is General).</param>
    /// <param name="appearance">Theme, accent and language.</param>
    /// <param name="trayAvailable">The host can show a tray panel (the desktop app); without it the Tray tab is hidden and a "tray" route falls back to General.</param>
    public SettingsPageViewModel(PageContext context, string? param, IAppearance appearance, bool trayAvailable = true) : base(AppRoute.Settings, param, context)
    {
        Appearance = appearance;
        TrayAvailable = trayAvailable;
        Api = new SettingsApi(context.Api, context.Queries, context.Settings, context.Providers, context.Tokens);
        _tab = TabOf(param, trayAvailable);
        Observe(Api.Settings);
        Observe(Api.Version);
        Observe(Api.Auth);
        Observe(Api.Listen);
        Observe(Api.Autostart);
        Observe(Api.Update);
        Observe(Api.Tunnel);
    }

    /// <summary>The queries and writes of the page.</summary>
    public SettingsApi Api { get; }

    /// <summary>Theme, accent and language.</summary>
    public IAppearance Appearance { get; }

    /// <summary>Whether the Tray tab exists.</summary>
    public bool TrayAvailable { get; }

    /// <summary>The selected tab.</summary>
    public SettingsTab Tab
    {
        get => _tab;
        set => SetProperty(ref _tab, value);
    }

    /// <summary>The tab a route segment names.</summary>
    public static SettingsTab TabOf(string? segment, bool trayAvailable) => segment switch
    {
        "gateway" => SettingsTab.Gateway,
        "models" => SettingsTab.Models,
        "network" => SettingsTab.Network,
        "usage" => SettingsTab.Usage,
        "logging" => SettingsTab.Logging,
        "about" => SettingsTab.About,
        "notifications" => SettingsTab.Notifications,
        "remote" => SettingsTab.Remote,
        "tray" when trayAvailable => SettingsTab.Tray,
        _ => SettingsTab.General,
    };

    /// <summary>The route segment of a tab (General has none).</summary>
    public static string? SegmentOf(SettingsTab tab) => tab switch
    {
        SettingsTab.Gateway => "gateway",
        SettingsTab.Models => "models",
        SettingsTab.Network => "network",
        SettingsTab.Usage => "usage",
        SettingsTab.Logging => "logging",
        SettingsTab.About => "about",
        SettingsTab.Notifications => "notifications",
        SettingsTab.Remote => "remote",
        SettingsTab.Tray => "tray",
        _ => null,
    };

    /// <inheritdoc />
    public override string? Subtitle => null;

    /// <summary>The page lays out its own scrolling: the side navigation stays put while the category's content scrolls.</summary>
    public override bool HostScrolls => false;

    /// <inheritdoc />
    public override bool OnMenuCommand(string command)
    {
        if (command != MenuCommands.SettingsTray || !TrayAvailable) return false;
        Tab = SettingsTab.Tray;
        return true;
    }
}
