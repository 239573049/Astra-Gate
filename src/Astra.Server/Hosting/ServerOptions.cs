using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Core;

namespace Astra.Server.Hosting;

/// <summary>Startup configuration from ~/.astra/config.json, overridable by command-line flags.</summary>
public sealed class ServerOptions
{
    public const int DefaultPort = 17321;
    public const string ApiVersion = "1.0";

    public int Port { get; set; } = DefaultPort;
    public string Host { get; set; } = "127.0.0.1";
    public string LogLevel { get; set; } = "Information";

    /// <summary>PBKDF2 hash of the admin password; required when listening on a non-loopback address.</summary>
    public string? AdminPasswordHash { get; set; }

    [JsonIgnore] public string StartedBy { get; set; } = "cli";

    /// <summary>ASTRA_PUBLIC_URL: the externally reachable base URL (container / reverse proxy); overrides <see cref="GatewayBaseUrl"/>.</summary>
    [JsonIgnore] public string? PublicUrl { get; set; }

    /// <summary>ASTRA_STRICT_PORT: fail when the port is taken instead of moving to the next free one (published container ports must not drift).</summary>
    [JsonIgnore] public bool StrictPort { get; set; }

    public bool IsLoopback => Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Base URL clients are pointed at (no trailing slash, no /v1): <see cref="PublicUrl"/> when set, else <see cref="LocalUrl"/>.</summary>
    [JsonIgnore]
    public string GatewayBaseUrl => PublicUrl ?? LocalUrl;

    /// <summary>URL that reaches this process from the same machine. Wildcard hosts map to 127.0.0.1.</summary>
    [JsonIgnore]
    public string LocalUrl
    {
        get
        {
            var host = Host is "0.0.0.0" or "*" or "localhost" ? "127.0.0.1" : Host;
            if (host is "::" or "[::]") host = "127.0.0.1";
            if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
            return $"http://{host}:{Port}";
        }
    }

    public static string Version =>
        typeof(ServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = JsonContexts.Resolver,
    };

    public static ServerOptions Load(AstraPaths paths)
    {
        if (!File.Exists(paths.ConfigFile)) return new ServerOptions();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(paths.ConfigFile), JsonContexts.Info<ServerOptions>(FileJson)) ?? new ServerOptions();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid {paths.ConfigFile}: {ex.Message}", ex);
        }
    }

    public void Save(AstraPaths paths)
    {
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(this, JsonContexts.Info<ServerOptions>(FileJson)));
        AstraPaths.RestrictToOwner(paths.ConfigFile);
    }

    /// <summary>
    /// Applies the container-oriented environment overrides (precedence: config.json &lt; environment &lt; flags):
    /// ASTRA_HOST, ASTRA_PORT, ASTRA_PUBLIC_URL, ASTRA_STRICT_PORT, ASTRA_ADMIN_PASSWORD or ASTRA_ADMIN_PASSWORD_FILE.
    /// The password is hashed in memory only; it is never written to config.json.
    /// </summary>
    public void ApplyEnvironment(Func<string, string?> get)
    {
        if (Nonblank(get("ASTRA_HOST")) is { } host) Host = host;
        if (Nonblank(get("ASTRA_PORT")) is { } port)
            Port = int.TryParse(port, out var p) && p is > 0 and < 65536 ? p : throw new ArgumentException("ASTRA_PORT must be 1-65535");
        if (Nonblank(get("ASTRA_PUBLIC_URL")) is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("ASTRA_PUBLIC_URL must be an absolute http(s) URL");
            PublicUrl = url.TrimEnd('/');
        }
        if (Nonblank(get("ASTRA_STRICT_PORT")) is { } strict)
            StrictPort = strict.ToLowerInvariant() is "1" or "true" or "yes" or "on";

        var password = Nonblank(get("ASTRA_ADMIN_PASSWORD"));
        if (password is null && Nonblank(get("ASTRA_ADMIN_PASSWORD_FILE")) is { } file)
        {
            try
            {
                password = File.ReadAllText(file).TrimEnd('\r', '\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"Cannot read ASTRA_ADMIN_PASSWORD_FILE: {ex.Message}");
            }
        }
        if (password is not null)
        {
            if (password.Length < 8) throw new ArgumentException("Admin password must be at least 8 characters.");
            AdminPasswordHash = Astra.Server.Security.PasswordHasher.Hash(password);
        }
    }

    private static string? Nonblank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Applies --port / --host / --started-by flags.</summary>
    public void ApplyArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--port":
                    Port = int.TryParse(Next(), out var p) && p is > 0 and < 65536 ? p : throw new ArgumentException("--port must be 1-65535");
                    break;
                case "--host":
                    Host = Next();
                    break;
                case "--started-by":
                    StartedBy = Next();
                    break;
            }
        }
    }
}
