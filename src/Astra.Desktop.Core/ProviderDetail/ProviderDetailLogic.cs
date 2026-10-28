using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>The detail page's tabs (web <c>Tab</c>).</summary>
public enum ProviderTab
{
    /// <summary>The provider's models and model mapping.</summary>
    Models,

    /// <summary>Accounts (subscription / plugin) or the API key, usage and balance query.</summary>
    Accounts,

    /// <summary>The provider's settings.</summary>
    Settings,
}

/// <summary>The pure rules of ProviderDetailPage.tsx outside the forms: texts, filters, the model map.</summary>
public static class ProviderDetailLogic
{
    /// <summary>The header subtitle: template id and "price: key" joined by " · " (empty parts dropped).</summary>
    public static string? Subtitle(ProviderDto p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(p.TemplateId)) parts.Add(p.TemplateId);
        if (!string.IsNullOrEmpty(p.PriceKey)) parts.Add(Loc.T("providers.priceKeyShort", ("key", p.PriceKey)));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>Whether the provider signs in with accounts (subscription logins, plugin sign-ins) instead of one key.</summary>
    public static bool UsesAccounts(ProviderDto p) => p.AuthScheme is "oauth-subscription" or "plugin";

    /// <summary>The second tab's label: accounts, or "API Key".</summary>
    public static string AccountsTabLabel(ProviderDto p) => Loc.T(UsesAccounts(p) ? "providers.tab.accounts" : "providers.tab.apiKey");

    /// <summary>The bound clients by name, comma separated, "—" for none.</summary>
    public static string BoundClients(IEnumerable<string> kinds)
    {
        var text = string.Join(", ", kinds.Select(k => ClientCatalog.Name(k)));
        return text.Length > 0 ? text : "—";
    }

    /// <summary>
    /// The toast of a failed delete (web <c>onError</c>): a 409 naming bound clients becomes <c>providers.deleteBound</c>, anything else is the
    /// error's message.
    /// </summary>
    public static string DeleteError(Exception e)
    {
        if (e is ApiException { Status: 409 } api && api.Details?["boundClients"] is JsonArray bound && bound.Count > 0)
            return Loc.T("providers.deleteBound", ("clients", BoundClients(bound.Select(b => b?.GetValue<string>() ?? ""))));
        return e.Message;
    }

    /// <summary>Model rows matching the search box (model id or display name, case-insensitive; web <c>list</c>).</summary>
    public static List<ProviderModelDto> FilterModels(IEnumerable<ProviderModelDto>? models, string search)
    {
        var q = search.ToLowerInvariant();
        return (models ?? []).Where(m => q.Length == 0 || m.ModelId.ToLowerInvariant().Contains(q, StringComparison.Ordinal)
            || m.Effective.DisplayName.ToLowerInvariant().Contains(q, StringComparison.Ordinal)).ToList();
    }

    /// <summary>A model row's detail line: display name (when different), context, price summary (web <c>detail</c>).</summary>
    public static string ModelDetail(ProviderModelDto m)
    {
        var parts = new List<string>();
        if (m.Effective.DisplayName != m.ModelId) parts.Add(m.Effective.DisplayName);
        if (m.Effective.ContextWindow is { } c && c != 0) parts.Add(Pricing.PriceFormat.Context(c));
        var price = Pricing.PriceFormat.Summary(m.EffectivePricing);
        if (!string.IsNullOrEmpty(price)) parts.Add(price);
        return string.Join(" · ", parts);
    }

