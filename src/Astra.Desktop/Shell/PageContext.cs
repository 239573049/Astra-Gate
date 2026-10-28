using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Shell;

/// <summary>
/// Everything a page needs from the app, handed to every page factory (<see cref="IPageModule"/>): the one <see cref="ApiClient"/>
/// (its base URL follows the server's port), the shared <see cref="QueryClient"/> and read-only stores, user feedback (toasts,
/// confirm), the desktop host (the web's <c>window.__ASTRA__</c>), navigation and the clock. A page view model keeps the context
/// (or just the parts it uses) and never reaches for <c>AppServices.Current</c>: that is what lets the visual tests build the same
/// page over a fixture <see cref="ApiClient"/> (<see cref="Create"/>).
/// </summary>
public sealed record PageContext(
    ApiClient Api,
    QueryClient Queries,
    ProvidersStore Providers,
    TokensStore Tokens,
    ClientsStore Clients,
    SettingsStore Settings,
    IFeedback Feedback,
    IDesktopHost Host,
    INavigator Navigator,
    IClock Clock)
{
    /// <summary>
    /// A context over <paramref name="api"/>; every other part defaults to the quiet one a test wants (no toasts, a host that does nothing,
    /// the system clock, navigation that is only recorded). The app passes its real ones (<see cref="AppServices"/>).
    /// </summary>
    /// <param name="api">The API client (a fixture handler in tests).</param>
    /// <param name="ui">UI-thread marshalling for query results; null runs them inline.</param>
    /// <param name="feedback">Toasts and confirm dialogs.</param>
    /// <param name="host">The desktop host.</param>
    /// <param name="navigator">Navigation.</param>
    /// <param name="clock">The clock; tests pin it to the fixture set's now.</param>
    /// <param name="queries">An existing query client, to share the cache with another context.</param>
    public static PageContext Create(
        ApiClient api, IUiDispatcher? ui = null, IFeedback? feedback = null, IDesktopHost? host = null, INavigator? navigator = null,
        IClock? clock = null, QueryClient? queries = null)
    {
        queries ??= new QueryClient(ui);
        return new PageContext(
            api, queries,
            new ProvidersStore(queries, api), new TokensStore(queries, api), new ClientsStore(queries, api), new SettingsStore(queries, api),
            feedback ?? new NullFeedback(), host ?? new NullDesktopHost(), navigator ?? new RecordingNavigator(), clock ?? SystemClock.Instance);
    }
}
