using Astra.Desktop.Core.I18n;
using Avalonia;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Web <c>ConfirmDialog</c> / <c>useFeedback().confirm</c> over Arc's <see cref="Dialog"/>: a modal with a title, optional
/// detail, a Cancel and a confirm button (danger styling when destructive). Defaults come from <c>common.cancel</c> /
/// <c>common.confirm</c>. Pages normally ask through <c>IFeedback.ConfirmAsync</c>; this is what <see cref="ArcFeedback"/> uses.
/// For a delete next to a button, Arc's <see cref="ConfirmMorph"/> confirms in place without a dialog.
/// Call on the UI thread; <paramref name="owner"/> is any control already in the window.
/// </summary>
public static class ConfirmDialog
{
    /// <summary>The web's confirm sheet width (overlays.tsx <c>FeedbackProvider</c>: <c>width={420}</c>).</summary>
    public const double Width = 420;

    /// <summary>Asks; true only when the confirm button was pressed.</summary>
    public static async Task<bool> AskAsync(Visual owner, string title, string? detail = null, string? confirmLabel = null, string? cancelLabel = null, bool destructive = false)
        => await Build(title, detail, confirmLabel, cancelLabel, destructive).ShowAsync(owner) is true;

    /// <summary>Builds the dialog without showing it (for cancellation or custom hosting): a 420 px kit <see cref="Sheet"/>.</summary>
    public static Dialog Build(string title, string? detail, string? confirmLabel, string? cancelLabel, bool destructive)
        => new Sheet
        {
            SheetWidth = Width,
            Title = title,
            Description = detail,
            Actions = Dialog.BuildActions(
            [
                new DialogAction(cancelLabel ?? Loc.T("common.cancel"), false),
                new DialogAction(confirmLabel ?? Loc.T("common.confirm"), true, destructive ? ButtonVariant.Danger : ButtonVariant.Primary),
            ]),
        };
}
