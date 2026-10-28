using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Astra.Desktop.Core.Accounts;

/// <summary>
/// The change notification of the account models (the React state of the ported components): a property set to a new value raises
/// <see cref="PropertyChanged"/>; views redraw the parts that depend on it.
/// </summary>
public abstract class ModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="name"/>.</summary>
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets a field and raises the change when the value differs; true when it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>A disposable that runs <paramref name="action"/> once.</summary>
    protected static IDisposable Disposer(Action action) => new Once(action);

    private sealed class Once(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
