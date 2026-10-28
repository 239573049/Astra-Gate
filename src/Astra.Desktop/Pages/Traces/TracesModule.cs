using System.Text.Json.Nodes;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;

namespace Astra.Desktop.Pages.Traces;

/// <summary>
/// The Traces route (web <c>/traces</c>, <c>/traces/sessions[/:key]</c>, <c>/traces/requests[/:id]</c>: one page, three views). The route
/// parameter is that web path (<see cref="Core.Traces.TracesLocation"/>); other pages open a request's trace with
/// <c>Navigator.Push("Traces", TraceRoutes.Request(id).Path)</c> and a session with <c>TraceRoutes.Session(key, turn).Path</c>.
/// Building the page attaches the canvases' remembered state (dragged topology positions, the 详情 switch) to the app's
/// <c>desktop-prefs.json</c> (web: localStorage), like <c>SettingsModule</c> hands the app's prefs to its page.
/// </summary>
public sealed class TracesModule : IPageModule
{
    private ITracePrefsStore? _prefs;

    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Traces, (ctx, param) =>
        {
            TracePrefs.Use(_prefs ??= new DesktopTracePrefs(AppServices.Current.Prefs));
            return new TracesPageViewModel(ctx, param);
        }, vm => new TracesView(vm));

    /// <summary>The <see cref="TracePrefs.Key"/> entry of the shell's prefs file (a top-level key <see cref="DesktopPrefs"/> keeps verbatim).</summary>
    private sealed class DesktopTracePrefs(DesktopPrefs prefs) : ITracePrefsStore
    {
        public JsonNode? Get(string key) => prefs.GetExtra(key);

        public void Set(string key, JsonNode? value) => prefs.SetExtra(key, value);
    }
}
