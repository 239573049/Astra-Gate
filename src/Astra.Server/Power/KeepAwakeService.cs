namespace Astra.Server.Power;

/// <summary>
/// Keeps the machine from idle sleep while gateway requests are in flight (<c>AppSettings.PreventSleep</c>, issue #14).
/// <see cref="KeepAwakeMiddleware"/> counts the requests — a streamed one until its stream ends. The first one takes the
/// OS's inhibitor; it is let go <see cref="Grace"/> after the last one finished, and a request arriving in between keeps it.
/// Turning the setting off lets go at once (<see cref="Refresh"/>), and so does stopping the host. Requests are counted
/// whether or not the setting is on, so turning it on mid-request takes the inhibitor right away.
/// The server host only: the relay (<c>RelayApp</c>) has no settings and never registers it.
/// </summary>
public sealed class KeepAwakeService : IHostedService, IDisposable
{
    /// <summary>How long the inhibitor outlives the last request: a client's next call usually follows within it.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);

    /// <summary>The reason the OS shows for the inhibitor (<c>pmset -g assertions</c>, <c>powercfg /requests</c>, <c>systemd-inhibit --list</c>).</summary>
    public const string Reason = "Astra is processing gateway requests";

    private readonly Func<bool> _enabled;
    private readonly ISleepInhibitor _inhibitor;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private int _active;
    private bool _held, _stopped, _failureLogged;
    private ITimer? _graceTimer;
    private int _graceTurn;

    /// <param name="enabled">Whether the setting is on; read on every change of state, so it must be cheap.</param>
    /// <param name="inhibitor">The OS's inhibitor (<see cref="SleepInhibitors.ForThisMachine"/>).</param>
    /// <param name="time">The clock the grace period runs on.</param>
    /// <param name="grace">How long the inhibitor outlives the last request.</param>
    /// <param name="log">Where taking and letting go are logged (debug), and a failure to (once).</param>
    public KeepAwakeService(Func<bool> enabled, ISleepInhibitor inhibitor, TimeProvider time, TimeSpan grace, ILogger<KeepAwakeService> log)
    {
        _enabled = enabled;
        _inhibitor = inhibitor;
        _time = time;
        Grace = grace;
        _log = log;
    }

    /// <summary>How long the inhibitor outlives the last request.</summary>
    public TimeSpan Grace { get; }

    /// <summary>Gateway requests in flight now.</summary>
    public int Active
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    /// <summary>Whether the OS inhibitor is held now.</summary>
    public bool Held
    {
        get
        {
            lock (_gate) return _held;
        }
    }

    /// <summary>A gateway request started.</summary>
    public void Enter()
    {
        lock (_gate)
        {
            _active++;
            Sync();
        }
    }

    /// <summary>A gateway request finished (its response, streamed or not, is complete or abandoned).</summary>
    public void Exit()
    {
        lock (_gate)
        {
            if (_active > 0) _active--;
            Sync();
        }
    }

    /// <summary>The setting may have changed: off lets go at once, on takes the inhibitor if requests are in flight.</summary>
    public void Refresh()
    {
        lock (_gate) Sync();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Host shutdown: lets go and takes nothing more. WebApplication registers the web server's hosted service last, so it
    /// stops first: in-flight streams drain (still kept awake) before this runs.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            Sync();
        }
    }

    /// <summary>Brings the inhibitor and the grace timer in line with the count and the setting (under <see cref="_gate"/>).</summary>
    private void Sync()
    {
        if (_stopped || !_enabled())
        {
            CancelGrace();
            LetGo("turned off");
        }
        else if (_active > 0)
        {
            CancelGrace();
            if (!_held) Take();
        }
        else if (_held && _graceTimer is null)
        {
            var turn = ++_graceTurn;
            _graceTimer = _time.CreateTimer(_ => GraceElapsed(turn), null, Grace, Timeout.InfiniteTimeSpan);
        }
    }

    private void GraceElapsed(int turn)
    {
        lock (_gate)
        {
            // A request that arrived, or a timer cancelled after it had already fired, makes this turn stale.
            if (turn != _graceTurn || _graceTimer is null) return;
            CancelGrace();
            if (_active == 0) LetGo("idle");
        }
    }

    private void CancelGrace()
    {
        if (_graceTimer is null) return;
        _graceTimer.Dispose();
        _graceTimer = null;
        _graceTurn++;
    }

    private void Take()
    {
        try
        {
            _held = _inhibitor.Acquire(Reason);
            if (_held) _log.LogDebug("Keeping the system awake: {Count} gateway request(s) in flight", _active);
        }
        catch (Exception e) when (IsInhibitorFailure(e))
        {
            if (_failureLogged) return;
            _failureLogged = true;
            _log.LogWarning(e, "Could not keep the system awake");
        }
    }

    /// <summary>
    /// What taking or letting go may throw — a native library or entry point missing (<see cref="TypeLoadException"/> covers
    /// both), a native call or process failing — which must never fail the gateway request that triggered it.
    /// </summary>
    private static bool IsInhibitorFailure(Exception e) =>
        e is TypeLoadException or System.Runtime.InteropServices.ExternalException or InvalidOperationException or IOException
            or UnauthorizedAccessException;

    private void LetGo(string why)
    {
        if (!_held) return;
        _held = false;
        try
        {
            _inhibitor.Release();
            _log.LogDebug("Let the system sleep again ({Why})", why);
        }
        catch (Exception e) when (IsInhibitorFailure(e))
        {
            _log.LogWarning(e, "Could not let go of the sleep inhibitor");
        }
    }
}
