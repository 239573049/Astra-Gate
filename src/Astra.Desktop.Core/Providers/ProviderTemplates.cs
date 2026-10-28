using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Providers;

/// <summary>One upstream address of a template or of a provider being created (web <c>TemplateEndpoint</c>).</summary>
/// <param name="Protocol">Wire protocol id: <c>openai-chat</c>, <c>openai-responses</c>, <c>anthropic</c>, <c>gemini</c>.</param>
/// <param name="BaseUrl">Base URL (or the full request URL when <paramref name="FullUrl"/>).</param>
/// <param name="FullUrl">True when <paramref name="BaseUrl"/> is the complete request URL; null = not set (omitted from the request).</param>
public sealed record TemplateEndpoint(string Protocol, string BaseUrl, bool? FullUrl = null);

/// <summary>A template variant (a region / plan with its own address and price key).</summary>
public sealed record TemplateVariant(string Id, string Name, string? PriceKey, List<TemplateEndpoint>? EndpointsOverride);

/// <summary>
/// A provider template as <c>GET /api/provider-templates</c> sends it (web <c>ProviderTemplate</c>). The server's own type lives in
/// <c>Astra.Providers</c>, which the desktop does not reference, so the fields the wizard reads are mirrored here and parsed tolerantly
/// from JSON (unknown fields ignored) — see <see cref="ProviderTemplates.Parse(JsonArray)"/>.
/// </summary>
public sealed record ProviderTemplateInfo(
    string Id, string Name, string Category, string? Icon, string? ApiKeyUrl, List<TemplateEndpoint> Endpoints,
    List<string> PreferredUpstreamProtocols, string AuthScheme, bool RequiresApiKey, int ModelCount, string? PriceKey,
    decimal? DefaultPriceMultiplier, List<TemplateVariant> Variants, List<(string Name, string Value)> DefaultHeaders, List<string> KeyAlternatives)
{
    /// <summary>Subscription templates sign in with accounts after creation (web <c>accounts</c>).</summary>
    public bool IsSubscription => AuthScheme == "oauth-subscription";
}

/// <summary>Protocol names the wizard and the cards print (web <c>PROTOCOL_OPTIONS</c>, <c>PROTOCOL_SHORT</c>, <c>AUTH_OPTIONS</c>).</summary>
public static class ProviderProtocols
{
    /// <summary>The four wire protocols with their full names, in the web's order.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Options =
    [
        ("openai-chat", "OpenAI Chat"), ("openai-responses", "OpenAI Responses"), ("anthropic", "Anthropic Messages"), ("gemini", "Gemini"),
    ];

    /// <summary>The auth schemes a custom provider can pick (labels are data in the web too; "oauth-subscription" is Chinese there).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> AuthOptions =
    [
        ("bearer", "Authorization: Bearer"), ("x-api-key", "x-api-key"), ("x-goog-api-key", "x-goog-api-key"), ("query-key", "?key="),
        ("none", "—"), ("oauth-subscription", "OAuth 订阅"),
    ];

    /// <summary>Full name of a protocol id (the id itself when unknown).</summary>
    public static string Name(string protocol) => Options.FirstOrDefault(o => o.Value == protocol).Label ?? protocol;

    /// <summary>Short name on template tiles: Chat / Responses / Anthropic / Gemini.</summary>
    public static string Short(string protocol) => protocol switch
    {
        "openai-chat" => "Chat",
        "openai-responses" => "Responses",
        "anthropic" => "Anthropic",
        "gemini" => "Gemini",
        _ => protocol,
    };
}

/// <summary>Parsing and listing of provider templates for the wizard (ProvidersPage.tsx <c>AddProviderSheet</c>).</summary>
public static partial class ProviderTemplates
{
    /// <summary>Category order of the template list (web <c>CATEGORY_ORDER</c>); each has a <c>providers.category.*</c> key.</summary>
    public static readonly IReadOnlyList<string> CategoryOrder = ["recommended", "official", "subscription", "coding", "cn", "aggregator", "inference", "local", "custom"];

    /// <summary>Prefix of a plugin provider's choice id (web <c>PLUGIN_CHOICE</c>).</summary>
    public const string PluginChoice = "plugin:";

    /// <summary>The choice id of the "custom" card.</summary>
    public const string CustomChoice = "custom";

