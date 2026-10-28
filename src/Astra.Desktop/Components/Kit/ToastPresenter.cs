using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// The window's toast stack (web <c>FeedbackProvider</c>'s fixed container: bottom center, newest last). Arc's
/// <see cref="ToastHost"/> creates a stack on the window's overlay layer on first use, bottom right; <see cref="Attach"/> makes
/// that stack bottom center like the web. Call it once when the window is open (<see cref="ArcFeedback"/> does).
/// </summary>
public static class ToastPresenter
{
    /// <summary>The window's stack, positioned bottom center. Needs <paramref name="owner"/> to be attached to a window.</summary>
    public static ToastStack Attach(Visual owner)
    {
        var stack = ToastHost.GetStack(owner);
        stack.Position = ToastStackPosition.BottomCenter;
        return stack;
    }

    /// <summary>A stack to place in the shell's XAML instead (bottom center), which then wins over the auto-created one.</summary>
    public static ToastStack Create() => new() { Position = ToastStackPosition.BottomCenter };
}
