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

    public bool IsLoopback => Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Base URL clients are pointed at (no trailing slash, no /v1). Wildcard hosts map to 127.0.0.1.</summary>
    [JsonIgnore]
    public string GatewayBaseUrl
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

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ServerOptions Load(AstraPaths paths)
    {
        if (!File.Exists(paths.ConfigFile)) return new ServerOptions();
        try
        {
            return JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(paths.ConfigFile), FileJson) ?? new ServerOptions();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid {paths.ConfigFile}: {ex.Message}", ex);
        }
    }

    public void Save(AstraPaths paths)
    {
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(this, FileJson));
        AstraPaths.RestrictToOwner(paths.ConfigFile);
    }

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
