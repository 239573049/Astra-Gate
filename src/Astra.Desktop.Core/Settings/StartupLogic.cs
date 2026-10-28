using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Settings;

/// <summary>Settings › General › Start at login (web/src/components/StartupSettings.tsx): the service entry and the app's own login item.</summary>
public static class StartupLogic
{
    /// <summary>The service row's detail: unsupported (why) / only this machine may change it / where the entry lives / what the mechanism does.</summary>
    public static Msg ServiceDetail(AutostartDto s)
    {
        if (!s.Supported || s.Mechanism == "none") return new Msg("settings.startup.unsupported." + (s.Unsupported ?? "platform"));
        if (!s.CanChange) return new Msg("settings.startup.localOnly");
        if (s.Enabled && !string.IsNullOrEmpty(s.Location)) return Msg.Of("settings.startup.entry", ("location", s.Location));
        return new Msg("settings.startup.service." + s.Mechanism);
    }

    /// <summary>Whether the group footer about macOS' Login Items settings shows.</summary>
    public static bool MacFooter(AutostartDto? autostart, bool hasLoginItem, bool isMac)
        => autostart?.Mechanism == "launchd" || (hasLoginItem && isMac);

    /// <summary>The app row's detail.</summary>
    public static Msg AppDetail(Shell.LoginItemState? state)
        => state is { Supported: false } ? new Msg("settings.startup.appUnsupported") : new Msg("settings.startup.appHint");

    /// <summary>Whether the app login item ended up as asked (otherwise a "failed" toast shows).</summary>
    public static bool AppChangeSucceeded(Shell.LoginItemState after, bool wanted) => after.Enabled == wanted;
}
