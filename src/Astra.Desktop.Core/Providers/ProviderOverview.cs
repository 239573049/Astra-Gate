using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Providers;

/// <summary>The one state a provider card, row and the health tiles show (web <c>ProviderHealth</c>, components/ProviderOverview.tsx).</summary>
public enum ProviderHealth
{
    /// <summary>Enabled, usable, and not failing (green).</summary>
    Healthy,

    /// <summary>At least <see cref="ProviderHealthRules.LowSuccessMinRequests"/> requests over the week and a success rate below <see cref="ProviderHealthRules.LowSuccessRate"/> (red).</summary>
    LowSuccess,

    /// <summary>Enabled but a key is required and missing (orange).</summary>
    NoKey,

    /// <summary>Switched off (neutral).</summary>
    Disabled,
}

/// <summary>How a provider's health is decided (web <c>providerHealth</c>).</summary>
public static class ProviderHealthRules
{
    /// <summary>Below this success rate a provider with enough requests is flagged (web <c>LOW_SUCCESS_RATE</c>).</summary>
    public const double LowSuccessRate = 0.9;

    /// <summary>Fewer requests than this never flag a provider: two failures out of two is noise.</summary>
    public const int LowSuccessMinRequests = 5;

    /// <summary>Whether the provider needs a key it does not have (web <c>missingKey</c>).</summary>
    public static bool MissingKey(ProviderDto p) => !p.HasApiKey && p.AuthScheme is not ("none" or "oauth-subscription" or "plugin");

    /// <summary>The provider's state from its row and its stats over the cards' week (null: no requests).</summary>
    public static ProviderHealth Of(ProviderDto p, ProviderStatDto? stat)
    {
        if (!p.Enabled) return ProviderHealth.Disabled;
        if (MissingKey(p)) return ProviderHealth.NoKey;
        if (stat is { } s && s.Requests >= LowSuccessMinRequests && s.SuccessRate < LowSuccessRate) return ProviderHealth.LowSuccess;
        return ProviderHealth.Healthy;
    }

    /// <summary>The i18n key of a state's label (web <c>healthLabel</c>).</summary>
    public static string LabelKey(ProviderHealth h) => h switch
    {
        ProviderHealth.Disabled => "providers.card.disabled",
        ProviderHealth.NoKey => "providers.noKey",
        ProviderHealth.LowSuccess => "providers.card.lowSuccess",
        _ => "providers.card.active",
    };

    /// <summary>Table sort order: the ones needing attention first (web <c>HEALTH_ORDER</c>).</summary>
    public static int SortOrder(ProviderHealth h) => h switch
    {
        ProviderHealth.LowSuccess => 0,
        ProviderHealth.NoKey => 1,
        ProviderHealth.Healthy => 2,
        _ => 3,
    };
}

/// <summary>One part of a share chart: a provider (or "other", <see cref="Key"/> <see cref="ProviderOverviewData.OtherKey"/>) and its color slot.</summary>
/// <param name="Key">Provider id, or <see cref="ProviderOverviewData.OtherKey"/>.</param>
/// <param name="Name">Shown name.</param>
/// <param name="Value">Requests or USD.</param>
/// <param name="ColorIndex">Index into the series palette; -1 for "other".</param>
public sealed record ShareSlice(string Key, string Name, double Value, int ColorIndex);

