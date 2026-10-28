namespace Astra.Desktop.Core.Settings;

/// <summary>
/// Remembers that the "anyone with the address can reach your gateway" warning was accepted, so later switches on are one click (web: the
/// <c>astra.remote.acknowledged</c> localStorage flag). <see cref="Shared"/> is the app-wide instance; the app <see cref="Bind"/>s it to the
/// desktop prefs file at startup so the acceptance survives restarts like the web's flag (and an Electron-era acceptance is migrated).
/// </summary>
public sealed class RemoteAckStore
{
    private Func<bool>? _load;
    private Action? _save;
    private bool _acknowledged;

    /// <summary>The app-wide instance.</summary>
    public static RemoteAckStore Shared { get; } = new();

    /// <summary>The warning was accepted (this run, or a stored acceptance).</summary>
    public bool Acknowledged => _acknowledged || (_load?.Invoke() ?? false);

    /// <summary>Records the acceptance (and stores it when bound).</summary>
    public void Acknowledge()
    {
        _acknowledged = true;
        _save?.Invoke();
    }

    /// <summary>Backs the flag with persistent storage: <paramref name="load"/> reads the stored acceptance, <paramref name="save"/> stores it.</summary>
    public void Bind(Func<bool> load, Action save)
    {
        _load = load;
        _save = save;
    }
}
