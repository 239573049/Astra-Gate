using System.Text.Json.Nodes;

namespace Astra.Plugins.Host;

/// <summary>
/// The transport of a plugin's provider (magpie's pluginFetch): the gateway sends to <c>plugin://&lt;provider&gt;/…</c>
/// as to any upstream, and this sends the request through the provider's plugin instead — to the base URL its loader
/// gives (else the model's, the provider's, its AI SDK package's), the model id the vendor knows it by, the
/// account's sign-in added by the plugin. The plugin's word on the account's sign-in (<c>X-Magpie-Sign-In</c>) is
/// taken off the reply and told to <see cref="SignIn"/>.
/// </summary>
public sealed class PluginFetchHandler(PluginProviders providers) : HttpMessageHandler
{
    /// <summary>The providers this transport serves (to re-ask an account's loader after its sign-in was refused).</summary>
    public PluginProviders Providers => providers;

    /// <summary>The scheme of a plugin provider's endpoints.</summary>
    public const string Scheme = "plugin";

    /// <summary>The conversation a request belongs to, handed to the plugin's chat.headers hook as its session; it goes no further.</summary>
    public const string ConversationHeader = "X-Magpie-Conversation";

    /// <summary>How a plugin says what its answer means for the account's sign-in: expired, kept or renewed.</summary>
    public const string SignInHeader = "X-Magpie-Sign-In";

    /// <summary>The plugin-auth.json key of the account a request is made with (id, or id#slot).</summary>
    public static readonly HttpRequestOptionsKey<string> AccountOption = new("astra.plugin.account");

    /// <summary>The account's or provider's own proxy ("direct", or its URL); absent follows Astra's.</summary>
    public static readonly HttpRequestOptionsKey<string> ProxyOption = new("astra.plugin.proxy");

    /// <summary>
    /// Told after every reply: (provider, account, what the plugin said of the sign-in — "" for nothing, status).
    /// Without a word, a 401 marks the account and a success clears the mark.
    /// </summary>
    public event Action<string, string, string, int>? SignIn;

    /// <summary>The endpoint base of a plugin provider for a wire API.</summary>
    public static string BaseFor(string providerId, Core.ApiProtocol protocol) => protocol switch
    {
        Core.ApiProtocol.Anthropic => $"{Scheme}://{providerId}",
        Core.ApiProtocol.Gemini => $"{Scheme}://{providerId}/v1beta",
        _ => $"{Scheme}://{providerId}/v1",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("no request URI");
        if (uri.Scheme != Scheme) throw new ArgumentException($"{uri} isn't a plugin provider's address");
        var providerId = uri.Host;
        var rest = uri.PathAndQuery;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        request.Options.TryGetValue(AccountOption, out var account);
        request.Options.TryGetValue(ProxyOption, out var proxy);

        var list = await providers.ListAsync(ct: ct);
        var pp = list.FirstOrDefault(p => p.Id == providerId)
                 ?? throw new HttpRequestException($"no plugin serves {providerId} now: is its plugin installed and switched on?");
        var gemini = rest.StartsWith("/v1beta/models/", StringComparison.Ordinal);
        var model = gemini ? GeminiModel(rest) : BodyModel(body);
        var m = pp.Model(model ?? "") ?? new PluginModelInfo(model ?? "", model ?? "", pp.Npm, null, null, 0, 0, 0, false, false, [], null, null, false, false);
        var api = m.UpstreamId;
        if (!gemini && api != model) body = WithModel(body, api);

        var loaderBase = account is null ? null : await providers.LoaderBaseAsync(pp.Id, account, proxy, ct);
        var baseUrl = FirstOf(loaderBase, m.Url, pp.Api, m.Npm is not null && PluginProviders.NpmBase.TryGetValue(m.Npm, out var nb) ? nb : null)
                      ?? throw new HttpRequestException($"{pp.Name}'s plugin says nowhere to send its requests");
        baseUrl = baseUrl.TrimEnd('/');
        string url;
        if (gemini)
        {
            // Gemini's own request, the model in the path as @ai-sdk/google sends it
            var tail = rest["/v1beta/models/".Length..];
            var colon = tail.IndexOf(':');
            url = baseUrl + "/models/" + Uri.EscapeDataString(api) + (colon >= 0 ? tail[colon..] : "");
        }
        else if (rest.StartsWith("/v1/", StringComparison.Ordinal))
        {
            // chat, responses and Anthropic's messages: the AI SDK's base ends where the gateway's /v1 does
            url = baseUrl + rest["/v1".Length..];
        }
        else
        {
            url = baseUrl + rest;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? session = null;
        foreach (var (name, values) in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            var value = values.FirstOrDefault();
            if (value is null) continue;
            if (name.Equals(ConversationHeader, StringComparison.OrdinalIgnoreCase))
            {
                session = value;
                continue;
            }
            // the plugin adds the account's own credentials; anything Astra's gateway would add is not the vendor's
            if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                continue;
            headers[name.ToLowerInvariant()] = value;
        }

        HttpResponseMessage response;
        try
        {
            response = await providers.Host.FetchAsync(new HostFetchRequest(
                pp.Id, account, api, m.Npm ?? pp.Npm, url, request.Method.Method, headers, body, session, proxy), ct);
        }
        catch (PluginHostException e)
        {
            throw new HttpRequestException($"{pp.Name}'s plugin: {e.Message}", e);
        }
        var said = response.Headers.TryGetValues(SignInHeader, out var values2) ? values2.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "" : "";
        response.Headers.Remove(SignInHeader);
        response.RequestMessage = request;
        if (account is not null) SignIn?.Invoke(pp.Id, account, said, (int)response.StatusCode);
        return response;
    }

    private static string? GeminiModel(string rest)
    {
        var tail = rest["/v1beta/models/".Length..];
        var colon = tail.IndexOf(':');
        return Uri.UnescapeDataString(colon >= 0 ? tail[..colon] : tail.Split('?')[0]);
    }

    private static string? BodyModel(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body)?["model"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static byte[] WithModel(byte[] body, string model)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o) return body;
            o["model"] = model;
            return System.Text.Encoding.UTF8.GetBytes(o.ToJsonString());
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }

    private static string? FirstOf(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
