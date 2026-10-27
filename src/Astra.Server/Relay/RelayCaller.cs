using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Providers.Templates;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Astra.Server.Relay;

/// <summary>
/// One request's caller: the upstream credential it brought and the provider made for it (<see cref="RelayTarget"/>).
/// The credential goes upstream as it is — never stored, exchanged, refreshed or logged — and only to the provider's
/// own origins (<see cref="RelayTransport"/> checks every request).
/// </summary>
internal sealed class RelayCaller
{
    private readonly HashSet<string> _origins;

    public RelayCaller(string credential, string variant, Provider provider, TemplateModelListEndpoint? modelList, string? modelListUrl,
        IHeaderDictionary clientHeaders)
    {
        Credential = credential;
        Provider = provider;
        Auth = UpstreamAuthResolver.ForSecret(provider, credential);
        ModelList = modelList;
        ModelListUrl = modelListUrl;
        ClientHeaders = clientHeaders;
        _origins = provider.Endpoints.Select(e => Origin(new Uri(e.BaseUrl))).ToHashSet(StringComparer.Ordinal);
        CacheKey = $"{variant}\n{modelListUrl}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))}";
    }

    public string Credential { get; }

    public Provider Provider { get; }

    /// <summary>Where the credential goes on an upstream request: the provider's auth scheme decides, as for a stored key.</summary>
    public UpstreamAuth Auth { get; }

    /// <summary>The template's model list (and the headers it carries); null when the template has none.</summary>
    public TemplateModelListEndpoint? ModelList { get; }

    public string? ModelListUrl { get; }

    /// <summary>The caller's own request headers: the client headers the provider reads come from here.</summary>
    public IHeaderDictionary ClientHeaders { get; }

    /// <summary>The caller's model list in the cache: variant, address and the credential's SHA-256 — never the credential.</summary>
    public string CacheKey { get; }

    /// <exception cref="InvalidOperationException">Outside a request.</exception>
    public static RelayCaller Current(IHttpContextAccessor accessor) =>
        accessor.HttpContext?.Features.Get<RelayCaller>()
        ?? throw new InvalidOperationException("Relay credentials exist only during a request.");

    /// <summary>Whether a request to <paramref name="uri"/> may carry this caller's credential: one of the provider's origins, no user info.</summary>
    public bool MaySend(Uri uri) => uri.UserInfo.Length == 0 && _origins.Contains(Origin(uri));

    /// <summary>Whether <paramref name="request"/> carries this caller's credential, exactly where the provider expects it.</summary>
    public bool Carries(HttpRequestMessage request)
    {
        if (Auth.HeaderName is { } name)
            return request.Headers.TryGetValues(name, out var values) && values.ToList() is [var sent] && sent == Auth.HeaderValue;
        return Auth.QueryName is { } key && request.RequestUri is { } uri
            && QueryHelpers.ParseQuery(uri.Query).TryGetValue(key, out var value) && value == Auth.QueryValue;
    }

    /// <summary>
    /// A request the relay sends itself (model list, embeddings, token counting), headed like the pipeline's: the
    /// provider's own headers, then the caller's client headers — and with <paramref name="perRequest"/> the ones the
    /// provider's client adds to every call — then the credential.
    /// </summary>
    public HttpRequestMessage Request(HttpMethod method, string url, bool perRequest)
    {
        var request = new HttpRequestMessage(method,
            Auth.QueryName is { } key && Auth.QueryValue is { } value ? UpstreamUrls.WithQuery(url, key, value) : url);
        foreach (var (name, header) in Provider.ExtraHeaders) request.Headers.TryAddWithoutValidation(name, header);
        if (perRequest) ProviderClientHeaders.Apply(Provider, request, ClientHeaders);
        else ProviderClientHeaders.Forward(Provider, request, ClientHeaders);
        Auth.Apply(request);
        return request;
    }

    /// <summary>
    /// The credential of a request: <c>x-api-key</c>, else <c>x-goog-api-key</c>, else <c>Authorization: Bearer</c>. The one
    /// chosen must be a single visible-ASCII value of at most 4096 characters; a bad one is refused, never passed over.
    /// </summary>
    /// <exception cref="GatewayException">401: none, or the chosen one is malformed.</exception>
    internal static string ReadCredential(IHeaderDictionary headers)
    {
        string? credential;
        if (!StringValues.IsNullOrEmpty(headers["x-api-key"]))
            credential = One(headers["x-api-key"])?.Trim();
        else if (!StringValues.IsNullOrEmpty(headers["x-goog-api-key"]))
            credential = One(headers["x-goog-api-key"])?.Trim();
        else
        {
            var authorization = One(headers.Authorization);
            if (authorization is null || !AuthenticationHeaderValue.TryParse(authorization, out var parsed)
                || !parsed.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                throw Unauthorized();
            credential = parsed.Parameter?.Trim();
        }
        if (credential is null || credential.Length is 0 or > 4096 || credential.Any(c => c <= ' ' || c >= 127 || c == ','))
            throw Unauthorized();
        return credential;
    }

    private static string? One(StringValues values) =>
        values.Count > 1 ? throw Unauthorized() : values.Count == 0 ? null : values[0];

    private static GatewayException Unauthorized() =>
        new(401, "authentication_error", "请在 x-api-key、x-goog-api-key 或 Authorization: Bearer 中提供一个上游凭据（订阅的访问令牌或 API Key）。");

    private static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
}
