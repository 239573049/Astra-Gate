using System.Net;
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

    /// <summary><c>ASTRA_PLUGINS=off</c>: no installed plugin runs and none can be installed (the list is kept).</summary>
    [JsonIgnore] public bool PluginsOff { get; set; }

    /// <summary>
    /// ASTRA_UPDATER_URL: the container updater (a Watchtower HTTP API, e.g. <c>http://updater:8080</c>) Astra asks to pull its
    /// image and recreate its container when the feed offers a newer version (<see cref="ContainerUpdater"/>). Null = none.
    /// </summary>
    [JsonIgnore] public string? UpdaterUrl { get; set; }

    /// <summary>
    /// The updater's API token: ASTRA_UPDATER_TOKEN, else the file ASTRA_UPDATER_TOKEN_FILE names — created with a random
    /// token when it does not exist yet, so a compose file can share it with the updater without a second secret to set.
    /// Never logged or returned.
    /// </summary>
    [JsonIgnore] public string? UpdaterToken { get; set; }

    /// <summary>ASTRA_UPDATER_TOKEN_FILE, when the token comes from a file (for the start-up log line; the token itself is never logged).</summary>
    [JsonIgnore] public string? UpdaterTokenFile { get; private set; }

    /// <summary>This start created <see cref="UpdaterTokenFile"/> (it did not exist yet).</summary>
    [JsonIgnore] public bool UpdaterTokenFileCreated { get; private set; }

    /// <summary>ASTRA_HOST chose <see cref="Host"/>: config.json's host does not apply (containers).</summary>
    [JsonIgnore] public bool HostFromEnvironment { get; private set; }

    public bool IsLoopback => Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Listening on every interface (0.0.0.0, *, ::).</summary>
    [JsonIgnore]
    public bool IsWildcard => Host is "0.0.0.0" or "*" or "::" or "[::]";

    /// <summary>A host Kestrel can bind (see <c>AstraApp.Create</c>): localhost, *, or an IP address literal.</summary>
    public static bool IsValidHost(string host) =>
        host is "localhost" or "*" || IPAddress.TryParse(host.Trim('[', ']'), out _);

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
    /// ASTRA_HOST, ASTRA_PORT, ASTRA_PUBLIC_URL, ASTRA_STRICT_PORT, ASTRA_ADMIN_PASSWORD or ASTRA_ADMIN_PASSWORD_FILE,
    /// ASTRA_UPDATER_URL with ASTRA_UPDATER_TOKEN or ASTRA_UPDATER_TOKEN_FILE.
    /// The password is hashed in memory only; it is never written to config.json. Only the server creates the updater's token
    /// file (<paramref name="createTokenFile"/>): a probe such as <c>healthcheck</c> must not race it into writing a second token.
    /// </summary>
    public void ApplyEnvironment(Func<string, string?> get, bool createTokenFile = true)
    {
        if (Nonblank(get("ASTRA_HOST")) is { } host)
        {
            Host = host;
            HostFromEnvironment = true;
        }
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
        if (Nonblank(get("ASTRA_PLUGINS")) is { } plugins)
            PluginsOff = plugins.ToLowerInvariant() is "0" or "false" or "no" or "off";

        if (Nonblank(get("ASTRA_UPDATER_URL")) is { } updater)
        {
            if (!Uri.TryCreate(updater, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new ArgumentException("ASTRA_UPDATER_URL must be an absolute http(s) URL without credentials");
            UpdaterUrl = updater.TrimEnd('/');
            UpdaterTokenFile = Nonblank(get("ASTRA_UPDATER_TOKEN_FILE"));
            if (Nonblank(get("ASTRA_UPDATER_TOKEN")) is { } token) UpdaterToken = token;
            else if (UpdaterTokenFile is null)
                throw new ArgumentException("ASTRA_UPDATER_URL needs ASTRA_UPDATER_TOKEN or ASTRA_UPDATER_TOKEN_FILE");
            else if (createTokenFile)
            {
                UpdaterToken = ReadOrCreateToken(UpdaterTokenFile, out var created);
                UpdaterTokenFileCreated = created;
            }
            else UpdaterToken = "";
        }

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

    /// <summary>
    /// The token in <paramref name="path"/>; a missing or empty file gets a new random token (owner-only, created with that mode)
    /// first. The file is created exclusively, so two processes starting together agree on one token: the one that loses the race
    /// reads what the winner wrote.
    /// </summary>
    internal static string ReadOrCreateToken(string path, out bool created)
    {
        created = false;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (File.Exists(path) && Nonblank(File.ReadAllText(path)) is { } existing) return existing;
                var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var stream = new FileStream(path, options)) stream.Write(System.Text.Encoding.ASCII.GetBytes(token));
                    created = true;
                    return token;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created it first and may not have written the token yet (or the file is empty and
                    // gets replaced below): look again.
                    if (new FileInfo(path).Length == 0 && attempt >= 5) File.Delete(path);
                    Thread.Sleep(20);
                }
            }
            throw new IOException("the token file never got a token");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Cannot read or create ASTRA_UPDATER_TOKEN_FILE: {ex.Message}");
        }
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
                    HostFromEnvironment = false; // a flag outranks ASTRA_HOST
                    break;
                case "--started-by":
                    StartedBy = Next();
                    break;
            }
        }
    }
}
