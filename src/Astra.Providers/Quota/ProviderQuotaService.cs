using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Models;

namespace Astra.Providers.Quota;

/// <summary>Machine-readable failure reasons of a balance / quota query (the UI translates them).</summary>
public static class QuotaErrors
{
    public const string Config = "config";
    public const string NoKey = "no_key";
    public const string Unauthorized = "unauthorized";
    public const string NoEndpoint = "no_endpoint";
    public const string RateLimited = "rate_limited";
    public const string Upstream = "upstream";
    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string NotJson = "not_json";
    public const string TooLarge = "too_large";
    public const string Empty = "empty";

    /// <summary>Failures caused by the configuration itself (the endpoints answer 400 for these).</summary>
    public static bool IsConfiguration(string? code) => code is Config or NoKey;
}

/// <summary>Everything one query needs. <see cref="SecretValues"/> are the decrypted secret parameters.</summary>
public sealed record QuotaQuery(Provider Provider, QuotaConfig Config, string? ApiKey, IReadOnlyDictionary<string, string> SecretValues);

/// <summary>
/// Outcome of a query. <see cref="Snapshot"/> is set when <see cref="Ok"/>; <see cref="Url"/> and
/// <see cref="Raw"/> (the parsed upstream body) are redacted — they never contain the API key or a secret.
/// </summary>
public sealed record QuotaQueryResult(
    bool Ok, string? ErrorCode, string? Error, int? HttpStatus, string? Url, JsonNode? Raw, JsonObject? Snapshot, string? Template);

/// <summary>
/// Sends a provider's balance / quota request and normalizes the answer (the API-key counterpart of
/// <c>SubscriptionQuotaService</c>). The snapshot has the shape
/// <c>{ fetchedAtUtc, template, kind, isValid, invalidMessage?, planLabel?, plans: [{ name?, unit?, remaining?,
/// total?, used?, usedPercent?, resetsAtUtc?, windowMinutes?, extra? }] }</c>. Failures are returned, never
/// thrown, and never change anything about the provider: a balance endpoint that refuses a key says
/// nothing about whether the key can call models.
/// </summary>
public sealed class ProviderQuotaService(IHttpClientFactory httpClientFactory, QuotaTemplateCatalog catalog, TimeProvider? timeProvider = null)
{
    public const string HttpClientName = "astra-provider-quota";
    public const int MaxResponseBytes = 1 << 20;
    private const string Mask = "••••";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The template a config resolves to (custom configs become an ad-hoc template); null when none applies.</summary>
    public QuotaTemplate? Effective(Provider provider, QuotaConfig config) => config.Template switch
    {
        QuotaTemplate.CustomId when config.Request is not null && config.Extract is not null => new QuotaTemplate
        {
            Id = QuotaTemplate.CustomId, Name = QuotaTemplate.CustomId, Request = config.Request, Extract = config.Extract,
        },
        QuotaTemplate.CustomId => null,
        null => catalog.Suggest(provider),
        var id => catalog.Get(id),
    };

    public async Task<QuotaQueryResult> QueryAsync(QuotaQuery query, CancellationToken ct = default)
    {
        var (provider, config) = (query.Provider, query.Config);
        var secrets = query.SecretValues.Values.Append(query.ApiKey ?? "").Where(s => s.Length >= 4).ToList();
        if (provider.AuthScheme == AuthSchemes.OAuthSubscription)
            return Fail(QuotaErrors.Config, "订阅提供商的额度在登录账号里查看", null);
        var template = Effective(provider, config);
        if (template is null)
            return Fail(QuotaErrors.Config, config.Template == QuotaTemplate.CustomId ? "自定义查询缺少 request 或 extract" : "没有适用于该提供商的查询模板，请选择模板或使用自定义查询", null);
        var spec = template.Request;

        // ----- variables -----
        var baseUrl = config.BaseUrl ?? BaseUrlOf(provider);
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return Fail(QuotaErrors.Config, "提供商没有可用的接口地址", template.Id);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in config.Params) vars[k] = v;
        foreach (var (k, v) in query.SecretValues) vars[k] = v;
        vars["baseUrl"] = baseUrl.TrimEnd('/');
        vars["origin"] = baseUri.GetLeftPart(UriPartial.Authority);
        vars["host"] = baseUri.Host;
        if (query.ApiKey is { Length: > 0 } key) vars["apiKey"] = key;

        foreach (var param in template.Params.Where(p => p.Required))
            if (!vars.TryGetValue(param.Name, out var value) || value.Length == 0)
                return Fail(QuotaErrors.Config, $"该查询需要参数 {param.Name}", template.Id);

        var missing = new List<string>();
        var url = QuotaConfig.Fill(spec.Url, vars, (name, value) => name is "origin" or "baseUrl" ? value : Uri.EscapeDataString(value), missing);
        var headers = spec.Headers.ToDictionary(h => h.Key, h => QuotaConfig.Fill(h.Value, vars, (_, value) => value, missing));
        if (missing.Contains("apiKey")) return Fail(QuotaErrors.NoKey, "该提供商没有 API Key", template.Id);
        if (missing.Count > 0) return Fail(QuotaErrors.Config, $"该查询需要参数 {string.Join(", ", missing)}", template.Id);

