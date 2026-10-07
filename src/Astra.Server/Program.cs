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
///   version                    print {"version","apiVersion"}
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
                    Console.WriteLine(JsonSerializer.Serialize(new { version = ServerOptions.Version, apiVersion = ServerOptions.ApiVersion }));
                    return 0;
                case "serve":
                    return await ServeAsync(paths, rest);
                case "migrate":
                    return await Commands.MigrateAsync(paths);
                case "restore-all":
                    return await Commands.RestoreAllAsync(paths, rest.Contains("--purge"));
                case "set-password":
                    return SetPassword(paths);
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'. Commands: serve, restore-all [--purge], migrate, set-password, version");
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
        options.ApplyArgs(args);
        if (!options.IsLoopback && string.IsNullOrEmpty(options.AdminPasswordHash))
        {
            Console.Error.WriteLine($"Refusing to listen on {options.Host}: set an admin password first (astra-server set-password).");
            return 2;
        }

        // Plan §2: a busy port moves us to the next free one; runtime.json records the port actually used.
        var configuredPort = options.Port;
        if (PortPicker.FindFree(options.Host, configuredPort) is not { } freePort)
        {
            Console.Error.WriteLine($"Cannot listen on {options.Host}: ports {configuredPort}-{configuredPort + PortPicker.MaxAttempts - 1} are all in use.");
            return 3;
        }
        if (freePort != configuredPort)
            Console.WriteLine($"Port {configuredPort} is in use; using {freePort} instead.");
        options.Port = freePort;

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
