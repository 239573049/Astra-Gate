using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Mcp;
using Astra.Data;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Astra.Gateway.Mcp;

/// <summary>What the host tells the MCP gateway about itself (set once at startup).</summary>
public sealed class McpRuntime
{
    /// <summary>Where the admin's browser reaches this server; the OAuth redirect address is built from it.</summary>
    public string GatewayBaseUrl { get; set; } = "http://127.0.0.1:17321";

    /// <summary>The path the browser lands on after authorizing (served by the admin API).</summary>
    public const string OAuthCallbackPath = "/api/mcp/oauth/callback";

    public Uri OAuthRedirectUri => new(GatewayBaseUrl.TrimEnd('/') + OAuthCallbackPath);
}

/// <summary>
/// The upstream side of the MCP gateway: one long-lived SDK client per upstream server, found again by the server's settings stamp
/// (<c>updated_at</c>), so editing a server drops its connection. The SDK finds out what the upstream speaks (Streamable HTTP of either
/// era, or the deprecated HTTP+SSE) and keeps the session of an initialize-based one; this class adds what the gateway needs on top:
/// a cached tool list, a time limit per call, the server's status written back, and failures sorted into
/// <see cref="McpUpstreamException"/>. A request here never starts an interactive OAuth flow (<see cref="McpNeedsAuthException"/>).
/// </summary>
public sealed class McpUpstreamPool(AstraDatabase db, ISecretProtector secrets, McpRuntime runtime, ILogger<McpUpstreamPool> logger) : IAsyncDisposable
{
    /// <summary>A status write is skipped when nothing changed and the last one is younger than this.</summary>
    private static readonly TimeSpan StatusRefresh = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new(NewHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private static SocketsHttpHandler NewHandler()
    {
        var handler = SystemProxy.CreateHandler(TimeSpan.FromSeconds(30));
        // Upstream requests never carry the gateway's trace context (linked update 15 does the same for model upstreams).
        handler.ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator();
        return handler;
    }

    /// <summary>Test seam: replaces the transport (an in-process upstream); null = the real network.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private sealed class Entry(string stamp)
    {
        public string Stamp { get; } = stamp;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public McpClient? Client;
        public IReadOnlyList<Tool>? Tools;
        public DateTimeOffset ToolsAt;
        public (string Status, string? Message, string? Version, DateTimeOffset At)? LastStatus;
    }

    private Entry EntryFor(McpServerRecord server)
    {
        var stamp = server.UpdatedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (true)
        {
            var entry = _entries.GetOrAdd(server.Id, _ => new Entry(stamp));
            if (entry.Stamp == stamp) return entry;
            // The settings changed since this connection was made: forget it.
            if (_entries.TryUpdate(server.Id, new Entry(stamp), entry)) _ = DisposeClientAsync(entry);
        }
    }

    /// <summary>Drops the connection and the cached list of a server (its settings, credentials or authorization changed, or it was deleted).</summary>
    public void Invalidate(string serverId)
    {
        if (_entries.TryRemove(serverId, out var entry)) _ = DisposeClientAsync(entry);
    }

    private static async Task DisposeClientAsync(Entry entry)
    {
        McpClient? client;
        await entry.Gate.WaitAsync();
        try
        {
            client = entry.Client;
            entry.Client = null;
        }
        finally
        {
            entry.Gate.Release();
        }
        if (client is not null) await client.DisposeAsync();
    }

    /// <summary>
    /// The transport for a server. <paramref name="authorize"/> is the OAuth callback: the data plane passes null (its callback refuses),
    /// an admin's "authorize" passes the handler that opens a browser.
    /// </summary>
    public HttpClientTransport CreateTransport(McpServerRecord server, Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>>? authorize = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
            TransportMode = server.Transport == McpTransports.Sse ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
            Name = "astra-" + server.Slug,
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, McpLimits.MinTimeoutSeconds, McpLimits.MaxTimeoutSeconds)),
        };
        if (server.AuthType == McpAuthTypes.Header && !string.IsNullOrEmpty(server.HeadersEnc))
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (JsonNode.Parse(secrets.Unprotect(server.HeadersEnc)) is JsonObject map)
                foreach (var (name, value) in map)
                    if ((string?)value is { Length: > 0 } text) headers[name] = text;
            options.AdditionalHeaders = headers;
        }
        if (server.AuthType == McpAuthTypes.OAuth)
        {
            options.OAuth = new ClientOAuthOptions
            {
                RedirectUri = runtime.OAuthRedirectUri,
                TokenCache = new McpTokenCache(db, secrets, server.Id),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Astra", ClientUri = new Uri("https://astra-gate.si") },
                AuthorizationCallbackHandler = authorize ?? ((_, _) => throw new McpNeedsAuthException("OAuth authorization is needed")),
            };
        }
        var http = HandlerFactory is { } factory ? new HttpClient(factory()) { Timeout = Timeout.InfiniteTimeSpan } : _http;
        return new HttpClientTransport(options, http, loggerFactory: null, ownsHttpClient: !ReferenceEquals(http, _http));
    }

    private async Task<McpClient> ClientAsync(McpServerRecord server, Entry entry, CancellationToken ct)
    {
        if (entry.Client is { } existing) return existing;
        // An OAuth server nobody has authorized has nothing to present, and the SDK would register a new client at the authorization server
        // before it ever asks us for a browser (every request would add one): the data plane does not even try.
        if (server.AuthType == McpAuthTypes.OAuth && await db.Mcp.GetOAuthAsync(server.Id, ct) is null)
            throw new McpNeedsAuthException("OAuth authorization is needed");
        var client = await McpClient.CreateAsync(CreateTransport(server), new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "astra-gateway", Version = ServerVersion },
        }, cancellationToken: ct);
        entry.Client = client;
        return client;
    }

    /// <summary>The version Astra reports to upstreams and to clients.</summary>
    public static string ServerVersion { get; set; } = "0.0.0";

    /// <summary>
    /// The upstream's own tool list, reused for <see cref="McpLimits.ToolListTtl"/> (<paramref name="refresh"/> asks again). One caller at a
    /// time asks the upstream; the others wait for it and share the answer.
    /// </summary>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(McpServerRecord server, bool refresh, CancellationToken ct)
    {
        var entry = EntryFor(server);
        if (!refresh && entry.Tools is { } cached && DateTimeOffset.UtcNow - entry.ToolsAt < McpLimits.ToolListTtl) return cached;
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (!refresh && entry.Tools is { } again && DateTimeOffset.UtcNow - entry.ToolsAt < McpLimits.ToolListTtl) return again;
            var tools = await RunAsync(server, entry, async (client, token) =>
            {
                var found = await client.ListToolsAsync(cancellationToken: token);
                return found.Select(t => t.ProtocolTool).ToList();
            }, ct);
            entry.Tools = tools;
            entry.ToolsAt = DateTimeOffset.UtcNow;
            return tools;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Calls a tool of the upstream (its own name). Never retried: the call may already have had its effect.</summary>
    public async Task<CallToolResult> CallToolAsync(McpServerRecord server, string tool, JsonObject? arguments, CancellationToken ct)
    {
        var entry = EntryFor(server);
        var parameters = new CallToolRequestParams { Name = tool, Arguments = ToArguments(arguments) };
        // The connection is made under the gate (one at a time), the call itself runs outside it: calls to one server run concurrently.
        return await RunAsync(server, entry, (client, token) => client.CallToolAsync(parameters, token).AsTask(), ct, gateOnlyForConnect: true);
    }

    private static Dictionary<string, JsonElement>? ToArguments(JsonObject? arguments)
    {
        if (arguments is null) return null;
        var result = new Dictionary<string, JsonElement>(arguments.Count, StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
            result[name] = JsonDocument.Parse(value is null ? "null" : value.ToJsonString()).RootElement.Clone();
        return result;
    }

    /// <summary>Connects if needed, runs <paramref name="action"/> within the server's time limit, records how it went, and sorts a failure out.</summary>
    private async Task<T> RunAsync<T>(McpServerRecord server, Entry entry, Func<McpClient, CancellationToken, Task<T>> action, CancellationToken ct,
        bool gateOnlyForConnect = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, McpLimits.MinTimeoutSeconds, McpLimits.MaxTimeoutSeconds)));
        var watch = Stopwatch.StartNew();
        McpClient client;
        try
        {
            if (gateOnlyForConnect)
            {
                await entry.Gate.WaitAsync(timeout.Token);
                try { client = await ClientAsync(server, entry, timeout.Token); }
                finally { entry.Gate.Release(); }
            }
            else client = await ClientAsync(server, entry, timeout.Token);
            var result = await action(client, timeout.Token);
            await NoteAsync(server, entry, McpServerStatuses.Ok, null, client.NegotiatedProtocolVersion);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller went away: neither the upstream's fault nor worth a status
        }
        catch (Exception ex) when (ex is not McpUpstreamException)
        {
            var failure = McpUpstreamErrors.Classify(ex, timedOut: timeout.IsCancellationRequested);
            logger.LogWarning("MCP 上游 {Slug} 失败（{Category}，{Ms} ms）：{Message}", server.Slug, failure.Category, watch.ElapsedMilliseconds, ex.GetType().Name);
            // A broken connection is rebuilt by the next request (which finds out again what the upstream speaks).
            if (failure.Category is McpErrorCategories.Unreachable or McpErrorCategories.Timeout or McpErrorCategories.Protocol) await ForgetClientAsync(entry);
            await NoteAsync(server, entry, failure.Status, failure.Message, null);
            throw failure;
        }
    }

    private static async Task ForgetClientAsync(Entry entry)
    {
        McpClient? client;
        lock (entry) { client = entry.Client; entry.Client = null; }
        if (client is not null)
        {
            try { await client.DisposeAsync(); }
            catch (Exception) { /* already broken */ }
        }
    }

    private async Task NoteAsync(McpServerRecord server, Entry entry, string status, string? message, string? version)
    {
        var now = DateTimeOffset.UtcNow;
        if (entry.LastStatus is { } last && last.Status == status && last.Message == message && (version is null || last.Version == version)
            && now - last.At < StatusRefresh) return;
        entry.LastStatus = (status, message, version ?? entry.LastStatus?.Version, now);
        try
        {
            await db.Mcp.UpdateStatusAsync(server.Id, status, message, version ?? entry.LastStatus?.Version, now);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "MCP 上游状态写入失败");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _entries.Keys.ToList()) Invalidate(id);
        _http.Dispose();
        await Task.CompletedTask;
    }
}
