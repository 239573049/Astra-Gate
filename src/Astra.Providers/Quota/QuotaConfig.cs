using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Providers.Quota;

/// <summary>
/// A provider's balance / quota query settings, stored under <c>settings_json.quota</c>:
/// <code>
/// { "enabled": false, "template": "deepseek" | "custom" | null, "interval_minutes": 30 | 0 | null,
///   "timeout_sec": 10, "base_url": null, "params": { "userId": "1" }, "secrets": { "accessToken": "&lt;encrypted&gt;" },
///   "request": { "method", "url", "auth", "headers" }, "extract": { … } }   // request / extract: custom only
/// </code>
/// Disabled by default: a query is only sent (manually or in the background) once the user turns it on.
/// </summary>
public sealed partial record QuotaConfig
{
    public const string SettingsKey = "quota";
    public const int DefaultTimeoutSec = 10;
    public const int MaxIntervalMinutes = 1440;

    public bool Enabled { get; init; }

    /// <summary>Built-in template id, <see cref="QuotaTemplate.CustomId"/>, or null for the suggested template.</summary>
    public string? Template { get; init; }

    /// <summary>Background refresh override in minutes; 0 = never in the background; null = the global setting.</summary>
    public int? IntervalMinutes { get; init; }

    public int TimeoutSec { get; init; } = DefaultTimeoutSec;

    /// <summary>Overrides the provider endpoint used for <c>{{baseUrl}}</c> / <c>{{origin}}</c> / <c>{{host}}</c>.</summary>
    public string? BaseUrl { get; init; }

    public Dictionary<string, string> Params { get; init; } = [];

    /// <summary>Encrypted secret parameters (ISecretProtector); never returned by the API.</summary>
    public Dictionary<string, string> Secrets { get; init; } = [];

    public QuotaRequestSpec? Request { get; init; }
    public JsonObject? Extract { get; init; }

    /// <summary>Reads <c>settings.quota</c> leniently (a malformed value means "not configured").</summary>
    public static QuotaConfig From(JsonObject? settings)
    {
        if (settings?[SettingsKey] is not JsonObject q) return new QuotaConfig();
        return new QuotaConfig
        {
            Enabled = q["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && enabled,
            Template = Str(q["template"]),
            IntervalMinutes = Int(q["interval_minutes"]) is { } i ? Math.Clamp(i, 0, MaxIntervalMinutes) : null,
            TimeoutSec = Int(q["timeout_sec"]) is { } t ? Math.Clamp(t, 1, 60) : DefaultTimeoutSec,
            BaseUrl = Str(q["base_url"]),
            Params = Map(q["params"]),
            Secrets = Map(q["secrets"]),
            Request = q["request"] is JsonObject r ? ReadRequest(r) : null,
            Extract = q["extract"] as JsonObject,
        };
    }

    /// <summary>The <c>settings.quota</c> node for this config.</summary>
    public JsonObject ToNode()
    {
        var node = new JsonObject { ["enabled"] = Enabled, ["timeout_sec"] = TimeoutSec };
        if (Template is not null) node["template"] = Template;
        if (IntervalMinutes is { } i) node["interval_minutes"] = i;
        if (BaseUrl is not null) node["base_url"] = BaseUrl;
        if (Params.Count > 0) node["params"] = ToObject(Params);
        if (Secrets.Count > 0) node["secrets"] = ToObject(Secrets);
        if (Request is not null)
        {
            var request = new JsonObject { ["method"] = Request.Method, ["url"] = Request.Url, ["auth"] = Request.Auth };
            if (Request.Headers.Count > 0) request["headers"] = ToObject(Request.Headers);
            node["request"] = request;
        }
        if (Extract is not null) node["extract"] = Extract.DeepClone();
        return node;
    }

    /// <summary>Problems that make this config unsavable (empty when valid).</summary>
    public List<string> Validate(QuotaTemplateCatalog catalog)
    {
        var errors = new List<string>();
        if (Template is not null && Template != QuotaTemplate.CustomId && catalog.Get(Template) is null)
            errors.Add($"未知的查询模板 {Template}");
        if (BaseUrl is not null && (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var b) || b.Scheme is not ("http" or "https") || b.UserInfo.Length > 0))
            errors.Add("base_url 必须是不含凭据的 http(s) 地址");
        foreach (var (key, value) in Params.Concat(Secrets))
            if (!ParamName().IsMatch(key) || value.Any(char.IsControl)) errors.Add($"参数 {key} 无效");
        if (Template == QuotaTemplate.CustomId)
        {
            if (Request is null) errors.Add("自定义查询需要 request");
            else errors.AddRange(ValidateRequest(Request, "request"));
            if (Extract is null) errors.Add("自定义查询需要 extract");
            else errors.AddRange(QuotaExtractor.Validate(Extract));
        }
        return errors;
    }

    /// <summary>Validates a request spec (built-in or custom).</summary>
    public static List<string> ValidateRequest(QuotaRequestSpec request, string where)
    {
        var errors = new List<string>();
        if (request.Method is not ("GET" or "POST")) errors.Add($"{where}.method 只支持 GET / POST");
        if (request.Auth is not (QuotaAuth.Provider or QuotaAuth.None)) errors.Add($"{where}.auth 只支持 provider / none");
        var url = request.Url.Trim();
        if (url.Length == 0 || url.Any(char.IsControl)) errors.Add($"{where}.url 不能为空");
        else
        {
            var probe = Placeholder().Replace(url, m => m.Groups[1].Value switch
            {
                "origin" or "baseUrl" => "https://example.com",
                _ => "x",
            });
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                errors.Add($"{where}.url 必须是 http(s) 地址（可以用 {{{{origin}}}} / {{{{baseUrl}}}} 开头）");
        }
        foreach (var (name, value) in request.Headers)
        {
            if (!HeaderName().IsMatch(name) || ReservedHeaders.Contains(name)) errors.Add($"{where}.headers: 请求头 {name} 无效或保留");
            if (value is null || value.Any(char.IsControl)) errors.Add($"{where}.headers.{name}: 值无效");
        }
        return errors;
    }

    /// <summary>Placeholder names used in a request spec, in order of appearance.</summary>
    public static IEnumerable<string> PlaceholdersOf(QuotaRequestSpec request) =>
        new[] { request.Url }.Concat(request.Headers.Values)
            .SelectMany(text => Placeholder().Matches(text).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal);

    /// <summary>Replaces <c>{{name}}</c> placeholders; <paramref name="missing"/> receives names without a value.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> vars, Func<string, string, string> encode, List<string> missing) =>
        Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (vars.TryGetValue(name, out var value) && value.Length > 0) return encode(name, value);
            if (!missing.Contains(name)) missing.Add(name);
            return "";
        });

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Transfer-Encoding", "X-Astra-Admin", "X-Astra-Runtime-Token",
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ParamName();

    private static QuotaRequestSpec ReadRequest(JsonObject r) => new()
    {
        Method = (Str(r["method"]) ?? "GET").ToUpperInvariant(),
        Url = Str(r["url"]) ?? "",
        Auth = Str(r["auth"]) ?? QuotaAuth.Provider,
        Headers = Map(r["headers"]),
    };

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out _) ? null
        : QuotaExpression.Number(node) is { } d ? (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue) : null;

    private static Dictionary<string, string> Map(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not JsonObject o) return result;
        foreach (var (key, value) in o)
            if (value is JsonValue v && (v.TryGetValue<string>(out var s) ? s : QuotaExpression.Text(v)) is { } text)
                result[key] = text;
        return result;
    }

    private static JsonObject ToObject(Dictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (key, value) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[key] = value;
        return o;
    }
}
