using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// <see cref="IFeedback"/> over Arc: success / info toasts go to the window's <see cref="ToastStack"/> (<see cref="ToastPresenter"/>), errors
/// are the web's danger alerts (<see cref="ErrorToasts"/>), a confirmation is the web's 420 px sheet (<see cref="ConfirmDialog"/>). Safe to call from any thread: work is posted to the UI thread. The
/// owner is resolved at call time (the main window may not exist when the view models are built); with no window yet, toasts are
/// dropped and a confirmation answers false.
/// </summary>
public sealed class ArcFeedback : IFeedback
{
    private readonly Func<Visual?> _owner;

    /// <summary>Feedback shown in the window <paramref name="owner"/> returns (e.g. the main window).</summary>
    public ArcFeedback(Func<Visual?> owner) => _owner = owner;

    /// <inheritdoc />
    public void Success(string title, string? description = null) => Toast(ToastKind.Success, title, description);

    /// <inheritdoc />
    public void Error(string title, string? description = null) => Dispatcher.UIThread.Post(() =>
    {
        if (_owner() is not { } owner || TopLevel.GetTopLevel(owner) is null) return;
        // The web shows errors as a danger alert (common.error + the message), not a toast; the toast stack's creation also
        // turns on the window's overlay layer the alerts live in.
        ToastPresenter.Attach(owner);
        ErrorToasts.Show(owner, title, description);
    });

    /// <inheritdoc />
    public void Info(string title, string? description = null) => Toast(ToastKind.Info, title, description);

    private void Toast(ToastKind kind, string title, string? description) => Dispatcher.UIThread.Post(() =>
    {
        if (_owner() is not { } owner || TopLevel.GetTopLevel(owner) is null) return;
        ToastPresenter.Attach(owner).Show(new ToastOptions { Kind = kind, Title = title, Description = description });
    });

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_owner() is not { } owner || TopLevel.GetTopLevel(owner) is null) return false;
            var dialog = ConfirmDialog.Build(request.Title, request.Message, request.ConfirmText, request.CancelText, request.Destructive);
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
            return await dialog.ShowAsync(owner) is true;
        });
    }
}
