using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Plugins.V2;

namespace Astra.Server.PluginsV2;

/// <summary>
/// The server's side of <see cref="IPluginV2Services"/>: secrets through DataProtection, the plugins' HTTP client (no
/// automatic redirects, no cookies, Astra's proxy, no connection to a private address), notifications kept for the
/// shells, the read-only data views (never a secret, a body or a client IP) and logging.
/// </summary>
public sealed class PluginV2HostServices(AstraDatabase db, ISecretProtector protector, ILoggerFactory loggers) : IPluginV2Services
{
    /// <summary>Reads the usage time zone setting (set by the composition root; UTC until then).</summary>
    public Func<TimeZoneInfo>? Zone { get; set; }

    public TimeZoneInfo UsageZone => Zone?.Invoke() ?? TimeZoneInfo.Utc;

    /// <summary>Reads the interface language setting (set by the composition root; "en" until then).</summary>
    public Func<string>? Language { get; set; }

    public string Locale => Language?.Invoke() ?? "en";

    private readonly ILogger _log = loggers.CreateLogger("Astra.Plugins.V2");
    private readonly ConcurrentQueue<PluginNotification> _notifications = new();
    private long _nextNotification;
    private HttpClient? _http;

    public string HostVersion => Hosting.ServerOptions.Version;

    public IPluginDataStore Data => db.PluginData;

