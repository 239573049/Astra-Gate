using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Core.Clients;

/// <summary>The install / update button of a client's header row (web <c>InstallActions</c>).</summary>
public enum InstallButton
{
    /// <summary>No button.</summary>
    None,

    /// <summary>"Running…" with a spinner: opens the log.</summary>
    Running,

    /// <summary>"Update to v": confirms and runs the update.</summary>
    Update,

    /// <summary>"Shadowed": scrolls to the alert that explains the PATH problem.</summary>
    Shadowed,

    /// <summary>"Install": confirms and runs the install.</summary>
    Install,

    /// <summary>"Download": opens the homepage of a client installed by hand.</summary>
    Download,
}

/// <summary>What the install sheet was opened for (web <c>InstallSheetMode</c>).</summary>
public enum InstallSheetMode
{
    /// <summary>Confirm, then run the install command.</summary>
    Install,

    /// <summary>Confirm, then run the update command.</summary>
    Update,

    /// <summary>Confirm (saying a taken-over config is restored first), then run the uninstall command.</summary>
    Uninstall,

    /// <summary>Only watch the current or last run.</summary>
    Log,
}

/// <summary>Which footer the install sheet shows.</summary>
public enum InstallFooter
{
    /// <summary>Cancel + start (or "copy sudo command" when the system cannot ask for admin rights).</summary>
    Confirm,

    /// <summary>Stop (unless elevated) + close.</summary>
    Running,

    /// <summary>Done (+ retry as admin).</summary>
    Finished,
}

/// <summary>The toast after a run ended (web: the effect on <c>data.state</c>).</summary>
public enum InstallOutcome
{
    /// <summary>Nothing ended.</summary>
    None,

    /// <summary>Ended without success (no toast, but the list is refreshed).</summary>
    Ended,

    /// <summary>Succeeded but did not take effect (a hint): neutral toast.</summary>
    NotEffective,

    /// <summary>Install succeeded.</summary>
    Installed,

    /// <summary>Update succeeded.</summary>
    Updated,

    /// <summary>Uninstall succeeded.</summary>
    Uninstalled,
}

/// <summary>
/// The install / update / uninstall rules of web/src/pages/ClientsPage.tsx (<c>InstallActions</c>, <c>VersionGroup</c>,
/// <c>uninstallUnavailableText</c>, <c>InstallSheet</c>): which button the header shows, the blocker footnote, the uninstall row, and the sheet's state (confirming or watching, which command, which footer, the status alert).
/// </summary>
public static class InstallLogic
{
    /// <summary>The header button for <paramref name="inst"/> (null = no install info).</summary>
    public static InstallButton ButtonFor(ClientInstallDto? inst)
    {
        if (inst is null) return InstallButton.None;
        if (inst.Busy) return InstallButton.Running;
        if (inst.UpdateAvailable && inst.UpdateCommand is not null && inst.LatestVersion is not null) return InstallButton.Update;
        if (inst.UpdateAvailable && inst.Blocker == "shadowed") return InstallButton.Shadowed;
        if (!inst.Installed && inst.InstallCommand is not null) return InstallButton.Install;
        if (!inst.Installed && inst.Method == "manual") return InstallButton.Download;
        return InstallButton.None;
    }

    /// <summary>The tooltip of a button that will ask for the administrator password, else null.</summary>
    public static string? AdminTipKey(ClientInstallDto inst)
        => inst.NeedsAdmin ? inst.AdminAvailable ? "clients.admin.tip" : "clients.admin.tipManual" : null;

    /// <summary>The version group's footnote: why an action is unavailable (web <c>blocker</c>).</summary>
    public static string? BlockerText(ClientInstallDto inst, Translate t) => inst.Blocker switch
    {
        null => null,
        "external-install" => t("clients.blocker.external-install", ("path", inst.Executable ?? "")),
        var b => t("clients.blocker." + b),
    };

    /// <summary>The newer copy behind the one that runs, if any (web <c>shadowing</c>).</summary>
    public static ClientCopyDto? Shadowing(ClientInstallDto inst) => inst.OtherCopies?.FirstOrDefault(c => c.Newer);

    /// <summary>Whether the version group shows the "latest version" row (npm / script installs).</summary>
    public static bool ShowsLatest(ClientInstallDto inst) => inst.Method is "npm" or "script";

    /// <summary>The detail of the "latest version" row: the check's error, else when it was checked.</summary>
    public static string? LatestDetail(ClientInstallDto inst, TimeSpan zone, string language, Translate t)
        => inst.LatestError ?? (inst.LatestCheckedAt is { } at ? t("clients.checkedAt", ("time", StatsFormat.DateTime(at, zone, language))) : null);