/// <summary>
/// The figures and charts above the provider list over one range (web <c>ProviderOverview</c>): totals, the request and cost shares (top
/// <see cref="SeriesColors"/> providers, the rest folded into "other"), and the health counts over the cards' week.
/// </summary>
public sealed record ProviderOverviewData(
    long Requests, long SuccessRequests, decimal CostUsd, long Failovers, int Enabled, int Total, int WithTraffic,
    IReadOnlyList<ShareSlice> RequestShare, IReadOnlyList<ShareSlice> CostShare, IReadOnlyDictionary<ProviderHealth, int> Health)
{
    /// <summary>Key of the folded slice.</summary>
    public const string OtherKey = "__other";

    /// <summary>How many providers get their own color (web <c>SHARE_COLORS.length</c>).</summary>
    public const int SeriesColors = 6;

    /// <summary>The overall success rate, or null without requests.</summary>
    public double? SuccessRate => Requests > 0 ? (double)SuccessRequests / Requests : null;

    /// <summary>Computes the overview from the provider list, the range's stats and the week's stats (by provider id).</summary>
    public static ProviderOverviewData Build(
        IReadOnlyList<ProviderDto> providers, IReadOnlyList<ProviderStatDto> stats, IReadOnlyDictionary<string, ProviderStatDto> week, string other)
    {
        var names = providers.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var health = Enum.GetValues<ProviderHealth>().ToDictionary(h => h, _ => 0);
        foreach (var p in providers) health[ProviderHealthRules.Of(p, week.GetValueOrDefault(p.Id))]++;
        return new ProviderOverviewData(
            stats.Sum(s => s.Requests), stats.Sum(s => s.SuccessRequests), stats.Sum(s => s.CostUsd), stats.Sum(s => s.FailoversOut),
            providers.Count(p => p.Enabled), providers.Count, stats.Count(s => s.Requests > 0 && names.ContainsKey(s.ProviderId)),
            Slices(stats, names, s => s.Requests, other), Slices(stats, names, s => (double)s.CostUsd, other), health);
    }

    private static List<ShareSlice> Slices(IReadOnlyList<ProviderStatDto> stats, Dictionary<string, string> names, Func<ProviderStatDto, double> value, string other)
    {
        var sorted = stats.Where(s => value(s) > 0).OrderByDescending(value).ToList();
        var list = sorted.Take(SeriesColors)
            .Select((s, i) => new ShareSlice(s.ProviderId, names.GetValueOrDefault(s.ProviderId) ?? s.Name ?? s.ProviderId, value(s), i))
            .ToList();
        var rest = sorted.Skip(SeriesColors).Sum(value);
        if (rest > 0) list.Add(new ShareSlice(OtherKey, other, rest, -1));
        return list;
    }
}

/// <summary>Bar heights of the seven-day mini chart (web <c>MiniBars</c>): square root of the shared maximum, so one busy provider does not flatten the rest.</summary>
public static class MiniBarScale
{
    /// <summary>Smallest height share of a day that had requests.</summary>
    public const double MinShare = 0.14;

    /// <summary>The height share (0..1) of <paramref name="value"/> against the shared <paramref name="max"/>; 0 for an empty day.</summary>
    public static double Share(long value, long max)
        => value <= 0 ? 0 : Math.Max(MinShare, Math.Sqrt(value / (double)Math.Max(1, max)));
}

/// <summary>Store for <see cref="ProvidersPrefs"/> (one key of <c>desktop-prefs.json</c>).</summary>
public interface IProvidersPrefsStore
{
    /// <summary>The stored value of <paramref name="key"/> (a copy), or null.</summary>
    JsonNode? Get(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (null removes it) and saves.</summary>
    void Set(string key, JsonNode? value);
}

/// <summary>
/// What the Providers page remembers (web localStorage <c>astra.providers.view</c> = "table" and <c>astra.providers.charts</c> = "0"): one
/// prefs key <see cref="Key"/> = <c>{ "view": "table", "charts": false }</c>, each field present only when it differs from the default
/// (cards, charts shown). Without a store (tests) the values last the app session.
/// </summary>
public static class ProvidersPrefs
{
    /// <summary>The top-level prefs key.</summary>
    public const string Key = "providers";

    /// <summary>The table view's value.</summary>
    public const string TableView = "table";

    /// <summary>The cards view's value (the default).</summary>
    public const string GridView = "grid";

    private static IProvidersPrefsStore? _store;
    private static string? _view;
    private static bool? _charts;

    /// <summary>Attaches <paramref name="store"/> (null detaches); the values are read from it when next asked for.</summary>
    public static void Use(IProvidersPrefsStore? store)
    {
        if (ReferenceEquals(store, _store)) return;
        _store = store;
        _view = null;
        _charts = null;
    }

    /// <summary><see cref="GridView"/> or <see cref="TableView"/>.</summary>
    public static string View
    {
        get => _view ??= Read()?["view"] is JsonValue v && v.TryGetValue<string>(out var s) && s == TableView ? TableView : GridView;
        set
        {
            _view = value == TableView ? TableView : GridView;
            Update(o =>
            {
                if (_view == TableView) o["view"] = TableView;
                else o.Remove("view");
            });
        }
    }

    /// <summary>Whether the overview shows its charts (default on).</summary>
    public static bool ChartsOpen
    {
        get => _charts ??= Read()?["charts"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;
        set
        {
            _charts = value;
            Update(o =>
            {
                if (value) o.Remove("charts");
                else o["charts"] = false;
            });
        }
    }

    private static JsonObject? Read() => _store?.Get(Key) as JsonObject;

    private static void Update(Action<JsonObject> change)
    {
        var store = _store;
        if (store is null) return;
        var o = store.Get(Key) as JsonObject ?? new JsonObject();
        change(o);
        store.Set(Key, o.Count == 0 ? null : o);
    }
}