    /// <summary>The v2 provider accounts (set at startup: PluginV2ProviderSync).</summary>
    public IPluginProviderAccounts? ProviderAccounts { get; set; }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);

    /// <summary>Tests answer the plugins' requests themselves (the integration suite); null sends them for real.</summary>
    internal HttpMessageHandler? TestHandler { get; set; }

    public HttpClient Http() => _http ??= TestHandler is { } test ? new HttpClient(test, disposeHandler: false) : new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Astra's proxy setting (system / custom / direct), as for the gateway's own upstream requests (02 §5)
        Proxy = SystemProxy.Dynamic,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // the host checked the name before the request; this checks the address actually dialled (DNS can change)
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault() ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} doesn't resolve");
            // through a proxy (often a local one, 127.0.0.1) this dials the proxy, not the target: the target's own name was
            // checked before the request (HostCalls), and the proxy is the user's own setting, so its address may be private
            if (Private(target) && !IsProxyHop(context.InitialRequestMessage.RequestUri, context.DnsEndPoint, SystemProxy.Dynamic)
                && !(IPAddress.IsLoopback(target) && Environment.GetEnvironmentVariable("ASTRA_PLUGIN_DEV_LOOPBACK") == "1"))
                throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves to a private address");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>
    /// Whether <paramref name="endPoint"/> is the proxy <paramref name="proxy"/> sends <paramref name="request"/> through. For an
    /// https request the connection being opened is the proxy's CONNECT, whose own address is <paramref name="request"/>: then
    /// it counts when it is the proxy the policy names for a public https address.
    /// </summary>
    internal static bool IsProxyHop(Uri? request, DnsEndPoint endPoint, IWebProxy? proxy)
    {
        if (request is null || proxy is null) return false;
        var tunnel = string.Equals(request.IdnHost, endPoint.Host, StringComparison.OrdinalIgnoreCase) && request.Port == endPoint.Port;
        var target = tunnel ? PublicProbe : request;
        if (proxy.IsBypassed(target) || proxy.GetProxy(target) is not { } via) return false;
        return string.Equals(via.IdnHost, endPoint.Host, StringComparison.OrdinalIgnoreCase) && via.Port == endPoint.Port;
    }

    private static readonly Uri PublicProbe = new("https://example.com/");

    private static bool Private(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 0;
    }

    public void Notify(string pluginId, string title, string? body, string tone)
    {
        _notifications.Enqueue(new PluginNotification(Interlocked.Increment(ref _nextNotification), pluginId, title, body, tone, DateTimeOffset.UtcNow));
        while (_notifications.Count > 200) _notifications.TryDequeue(out _);
        _log.LogInformation("Plugin {Plugin} notification: {Title}", pluginId, title);
    }

    /// <summary>Notifications after <paramref name="after"/> (an id the caller saw), oldest first.</summary>
    public IReadOnlyList<PluginNotification> Notifications(long after) => [.. _notifications.Where(n => n.Id > after)];

    public void Log(string pluginId, string level, string message)
    {
        var l = level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, "debug" => LogLevel.Debug, _ => LogLevel.Information };
        _log.Log(l, "plugin {Plugin}: {Message}", pluginId, message);
    }

    public async ValueTask<JsonNode?> QueryAsync(string what, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        switch (what)
        {
            case "requests.query":
            {
                // one request by id, or a page of them (newest first)
                if (Str(o?["id"]) is { } id)
                {
                    var one = await db.Requests.GetAsync(id, ct);
                    return new JsonObject { ["total"] = one is null ? 0 : 1, ["items"] = one is null ? new JsonArray() : new JsonArray(PluginRequestRows.Row(one)) };
                }
                var page = await db.Requests.QueryAsync(new RequestQuery
                {
                    From = Date(o?["from"]), To = Date(o?["to"]),
                    Model = Str(o?["model"]), ClientKind = Str(o?["client"]), Status = Str(o?["status"]),
                    TokenId = Str(o?["token"]), ProviderId = Str(o?["provider"]),
                    Page = Math.Max(1, Int(o?["page"]) ?? 1), PageSize = Math.Clamp(Int(o?["pageSize"]) ?? 50, 1, 200),
                }, ct);
                return new JsonObject
                {
                    ["total"] = page.Total,
                    ["items"] = new JsonArray([.. page.Items.Select(r => (JsonNode?)PluginRequestRows.Row(r))]),
                };
            }
            case "requests.stats":
            {
                var to = Date(o?["to"]) ?? DateTimeOffset.UtcNow;
                var from = Date(o?["from"]) ?? to.AddDays(-1);
                var client = Str(o?["client"]);
                var s = await db.Requests.SummaryAsync(from, to, client, null, ct);
                var result = new JsonObject
                {
                    ["requests"] = s.TotalRequests, ["success"] = s.SuccessRequests, ["costNanoUsd"] = s.TotalCostNanoUsd,
                    ["inputTokens"] = s.TotalInputTokens, ["outputTokens"] = s.TotalOutputTokens,
                    ["cacheReadTokens"] = s.TotalCacheReadTokens, ["cacheWriteTokens"] = s.TotalCacheWriteTokens,
                };
                // design/plugin-v2 02 §10 / 09 §4.2: groupBy day | hour → series, model | provider | client | token → groups
                if (Str(o?["groupBy"]) is { } groupBy)
                {
                    var offset = Math.Clamp(Int(o?["utcOffsetMinutes"]) ?? 0, -14 * 60, 14 * 60);
                    switch (groupBy)
                    {
                        case "day" or "hour":
                        {
                            var points = await db.Requests.TimeseriesAsync(from, to, groupBy, "client", client, offset, ct: ct);
                            result["series"] = new JsonArray([.. points.GroupBy(p => p.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal)
                                .Select(g => (JsonNode?)Totals(new JsonObject { ["bucket"] = g.Key }, g))]);
                            break;
                        }
                        case "model" or "provider" or "client" or "token":
                        {
                            var points = await db.Requests.TimeseriesAsync(from, to, "day", groupBy, client, offset, ct: ct);
                            result["groups"] = new JsonArray([.. points.GroupBy(p => p.GroupKey ?? "")
                                .Select(g => Totals(new JsonObject { ["key"] = g.Key.Length > 0 ? g.Key : null, ["label"] = g.Select(p => p.Label).LastOrDefault(l => l is not null) ?? (g.Key.Length > 0 ? g.Key : null) }, g))
                                .OrderByDescending(g => (long)g["costNanoUsd"]!).ThenByDescending(g => (long)g["requests"]!)
                                .Select(g => (JsonNode?)g)]);
                            break;
                        }
                        default:
                            throw new InvalidOperationException("groupBy must be day, hour, model, provider, client or token");
                    }
                }
                return result;
            }
            case "providers.list":
                return new JsonArray([.. (await db.Providers.ListAsync(ct)).Select(p => (JsonNode?)new JsonObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["enabled"] = p.Enabled, ["category"] = p.Category,
                    ["protocols"] = new JsonArray([.. p.Endpoints.Select(e => (JsonNode?)e.Protocol.ToId())]),
                })]);
            case "models.list":
                return new JsonArray([.. (await db.Models.ListAsync(null, ct)).Select(m => (JsonNode?)new JsonObject
                {
                    ["id"] = m.Id, ["name"] = m.DisplayName, ["vendor"] = m.Vendor, ["context"] = m.ContextWindow, ["maxOutput"] = m.MaxOutputTokens,
                })]);
            case "tokens.list":
                return new JsonArray([.. (await db.Tokens.ListAsync(ct)).Select(t => (JsonNode?)new JsonObject
                {
                    ["id"] = t.Id, ["name"] = t.Name, ["enabled"] = t.Enabled, ["mask"] = t.KeyPrefix is { } k ? k + "…" : null,
                })]);
        }
        return null;
    }

    private static JsonObject Totals(JsonObject o, IEnumerable<TimeseriesPoint> points)
    {
        long requests = 0, cost = 0, input = 0, output = 0;
        foreach (var p in points)
        {
            requests += p.Requests;
            cost += p.TotalCostNanoUsd;
            input += p.TotalInputTokens;
            output += p.TotalOutputTokens;
        }
        o["requests"] = requests;
        o["costNanoUsd"] = cost;
        o["inputTokens"] = input;
        o["outputTokens"] = output;
        return o;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static DateTimeOffset? Date(JsonNode? n) => Str(n) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
}

/// <summary>A notification a plugin sent (notifications.send), kept for the shells to show.</summary>
public sealed record PluginNotification(long Id, string Plugin, string Title, string? Body, string Tone, DateTimeOffset At);