        // ----- authentication (the provider's own scheme, like the gateway) -----
        string? authName = null, authValue = null;
        if (spec.Auth == QuotaAuth.Provider && provider.AuthScheme != AuthSchemes.None)
        {
            if (query.ApiKey is not { Length: > 0 } apiKey) return Fail(QuotaErrors.NoKey, "该提供商没有 API Key", template.Id);
            switch (provider.AuthScheme)
            {
                case AuthSchemes.XApiKey: (authName, authValue) = ("x-api-key", apiKey); break;
                case AuthSchemes.XGoogApiKey: (authName, authValue) = ("x-goog-api-key", apiKey); break;
                case AuthSchemes.QueryKey: url = WithQuery(url, "key", apiKey); break;
                default: (authName, authValue) = ("Authorization", $"Bearer {apiKey}"); break;
            }
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
            return Fail(QuotaErrors.Config, "查询地址无效", template.Id);
        var shownUrl = Redact(target.ToString(), secrets);

        using var request = new HttpRequestMessage(new HttpMethod(spec.Method), target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in provider.ExtraHeaders) request.Headers.TryAddWithoutValidation(name, value);
        foreach (var (name, value) in headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (authName is not null)
        {
            request.Headers.Remove(authName);
            request.Headers.TryAddWithoutValidation(authName, authValue);
        }
        if (!request.Headers.UserAgent.Any()) request.Headers.TryAddWithoutValidation("User-Agent", "astra-quota/1.0");

        // ----- send -----
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSec));
        HttpStatusCode status;
        byte[] body;
        bool tooLarge;
        HttpClient? owned = null;
        try
        {
            var client = provider.HttpProxy is { } proxy ? owned = ProxyClient(proxy) : httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = response.StatusCode;
            (body, tooLarge) = await ReadLimitedAsync(response, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(QuotaErrors.Timeout, $"连接超时（{config.TimeoutSec} 秒）", template.Id, url: shownUrl);
        }
        catch (HttpRequestException e)
        {
            return Fail(QuotaErrors.Network, Redact($"网络错误：{e.Message}", secrets), template.Id, url: shownUrl);
        }
        finally
        {
            owned?.Dispose();
        }

        var code = (int)status;
        if (tooLarge) return Fail(QuotaErrors.TooLarge, "响应超过 1 MiB，已放弃解析", template.Id, code, shownUrl);
        var raw = TryParse(body, out var parsed) ? Redact(parsed, secrets) : null;
        if (code is < 200 or >= 300)
        {
            var upstream = UpstreamMessage(raw) is { } m ? $"：{Truncate(m, 200)}" : "";
            var (errorCode, text) = code switch
            {
                401 or 403 => (QuotaErrors.Unauthorized, $"上游拒绝了额度查询（HTTP {code}）"),
                404 or 405 or 501 => (QuotaErrors.NoEndpoint, $"该地址没有额度接口（HTTP {code}）"),
                429 => (QuotaErrors.RateLimited, "上游限流（HTTP 429），请调大查询间隔"),
                _ => (QuotaErrors.Upstream, $"上游错误（HTTP {code}）"),
            };
            return Fail(errorCode, text + upstream, template.Id, code, shownUrl, raw);
        }
        if (raw is null) return Fail(QuotaErrors.NotJson, "响应不是 JSON", template.Id, code, shownUrl);

        var extracted = QuotaExtractor.Extract(template.Extract, parsed, vars);
        if (extracted.Plans.Count == 0 && extracted.IsValid != false)
            return Fail(QuotaErrors.Empty, "查询成功但没有取到额度字段", template.Id, code, shownUrl, raw);

        var snapshot = new JsonObject
        {
            ["fetchedAtUtc"] = _clock.GetUtcNow().ToString("o"),
            ["template"] = template.Id,
            ["kind"] = template.Kind,
            ["isValid"] = extracted.IsValid ?? true,
        };
        if (extracted.IsValid == false && extracted.InvalidMessage is { } invalid) snapshot["invalidMessage"] = Truncate(Redact(invalid, secrets), 200);
        if (extracted.PlanLabel is { } label) snapshot["planLabel"] = Truncate(label, 100);
        snapshot["plans"] = new JsonArray([.. extracted.Plans]);
        return new QuotaQueryResult(true, null, null, code, shownUrl, raw, snapshot, template.Id);
    }

    /// <summary>The endpoint used for <c>{{baseUrl}}</c>: the first one in upstream-preference order.</summary>
    public static string? BaseUrlOf(Provider provider) =>
        provider.PreferredUpstreamProtocols.Select(provider.EndpointFor).FirstOrDefault(e => e is not null)?.BaseUrl
        ?? provider.Endpoints.FirstOrDefault()?.BaseUrl;

    private static QuotaQueryResult Fail(string code, string error, string? template, int? status = null, string? url = null, JsonNode? raw = null) =>
        new(false, code, error, status, url, raw, null, template);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client owns the handler and is disposed by the caller.")]
    private static HttpClient ProxyClient(string proxy) => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy(proxy), UseProxy = true, AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All,
    }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90) };

    private static async Task<(byte[] Body, bool TooLarge)> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return ([], true);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return ([], true);
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static bool TryParse(byte[] body, [NotNullWhen(true)] out JsonNode? node)
    {
        node = null;
        if (body.Length == 0) return false;
        try
        {
            node = JsonNode.Parse(body);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? UpstreamMessage(JsonNode? raw) =>
        QuotaExpression.Text(QuotaExpression.Evaluate(
            new JsonArray("error.message", "error", "message", "msg", "base_resp.status_msg"),
            new QuotaExpression.Scope(raw, raw, new Dictionary<string, string>())));

    private static string WithQuery(string url, string name, string value) =>
        url + (url.Contains('?') ? "&" : "?") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret) text = text.Replace(escaped, Mask, StringComparison.Ordinal);
        }
        return text;
    }

    private static JsonNode? Redact(JsonNode node, IReadOnlyList<string> secrets)
    {
        if (secrets.Count == 0) return node;
        var json = node.ToJsonString();
        var redacted = Redact(json, secrets);
        return redacted == json ? node : JsonNode.Parse(Encoding.UTF8.GetBytes(redacted));
    }
}