    /// <summary>Remote models matching the search box (id, case-insensitive).</summary>
    public static List<RemoteModelDto> FilterRemote(IEnumerable<RemoteModelDto>? models, string search)
    {
        var q = search.ToLowerInvariant();
        return (models ?? []).Where(m => q.Length == 0 || m.Id.ToLowerInvariant().Contains(q, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The ids typed into the add-models box: split on whitespace and commas, trimmed, de-duplicated in order.</summary>
    public static List<string> ParseModelIds(string text)
        => text.Split([' ', '\t', '\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The non-empty string entries of <c>settings.model_map</c> (web <c>modelMapOf</c>), in their stored order.</summary>
    public static List<KeyValuePair<string, string>> ModelMapOf(JsonObject? settings)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (settings?["model_map"] is not JsonObject raw) return list;
        foreach (var (from, to) in raw)
            if (from.Length > 0 && to is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) list.Add(new(from, s));
        return list;
    }

    /// <summary>The map with <paramref name="from"/> set to <paramref name="to"/> (a new key goes last, an existing one keeps its place).</summary>
    public static Dictionary<string, string> WithMapping(IEnumerable<KeyValuePair<string, string>> map, string from, string to)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in map) d[k] = v;
        d[from] = to;
        return d;
    }

    /// <summary>The map without <paramref name="from"/>.</summary>
    public static Dictionary<string, string> WithoutMapping(IEnumerable<KeyValuePair<string, string>> map, string from)
        => map.Where(kv => kv.Key != from).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>The template update sheet: nothing newer than what the provider has.</summary>
    public static bool UpToDate(TemplateUpdateDto? info) => info is not null && info.LatestVersion <= info.CurrentVersion;

    /// <summary>"Last N days" hint under the usage table plus the day's time zone (web <c>ProviderUsageHint</c>).</summary>
    public static string UsageHint(string? timeZone)
        => string.IsNullOrEmpty(timeZone) ? Loc.T("providers.usage.hint") : Loc.T("providers.usage.hint") + " " + Loc.T("providers.usage.dayHint", ("zone", timeZone));

    /// <summary>Price-key choices of the billing select: inherit (named after the template), none, then every known key.</summary>
    public static List<(string Value, string Label, string? Detail)> PriceKeyOptions(ProviderDto p, IEnumerable<PriceKeyInfo>? keys)
    {
        var list = new List<(string, string, string?)>
        {
            ("inherit", Loc.T("providers.priceKeyDefault", ("id", p.TemplateId ?? "—")), null),
            ("price:", Loc.T("providers.priceKeyNone"), null),
        };
        list.AddRange((keys ?? []).Select(k => ("price:" + k.PriceKey, k.Label, (string?)k.PriceKey)));
        return list;
    }
}

/// <summary>Moving an API-key provider onto another template (web components/TemplateSwitch.tsx, logic only).</summary>
public static class TemplateSwitchLogic
{
    /// <summary>A provider signed in with accounts has no template to switch (web <c>canSwitchTemplate</c>).</summary>
    public static bool CanSwitch(ProviderDto p) => p.AuthScheme is not ("oauth-subscription" or "plugin");

    /// <summary>Templates a provider can move onto: services that take an API key (web <c>switchable</c>).</summary>
    public static bool Switchable(ProviderTemplateInfo t) => t.Category is not ("subscription" or "custom") && t.AuthScheme != "oauth-subscription";

    /// <summary>The endpoints the target (or its variant) would give the provider.</summary>
    public static IReadOnlyList<TemplateEndpoint> Endpoints(ProviderTemplateInfo? target, string variantId)
        => target is null ? [] : target.Variants.FirstOrDefault(v => v.Id == variantId)?.EndpointsOverride ?? target.Endpoints;

    /// <summary>The target option's label: "current" marked.</summary>
    public static string OptionLabel(ProviderDto p, ProviderTemplateInfo t)
        => t.Id == p.TemplateId ? Loc.T("providers.templateSwitchCurrent", ("name", t.Name)) : t.Name;

    /// <summary>Whether a 401/403 should offer key alternatives (web <c>KeyAlternativeHint</c> guard).</summary>
    public static bool OffersAlternatives(ProviderDto p, int? httpStatus)
        => httpStatus is 401 or 403 && !string.IsNullOrEmpty(p.TemplateId) && CanSwitch(p);

    /// <summary>The current template and its key alternatives that exist (empty when none).</summary>
    public static (ProviderTemplateInfo? Current, List<ProviderTemplateInfo> Alternatives) Alternatives(ProviderDto p, IReadOnlyList<ProviderTemplateInfo>? all)
    {
        var current = all?.FirstOrDefault(x => x.Id == p.TemplateId);
        var alts = current is null ? [] : current.KeyAlternatives.SelectMany(id => all!.Where(x => x.Id == id)).ToList();
        return (current, alts);
    }

    /// <summary>The distinct base URLs of a template, comma separated (the confirm's detail).</summary>
    public static string Urls(ProviderTemplateInfo t) => string.Join(", ", t.Endpoints.Select(e => e.BaseUrl).Distinct());
}

/// <summary>Relative times of the quota views (web lib/tray.ts <c>resetsIn</c>, <c>updatedAgo</c>; <c>Intl.RelativeTimeFormat</c> with numeric "auto").</summary>
public static class QuotaTimes
{
    /// <summary>"in 3 hours" / "3小时后"; null when unknown or past.</summary>
    public static string? ResetsIn(string? iso, DateTimeOffset now, string language)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        var ms = (at - now).TotalMilliseconds;
        if (ms <= 0) return null;
        var hours = ms / 3_600_000;
        if (hours >= 48) return Future(Round(hours / 24), "day", language);
        if (hours >= 1) return Future(Round(hours), "hour", language);
        return Future(Math.Max(1, Round(ms / 60_000)), "minute", language);
    }

    /// <summary><paramref name="justNow"/> under a minute, then "3 minutes ago" / "3分钟前"; null for none.</summary>
    public static string? UpdatedAgo(DateTimeOffset? at, DateTimeOffset now, string language, string justNow)
    {
        if (at is null) return null;
        var seconds = Math.Max(0, (now - at.Value).TotalSeconds);
        if (seconds < 60) return justNow;
        if (seconds < 3600) return Past(Round(seconds / 60), "minute", language);
        return Past(Round(seconds / 3600), "hour", language);
    }

    /// <summary>"重置" line of a plan: <c>providers.quota.resets</c> with the time, or null.</summary>
    public static string? PlanResets(QuotaPlan p, DateTimeOffset now)
        => ResetsIn(p.ResetsAtUtc, now, Loc.Language) is { } when ? Loc.T("providers.quota.resets", ("time", when)) : null;

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    private static string Future(long n, string unit, string language)
    {
        if (language == "zh")
        {
            if (unit == "day" && n == 1) return "明天";
            if (unit == "day" && n == 2) return "后天";
            return n + (unit switch { "day" => "天", "hour" => "小时", _ => "分钟" }) + "后";
        }
        if (unit == "day" && n == 1) return "tomorrow";
        return $"in {n} {unit}{(n == 1 ? "" : "s")}";
    }

    private static string Past(long n, string unit, string language)
    {
        if (language == "zh") return n + (unit == "hour" ? "小时" : "分钟") + "前";
        return $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }
}
