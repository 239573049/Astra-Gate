using Astra.Api.Contracts;
using Astra.Desktop.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.Shell;

public enum ConnectionState { Connecting, Connected, Down }

/// <summary>
/// The server connection as the shell shows it: the sidebar's status card and the top banners
/// (web ServiceStatus + ConnectionBanner). A supervisor loop starts or adopts the server through <see cref="ServiceManager"/>,
/// retries with a growing delay while it cannot, and then watches its health; "Start service" retries at once.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
    private const int HealthMissesBeforeDown = 2;

    private readonly ServiceManager? _manager;
    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly SettingsStore _settings;
    private IDisposable? _settingsObserver;
    private string? _apiBase;
    private CancellationTokenSource? _watchBreak;
    private bool _skipRetryDelay;
    private TaskCompletionSource _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConnectionState _state = ConnectionState.Connecting;
    private string? _address;
    private string? _failure;
    private bool _mismatch;
    private bool _loginRequired;
    private bool _starting;
    // "Stop service" (the tray): the supervisor is parked until StartNowAsync resumes it, so a stopped server stays stopped.
    private volatile bool _paused;
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="manager">Null for a shell that never connects (the smoke test, visual tests): it stays "connecting" until <see cref="AssumeConnected"/>.</param>
    /// <param name="api">The app's one API client: its base URL follows the server's port (<see cref="ApiClient.SetPort"/>) each time a connection is established, so a port that drifted at startup is followed.</param>
    /// <param name="queries">The shared cache: invalidated when a connection is (re)established, so pages refetch from the right server.</param>
    public ConnectionViewModel(ServiceManager? manager, ApiClient api, QueryClient queries)
    {
        _manager = manager;
        _api = api;
        _queries = queries;
        _settings = new SettingsStore(queries, api);
        _settings.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Data" or "HasData") UpdateAddress();
        };
    }

    /// <summary>The API address of the running server (<c>http://127.0.0.1:port</c>), or null while not connected.</summary>
    public string? ApiBase => _apiBase;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) RaiseDerived();
        }
    }

    /// <summary>Host and port of the running server (web: the gateway base URL without scheme).</summary>
    public string? Address
    {
        get => _address;
        private set
        {
            if (SetProperty(ref _address, value)) OnPropertyChanged(nameof(ShowAddress));
        }
    }

    /// <summary>Why the last attempt failed (shown under the "cannot reach" banner).</summary>
    public string? Failure
    {
        get => _failure;
        private set
        {
            if (SetProperty(ref _failure, value)) OnPropertyChanged(nameof(HasFailure));
        }
    }

    public bool HasFailure => !string.IsNullOrEmpty(_failure);

    /// <summary>True while a (re)start attempt is under way: the banner's button shows its spinner.</summary>
    public bool IsStarting
    {
        get => _starting;
        private set => SetProperty(ref _starting, value);
    }

    /// <summary>The server speaks another API major than this client (web: banner.mismatch).</summary>
    public bool ApiVersionMismatch
    {
        get => _mismatch;
        private set => SetProperty(ref _mismatch, value);
    }

    /// <summary>
    /// The server wants an admin session even from this machine (an outdated server listening on the LAN asks it of loopback too), so
    /// every page's query fails with 401. This client has no sign-in page — a current server never asks it of loopback — so the banner says why.
    /// </summary>
    public bool LoginRequired
    {
        get => _loginRequired;
        private set => SetProperty(ref _loginRequired, value);
    }

    public bool IsConnected => _state == ConnectionState.Connected;
    public bool IsDown => _state == ConnectionState.Down;
    public bool IsConnecting => _state == ConnectionState.Connecting;
    public bool ShowAddress => IsConnected && !string.IsNullOrEmpty(_address);

    public string StatusText => _state switch
    {
        ConnectionState.Connected => Loc.T("shell.running"),
        ConnectionState.Connecting => Loc.T("shell.connecting"),
        _ => Loc.T("shell.disconnected"),
    };

    public string DownTitle => Loc.T("banner.downDesktop");
    public string MismatchTitle => Loc.T("banner.mismatch");
    public string LoginRequiredTitle => Loc.T("banner.loginRequired");
    public string StartServiceText => Loc.T("shell.startService");

    /// <summary>The "Start service" button: skip the retry delay.</summary>
    [RelayCommand]
    private void StartService() => _retryNow.TrySetResult();

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DownTitle));
        OnPropertyChanged(nameof(MismatchTitle));
        OnPropertyChanged(nameof(LoginRequiredTitle));
        OnPropertyChanged(nameof(StartServiceText));
    }

    /// <summary>
    /// Marks the connection established to <paramref name="apiBase"/> without a supervisor loop: what <see cref="RunAsync"/> does on each
    /// successful start, also used by hosts that do not start a server (the visual tests, whose API client answers from fixtures — the
    /// client's own base URL is left alone).
    /// </summary>
    public void AssumeConnected(string apiBase, bool pointApiClient = false)
    {
        _apiBase = apiBase;
        if (pointApiClient) _api.BaseUrl = apiBase;
        ApplyConnected(apiBase);
    }

    /// <summary>Shows the server as down with <paramref name="failure"/> (the visual tests' "cannot reach" state; no supervisor runs).</summary>
    internal void AssumeDown(string? failure = null)
    {
        Failure = failure;
        State = ConnectionState.Down;
    }

    private void ApplyConnected(string apiBase)
    {
        _apiBase = apiBase;
        Address = apiBase.Replace("http://", "", StringComparison.Ordinal);
        State = ConnectionState.Connected;
        // Pages that failed or loaded from another server while we were not connected fetch again (observed queries refetch at once,
        // the settings among them: the first call also starts watching them).
        _queries.Invalidate();
        _settingsObserver ??= _settings.Settings.Activate();
        UpdateAddress();
        _ = CheckLoginAsync();
    }

    private async Task CheckLoginAsync()
    {
        try
        {
            var auth = await _api.GetAsync<AuthStatusDto>("/api/auth/status");
            LoginRequired = auth is { Required: true, SignedIn: false };
        }
        catch (ApiException)
        {
            // an unreachable server is the health watch's business
        }
    }

    /// <summary>The sidebar's address line: <c>gatewayBaseUrl</c> of the settings (what clients should call), else the address the app reached the server on.</summary>
    private void UpdateAddress()
    {
        if (_apiBase is null) return;
        var fallback = _apiBase.Replace("http://", "", StringComparison.Ordinal);
        Address = SettingsStore.DisplayAddress(_settings.Settings.Data?.GatewayBaseUrl, fallback);
    }

    /// <summary>"Start service" from a page or the tray: skips the retry delay and waits for the outcome.</summary>
    public async Task<HostResult> StartNowAsync()
    {
        if (_manager is null) return new HostResult(false, "No service manager");
        if (IsConnected) return new HostResult(true);
        Failure = null;
        if (_paused)
        {
            _paused = false;
            _resume.TrySetResult();
        }
        _retryNow.TrySetResult();
        return await WaitConnectedAsync();
    }

    /// <summary>Restarts the server and waits until the supervisor has it connected again (the port may drift; the API client follows).</summary>
    public async Task<HostResult> RestartAsync()
    {
        if (_manager is null) return new HostResult(false, "No service manager");
        try
        {
            if (await _manager.StopServiceAsync() == StopResult.Failed) return new HostResult(false, Loc.T("shell.disconnected"));
        }
        catch (Exception e)
        {
            return new HostResult(false, e.Message);
        }
        // The supervisor is parked in its health watch (or its retry wait): wake it so it starts the server now instead of after the misses.
        Failure = null;
        State = ConnectionState.Connecting;
        IsStarting = true;
        _skipRetryDelay = true;
        _watchBreak?.Cancel();
        _retryNow.TrySetResult();
        return await WaitConnectedAsync();
    }

    /// <summary>
    /// Stops the server and keeps it stopped (the tray's "Stop service", desktop/src/main.ts <c>onStopService</c>): the supervisor stops
    /// watching and waits for <see cref="StartNowAsync"/> instead of restarting it.
    /// </summary>
    public async Task<HostResult> StopAsync()
    {
        if (_manager is null) return new HostResult(false, "No service manager");
        _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _paused = true;
        _watchBreak?.Cancel();
        try
        {
            if (await _manager.StopServiceAsync() == StopResult.Failed) return new HostResult(false, Loc.T("shell.disconnected"));
        }
        catch (Exception e)
        {
            return new HostResult(false, e.Message);
        }
        State = ConnectionState.Down;
        return new HostResult(true);
    }

    private async Task<HostResult> WaitConnectedAsync()
    {
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        // Wait for a *new* connection: leave the Connected state first when a restart is under way.
        await Task.Delay(50);
        while (DateTime.UtcNow < stop)
        {
            if (IsConnected) return new HostResult(true);
            if (HasFailure && IsDown) return new HostResult(false, Failure);
            await Task.Delay(100);
        }
        return new HostResult(false, Failure ?? Loc.T("shell.disconnected"));
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled. Call from the UI thread; its state changes continue there.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_manager is null) return;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                IsStarting = false;
                State = ConnectionState.Down;
                try
                {
                    await Task.WhenAny(_resume.Task, Task.Delay(Timeout.Infinite, ct));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                attempt = 0;
                continue;
            }
            try
            {
                IsStarting = true;
                var running = await _manager.EnsureRunningAsync();
                Failure = null;
                // The port can differ from the configured one (it drifts at startup): every request from now on goes to this one.
                _api.SetPort(running.Port);
                ApiVersionMismatch = running.ApiVersionMismatch;
                ApplyConnected(running.ApiBase);
                attempt = 0;
                IsStarting = false;
                await WatchAsync(running.ApiBase, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Only the restart request broke the watch: go round again and start the server.
            }
            catch (Exception e)
            {
                // StartupException carries a message meant for the user; anything else is unexpected but must not end the loop.
                Failure = e.Message;
            }
            IsStarting = false;
            if (ct.IsCancellationRequested) return;
            _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_paused) continue;
            if (_skipRetryDelay)
            {
                // A restart in progress shows "connecting", not the red banner.
                _skipRetryDelay = false;
                continue;
            }
            State = ConnectionState.Down;
            var delay = RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)];
            try
            {
                await Task.WhenAny(_retryNow.Task, Task.Delay(delay, ct));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Polls the health endpoint until it misses twice in a row, then returns so the loop restarts the server.</summary>
    private async Task WatchAsync(string apiBase, CancellationToken ct)
    {
        _watchBreak = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var misses = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HealthInterval, _watchBreak.Token);
                misses = await ServerApi.CheckHealthAsync(apiBase, TimeSpan.FromSeconds(2), ct) ? 0 : misses + 1;
                if (misses >= HealthMissesBeforeDown) return;
            }
        }
        finally
        {
            _watchBreak.Dispose();
            _watchBreak = null;
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDown));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(ShowAddress));
        OnPropertyChanged(nameof(StatusText));
    }
}
