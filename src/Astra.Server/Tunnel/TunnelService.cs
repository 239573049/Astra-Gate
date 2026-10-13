using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Tunnel;

/// <summary>
/// Remote access through a Cloudflare Tunnel: runs cloudflared as a child process pointed at this server, restarts it
/// with a backoff when it exits, and keeps what the remote access page shows (state, public address, recent output).
/// "quick" asks Cloudflare for a temporary *.trycloudflare.com address (no account; Cloudflare's quick tunnels carry no
/// server-sent events, so streamed responses don't work through them); "named" runs the user's own tunnel from its
/// token, whose public hostname and origin (this server's local address) are set up in the Cloudflare dashboard.
/// What remote requests may do is <see cref="Security.SecurityMiddleware"/>'s business; this only needs to tell it the
/// hostname those requests arrive with (<see cref="PublicHost"/>).
/// </summary>
public sealed partial class TunnelService(
    ServerOptions server, SettingsService settings, CloudflaredRuntime runtime, ISecretProtector secrets, AstraPaths paths,
    ILogger<TunnelService> log, IHostApplicationLifetime lifetime) : IHostedService
{
    public const string StateOff = "off";
    public const string StateDownloading = "downloading";
    public const string StateStarting = "starting";
    public const string StateConnected = "connected";
    public const string StateReconnecting = "reconnecting";

    private const int LogLines = 200;
    private static readonly int[] BackoffSeconds = [2, 5, 10, 30, 60];

    private readonly Lock _gate = new();
    private readonly Queue<string> _log = new();
    private CancellationTokenSource? _cts;
    private Process? _process;
    private string _state = StateOff;
    private string? _url;
    private string? _publicHost;
    private string? _error;
    private DateTimeOffset? _connectedAt;
    private int _failures;

    /// <summary>Docker images run cloudflared as a sidecar instead (the server's container has no business spawning it).</summary>
    public bool Supported => !InContainer(Environment.GetEnvironmentVariable);

    internal static bool InContainer(Func<string, string?> getEnv) =>
        string.Equals(getEnv("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The hostname remote requests arrive with while the tunnel runs (Host header); null when it is off.</summary>
    public string? PublicHost
    {
        get
        {
            lock (_gate) return _state == StateOff ? null : _publicHost;
        }
    }

    /// <summary>The origin cloudflared forwards to; a named tunnel's dashboard must point its public hostname here.</summary>
    public string OriginUrl => server.LocalUrl;

    public TunnelRuntimeState Snapshot()
    {
        lock (_gate) return new TunnelRuntimeState(_state, _url, _error, _connectedAt, [.. _log]);
    }

    /// <summary>Why the tunnel cannot start with these settings (an English sentence for the API), or null.</summary>
    public string? Validate(TunnelSettings s)
    {
        if (!Supported) return "Remote access is not managed inside a container; run cloudflared as a sidecar instead";
        if (runtime.Override is null && CloudflaredRuntime.Asset(out var why) is null) return why;
        if (s.Mode == TunnelSettings.ModeNamed)
        {
            if (string.IsNullOrEmpty(s.TokenProtected)) return "A named tunnel needs its tunnel token";
            if (string.IsNullOrEmpty(s.Hostname)) return "A named tunnel needs its public hostname";
        }
        if (s.AllowAdmin && string.IsNullOrEmpty(server.AdminPasswordHash))
            return "Set an admin password before opening the admin UI to remote access";
        return null;
    }

    /// <summary>Turns remote access on (kept across restarts) and starts the tunnel.</summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        if (Validate(settings.Current.Tunnel) is { } error) throw new InvalidOperationException(error);
        await settings.UpdateAsync(s => s.Tunnel.Enabled = true, ct);
        Restart();
    }

    /// <summary>Turns remote access off and stops the tunnel.</summary>
    public async Task DisableAsync(CancellationToken ct = default)
    {
        await settings.UpdateAsync(s => s.Tunnel.Enabled = false, ct);
        Halt();
    }

    /// <summary>Starts the tunnel again with the current settings (they changed); no-op while it is off.</summary>
    public void RestartIfRunning()
    {
        bool running;
        lock (_gate) running = _cts is not null;
        if (running) Restart();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        // The port is only final once Kestrel listens; a tunnel left enabled comes back with the server.
        lifetime.ApplicationStarted.Register(() =>
        {
            KillStale();
            if (settings.Current.Tunnel.Enabled && Validate(settings.Current.Tunnel) is null) Restart();
        });
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Halt();
        return Task.CompletedTask;
    }

    private void Restart()
    {
        Halt();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _state = StateStarting;
            _error = null;
            _failures = 0;
            _log.Clear();
        }
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private void Halt()
    {
        CancellationTokenSource? cts;
        Process? process;
        lock (_gate)
        {
            (cts, _cts) = (_cts, null);
            (process, _process) = (_process, null);
            _state = StateOff;
            _url = null;
            _publicHost = null;
            _connectedAt = null;
        }
        cts?.Cancel();
        cts?.Dispose();
        Kill(process);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (runtime.Current is null) SetState(StateDownloading, ct);
                var exe = await runtime.EnsureAsync(ct);
                SetState(_failures == 0 ? StateStarting : StateReconnecting, ct);
                await RunOnceAsync(exe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                lock (_gate) if (!ct.IsCancellationRequested) _error = e.Message;
                log.LogWarning("Remote access tunnel: {Error}", e.Message);
            }
            if (ct.IsCancellationRequested) return;

            int failures;
            lock (_gate)
            {
                failures = ++_failures;
                _state = StateReconnecting;
                _connectedAt = null;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[Math.Min(failures, BackoffSeconds.Length) - 1]), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(string exe, CancellationToken ct)
    {
        var s = settings.Current.Tunnel;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("tunnel");
        psi.ArgumentList.Add("--no-autoupdate");
        if (s.Mode == TunnelSettings.ModeNamed)
        {
            string token;
            try
            {
                token = secrets.Unprotect(s.TokenProtected ?? "");
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("The stored tunnel token can no longer be read; enter it again");
            }
            psi.ArgumentList.Add("run");
            // The environment, not the command line: other users can read a process's arguments.
            psi.Environment["TUNNEL_TOKEN"] = token;
            lock (_gate)
            {
                _publicHost = s.Hostname;
                _url = "https://" + s.Hostname;
            }
        }
        else
        {
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(OriginUrl);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data, ct); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data, ct); };
        process.Start();
        lock (_gate)
        {
            if (ct.IsCancellationRequested)
            {
                Kill(process);
                return;
            }
            _process = process;
        }
        WritePid(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            lock (_gate) if (_process == process) _process = null;
            Kill(process);
            DeletePid();
        }
        lock (_gate) _error ??= $"cloudflared exited with code {process.ExitCode}";
    }

    private void OnLine(string line, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;
            _log.Enqueue(line);
            while (_log.Count > LogLines) _log.Dequeue();

            if (ParseQuickUrl(line) is { } url)
            {
                _url = url;
                _publicHost = new Uri(url).Host;
            }
            if (line.Contains("Registered tunnel connection", StringComparison.Ordinal))
            {
                _state = StateConnected;
                _connectedAt ??= DateTimeOffset.UtcNow;
                _error = null;
                _failures = 0;
            }
            else if (ErrorLine(line) is { } error)
            {
                _error = error;
            }
        }
    }

    private void SetState(string state, CancellationToken ct)
    {
        lock (_gate) if (!ct.IsCancellationRequested) _state = state;
    }

    /// <summary>The *.trycloudflare.com address in a quick tunnel's banner, or null.</summary>
    internal static string? ParseQuickUrl(string line) => QuickUrl().Match(line) is { Success: true } m ? m.Value : null;

    /// <summary>The message of a cloudflared error line ("2026-01-01T00:00:00Z ERR message key=value"), or null.</summary>
    internal static string? ErrorLine(string line)
    {
        var at = line.IndexOf(" ERR ", StringComparison.Ordinal);
        return at < 0 ? null : line[(at + 5)..].Trim();
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.CultureInvariant)]
    private static partial Regex QuickUrl();

    private string PidFile => Path.Combine(paths.ToolsDir, "cloudflared.pid");

    private void WritePid(int pid)
    {
        try
        {
            Directory.CreateDirectory(paths.ToolsDir);
            File.WriteAllText(PidFile, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DeletePid()
    {
        try
        {
            File.Delete(PidFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A cloudflared a previous server left behind (it was killed before it could stop it) would keep forwarding to
    /// this port; stop it — only when the recorded process still is a cloudflared, since pids get reused.
    /// </summary>
    private void KillStale()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid)) return;
            using var stale = Process.GetProcessById(pid);
            if (stale.ProcessName.Contains("cloudflared", StringComparison.OrdinalIgnoreCase)) Kill(stale);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
        }
        DeletePid();
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException
                                      or ObjectDisposedException)
        {
        }
    }
}

/// <summary>The tunnel as it runs: state, public address, last error, and cloudflared's recent output.</summary>
public sealed record TunnelRuntimeState(string State, string? Url, string? Error, DateTimeOffset? ConnectedAt, List<string> Log);
