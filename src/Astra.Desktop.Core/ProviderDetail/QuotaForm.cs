using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>
/// The editable state of <c>ProviderQuotaSection</c> (web <c>Form</c>): the template choice, the parameters, newly typed secrets, the custom
/// request and extractor as JSON text. Immutable; edits produce a copy. <see cref="Same"/> is the web's <c>JSON.stringify(form) !== JSON.stringify(base)</c>.
/// </summary>
public sealed record QuotaForm(
    bool Enabled, string Template, decimal? IntervalMinutes, decimal? TimeoutSec, string BaseUrl,
    IReadOnlyDictionary<string, string> Params, IReadOnlyDictionary<string, string> Secrets,
    string Method, string Url, string Auth, string Headers, string Extract)
{
    /// <summary>The template choice "suggested by the server" (web <c>AUTO</c>).</summary>
    public const string Auto = "";

    /// <summary>The custom-query choice.</summary>
    public const string Custom = "custom";

    /// <summary>The parameters a custom query can use besides {{apiKey}} / {{baseUrl}} / {{origin}} / {{host}} (web <c>CUSTOM_PARAMS</c>).</summary>
    public static readonly IReadOnlyList<QuotaParamSpecDto> CustomParams = [new("userId", false, false), new("accessToken", true, false)];

    /// <summary>Starting point of a custom query when no template applies (web <c>GENERIC_REQUEST</c>).</summary>
    public static readonly QuotaRequestSpecDto GenericRequest = new("GET", "{{baseUrl}}/user/balance", "provider", []);

    /// <summary>web <c>GENERIC_EXTRACT</c>.</summary>
    public static JsonObject GenericExtract() => new() { ["isValid"] = "is_active", ["remaining"] = "balance", ["unit"] = new JsonObject { ["$const"] = "USD" } };

    /// <summary>web <c>pretty</c>: <c>JSON.stringify(v ?? {}, null, 2)</c>.</summary>
    public static string Pretty(JsonNode? node) => Astra.Desktop.Core.Pricing.PricingDocument.PrettyJson(node ?? new JsonObject());

    /// <summary>The form of a saved configuration (web <c>toForm</c>).</summary>
    public static QuotaForm From(ProviderQuotaConfigDto c)
    {
        var r = c.Request;
        return new QuotaForm(c.Enabled, c.Template ?? Auto, c.IntervalMinutes, c.TimeoutSec, c.BaseUrl ?? "",
            new Dictionary<string, string>(c.Params), new Dictionary<string, string>(),
            (r is null ? null : Str(r, "method")) ?? "GET", (r is null ? null : Str(r, "url")) ?? "",
            (r is null ? null : Str(r, "auth")) ?? "provider", Pretty(r?["headers"]), c.Extract is null ? "" : Pretty(c.Extract));
    }

    // The saved configuration's request is still free JSON on ProviderQuotaConfigDto.
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Whether two forms hold the same values (dictionary contents compared).</summary>
    public bool Same(QuotaForm other)
        => Enabled == other.Enabled && Template == other.Template && IntervalMinutes == other.IntervalMinutes && TimeoutSec == other.TimeoutSec
           && BaseUrl == other.BaseUrl && SameMap(Params, other.Params) && SameMap(Secrets, other.Secrets) && Method == other.Method && Url == other.Url
           && Auth == other.Auth && Headers == other.Headers && Extract == other.Extract;

    private static bool SameMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>The custom form is open.</summary>
    public bool IsCustom => Template == Custom;

    /// <summary>The template in effect: the chosen one, or the server's suggestion for <see cref="Auto"/>.</summary>
    public QuotaTemplateDto? Chosen(IReadOnlyList<QuotaTemplateDto> all, string? suggested)
        => Template == Auto ? all.FirstOrDefault(x => x.Id == suggested) : all.FirstOrDefault(x => x.Id == Template);

    /// <summary>The parameter rows to show (custom: <see cref="CustomParams"/>).</summary>
    public IReadOnlyList<QuotaParamSpecDto> ParamsFor(IReadOnlyList<QuotaTemplateDto> all, string? suggested)
        => IsCustom ? CustomParams : Chosen(all, suggested)?.Params ?? [];

    /// <summary>
    /// Picks another template (web <c>changeTemplate</c>): switching to Custom with no URL yet starts from the template in effect, so the person
    /// edits a working example; any other switch keeps the rest of the form.
    /// </summary>
    public QuotaForm WithTemplate(string template, IReadOnlyList<QuotaTemplateDto> all, string? suggested)
    {
        if (template != Custom || Url.Length > 0) return this with { Template = template };
        var from = Chosen(all, suggested) ?? all.FirstOrDefault(x => x.Id == suggested);
        var req = from?.Request ?? GenericRequest;
        var headers = new JsonObject();
        foreach (var (k, v) in req.Headers ?? []) headers[k] = v;
        return this with
        {
            Template = template, Method = req.Method, Url = req.Url, Auth = req.Auth, Headers = Pretty(headers),
            Extract = Pretty(from?.Extract?.DeepClone() ?? GenericExtract()),
        };
    }

    /// <summary>A copy with one parameter set.</summary>
    public QuotaForm WithParam(string name, string value) => this with { Params = new Dictionary<string, string>(Params) { [name] = value } };

    /// <summary>A copy with one secret typed.</summary>
    public QuotaForm WithSecret(string name, string value) => this with { Secrets = new Dictionary<string, string>(Secrets) { [name] = value } };

    /// <summary>
    /// The body of save / test (web <c>input()</c>). For a custom query whose headers or extractor is not a JSON object, returns null and the
    /// toast text in <paramref name="error"/> (<c>providers.quota.invalidJson</c> naming the field).
    /// </summary>
    public JsonObject? ToInput(out string? error)
    {
        error = null;
        var body = new JsonObject
        {
            ["enabled"] = Enabled,
            ["template"] = Template == Auto ? null : Template,
            ["intervalMinutes"] = IntervalMinutes is { } i ? JsonValue.Create(i) : null,
            ["timeoutSec"] = TimeoutSec ?? 10,
            ["baseUrl"] = BaseUrl.Trim() is { Length: > 0 } b ? b : null,
        };
        var ps = new JsonObject();
        foreach (var (k, v) in Params) if (v.Trim().Length > 0) ps[k] = v.Trim();
        body["params"] = ps;
        var ss = new JsonObject();
        foreach (var (k, v) in Secrets) if (v.Trim().Length > 0) ss[k] = v;
        body["secrets"] = ss;
        if (IsCustom)
        {
            var headers = ParseObject(Headers);
            var extract = ParseObject(Extract);
            if (headers is null || extract is null)
            {
                error = Loc.T("providers.quota.invalidJson", ("field", Loc.T(headers is not null ? "providers.quota.extract" : "providers.quota.headers")));
                return null;
            }
            body["request"] = new JsonObject { ["method"] = Method, ["url"] = Url.Trim(), ["auth"] = Auth, ["headers"] = headers };
            body["extract"] = extract;
        }
        return body;
    }

    /// <summary>web <c>parseObject</c>: the text as a JSON object ("" = {}), else null.</summary>
    public static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The template choices (web <c>templateOptions</c>): Auto (named after the suggestion), every template with its kind, Custom.</summary>
    public static List<(string Value, string Label, string? Detail)> TemplateOptions(IReadOnlyList<QuotaTemplateDto> all, string? suggestedId)
    {
        var suggested = all.FirstOrDefault(x => x.Id == suggestedId);
        var list = new List<(string, string, string?)>
        {
            (Auto, suggested is not null ? Loc.T("providers.quota.templateAuto", ("name", suggested.Name)) : Loc.T("providers.quota.templateNone"), null),
        };
        list.AddRange(all.Select(x => (x.Id, x.Name, (string?)Loc.T("providers.quota.kind." + x.Kind))));
        list.Add((Custom, Loc.T("providers.quota.custom"), null));
        return list;
    }

    /// <summary>"every N minutes" / "off" for the interval hint.</summary>
    public static string IntervalNow(ProviderQuotaConfigDto c) => c.EffectiveIntervalMinutes > 0
        ? Loc.T("providers.quota.intervalEvery", ("minutes", c.EffectiveIntervalMinutes))
        : Loc.T("providers.quota.intervalOff");
}