    /// <summary>Reads the <c>GET /api/provider-templates</c> array.</summary>
    public static List<ProviderTemplateInfo> Parse(JsonArray? array)
    {
        var list = new List<ProviderTemplateInfo>();
        foreach (var node in array ?? [])
        {
            if (node is not JsonObject o || Str(o, "id") is not { } id) continue;
            var auth = Str(o, "authScheme") ?? (o["auth"] as JsonObject is { } a ? Str(a, "scheme") : null) ?? "bearer";
            var headers = new List<(string, string)>();
            if (o["defaultHeaders"] is JsonObject h)
                foreach (var kv in h) if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s)) headers.Add((kv.Key, s));
            var variants = new List<TemplateVariant>();
            if (o["variants"] is JsonArray va)
                foreach (var vn in va.OfType<JsonObject>())
                    variants.Add(new TemplateVariant(Str(vn, "id") ?? "", Str(vn, "name") ?? "", Str(vn, "priceKey"), vn["endpointsOverride"] is JsonArray eo ? Endpoints(eo) : null));
            list.Add(new ProviderTemplateInfo(
                id, Str(o, "name") ?? id, Str(o, "category") ?? "custom", Str(o, "icon"), Str(o, "apiKeyUrl"),
                o["endpoints"] is JsonArray ea ? Endpoints(ea) : [],
                Strings(o["preferredUpstreamProtocols"]), auth, o["requiresApiKey"] is JsonValue r && r.TryGetValue<bool>(out var rk) && rk,
                (o["models"] as JsonArray)?.Count ?? 0, Str(o, "priceKey"),
                o["defaultPriceMultiplier"] is JsonValue m && m.TryGetValue<decimal>(out var mult) ? mult : null,
                variants, headers, Strings(o["keyAlternatives"])));
        }
        return list;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string> Strings(JsonNode? node)
        => node is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null).Select(s => s!).ToList() : [];

    /// <summary>Reads an endpoint array (templates and the provider DTO share the shape).</summary>
    public static List<TemplateEndpoint> Endpoints(JsonArray array)
        => array.OfType<JsonObject>().Select(e => new TemplateEndpoint(Str(e, "protocol") ?? "openai-chat", Str(e, "baseUrl") ?? "",
            e["fullUrl"] is JsonValue f && f.TryGetValue<bool>(out var fu) ? fu : null)).ToList();

    /// <summary>Templates filtered by the search box (name or id contains it, case-insensitive) and grouped in <see cref="CategoryOrder"/>, empty groups dropped.</summary>
    public static List<(string Category, List<ProviderTemplateInfo> Items)> Group(IEnumerable<ProviderTemplateInfo> templates, string search)
    {
        var q = search.Trim().ToLowerInvariant();
        var list = templates.Where(x => q.Length == 0 || x.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal) || x.Id.Contains(q, StringComparison.Ordinal)).ToList();
        return CategoryOrder.Select(c => (c, list.Where(x => x.Category == c).ToList())).Where(g => g.Item2.Count > 0).ToList();
    }

    /// <summary>"Claude 订阅（Pro/Max）" -> ("Claude 订阅", "Pro/Max"): a tile shows the plan on its second line instead of cutting it off (web <c>splitPlan</c>).</summary>
    public static (string Label, string? Plan) SplitPlan(string name)
    {
        var m = PlanPattern().Match(name);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (name, null);
    }

    [GeneratedRegex(@"^(.+?)\s*[（(]([^（）()]+)[）)]$")]
    private static partial Regex PlanPattern();

    /// <summary>Ids of subscription templates that already have an instance (single-instance: greyed out in the wizard).</summary>
    public static HashSet<string> AddedSubscriptionTemplateIds(IEnumerable<Astra.Api.Contracts.ProviderDto> providers)
        => providers.Where(p => p.AuthScheme == "oauth-subscription" && !string.IsNullOrEmpty(p.TemplateId)).Select(p => p.TemplateId!).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Providers that plugins supply (generated by plugin sync, nothing to create: choosing one opens it), minus plugins no longer installed
    /// (<c>settings.plugin.absent</c>), filtered by the search text on name or id.
    /// </summary>
    public static List<Astra.Api.Contracts.ProviderDto> PluginProviders(IEnumerable<Astra.Api.Contracts.ProviderDto> providers, string search)
    {
        var q = search.Trim().ToLowerInvariant();
        return providers.Where(p => p.AuthScheme == "plugin"
            && !(p.Settings["plugin"] is JsonObject pl && pl["absent"] is JsonValue ab && ab.TryGetValue<bool>(out var absent) && absent)
            && (q.Length == 0 || p.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal) || p.Id.Contains(q, StringComparison.Ordinal))).ToList();
    }
}
