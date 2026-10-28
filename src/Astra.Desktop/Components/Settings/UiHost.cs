using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using TKCode.Arc;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// What a page's parts need to talk to the user, bundled so row sets and sheets (which are not controls) can use it: toasts, the confirm
/// dialog, the clipboard, an owner for overlays, and the failure handling of a write (web <c>useFeedback()</c> + <c>systemActions</c>).
/// </summary>
public sealed class UiHost(PageContext context, Func<Visual?> owner)
{
    /// <summary>The app context.</summary>
    public PageContext Context => context;

    /// <summary>The visual overlays and the clipboard hang off (null before the view is on screen).</summary>
    public Visual? Owner => owner();

    /// <summary>The text of a failure (web <c>errorText</c>): the API's own message.</summary>
    public static string ErrorText(Exception e) => e is ApiException { Message.Length: > 0 } api ? api.Message : e.Message;

    /// <summary>Runs a write: a failure becomes an error toast; true when it succeeded.</summary>
    public async Task<bool> TryAsync(Func<Task> write)
    {
        try
        {
            await write();
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            context.Feedback.Error(ErrorText(e));
            return false;
        }
    }

    /// <summary>Runs a write that gives a result; null (and an error toast) on failure.</summary>
    public async Task<T?> TryAsync<T>(Func<Task<T>> write) where T : class
    {
        try
        {
            return await write();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            context.Feedback.Error(ErrorText(e));
            return null;
        }
    }

    /// <summary>Raises a toast for a <see cref="ToastMsg"/>.</summary>
    public void Toast(ToastMsg toast)
    {
        if (toast.IsError) context.Feedback.Error(toast.Message.Text);
        else context.Feedback.Success(toast.Message.Text);
    }

    /// <summary>A success toast.</summary>
    public void Success(string text) => context.Feedback.Success(text);

    /// <summary>An error toast.</summary>
    public void Error(string text) => context.Feedback.Error(text);

    /// <summary>Copies text and says so ("copied" toast).</summary>
    public async Task CopyAsync(string text)
    {
        if (await SystemActions.CopyAsync(Owner, text)) context.Feedback.Success(Loc.T("common.copied"));
    }

    /// <summary>A confirmation dialog (web <c>confirm</c>).</summary>
    public Task<bool> ConfirmAsync(string title, string? detail, string? confirmLabel, bool destructive = false)
        => context.Feedback.ConfirmAsync(new ConfirmRequest(title, detail, confirmLabel, null, destructive));

    /// <summary>Shows a sheet over the page; completes when it closes.</summary>
    public Task<object?> ShowAsync(Dialog sheet) => sheet.ShowAsync(Owner);
}
