using System.Globalization;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Core.Tray;

/// <summary>What a button on the update card does; the shell that hosts the card carries it out.</summary>
public enum UpdateCardAction
{
    /// <summary>Install the update this card offers.</summary>
    Update,

    /// <summary>Open the download page (a copy that cannot replace itself).</summary>
    Download,

    /// <summary>Close the reminder; it comes back after the cooldown (<see cref="UpdateReminder"/>).</summary>
    Later,

    /// <summary>Never remind about this version again.</summary>
    Skip,

    /// <summary>Run the failed check or apply again.</summary>
    Retry,

    /// <summary>Close the card.</summary>
    Close,

    /// <summary>Hide a running update's card; the update goes on and the card comes back when it ends.</summary>
    Background,
}

/// <summary>The card's look: its icon and accent.</summary>
public enum UpdateCardTone { Info, Busy, Success, Danger }

/// <summary>What kind of card it is (the host keeps an offer up until it is answered, closes a result on its own).</summary>
public enum UpdateCardKind { Available, Checking, Progress, Done, UpToDate, Failed }

/// <summary>A button on the card; the first <see cref="Primary"/> one is the accent button.</summary>
public sealed record UpdateCardButton(string Label, UpdateCardAction Action, bool Primary = false);

/// <summary>
/// One state of the bottom-right update card: the reminder ("Astra x is available" with update / later / skip), a running update (stage,
/// a progress bar, the bytes), and its end (updated, restarting to install, failed with retry). Built by <see cref="UpdateCards"/>; drawn by
/// the desktop's <c>UpdateCardView</c>, which shows it in the main window's corner or, with no window on screen, in a corner window of its own.
/// </summary>
/// <param name="Kind">What kind of card.</param>
/// <param name="Tone">Icon and accent.</param>
/// <param name="Title">The heading.</param>
/// <param name="Message">The line under it, or null.</param>
/// <param name="Notes">Release notes (trimmed), or null.</param>
/// <param name="ShowProgress">Whether the card has a progress bar.</param>
/// <param name="Fraction">The bar's value 0–1; null with <paramref name="ShowProgress"/> = an indeterminate bar.</param>
/// <param name="ProgressText">The text beside the bar (percent, bytes), or null.</param>
/// <param name="Buttons">The buttons, left to right.</param>
/// <param name="Dismiss">What the close (×) button does, or null for no close button (the app is about to restart).</param>
/// <param name="AutoClose">How long the card stays before it closes itself, or null to stay until answered.</param>
/// <param name="Version">The version the card is about.</param>
public sealed record UpdateCardModel(
    UpdateCardKind Kind,
    UpdateCardTone Tone,
    string Title,
    string? Message,
    string? Notes,
    bool ShowProgress,
    double? Fraction,
    string? ProgressText,
    IReadOnlyList<UpdateCardButton> Buttons,
    UpdateCardAction? Dismiss,
    TimeSpan? AutoClose,
    string? Version);

/// <summary>Builds the update card's states (<see cref="UpdateCardModel"/>) from a check's answer or an apply's progress.</summary>
public static class UpdateCards
{
    /// <summary>How long a "done" or "up to date" card stays.</summary>
    public static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The offer for <paramref name="summary"/> (an available update). The reminder (<paramref name="reminder"/>) offers to skip the version; the
    /// tray's check does not. A copy that cannot replace itself offers the download page; the npm track says the app is updated with the CLI.
    /// </summary>
    public static UpdateCardModel Available(UpdateDialogLabels t, UpdateSummary summary, bool reminder)
    {
        var version = summary.Version;
        var title = version is null ? t.Title : t.Message(version);
        var (action, label, hint) = summary.Action switch
        {
            UpdateAction.DesktopDownload => (UpdateCardAction.Download, t.Download, t.DownloadHint),
            UpdateAction.Server or UpdateAction.DesktopNpm => (UpdateCardAction.Update, t.UpdateServer, (string?)t.NpmDesktopHint),
            _ => (UpdateCardAction.Update, t.Update, (string?)null),
        };
        var buttons = new List<UpdateCardButton> { new(label, action, Primary: true), new(t.Later, UpdateCardAction.Later) };
        if (reminder && version is not null) buttons.Add(new UpdateCardButton(t.Skip, UpdateCardAction.Skip));
        return new UpdateCardModel(UpdateCardKind.Available, UpdateCardTone.Info, title, hint, UpdateDialogLabels.Notes(summary.Notes),
            ShowProgress: false, Fraction: null, ProgressText: null, buttons, UpdateCardAction.Later, AutoClose: null, version);
    }

