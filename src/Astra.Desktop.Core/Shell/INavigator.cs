namespace Astra.Desktop.Core.Shell;

/// <summary>
/// What a page view model needs from navigation, without the Avalonia project's <c>AppRoute</c> enum (which Desktop.Core
/// cannot see). <c>route</c> is the enum member name ("Providers", "ProviderDetail", ...), <c>param</c> the route parameter (an
/// id, a tab). The app's <c>Navigator</c> implements it by parsing the name (<c>Enum.Parse&lt;AppRoute&gt;</c> is AOT-safe).
/// Mirrors the web router calls <c>navigate(path)</c> / <c>navigate(-1)</c>.
/// </summary>
public interface INavigator
{
    /// <summary>Shows a top-level page and forgets the history (a sidebar click).</summary>
    void Navigate(string route, string? param = null);

    /// <summary>Shows a page on top of the current one (a list opening its detail page).</summary>
    void Push(string route, string? param = null);

    /// <summary>Back to the page below, if any.</summary>
    void GoBack();

    /// <summary>Whether <see cref="GoBack"/> has somewhere to go.</summary>
    bool CanGoBack { get; }
}

/// <summary>Records navigation instead of showing pages; for tests.</summary>
public sealed class RecordingNavigator : INavigator
{
    private readonly Stack<(string Route, string? Param)> _back = new();

    /// <summary>The page currently shown (null before the first call).</summary>
    public (string Route, string? Param)? Current { get; private set; }

    /// <summary>Every call in order: "navigate", "push" or "back".</summary>
    public List<(string Action, string? Route, string? Param)> Calls { get; } = [];

    /// <inheritdoc />
    public bool CanGoBack => _back.Count > 0;

    /// <inheritdoc />
    public void Navigate(string route, string? param = null)
    {
        Calls.Add(("navigate", route, param));
        _back.Clear();
        Current = (route, param);
    }

    /// <inheritdoc />
    public void Push(string route, string? param = null)
    {
        Calls.Add(("push", route, param));
        if (Current is { } c) _back.Push(c);
        Current = (route, param);
    }

    /// <inheritdoc />
    public void GoBack()
    {
        Calls.Add(("back", null, null));
        if (_back.Count > 0) Current = _back.Pop();
    }
}
