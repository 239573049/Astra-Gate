namespace Astra.Desktop.Core.Shell;

/// <summary>
/// The time a page shows. Pages never read <c>DateTimeOffset.UtcNow</c> directly (relative times, "today" ranges): they ask
/// this, so the visual tests can pin it to the fixture set's frozen instant (tests/Astra.Desktop.UiTests/FixtureSet.cs <c>Now</c>).
/// </summary>
public interface IClock
{
    /// <summary>The current instant.</summary>
    DateTimeOffset Now { get; }
}

/// <summary>The real clock; the default of the app.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>A clock that stands still (or is moved by the test).</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>
/// Implemented by a page view model that loads data: the visual-regression capture awaits <see cref="WhenSettledAsync"/> instead of
/// sleeping, so a scene is rendered after its queries answered and never half loaded. A page that loads nothing needs no
/// implementation (the base <c>PageViewModel</c> settles on the shared <c>QueryClient</c>).
/// </summary>
public interface IPageLoadState
{
    /// <summary>Completes when every request the page started has been answered (successfully or not) and its result applied.</summary>
    Task WhenSettledAsync(CancellationToken ct = default);
}
