using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// The window's toast stack (web <c>FeedbackProvider</c>'s fixed container: bottom center, newest last). Arc's
/// <see cref="ToastHost"/> creates a stack on the window's overlay layer on first use, bottom right; <see cref="Attach"/> makes
/// that stack bottom center like the web. Call it once when the window is open (<see cref="ArcFeedback"/> does).
/// The stack shares that layer with Arc's sheets and dialogs, which take a rising <c>ZIndex</c> per open; <see cref="Attach"/> lifts it
/// above them (<see cref="AboveModals"/>), as the web's toast container (<c>z-[95]</c>) sits above its sheets (issue #41).
/// </summary>
public static class ToastPresenter
{
    /// <summary>
    /// The <c>ZIndex</c> of the toast stack and of the error alerts (<see cref="ErrorToasts"/>) on the overlay layer: above every
    /// sheet and dialog, which Arc numbers 1, 2, 3, … as they open.
    /// </summary>
    public const int AboveModals = int.MaxValue;

    /// <summary>The window's stack, positioned bottom center and above open sheets. Needs <paramref name="owner"/> to be attached to a window.</summary>
    public static ToastStack Attach(Visual owner)
    {
        var stack = ToastHost.GetStack(owner);
        stack.Position = ToastStackPosition.BottomCenter;
        stack.ZIndex = AboveModals;
        return stack;
    }

    /// <summary>A stack to place in the shell's XAML instead (bottom center), which then wins over the auto-created one.</summary>
    public static ToastStack Create() => new() { Position = ToastStackPosition.BottomCenter };
}
