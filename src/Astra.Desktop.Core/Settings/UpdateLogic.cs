using Astra.Api.Contracts;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Settings;

/// <summary>A toast the page raises: a message and whether it is an error.</summary>
public readonly record struct ToastMsg(Msg Message, bool IsError = false);

/// <summary>
/// The update rows of Settings › About (web SettingsPage <c>UpdateRows</c>): what each row says, what a finished check or apply toasts,
/// and the channel switch. The server's status supplies the channel, the last check time and feed errors; what an update would bring
/// comes from the desktop host's check when it has one (<see cref="IDesktopHost.LastUpdateCheck"/>), because only the host knows the
/// install track — a packaged app updates as a whole, so its offer is the app's version even when the server is already current.
/// </summary>
public static class UpdateLogic
{
    /// <summary>The version an update would bring: the host's answer when it has one, else the server's status.</summary>
    public static string? AvailableVersion(UpdateStatusDto? status, UpdateSummary? host)
        => host is null ? status?.Available : host.Action == UpdateAction.None ? null : host.Version ?? status?.Available;

    /// <summary>The desktop row: the app's version, plus a warning when the server it talks to runs another version.</summary>
    public static string DesktopVersionDetail(string? appVersion, string? serverVersion)
    {
        if (string.IsNullOrEmpty(appVersion)) return "—";
        if (string.IsNullOrEmpty(serverVersion) || serverVersion == appVersion) return appVersion;
        return $"{appVersion} · {Msg.Of("settings.versionMismatch", ("server", serverVersion)).Text}";
    }

    /// <summary>The auto-check row's detail: only when no feed is configured.</summary>
    public static Msg? AutoCheckDetail(UpdateStatusDto? s)
        => s is { FeedConfigured: false } ? Msg.Of("settings.updateCheckFailed", ("error", "feed-not-configured")) : null;

    /// <summary>The channel the preview switch shows: the saved setting, not the status (that only changes once a feed check returns).</summary>
    public static string? Channel(SettingsDto? settings, UpdateStatusDto? status) => settings?.UpdateChannel ?? status?.Channel;

    /// <summary>The check row's detail: a failed check / the available version (with the apply hint where no app can apply it) / when it last looked.</summary>
    /// <param name="status">The status; null before it loaded.</param>
    /// <param name="checkError">The error text of the last manual check, if it failed.</param>
    /// <param name="canApply">The host can apply updates (the desktop app).</param>
    /// <param name="language">The UI language (for the date).</param>
    /// <param name="host">The desktop host's last check (<see cref="AvailableVersion"/>).</param>
    public static string? CheckDetail(UpdateStatusDto? status, string? checkError, bool canApply, string language, UpdateSummary? host = null)
    {
        if (checkError is not null) return Msg.Of("settings.updateCheckFailed", ("error", checkError)).Text;
        if (AvailableVersion(status, host) is { } version)
        {
            var available = Msg.Of("settings.updateAvailable", ("version", version)).Text;
            return canApply ? available : $"{available} {new Msg("settings.updateApplyHint").Text}";
        }
        if (status?.LastCheckAt is { } at) return Msg.Of("settings.lastChecked", ("time", SettingsFormat.Full(at, language))).Text;
        return null;
    }

    /// <summary>
    /// What a manual check toasts: a failed feed fetch still answers 200 with the previous result plus an error — say it failed, unless the
    /// host's own check found an update.
    /// </summary>
    public static ToastMsg CheckToast(UpdateStatusDto result, UpdateSummary? host = null)
        => AvailableVersion(result, host) is { } available && (result.Error is null || host is { Action: not UpdateAction.None })
            ? new ToastMsg(Msg.Of("settings.updateAvailable", ("version", available)))
            : result.Error is { } error ? new ToastMsg(Msg.Of("settings.updateCheckFailed", ("error", error)), true)
            : new ToastMsg(new Msg("settings.upToDate"));

    /// <summary>What an apply toasts (web <c>runApply</c>).</summary>
    public static Msg ApplyToast(UpdateApplyOutcome outcome) => outcome switch
    {
        UpdateApplyOutcome.Server => new Msg("settings.updateApplied"),
        UpdateApplyOutcome.Desktop => new Msg("settings.updateDesktopRelaunch"),
        UpdateApplyOutcome.Download => new Msg("settings.updateDownloadOpened"),
        UpdateApplyOutcome.Downloading => new Msg("settings.updateDownloading"),
        _ => new Msg("settings.upToDate"),
    };

    /// <summary>Whether an apply refreshes the version and update status (the server was replaced).</summary>
    public static bool ApplyRefreshes(UpdateApplyOutcome outcome) => outcome == UpdateApplyOutcome.Server;

    /// <summary>The channel the preview switch asks for.</summary>
    public static string ChannelFor(bool preview) => preview ? "preview" : "stable";
}
