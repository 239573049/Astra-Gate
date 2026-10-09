using System.Text.Json;
using Astra.Core;
using Astra.Server.Hosting;
using Astra.Server.Security;

namespace Astra.Server;

/// <summary>
/// astra-server subcommands:
///   serve [--port n] [--host h] [--started-by cli|desktop|autostart]   (default)
///   restore-all [--purge]      offline restore of every client config Astra modified
///   migrate                    apply database migrations and exit
///   set-password               read a new admin password from stdin (required for non-loopback hosts)
///   healthcheck                GET /api/health on the configured address; exit 0 when healthy (Docker HEALTHCHECK)
///   version                    print {"version","apiVersion"}
/// serve and healthcheck also read ASTRA_HOST / ASTRA_PORT / ASTRA_PUBLIC_URL / ASTRA_STRICT_PORT /
/// ASTRA_ADMIN_PASSWORD[_FILE] (see ServerOptions.ApplyEnvironment); flags win over the environment,
/// the environment over config.json.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;
        var paths = new AstraPaths();

        try
        {
            switch (command)
            {
                case "version":
                    Console.WriteLine(Json.SerializeApi(new Api.VersionDto(ServerOptions.Version, ServerOptions.ApiVersion)));
                    return 0;
                case "serve":
                    return await ServeAsync(paths, rest);
                case "migrate":
                    return await Commands.MigrateAsync(paths);
                case "restore-all":
                    return await Commands.RestoreAllAsync(paths, rest.Contains("--purge"));
                case "set-password":
                    return SetPassword(paths);
                case "healthcheck":
                    return await HealthCheckAsync(paths, rest);
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'. Commands: serve, restore-all [--purge], migrate, set-password, healthcheck, version");
                    return 64;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 64;
        }
    }

    private static async Task<int> ServeAsync(AstraPaths paths, string[] args)
    {
        paths.EnsureCreated();
        var options = ServerOptions.Load(paths);
        options.ApplyEnvironment(Environment.GetEnvironmentVariable);
        options.ApplyArgs(args);
        if (!options.IsLoopback && string.IsNullOrEmpty(options.AdminPasswordHash))
        {
            Console.Error.WriteLine($"Refusing to listen on {options.Host}: set an admin password first (astra-server set-password, or ASTRA_ADMIN_PASSWORD).");
            return 2;
        }

        // Plan §2: a busy port moves us to the next free one; runtime.json records the port actually used.
        // ASTRA_STRICT_PORT (containers) turns that into an error: a published port must not silently drift.
        var configuredPort = options.Port;
        if (options.StrictPort)
        {
            if (!PortPicker.CanListen(options.Host, configuredPort))
            {
                Console.Error.WriteLine($"Cannot listen on {options.Host}:{configuredPort}: the port is in use (ASTRA_STRICT_PORT is set).");
                return 3;
            }
        }
        else if (PortPicker.FindFree(options.Host, configuredPort) is not { } freePort)
        {
            Console.Error.WriteLine($"Cannot listen on {options.Host}: ports {configuredPort}-{configuredPort + PortPicker.MaxAttempts - 1} are all in use.");
            return 3;
        }
        else
        {
            if (freePort != configuredPort)
                Console.WriteLine($"Port {configuredPort} is in use; using {freePort} instead.");
            options.Port = freePort;
        }

        var app = AstraApp.Create(new AstraApp.BuildOptions { Paths = paths, Server = options });
        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Cannot listen on {options.Host}:{options.Port}: {ex.Message}");
            return 3;
        }
        Console.WriteLine($"Astra {ServerOptions.Version} listening on http://{(options.Host.Contains(':') ? $"[{options.Host}]" : options.Host)}:{options.Port}");
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static async Task<int> HealthCheckAsync(AstraPaths paths, string[] args)
    {
        var options = ServerOptions.Load(paths);
        options.ApplyEnvironment(Environment.GetEnvironmentVariable);
        options.ApplyArgs(args);
        try
        {
            // No proxy: the probe targets this machine, whatever HTTP_PROXY says.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"{options.LocalUrl}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Unhealthy: {ex.Message}");
            return 1;
        }
    }

    private static int SetPassword(AstraPaths paths)
    {
        paths.EnsureCreated();
        var password = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            Console.Error.WriteLine("Password must be at least 8 characters.");
            return 64;
        }
        var options = ServerOptions.Load(paths);
        options.AdminPasswordHash = PasswordHasher.Hash(password);
        options.Save(paths);
        Console.WriteLine("Admin password updated.");
        return 0;
    }
}
