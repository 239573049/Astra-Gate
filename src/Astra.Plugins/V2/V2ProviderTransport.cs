using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2;

/// <summary>
/// How a v2 provider's requests go out (design/plugin-v2 08 §2). The gateway sends to <c>plugin://v2.&lt;plugin id&gt;.&lt;provider&gt;/…</c>
/// like to any plugin provider; this asks the plugin's <c>prepare</c> for the address, the headers and maybe a new body, checks
/// the address against the plugin's <c>net:</c> grants and private addresses (as <c>http.fetch</c> does), and the host sends
/// it — so the reply streams straight back into the pipeline, and failover, accounts, billing and logs stay the gateway's.
/// <c>prepare</c> may return a JSON <c>body</c> or bytes (a <c>Uint8Array</c>, carried as <c>bodyBase64</c>; content-type from its
/// headers, else application/octet-stream), and a <c>state</c> object that its <c>transformResponse</c> starts with (08 §2.2).
/// </summary>
public sealed class V2ProviderTransport(PluginV2Host host)
{
    /// <summary>The address prefix (the URI host's first label) of a v2 provider.</summary>
    public const string HostPrefix = "v2.";

    /// <summary>The endpoint base of v2 provider <paramref name="providerId"/> of plugin <paramref name="pluginId"/> for a wire API.</summary>
    public static string BaseFor(string pluginId, string providerId, Core.ApiProtocol protocol) => protocol switch
    {
        Core.ApiProtocol.Anthropic => $"plugin://{HostPrefix}{pluginId}.{providerId}",
        Core.ApiProtocol.Gemini => $"plugin://{HostPrefix}{pluginId}.{providerId}/v1beta",
        _ => $"plugin://{HostPrefix}{pluginId}.{providerId}/v1",
    };

