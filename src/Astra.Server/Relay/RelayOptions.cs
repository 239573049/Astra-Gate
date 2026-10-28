using System.Globalization;
using Astra.Server.Hosting;

namespace Astra.Server.Relay;

/// <summary>
/// Startup configuration of <c>astra-server relay</c>. Flags (<c>--provider</c>, <c>--variant</c>, <c>--base-url</c>,
/// <c>--host</c>, <c>--port</c>) win over the environment (<c>ASTRA_RELAY_*</c>, <c>ASTRA_HOST</c>, <c>ASTRA_PORT</c>);
/// the limits are environment-only. Nothing is read from or written to the data directory.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>The provider template every request goes to (<c>ASTRA_RELAY_PROVIDER</c>).</summary>
    public string Provider { get; set; } = "";

    /// <summary>The template variant used when a request names none (<c>ASTRA_RELAY_VARIANT</c>); null = the template's own endpoints.</summary>
    public string? Variant { get; set; }

    /// <summary>
    /// An origin that replaces the template's (<c>ASTRA_RELAY_BASE_URL</c>), each endpoint keeping its path: https, or
    /// http on loopback only. Credentials go to the template's origin or this one, never to an address a request names.
    /// </summary>
    public string? BaseUrl { get; set; }

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = ServerOptions.DefaultPort;

    /// <summary>Requests in flight; one more is answered 503 at once (health checks never wait).</summary>
    public int MaxConcurrentRequests { get; set; } = 64;

    /// <summary>The largest request body, as sent and once decompressed (16 KiB to 64 MiB).</summary>
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>How long a request body may take to arrive in full (408 after that).</summary>
    public int RequestBodyTimeoutSeconds { get; set; } = 300;

    /// <summary>How long the upstream may take to send its response headers (504 after that).</summary>
    public int HeadersTimeoutSeconds { get; set; } = 1800;

    /// <summary>How long an upstream body may send nothing.</summary>
    public int UpstreamIdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>How long writing one chunk of a relayed (model list, embeddings, token count) answer to the client may take.</summary>
    public int DownstreamWriteTimeoutSeconds { get; set; } = 300;

    public int ShutdownTimeoutSeconds { get; set; } = 600;

    /// <summary>How long a credential's model list is reused.</summary>
    public int ModelCacheSeconds { get; set; } = 300;

    /// <summary>How many model lists (credential × origin) are kept.</summary>
    public int ModelCacheEntries { get; set; } = 256;

    public const string Usage = """
        astra-server relay --provider <template id> [--variant <id>] [--base-url <origin>] [--host <address>] [--port <n>]

        A gateway without storage: every request brings its own upstream credential (x-api-key, x-goog-api-key or
        Authorization: Bearer) — a subscription's access token or an API key — which is sent to the template's endpoints
        as it is and never stored. A request may pick one of the template's variants with x-astra-variant.
        Environment: ASTRA_RELAY_PROVIDER, ASTRA_RELAY_VARIANT, ASTRA_RELAY_BASE_URL, ASTRA_HOST, ASTRA_PORT, and the limits
        ASTRA_RELAY_MAX_CONCURRENT_REQUESTS, ASTRA_RELAY_MAX_REQUEST_BODY_BYTES, ASTRA_RELAY_REQUEST_BODY_TIMEOUT_SECONDS,
        ASTRA_RELAY_HEADERS_TIMEOUT_SECONDS, ASTRA_RELAY_UPSTREAM_IDLE_TIMEOUT_SECONDS,
        ASTRA_RELAY_DOWNSTREAM_WRITE_TIMEOUT_SECONDS, ASTRA_RELAY_SHUTDOWN_TIMEOUT_SECONDS, ASTRA_RELAY_MODEL_CACHE_SECONDS,
        ASTRA_RELAY_MODEL_CACHE_ENTRIES.
        """;

    /// <exception cref="ArgumentException">A value is missing, malformed or out of range.</exception>
    public static RelayOptions Load(IReadOnlyList<string> args, Func<string, string?> env)
    {
        var options = new RelayOptions
        {
            Provider = Nonblank(env("ASTRA_RELAY_PROVIDER")) ?? "",
            Variant = Nonblank(env("ASTRA_RELAY_VARIANT")),
            BaseUrl = Nonblank(env("ASTRA_RELAY_BASE_URL")),
        };
        if (Nonblank(env("ASTRA_HOST")) is { } host) options.Host = host;
        if (Nonblank(env("ASTRA_PORT")) is { } port) options.Port = Integer(port, "ASTRA_PORT");
        int Limit(string name, int fallback) => Nonblank(env(name)) is { } value ? Integer(value, name) : fallback;
        options.MaxConcurrentRequests = Limit("ASTRA_RELAY_MAX_CONCURRENT_REQUESTS", options.MaxConcurrentRequests);
        options.MaxRequestBodyBytes = Limit("ASTRA_RELAY_MAX_REQUEST_BODY_BYTES", options.MaxRequestBodyBytes);
        options.RequestBodyTimeoutSeconds = Limit("ASTRA_RELAY_REQUEST_BODY_TIMEOUT_SECONDS", options.RequestBodyTimeoutSeconds);
        options.HeadersTimeoutSeconds = Limit("ASTRA_RELAY_HEADERS_TIMEOUT_SECONDS", options.HeadersTimeoutSeconds);
        options.UpstreamIdleTimeoutSeconds = Limit("ASTRA_RELAY_UPSTREAM_IDLE_TIMEOUT_SECONDS", options.UpstreamIdleTimeoutSeconds);
        options.DownstreamWriteTimeoutSeconds = Limit("ASTRA_RELAY_DOWNSTREAM_WRITE_TIMEOUT_SECONDS", options.DownstreamWriteTimeoutSeconds);
        options.ShutdownTimeoutSeconds = Limit("ASTRA_RELAY_SHUTDOWN_TIMEOUT_SECONDS", options.ShutdownTimeoutSeconds);
        options.ModelCacheSeconds = Limit("ASTRA_RELAY_MODEL_CACHE_SECONDS", options.ModelCacheSeconds);
        options.ModelCacheEntries = Limit("ASTRA_RELAY_MODEL_CACHE_ENTRIES", options.ModelCacheEntries);

        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[++i].Trim() : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--provider": options.Provider = Next(); break;
                case "--variant": options.Variant = Next(); break;
                case "--base-url": options.BaseUrl = Next(); break;
                case "--host": options.Host = Next(); break;
                case "--port": options.Port = Integer(Next(), "--port"); break;
                default: throw new ArgumentException($"Unknown relay option '{args[i]}'.\n{Usage}");
            }
        }
        options.Validate();
        return options;
    }

    /// <exception cref="ArgumentException">A value is missing, malformed or out of range.</exception>
    public void Validate()
    {
        if (Provider.Length == 0) throw new ArgumentException($"Name the provider template: --provider or ASTRA_RELAY_PROVIDER.\n{Usage}");
        if (!ServerOptions.IsValidHost(Host)) throw new ArgumentException("The host must be localhost, * or an IP address.");
        if (Port is < 1 or > 65535) throw new ArgumentException("The port must be 1-65535.");
        if (BaseUrl is not null && !IsOrigin(BaseUrl))
            throw new ArgumentException("The base URL must be an https origin (no path, query or credentials); http is allowed on loopback only.");
        if (MaxConcurrentRequests is < 1 or > 1024) throw new ArgumentException("ASTRA_RELAY_MAX_CONCURRENT_REQUESTS must be 1-1024.");
        if (MaxRequestBodyBytes is < 16 * 1024 or > 64 * 1024 * 1024)
            throw new ArgumentException("ASTRA_RELAY_MAX_REQUEST_BODY_BYTES must be 16384-67108864 (16 KiB to 64 MiB).");
        if (ModelCacheEntries is < 1 or > 4096) throw new ArgumentException("ASTRA_RELAY_MODEL_CACHE_ENTRIES must be 1-4096.");
        if (ModelCacheSeconds is < 1 or > 3600) throw new ArgumentException("ASTRA_RELAY_MODEL_CACHE_SECONDS must be 1-3600.");
        foreach (var seconds in new[] { RequestBodyTimeoutSeconds, HeadersTimeoutSeconds, UpstreamIdleTimeoutSeconds,
                     DownstreamWriteTimeoutSeconds, ShutdownTimeoutSeconds })
            if (seconds is < 1 or > 86400) throw new ArgumentException("Relay timeouts must be 1-86400 seconds.");
    }

    /// <summary>An absolute https origin, or http on loopback; nothing after the host and port.</summary>
    internal static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static int Integer(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : throw new ArgumentException($"{name} must be a whole number.");

    private static string? Nonblank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
