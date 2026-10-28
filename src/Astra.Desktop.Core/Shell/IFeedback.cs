namespace Astra.Desktop.Core.Shell;

/// <summary>What a confirmation dialog asks. Null texts mean the localized defaults (<c>common.confirm</c> / <c>common.cancel</c>).</summary>
/// <param name="Title">Dialog title.</param>
/// <param name="Message">Body text, optional.</param>
/// <param name="ConfirmText">Confirm button label.</param>
/// <param name="CancelText">Cancel button label.</param>
/// <param name="Destructive">Styles the confirm button as dangerous (delete, reset).</param>
public sealed record ConfirmRequest(string Title, string? Message = null, string? ConfirmText = null, string? CancelText = null, bool Destructive = false);

/// <summary>
/// User feedback a page can raise without knowing the UI: toasts and a confirm dialog (web: the Toast/ConfirmDialog
/// pairing of web/src/components/arc). The app implements it with Arc's Toast/Dialog; view models take it by constructor so
/// tests can use <see cref="NullFeedback"/> or a recorder.
/// </summary>
public interface IFeedback
{
    /// <summary>A success toast.</summary>
    void Success(string title, string? description = null);

    /// <summary>An error toast.</summary>
    void Error(string title, string? description = null);

    /// <summary>A neutral toast.</summary>
    void Info(string title, string? description = null);

    /// <summary>Shows a modal confirmation; true when the user confirmed.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default);
}

/// <summary>Swallows toasts and answers every confirmation with <see cref="Answer"/> (default: confirmed); for tests and headless runs.</summary>
public sealed class NullFeedback : IFeedback
{
    /// <summary>What <see cref="ConfirmAsync"/> returns.</summary>
    public bool Answer { get; set; } = true;

    /// <inheritdoc />
    public void Success(string title, string? description = null) { }

    /// <inheritdoc />
    public void Error(string title, string? description = null) { }

    /// <inheritdoc />
    public void Info(string title, string? description = null) { }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default) => Task.FromResult(Answer);
}
