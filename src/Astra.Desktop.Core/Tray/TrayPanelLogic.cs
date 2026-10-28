using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>The panel's tabs (web <c>TrayTab</c>).</summary>
public enum TrayTab { Overview, Quota, Clients }

/// <summary>An attention line at the top of the overview (web <c>Alerts</c>).</summary>
/// <param name="Critical">Red (true) or orange.</param>
/// <param name="Text">The message.</param>
/// <param name="OpensQuota">Clicking it shows the quota tab.</param>
public sealed record TrayAlert(bool Critical, string Text, bool OpensQuota);

/// <summary>A chip under today's numbers (web <c>Chip</c>): label, value, optional tooltip, the top model's share, and for the yesterday comparison the arrow.</summary>
public sealed record TrayChip(string Label, string Value, string? Tooltip = null, bool? Up = null, string? Share = null);

/// <summary>
/// The decisions of web/src/components/tray/TrayPanelView.tsx that are not drawing: which tabs exist, the status line, the address, the
/// alerts, the tiles and chips of today's block and which quota rows the overview teases. The view only lays these out.
/// </summary>
public static class TrayPanelLogic
{
    /// <summary>The tabs with content: the overview always, quota when on and there is something (or a subscription waiting), clients when on and one is enabled.</summary>
    public static List<TrayTab> Tabs(TrayPrefs prefs, TrayData data)
    {
        var tabs = new List<TrayTab> { TrayTab.Overview };
        if (On(prefs, TraySection.Quota) && (data.Plans.Count > 0 || data.HasSubscriptions)) tabs.Add(TrayTab.Quota);
        if (On(prefs, TraySection.Clients) && data.Clients.Any(c => c.Enabled)) tabs.Add(TrayTab.Clients);
        return tabs;
    }

    /// <summary>The requested tab when it exists, else the overview.</summary>
    public static TrayTab Active(TrayTab requested, IReadOnlyList<TrayTab> tabs) => tabs.Contains(requested) ? requested : TrayTab.Overview;

    /// <summary>The tab bar shows while the service runs (or is busy) and there is more than the overview.</summary>
    public static bool ShowTabs(TrayPanelState state, IReadOnlyList<TrayTab> tabs) => (state.Running || state.Activity is not null) && tabs.Count > 1;

    /// <summary>A tab's label key.</summary>
    public static string TabKey(TrayTab tab) => tab switch { TrayTab.Quota => "tray.tab.quota", TrayTab.Clients => "tray.clients", _ => "tray.tab.overview" };

    /// <summary>The status line's key: the activity, else running / stopped.</summary>
    public static string StatusKey(TrayPanelState state) => state.Activity switch
    {
        TrayActivity.Starting => "tray.status.starting",
        TrayActivity.Stopping => "tray.status.stopping",
        TrayActivity.Restarting => "tray.status.restarting",
        _ => state.Running ? "tray.status.running" : "tray.status.stopped",
    };

