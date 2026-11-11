using System.Text.Json.Nodes;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;

namespace Astra.Desktop.Pages.Overview;

/// <summary>The Overview route (web <c>OverviewPage</c>).</summary>
public sealed class OverviewModule : IPageModule
{
    private IBillPrefsStore? _prefs;

    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Overview, (ctx, param) =>
        {
            // The daily bill's style is remembered in desktop-prefs.json (web: localStorage astra.bill.style).
            BillPrefs.Use(_prefs ??= new DesktopBillPrefs(AppServices.Current.Prefs));
            return new OverviewPage(ctx, param);
        }, vm => new OverviewView(vm));

    /// <summary>The <see cref="BillPrefs.Key"/> entry of the shell's prefs file.</summary>
    private sealed class DesktopBillPrefs(DesktopPrefs prefs) : IBillPrefsStore
    {
        public JsonNode? Get(string key) => prefs.GetExtra(key);

        public void Set(string key, JsonNode? value) => prefs.SetExtra(key, value);
    }
}
