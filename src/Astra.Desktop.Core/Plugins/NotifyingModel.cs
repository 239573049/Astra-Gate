using System.ComponentModel;
using System.Runtime.CompilerServices;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Plugins;

/// <summary>
/// The smallest observable base for the page models of this package (Desktop.Core has no MVVM toolkit): a property setter calls
/// <see cref="Set{T}"/>, which raises <see cref="PropertyChanged"/> on the thread that changed it. A model that is changed from a timer
/// or a request continuation routes the change through the <see cref="IUiDispatcher"/> it was given first.
/// </summary>
public abstract class NotifyingModel : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores <paramref name="value"/> and raises the change when it differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Waits for a text to rest before acting on it (web: <c>setTimeout(…, 400)</c> in an effect keyed on the text). Every
/// <see cref="Change"/> restarts the wait; the callback runs on the UI dispatcher.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly TimeSpan _delay;
    private readonly Action<string> _fire;
    private readonly object _gate = new();
    private ITimer? _timer;
    private int _generation;

    /// <summary>Creates a debouncer calling <paramref name="fire"/> with the last text after <paramref name="delay"/>.</summary>
    public Debouncer(TimeProvider time, IUiDispatcher ui, TimeSpan delay, Action<string> fire)
    {
        _time = time;
        _ui = ui;
        _delay = delay;
        _fire = fire;
    }

    /// <summary>The text changed: restart the wait.</summary>
    public void Change(string text)
    {
        int generation;
        lock (_gate)
        {
            _timer?.Dispose();
            generation = ++_generation;
            _timer = _time.CreateTimer(_ => _ui.Post(() =>
            {
                lock (_gate)
                    if (generation != _generation) return;
                _fire(text);
            }), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops a pending call.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