    /// <summary>(plugin id, provider id) of a v2 provider address's host, or null. A plugin id has exactly one dot.</summary>
    public static (string Plugin, string Provider)? Parse(string uriHost)
    {
        if (!uriHost.StartsWith(HostPrefix, StringComparison.Ordinal)) return null;
        var parts = uriHost[HostPrefix.Length..].Split('.');
        return parts.Length < 3 ? null : (parts[0] + "." + parts[1], string.Join('.', parts[2..]));
    }

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "x-api-key", "x-goog-api-key", "content-length", "host", "cookie", "transfer-encoding", "connection",
        Host.PluginFetchHandler.ConversationHeader,
    };

    private static readonly HashSet<string> Transport = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "transfer-encoding", "connection", "upgrade", "te", "trailer", "keep-alive", "proxy-authorization",
    };

    /// <summary>Sends one gateway request to a v2 provider. Failures of the plugin are <see cref="HttpRequestException"/>s, so the gateway fails over.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var (pluginId, providerId) = Parse(uri.Host) ?? throw new HttpRequestException($"{uri.Host} isn't a v2 provider's address");
        var plugin = await host.FindAsync(pluginId, ct) ?? throw new HttpRequestException($"{pluginId} isn't running: is it installed, switched on and granted?");
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        JsonNode? body = null;
        if (bytes.Length > 0)
        {
            try
            {
                body = JsonNode.Parse(bytes);
            }
            catch (JsonException)
            {
            }
        }
        var path = uri.AbsolutePath;
        var headers = new JsonObject();
        foreach (var h in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            if (!Dropped.Contains(h.Key)) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        request.Options.TryGetValue(Host.PluginFetchHandler.AccountOption, out var accountKey);
        var req = new JsonObject
        {
            ["provider"] = providerId,
            ["protocol"] = ProtocolOf(path),
            ["model"] = path.StartsWith("/v1beta/models/", StringComparison.Ordinal) ? path["/v1beta/models/".Length..].Split(':')[0] : body?["model"]?.GetValue<string>(),
            ["path"] = path + uri.Query,
            ["method"] = request.Method.Method,
            ["headers"] = headers,
            ["body"] = body?.DeepClone(),
            ["stream"] = body?["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) ? st : path.Contains(":streamGenerateContent", StringComparison.Ordinal),
        };
        JsonObject prepared;
        try
        {
            prepared = await host.ProviderCallAsync(pluginId, providerId, "prepare", req, accountKey is null ? null : new JsonObject { ["key"] = accountKey }, ct) as JsonObject
                       ?? throw new HttpRequestException($"{pluginId}: prepare returned nothing (it must return {{ url, headers }})");
        }
        catch (PluginCallException e)
        {
            throw new HttpRequestException($"{pluginId}: prepare failed: {e.Message}", e);
        }

        var url = prepared["url"]?.GetValue<string>() ?? throw new HttpRequestException($"{pluginId}: prepare returned no url");
        if (PluginPermissions.Origin(url, plugin.Dev) is not { } origin || !PluginPermissions.AllowsUrl(plugin.Granted, url, s => plugin.Setting(s), plugin.Dev))
            throw new HttpRequestException($"{pluginId}: {url} is not an origin the plugin may reach (net: permissions)");
        var target = new Uri(url);
        if ((!plugin.Dev || !target.IsLoopback) && await HostCalls.PrivateAsync(target.DnsSafeHost, ct) is { } why)
            throw new HttpRequestException($"{pluginId}: {target.Host} {why}");

        var method = prepared["method"]?.GetValue<string>() is { Length: > 0 } m ? new HttpMethod(m.ToUpperInvariant()) : request.Method;
        var outgoing = new HttpRequestMessage(method, target);
        // a new body: JSON (body), or bytes the boot carried as base64 (prepare returned a Uint8Array — protobuf, Connect frames, …)
        byte[] content;
        var binary = false;
        if (prepared["bodyBase64"] is JsonValue b64v && b64v.TryGetValue<string>(out var b64))
        {
            try
            {
                content = Convert.FromBase64String(b64);
            }
            catch (FormatException e)
            {
                throw new HttpRequestException($"{pluginId}: prepare returned a body that is not valid base64", e);
            }
            binary = true;
        }
        else content = prepared["body"] is { } newBody ? System.Text.Encoding.UTF8.GetBytes(newBody.ToJsonString()) : bytes;
        if (content.Length > 0 || request.Content is not null)
        {
            outgoing.Content = new ByteArrayContent(content);
            outgoing.Content.Headers.ContentType = binary
                ? new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream")
                : request.Content?.Headers.ContentType ?? new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        // the plugin sets the credential (that is its job); never the transport's own headers
        foreach (var (k, v) in prepared["headers"] as JsonObject ?? [])
        {
            if (v is not JsonValue hv || !hv.TryGetValue<string>(out var value) || Transport.Contains(k)) continue;
            if (outgoing.Headers.TryAddWithoutValidation(k, value) || outgoing.Content is null) continue;
            // a content header the plugin sets (content-type above all) replaces the default rather than adding a second value
            outgoing.Content.Headers.Remove(k);
            outgoing.Content.Headers.TryAddWithoutValidation(k, value);
        }
        // the gateway's own non-credential headers ride along unless the plugin set them
        foreach (var h in request.Headers)
            if (!Dropped.Contains(h.Key) && !outgoing.Headers.Contains(h.Key)) outgoing.Headers.TryAddWithoutValidation(h.Key, h.Value);
        await host.ActivityAsync(plugin, "provider.request", providerId + " → " + origin, "sent");
        var response = await host.Services.Http().SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);
        // a provider whose upstream isn't in a wire format the gateway decodes: its plugin translates the reply frame by frame (08 §2.2)
        if (plugin.Manifest.Providers.FirstOrDefault(p => p.Id == providerId)?.Reply is { } reply)
            return await Reply.V2ReplyTransform.ApplyAsync(host, pluginId, providerId, reply, req, accountKey is null ? null : new JsonObject { ["key"] = accountKey }, response, ct,
                prepared["state"] as JsonObject);
        return response;
    }

    /// <summary>
    /// After a 401 / 403: the plugin's refresh for the account — "renewed", "expired", or null (it has none). The plugin sees the
    /// refused reply as <c>{ status, body }</c> (<paramref name="body"/>: an excerpt, see <see cref="ExcerptAsync"/>).
    /// </summary>
    public async Task<string?> RefreshAsync(string pluginId, string providerId, string accountKey, int status, string? body, CancellationToken ct)
    {
        try
        {
            return (await host.ProviderCallAsync(pluginId, providerId, "refresh", new JsonObject { ["status"] = status, ["body"] = body ?? "" },
                new JsonObject { ["key"] = accountKey }, ct))?.GetValue<string>();
        }
        catch (PluginCallException)
        {
            return "expired";
        }
    }

    /// <summary>The first 4 KB of a refused reply's body as text (what a plugin's refresh is told); the reply stays readable only for this call.</summary>
    public static async Task<string?> ExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var s = await response.Content.ReadAsStreamAsync(ct);
            var buf = new byte[4096];
            var n = 0;
            int r;
            while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += r;
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ProtocolOf(string path) =>
        path.EndsWith("/messages", StringComparison.Ordinal) ? "anthropic"
        : path.EndsWith("/responses", StringComparison.Ordinal) ? "responses"
        : path.StartsWith("/v1beta/", StringComparison.Ordinal) ? "gemini"
        : "chat";
}