    /// <summary>The version group shows an "uninstall" row for every installed client (a button, or why there is none).</summary>
    public static bool ShowsUninstall(ClientInstallDto inst) => inst.Installed;

    /// <summary>
    /// Why Astra will not uninstall an installed client (no <see cref="ClientInstallDto.UninstallCommand"/>): an npm client installed
    /// another way, a VS Code built-in, or an app / script install (the vendor's instructions); null when it can or the client is absent.
    /// </summary>
    public static string? UninstallUnavailableText(ClientInstallDto inst, Translate t)
    {
        if (!inst.Installed || inst.UninstallCommand is not null) return null;
        return inst.Method switch
        {
            "npm" => t("clients.uninstallExternal", ("path", inst.Executable ?? "")),
            "vscode-extension" => t("clients.uninstallBuiltIn"),
            _ => t("clients.uninstallManual"),
        };
    }

    /// <summary>The confirm step says the original config comes back first: an uninstall of a client Astra has taken over.</summary>
    public static bool RestoresFirst(InstallSheetMode? mode, bool clientEnabled) => mode == InstallSheetMode.Uninstall && clientEnabled;

    /// <summary>The command the sheet would run for <paramref name="mode"/>.</summary>
    public static string? CommandFor(InstallSheetMode? mode, ClientInstallDto? inst) => mode switch
    {
        InstallSheetMode.Install => inst?.InstallCommand,
        InstallSheetMode.Update => inst?.UpdateCommand,
        InstallSheetMode.Uninstall => inst?.UninstallCommand,
        _ => null,
    };

    /// <summary>The wire name of an action ("install" / "update" / "uninstall").</summary>
    public static string ActionName(InstallSheetMode mode) => mode switch
    {
        InstallSheetMode.Update => "update",
        InstallSheetMode.Uninstall => "uninstall",
        _ => "install",
    };

    /// <summary>The sheet asks for confirmation first unless a run is already going or it was opened to watch the log.</summary>
    public static bool Confirming(InstallSheetMode? mode, bool started, ClientInstallJobDto? job)
        => mode is InstallSheetMode.Install or InstallSheetMode.Update or InstallSheetMode.Uninstall && !started && job?.State != "running";

    /// <summary>The sheet's footer.</summary>
    public static InstallFooter FooterFor(bool confirming, ClientInstallJobDto? job)
        => confirming ? InstallFooter.Confirm : job?.State == "running" ? InstallFooter.Running : InstallFooter.Finished;

    /// <summary>A failed run that hit a permission error can be retried once with admin rights (an elevated run is final).</summary>
    public static bool RetryAsAdmin(ClientInstallJobDto? job, ClientInstallDto? inst)
        => job is { State: "failed", Hint: "permission-denied", Elevated: false } && inst?.AdminAvailable == true;

    /// <summary>The sheet title key for the action (or the last run's): update / uninstall, else install.</summary>
    public static string TitleKey(InstallSheetMode? mode, ClientInstallJobDto? job)
    {
        var action = mode is InstallSheetMode.Install or InstallSheetMode.Update or InstallSheetMode.Uninstall ? ActionName(mode.Value) : job?.Action;
        return action switch
        {
            "update" => "clients.updateTitle",
            "uninstall" => "clients.uninstallTitle",
            _ => "clients.installTitle",
        };
    }

    /// <summary>A success that did not take effect (it carries a hint) shows as a warning.</summary>
    public static bool NotEffective(ClientInstallJobDto job) => job.State == "succeeded" && job.Hint is not null;

    /// <summary>The status alert's title key.</summary>
    public static string StatusTitleKey(ClientInstallJobDto job) => NotEffective(job) ? "clients.job.notEffective" : "clients.job." + job.State;

    /// <summary>The status alert's body: the elevated-run note, else the hint (+ the retry note), else nothing.</summary>
    public static string? StatusBody(ClientInstallJobDto job, bool retryAsAdmin, Translate t)
    {
        if (job.State == "running" && job.Elevated) return t("clients.admin.running");
        if (job.Hint is { } hint) return t("clients.hint." + hint) + (retryAsAdmin ? t("clients.admin.retryHint") : "");
        return null;
    }

    /// <summary>What to say when the job state moved from <paramref name="previous"/> to the job's (web: the effect on the job's state).</summary>
    public static InstallOutcome Transition(string? previous, ClientInstallJobDto? job)
    {
        if (previous != "running" || job is null || job.State == "running") return InstallOutcome.None;
        if (job.State != "succeeded") return InstallOutcome.Ended;
        if (job.Hint is not null) return InstallOutcome.NotEffective;
        return job.Action switch
        {
            "install" => InstallOutcome.Installed,
            "uninstall" => InstallOutcome.Uninstalled,
            _ => InstallOutcome.Updated,
        };
    }
}
