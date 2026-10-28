using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// A debounced copy of a value (web <c>useDebounced</c>, 250 ms): <see cref="Set"/> restarts the wait, and when the value has been still for the
/// delay <see cref="Value"/> takes it and <see cref="Settled"/> fires on the UI thread. Checking several values in one open menu settles into a
/// single query instead of one per click.
/// </summary>
/// <typeparam name="T">The value type; compared with its default equality, so setting the same value again changes nothing.</typeparam>
public sealed class Debounce<T> : IDisposable
{
    /// <summary>The web's delay.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _delay;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private T _pending;
    private bool _disposed;

    /// <param name="initial">The value before any change (taken as already settled).</param>
    /// <param name="ui">Where <see cref="Settled"/> is raised.</param>
    /// <param name="delay">Quiet time before a change settles (default 250 ms).</param>
    /// <param name="time">Clock for the wait (tests inject a manual one).</param>
    public Debounce(T initial, IUiDispatcher ui, TimeSpan? delay = null, TimeProvider? time = null)
    {
        Value = initial;
        _pending = initial;
        _ui = ui;
        _delay = delay ?? DefaultDelay;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settled value.</summary>
    public T Value { get; private set; }

    /// <summary>The latest value set, settled or not.</summary>
    public T Latest => _pending;

    /// <summary>The settled value changed.</summary>
    public event Action<T>? Settled;

    /// <summary>Sets a new value; it settles after the delay unless another follows.</summary>
    public void Set(T value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = value;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Fire(), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        T value;
        lock (_gate)
        {
            if (_disposed) return;
            value = _pending;
        }
        _ui.Post(() =>
        {
            if (EqualityComparer<T>.Default.Equals(Value, value)) return;
            Value = value;
            Settled?.Invoke(value);
        });
    }

    /// <summary>Cancels a pending change.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
