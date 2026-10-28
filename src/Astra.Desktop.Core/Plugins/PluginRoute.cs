using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Plugins;

/// <summary>The three tabs of the Plugins page (web <c>Tab</c> in PluginsPage.tsx).</summary>
public enum PluginTab
{
    /// <summary>What is installed (the default).</summary>
    Installed,

    /// <summary>The market: the community registry and searches.</summary>
    Market,

    /// <summary>Bun and the plugin host.</summary>
    Runtime,
}

/// <summary>
/// What the route parameter of <c>/plugins[/:tab]</c> asks for: the tab and, for a link from the website
/// (<c>/plugins/market?install=&lt;package&gt;</c>), a package to offer to install.
/// </summary>
/// <param name="Tab">The tab to show.</param>
/// <param name="Install">The requested package or <c>github:owner/repo</c>, null for none.</param>
public sealed record PluginRoute(PluginTab Tab, string? Install = null)
{
    /// <summary>
    /// Reads a route parameter: "market" / "runtime" select those tabs, anything else the installed tab (web: an unknown
    /// <c>:tab</c> falls back to "installed"). A <c>?install=</c> suffix is kept whatever the tab, as the web page only
    /// looks for it on the market tab but the link always points there.
    /// </summary>
    public static PluginRoute Parse(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return new PluginRoute(PluginTab.Installed);
        var query = param.IndexOf('?', StringComparison.Ordinal);
        var tabPart = (query < 0 ? param : param[..query]).Trim('/');
        string? install = null;
        if (query >= 0)
            foreach (var pair in param[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && pair[..eq] == "install") install = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        var tab = tabPart switch { "market" => PluginTab.Market, "runtime" => PluginTab.Runtime, _ => PluginTab.Installed };
        return new PluginRoute(tab, string.IsNullOrEmpty(install) ? null : install);
    }

    /// <summary>The route parameter of a tab (web: <c>/plugins</c> for installed, <c>/plugins/&lt;tab&gt;</c> otherwise).</summary>
    public static string? ParamOf(PluginTab tab) => tab switch
    {
        PluginTab.Market => "market",
        PluginTab.Runtime => "runtime",
        _ => null,
    };
}

/// <summary>What a website link may ask the page to install (web <c>LINK_SPEC</c>).</summary>
public static partial class PluginSpecs
{
    // An npm package name (scoped or not) or github:owner/repo — never a path, which would run local code.
    [GeneratedRegex(@"^(?:(?:@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*|github:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinkSpec();

    /// <summary>True when <paramref name="spec"/> is something a website link may ask this page to install.</summary>
    public static bool IsLinkable(string? spec) => !string.IsNullOrEmpty(spec) && LinkSpec().IsMatch(spec);
}
