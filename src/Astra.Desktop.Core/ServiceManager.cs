using Astra.Core;
using System.Diagnostics;

namespace Astra.Desktop.Core;

public sealed class StartupException(string message, string? logPath) : Exception(message)
{
    public string? LogPath { get; } = logPath;
}

public sealed record ProbeResult(bool Running, int? Port, RuntimeInfo? Runtime, bool RuntimeStale, string? ApiVersion, string? Version);

public sealed record RunningService(int Port, string ApiBase, bool WeStarted, string? ApiVersion, bool ApiVersionMismatch);

public enum StopResult { Stopped, NotRunning, Failed }

public sealed class ServiceManagerOptions
{
    public required AstraPaths Paths { get; init; }
    public string? RepoRoot { get; init; }
    /// <summary>The server shipped inside the packaged app (standalone installers); null in dev.</summary>
    public BundledServer? BundledServer { get; init; }
    public int ExpectedApiMajor { get; init; } = ReleaseVersion.ExpectedApiMajor;
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;
    public Action<string> Log { get; init; } = line => Console.Error.WriteLine($"[astra] {line}");
    /// <summary>Test seam for the port-owner lookup and kill commands; defaults to running the process.</summary>
    public RunCommand? RunCommand { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Owns the Astra server lifecycle for this desktop session: probe → adopt a version-matching server or replace a
/// mismatching one (and spawn with <c>--started-by desktop</c> when none runs) → wait for health, and shut down on
/// exit only a server this session spawned. Port of desktop/src/service.ts.
/// </summary>
public sealed class ServiceManager
{
    public const string StartedBy = "desktop";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShutdownHttpGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(3);

    private readonly ServiceManagerOptions _o;
    private readonly RunCommand _run;
    private Process? _child;

    public ServiceManager(ServiceManagerOptions options)
    {
        _o = options;
        _run = options.RunCommand ?? ProcessRunner.RunAsync;
    }

    /// <summary>True only when THIS app run spawned the currently-known server.</summary>
    public bool WeStarted { get; private set; }

    /// <summary>The pid of the server this run spawned (what a successor inherits), or null when it adopted someone else's.</summary>
    public int? OwnedServerPid => WeStarted ? ReadRuntime()?.Pid : null;

    /// <summary>
    /// The server a predecessor run of this app spawned and handed over (the desktop app restarting itself into the tray): adopting a
    /// server with this pid makes it this run's, so Quit still stops it. Null for an ordinary start.
    /// </summary>
    public int? InheritedServerPid { get; set; }

    public AstraPaths Paths => _o.Paths;

    public RuntimeInfo? ReadRuntime() => RuntimeInfo.Parse(ReadText(Paths.RuntimeFile));
    public ServerConfig ReadConfig() => ServerConfig.Parse(ReadText(Paths.ConfigFile));
    private InstallInfo? ReadInstall() => InstallInfo.Parse(ReadText(Paths.InstallFile));

    public bool ComputeMismatch(string? apiVersion)
        => !string.IsNullOrEmpty(apiVersion) && !ReleaseVersion.IsApiVersionCompatible(apiVersion, _o.ExpectedApiMajor);

    /// <summary>Health-check whatever we believe the server is (the runtime port, or the config port).</summary>
    public async Task<ProbeResult> ProbeAsync(TimeSpan? healthTimeout = null)
    {
        var runtime = ReadRuntime();
        var stale = runtime is not null && !ProcessRunner.IsAlive(runtime.Pid);
        var port = runtime is not null && !stale ? runtime.Port : ReadConfig().Port;
        var baseUrl = ServerApi.BaseUrl(port);
        var running = await ServerApi.CheckHealthAsync(baseUrl, healthTimeout ?? TimeSpan.FromMilliseconds(1500));
        var info = running ? await ServerApi.FetchVersionAsync(baseUrl, TimeSpan.FromSeconds(2)) : null;
        return new ProbeResult(running, port, runtime, stale, info?.ApiVersion, info?.Version);
    }

    /// <summary>
    /// Ensures a healthy server running the binary this app resolved: adopts a running server whose version matches the
    /// target; stops and replaces a mismatching one (adopting it after all when it cannot be stopped); otherwise spawns
    /// the resolved binary and waits for health. Throws <see cref="StartupException"/> with the log path on failure.
    /// </summary>
    public async Task<RunningService> EnsureRunningAsync()
    {
        var probed = await ProbeAsync();
        if (probed is { Running: true, Port: { } port })
        {
            RunningService Adopt()
            {
                if (InheritedServerPid is { } inherited && !probed.RuntimeStale && probed.Runtime?.Pid == inherited) WeStarted = true;
                return new(port, ServerApi.BaseUrl(port), WeStarted, probed.ApiVersion, ComputeMismatch(probed.ApiVersion));
            }
            var target = ResolveBinary();
            // Nothing installed keeps the running server: it beats failing.
            if (target is null || !ServerBinaryResolver.ShouldReplaceRunning(probed.Version, target)) return Adopt();
            _o.Log($"replacing running server {probed.Version} with the resolved {target.Version} ({target.Source} build)");
            if (await StopServiceAsync() == StopResult.Failed)
            {
                // Spawning anyway would put a second server on the next free port while this one keeps the configured one.
                _o.Log($"could not stop the running server {probed.Version}; using it instead of starting a second one");
                return Adopt();
            }
        }
        return await StartServerAsync();
    }

    /// <summary>
    /// POST /api/admin/shutdown with the runtime token and wait; then fall back to a kill aimed at the pid runtime.json
    /// records, else the astra-server listening on the port, else the process this app spawned.
    /// </summary>
    public async Task<StopResult> StopServiceAsync()
    {
        var probed = await ProbeAsync(TimeSpan.FromMilliseconds(1200));
        var child = LiveChildPid();
        if (!probed.Running || probed.Port is not { } port)
        {
            if (child is not { } c) return StopResult.NotRunning;
            await KillTreeAsync(c);
            return await WaitAsync(() => Task.FromResult(!ProcessRunner.IsAlive(c)), KillGrace, 250) ? StopResult.Stopped : StopResult.Failed;
        }

        var baseUrl = ServerApi.BaseUrl(port);
        // A stale runtime.json belongs to a dead server: its token and pid say nothing about this one.
        var runtime = probed.RuntimeStale ? null : probed.Runtime;
        if (!string.IsNullOrEmpty(runtime?.RuntimeToken)
            && await ServerApi.RequestShutdownAsync(baseUrl, runtime.RuntimeToken, TimeSpan.FromSeconds(3))
            && await WaitAsync(async () => !await ServerApi.CheckHealthAsync(baseUrl, TimeSpan.FromMilliseconds(800)), ShutdownHttpGrace, 250))
            return StopResult.Stopped;

        var pid = runtime?.Pid ?? await ListenerLookup.FindServerListenerPidAsync(port, _run) ?? child;
        if (pid is not { } target || target <= 0) return StopResult.Failed;
        await KillTreeAsync(target);
        return await WaitAsync(async () => !await ServerApi.CheckHealthAsync(baseUrl, TimeSpan.FromMilliseconds(600)), KillGrace, 250)
            ? StopResult.Stopped : StopResult.Failed;
    }

    /// <summary>Quit-time hook: only stop the server when this session spawned it.</summary>
    public async Task StopIfOursAsync()
    {
        if (!WeStarted)
        {
            _o.Log("not stopping server on exit: it was not started by the desktop app");
            return;
        }
        _o.Log($"stop service on exit: {await StopServiceAsync()}");
    }

    private async Task<RunningService> StartServerAsync()
    {
        var config = ReadConfig();
        var todayLog = Paths.DailyLogFile(DateTime.Now);
        var binary = ResolveBinary() ?? throw new StartupException(
            "Astra server binary not found. Looked at install.json serverPath, ASTRA_SERVER_BIN, the server bundled with this app, " +
            "and the dev build path (<repo>/src/Astra.Server/bin/Debug/net11.0/astra-server). " +
            "Reinstall the desktop app from the download page, run \"astra update\", or build the server first.", todayLog);

        Directory.CreateDirectory(Paths.LogsDir);
        var spawnedAt = DateTimeOffset.Now;
        var args = new[] { "serve", "--started-by", StartedBy, "--port", config.Port.ToString() };
        _child = ProcessRunner.StartDetached(binary.Path, args, todayLog);
        _o.Log($"starting server: {binary.Path} {string.Join(' ', args)} (source: {binary.Source})");

        // The server moves to the next free port when the configured one is taken, so follow runtime.json — and only the
        // entry our child writes (at ApplicationStarted, seconds after it listens): until then the configured port can
        // still answer health checks for another server holding it.
        var deadline = DateTime.UtcNow + _o.StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            if (ChildExited() is { } early) throw new StartupException(early, todayLog);
            var runtime = ReadRuntime();
            if (runtime is null || !runtime.IsFrom(SafePid(_child), spawnedAt)) continue;
            var baseUrl = ServerApi.BaseUrl(runtime.Port);
            if (!await ServerApi.CheckHealthAsync(baseUrl, TimeSpan.FromSeconds(1))) continue;
            WeStarted = true;
            var info = await ServerApi.FetchVersionAsync(baseUrl, TimeSpan.FromSeconds(2));
            _o.Log($"server is healthy on port {runtime.Port}{(runtime.Port != config.Port ? $" (port {config.Port} was busy)" : "")} (pid {SafePid(_child)?.ToString() ?? "?"})");
            return new RunningService(runtime.Port, baseUrl, true, info?.ApiVersion, ComputeMismatch(info?.ApiVersion));
        }
        throw new StartupException($"Server did not become healthy within {_o.StartTimeout.TotalSeconds:0}s on port {config.Port}.", todayLog);
    }

    /// <summary>The binary this app would serve with (install.json &gt; ASTRA_SERVER_BIN &gt; bundled &gt; dev).</summary>
    private ServerBinary? ResolveBinary()
        => ServerBinaryResolver.Resolve(ReadInstall(), _o.GetEnvironmentVariable("ASTRA_SERVER_BIN"), _o.BundledServer, _o.RepoRoot, File.Exists);

    private string? ChildExited()
    {
        if (_child is null) return null;
        try
        {
            return _child.HasExited ? $"server process exited before becoming healthy (code={_child.ExitCode})" : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private int? LiveChildPid()
    {
        if (SafePid(_child) is not { } pid) return null;
        try
        {
            return _child!.HasExited ? null : pid;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? SafePid(Process? p)
    {
        try
        {
            return p?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task KillTreeAsync(int pid)
    {
        _o.Log($"sending kill to server pid {pid}");
        if (OperatingSystem.IsWindows()) await _run("taskkill", ["/PID", pid.ToString(), "/T", "/F"]);
        else await _run("kill", ["-TERM", pid.ToString()]);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> done, TimeSpan within, int pollMs)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (await done()) return true;
            await Task.Delay(pollMs);
        }
        return false;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