    /// <summary>The tray's check while it runs.</summary>
    public static UpdateCardModel Checking(UpdateDialogLabels t)
        => new(UpdateCardKind.Checking, UpdateCardTone.Busy, t.CheckTitle, t.Checking, null, ShowProgress: true, Fraction: null, ProgressText: null,
            [], UpdateCardAction.Close, AutoClose: null, Version: null);

    /// <summary>The answer of a check that found nothing newer, or the reason it failed (<see cref="UpdateSummary.Error"/>).</summary>
    public static UpdateCardModel CheckResult(UpdateDialogLabels t, UpdateSummary summary)
    {
        if (summary.Error is { } error)
        {
            return new UpdateCardModel(UpdateCardKind.Failed, UpdateCardTone.Danger, t.CheckFailed, error, null, ShowProgress: false, Fraction: null,
                ProgressText: null, [new UpdateCardButton(t.Retry, UpdateCardAction.Retry, Primary: true)], UpdateCardAction.Close, AutoClose: null, Version: null);
        }
        if (summary.Action != UpdateAction.None) return Available(t, summary, reminder: false);
        return new UpdateCardModel(UpdateCardKind.UpToDate, UpdateCardTone.Success, t.UpToDate, null, null, ShowProgress: false, Fraction: null,
            ProgressText: null, [], UpdateCardAction.Close, ResultLifetime, Version: null);
    }

    /// <summary>The card for one step of a running (or ended) update.</summary>
    public static UpdateCardModel Progress(UpdateDialogLabels t, UpdateProgress p)
    {
        switch (p.Stage)
        {
            case UpdateStage.Done:
                return new UpdateCardModel(UpdateCardKind.Done, UpdateCardTone.Success, t.UpdatedTo(p.Version), t.DoneDetail, null, ShowProgress: false,
                    Fraction: null, ProgressText: null, [], UpdateCardAction.Close, ResultLifetime, p.Version);
            case UpdateStage.Failed:
                return new UpdateCardModel(UpdateCardKind.Failed, UpdateCardTone.Danger, t.Failed, p.Error, null, ShowProgress: false, Fraction: null,
                    ProgressText: null, [new UpdateCardButton(t.Retry, UpdateCardAction.Retry, Primary: true)], UpdateCardAction.Close, AutoClose: null, p.Version);
            case UpdateStage.Relaunching:
                // The app quits in a moment: nothing to press, nothing to close.
                return new UpdateCardModel(UpdateCardKind.Progress, UpdateCardTone.Busy, t.Updating(p.Version), t.Relaunching, null, ShowProgress: true,
                    Fraction: 1, ProgressText: null, [], Dismiss: null, AutoClose: null, p.Version);
        }
        var (message, fraction, text) = p.Stage switch
        {
            UpdateStage.Downloading => (t.Downloading, p.Fraction, Transfer(p)),
            UpdateStage.Verifying => (t.Verifying, (double?)null, (string?)null),
            UpdateStage.Installing => (t.Installing, null, null),
            UpdateStage.Restarting => (t.Restarting, null, null),
            _ => (t.Preparing, null, null),
        };
        return new UpdateCardModel(UpdateCardKind.Progress, UpdateCardTone.Busy, t.Updating(p.Version), message, null, ShowProgress: true, fraction, text,
            [new UpdateCardButton(t.Background, UpdateCardAction.Background)], UpdateCardAction.Background, AutoClose: null, p.Version);
    }

    /// <summary>"31% · 12.4 / 40.1 MB", or just the bytes when the size is unknown.</summary>
    public static string Transfer(UpdateProgress p)
    {
        if (p.Fraction is not { } f || p.Total is not { } total) return Megabytes(p.Received) + " MB";
        var percent = (int)Math.Floor(f * 100);
        return $"{percent}% · {Megabytes(p.Received)} / {Megabytes(total)} MB";
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}
