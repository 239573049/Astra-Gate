using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Plugins;

/// <summary>The market's kind filter (web <c>KindFilter</c>).</summary>
public enum KindFilter
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Provider plugins.</summary>
    Provider,

    /// <summary>Gateway middleware.</summary>
    Middleware,

    /// <summary>Agents.</summary>
    Agent,
}

/// <summary>One card of the market grid, from a registry listing or a search result.</summary>
/// <param name="Key">Identity: the package, or the spec of a search result.</param>
/// <param name="Title">The plugin's name.</param>
/// <param name="Subtitle">The package or spec (monospace).</param>
/// <param name="Kind">provider, middleware, agent or pi.</param>
/// <param name="Icon">The icon URL the registry gave, if any.</param>
/// <param name="Detail">Up to two lines of text, null for none.</param>
/// <param name="Tags">The small badges (empty ones are dropped).</param>
/// <param name="Installed">Astra has it already.</param>
/// <param name="InstallSpec">What the Install button asks to install.</param>
/// <param name="InstallSource">The <c>source</c> sent with it: market, github or npm.</param>
public sealed record ListingCardModel(
    string Key, string Title, string Subtitle, string Kind, string? Icon, string? Detail, IReadOnlyList<string> Tags, bool Installed, string InstallSpec, string InstallSource);

/// <summary>The market tab's data logic (web <c>MarketTab</c>), free of any control.</summary>
public static class PluginMarketLogic
{
    /// <summary>The kind filters in the order the segmented control shows them.</summary>
    public static readonly KindFilter[] Filters = [KindFilter.All, KindFilter.Provider, KindFilter.Middleware, KindFilter.Agent];

    /// <summary>How long the search text must rest before the registry is asked (web: 400 ms).</summary>
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>A search is only run for two or more characters.</summary>
    public const int MinSearchLength = 2;

    /// <summary>The API's word for a filter (<c>kind</c>), null for All.</summary>
    public static string? KindOf(KindFilter filter) => filter switch
    {
        KindFilter.Provider => PluginKinds.Provider,
        KindFilter.Middleware => PluginKinds.Middleware,
        KindFilter.Agent => PluginKinds.Agent,
        _ => null,
    };

    /// <summary>The filter's label: "All" or the kind's name.</summary>
    public static string FilterLabel(KindFilter filter) => KindOf(filter) is { } kind ? PluginKinds.Label(kind) : Loc.T("plugins.kind.all");

    /// <summary>True when <paramref name="debounced"/> is long enough to search for.</summary>
    public static bool IsSearching(string debounced) => debounced.Length >= MinSearchLength;

    /// <summary>How many listings a filter would show (the number in its label).</summary>
    public static int Count(IReadOnlyList<MarketListingDto>? market, KindFilter filter)
        => (market ?? []).Count(l => KindOf(filter) is not { } k || l.Kind == k);

    /// <summary>The listings of a filter, in the registry's order.</summary>
    public static List<MarketListingDto> Filter(IReadOnlyList<MarketListingDto>? market, KindFilter filter)
        => (market ?? []).Where(l => KindOf(filter) is not { } k || l.Kind == k).ToList();

    /// <summary>The segmented control's label: "All 21".</summary>
    public static string FilterText(IReadOnlyList<MarketListingDto>? market, KindFilter filter)
        => $"{FilterLabel(filter)} {Count(market, filter)}";

    /// <summary>Whether a plugin is installed: another with the same package name, or the same spec (web <c>isInstalled</c>).</summary>
    public static bool IsInstalled(IReadOnlyList<PluginDto> installed, string spec, string name)
        => installed.Any(p => p.Name == name || p.Spec == spec);

    /// <summary>The card of a registry listing (the market's own list).</summary>
    public static ListingCardModel CardOf(MarketListingDto l, string locale, IReadOnlyList<PluginDto> installed)
    {
        var detail = (l.Summary.TryGetValue(locale, out var s) ? s : null)
            ?? (l.Summary.TryGetValue("en", out var en) ? en : null)
            ?? l.Description;
        var tags = new List<string> { PluginKinds.Label(l.Kind) };
        if (!string.IsNullOrEmpty(l.Latest)) tags.Add("v" + l.Latest);
        if (l.Origin == "astra") tags.Add(Loc.T("plugins.origin.astra"));
        if (!string.IsNullOrEmpty(l.MinAstraVersion)) tags.Add(Loc.T("plugins.minVersion", ("version", l.MinAstraVersion)));
        return new ListingCardModel(l.Package, l.Name, l.Package, l.Kind, l.Icon, detail, tags, IsInstalled(installed, l.Package, l.Package), l.Package, "market");
    }

    /// <summary>The card of a search result.</summary>
    public static ListingCardModel CardOf(MarketSearchResultDto r, IReadOnlyList<PluginDto> installed)
    {
        var tags = new List<string>
        {
            PluginKinds.Label(r.Kind),
            r.Source == "github" ? $"★ {r.Stars}" : !string.IsNullOrEmpty(r.Version) ? "v" + r.Version : "",
            r.Source == "astra" ? Loc.T("plugins.origin.astra") : "",
        };
        return new ListingCardModel(
            r.Spec, r.Name, r.Spec, r.Kind, null, r.Description, tags.Where(t => t.Length > 0).ToList(), IsInstalled(installed, r.Spec, r.Name), r.Spec,
            r.Source switch { "github" => "github", "astra" => "market", _ => "npm" });
    }
}

/// <summary>The job sheet's texts (web <c>JobSheet</c>).</summary>
public static class PluginJobTexts
{
    /// <summary>The sheet's description for a job: what it is doing or how it ended; null before the job is known.</summary>
    public static string? Description(PluginJobDto? job)
    {
        if (job is null) return null;
        var key = job.State switch { "running" => "plugins.job.running", "succeeded" => "plugins.job.done", _ => "plugins.job.failed" };
        return Loc.T(key, ("target", job.Target));
    }

    /// <summary>The log shown in the code block: its lines, or an ellipsis while it is empty.</summary>
    public static string LogText(PluginJobDto job) => job.Log.Count > 0 ? string.Join('\n', job.Log) : "…";

    /// <summary>True when the job has not ended (the page keeps polling it).</summary>
    public static bool IsRunning(PluginJobDto? job) => job is null || job.State == "running";

    /// <summary>The red box under the log: only for a failed job that says why.</summary>
    public static string? FailureText(PluginJobDto? job) => job is { State: "failed", Error: { Length: > 0 } e } ? e : null;

    /// <summary>How often a running job is read (web <c>refetchInterval</c>).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
}