    /// <summary>The address line: the gateway base URL without its scheme, else 127.0.0.1:port, else empty.</summary>
    public static string Address(SettingsDto? settings, int? port)
    {
        if (settings?.GatewayBaseUrl is { } url)
        {
            foreach (var scheme in new[] { "https://", "http://" })
                if (url.StartsWith(scheme, StringComparison.Ordinal)) return url[scheme.Length..];
            return url;
        }
        return port is { } p ? "127.0.0.1:" + p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    /// <summary>The address the copy button puts on the clipboard (<c>gatewayBaseUrl + "/v1"</c>); null before the settings arrived.</summary>
    public static string? CopyText(SettingsDto? settings) => settings?.GatewayBaseUrl is { } url ? url + "/v1" : null;

    /// <summary>The overview's attention lines: failed requests today, a quota window running low. Empty when all is well.</summary>
    public static List<TrayAlert> Alerts(TrayPrefs prefs, TrayData data, Func<string, (string, object?)[], string> t)
    {
        var alerts = new List<TrayAlert>();
        var s = data.Summary;
        if (s is null) return alerts;
        var failed = On(prefs, TraySection.Overview) ? TrayMath.FailedRequests(s) : 0;
        var tight = On(prefs, TraySection.Quota) ? TrayMath.TightestQuota(data.Plans) : null;
        var low = tight is not null && tight.Remaining < TrayMath.QuotaLow ? tight : null;
        if (failed > 0)
            alerts.Add(new TrayAlert(true, t("tray.alert.failures", [("count", failed), ("share", StatsFormat.Percent((double)failed / s.Requests))]), false));
        if (low is not null)
            alerts.Add(new TrayAlert(low.Remaining < TrayMath.QuotaCritical,
                t("tray.alert.quota", [("plan", low.Plan.ProviderName), ("window", TrayMath.RowName(low.Row)), ("remaining", TrayMath.Percent(low.Remaining))]), true));
        return alerts;
    }

    /// <summary>Whether today's block shows at all (tiles, chart or chips).</summary>
    public static bool HasUsage(TrayPrefs prefs)
        => On(prefs, TraySection.Overview) || On(prefs, TraySection.Cost) || On(prefs, TraySection.Chart) || HasChips(prefs);

    /// <summary>Whether the chip row shows.</summary>
    public static bool HasChips(TrayPrefs prefs) => On(prefs, TraySection.TopModel) || On(prefs, TraySection.CacheHitRate) || On(prefs, TraySection.CompareYesterday);

    /// <summary>The tiles: requests and tokens (overview), cost; label key and value.</summary>
    public static List<(string LabelKey, string Value)> Tiles(TrayPrefs prefs, StatsSummaryDto s)
    {
        var tiles = new List<(string, string)>();
        if (On(prefs, TraySection.Overview))
        {
            tiles.Add(("tray.requests", StatsFormat.Tokens(s.Requests, true)));
            tiles.Add(("tray.tokens", StatsFormat.Tokens(s.InputTokens + s.OutputTokens, true)));
        }
        if (On(prefs, TraySection.Cost)) tiles.Add(("tray.cost", TrayMath.FormatCostShort(s.CostUsd)));
        return tiles;
    }

    /// <summary>The chips: top model (when there is one), cache hits, change vs yesterday ("—" when unknown).</summary>
    public static List<TrayChip> Chips(TrayPrefs prefs, TrayData data, LocalTime now, Func<string, string> t)
    {
        var chips = new List<TrayChip>();
        var s = data.Summary;
        if (s is null) return chips;
        var tokens = s.InputTokens + s.OutputTokens;
        if (On(prefs, TraySection.TopModel) && TrayMath.TopModelShare(data.Top, tokens) is { } top)
            chips.Add(new TrayChip(t("tray.topModel"), top.Model, top.Model, Share: StatsFormat.Percent(top.Share)));
        if (On(prefs, TraySection.CacheHitRate))
            chips.Add(new TrayChip(t("tray.cacheHit"), UsageMath.CacheHitRatio(s.CacheReadTokens, s.InputTokens) is { } hit ? StatsFormat.Percent(hit) : StatsFormat.Dash));
        if (On(prefs, TraySection.CompareYesterday))
        {
            var delta = TrayMath.ChangeRatio(tokens, TrayMath.YesterdayTokens(data.Daily, now));
            chips.Add(delta is { } d ? new TrayChip(t("tray.vsYesterday"), StatsFormat.Percent(Math.Abs(d)), Up: d >= 0) : new TrayChip(t("tray.vsYesterday"), StatsFormat.Dash));
        }
        return chips;
    }

    /// <summary>The bar heights of the hourly chart (64px tall: at least 4 for a non-empty hour, 2 for an empty one).</summary>
    public static double BarHeight(long value, long peak) => value > 0 ? Math.Max(4, Math.Floor((double)value / Math.Max(1, peak) * 64 + 0.5)) : 2;

    /// <summary>How many quota rows exist in total (the "view all" link shows when the teaser leaves some out).</summary>
    public static int RowCount(IEnumerable<TrayQuotaPlan> plans) => plans.Sum(p => p.Rows.Count);

    /// <summary>Accounts per provider: a provider with several shows each plan's account name.</summary>
    public static bool ShowAccount(IReadOnlyList<TrayQuotaPlan> plans, TrayQuotaPlan plan) => plans.Count(p => p.ProviderId == plan.ProviderId) > 1;

    /// <summary>A client row's provider name, or "未绑定".</summary>
    public static string ProviderName(IReadOnlyList<ProviderDto> providers, string? id, string unbound)
        => providers.FirstOrDefault(p => p.Id == id)?.Name ?? unbound;

    /// <summary>The provider choices the client switch menu offers (checkbox items; a disabled provider is named so).</summary>
    public static List<ContextMenuItem> SwitchMenu(IReadOnlyList<ProviderDto> providers, ClientInfoDto client, Func<string, string> disabledName)
        => providers.Select(p => new ContextMenuItem(p.Id, p.Enabled ? p.Name : disabledName(p.Name), Checked: p.Id == client.ProviderId, Checkbox: true)).ToList();

    private static bool On(TrayPrefs prefs, TraySection s) => prefs.Sections.TryGetValue(s, out var on) && on;
}
