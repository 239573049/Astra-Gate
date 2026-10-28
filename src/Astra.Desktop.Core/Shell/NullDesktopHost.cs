namespace Astra.Desktop.Core.Shell;

/// <summary>
/// An <see cref="IDesktopHost"/> that does nothing, like the web build without <c>window.__ASTRA__</c>: no accent color,
/// a login item that is unsupported, nothing to update. Values that a test may want to set or observe are plain properties
/// and the events can be raised through <c>Raise*</c>.
/// </summary>
public sealed class NullDesktopHost : IDesktopHost
{
    private LoginItemState _login = new(false, false);

    /// <inheritdoc />
    public string Platform { get; set; } = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";

    /// <inheritdoc />
    public string Version { get; set; } = "0.0.0";

    /// <inheritdoc />
    public bool ApiVersionMismatch { get; set; }

    /// <inheritdoc />
    public string? AccentColor { get; set; }

    /// <summary>Paths passed to <see cref="OpenPathAsync"/>, in order.</summary>
    public List<string> OpenedPaths { get; } = [];

    /// <summary>Theme sources passed to <see cref="SetThemeSourceAsync"/>, in order.</summary>
    public List<ThemeSource> ThemeSources { get; } = [];

    /// <inheritdoc />
    public event Action<string?>? AccentColorChanged;

    /// <inheritdoc />
    public event Action<LoginItemState>? LoginItemChanged;

    /// <inheritdoc />
    public event Action<string>? MenuCommandReceived;

    /// <summary>Fires <see cref="AccentColorChanged"/>.</summary>
    public void RaiseAccentColor(string? color) { AccentColor = color; AccentColorChanged?.Invoke(color); }

    /// <summary>Fires <see cref="MenuCommandReceived"/>.</summary>
    public void RaiseMenuCommand(string command) => MenuCommandReceived?.Invoke(command);

    /// <inheritdoc />
    public Task<string?> GetAccentColorAsync() => Task.FromResult(AccentColor);

    /// <inheritdoc />
    public Task<bool> SetThemeSourceAsync(ThemeSource source) { ThemeSources.Add(source); return Task.FromResult(true); }

    /// <inheritdoc />
    public Task<bool> OpenPathAsync(string path) { OpenedPaths.Add(path); return Task.FromResult(false); }

    /// <inheritdoc />
    public Task<bool> RevealLogsAsync() => Task.FromResult(false);

    /// <inheritdoc />
    public Task<HostResult> StartServiceAsync() => Task.FromResult(new HostResult(false, "No desktop host"));

    /// <inheritdoc />
    public Task<HostResult> RestartServiceAsync() => Task.FromResult(new HostResult(false, "No desktop host"));

    /// <inheritdoc />
    public Task<LoginItemState> GetLoginItemAsync() => Task.FromResult(_login);

    /// <inheritdoc />
    public Task<LoginItemState> SetLoginItemAsync(bool enabled)
    {
        // Unsupported stays unchanged, like a refused system call.
        if (_login.Supported) { _login = _login with { Enabled = enabled }; LoginItemChanged?.Invoke(_login); }
        return Task.FromResult(_login);
    }

    /// <summary>Makes the login item supported (or not) for a test.</summary>
    public void SetLoginSupported(bool supported, bool enabled = false) => _login = new LoginItemState(supported, enabled);

    /// <inheritdoc />
    public Task<UpdateSummary> CheckForUpdateAsync() => Task.FromResult(new UpdateSummary("", UpdateAction.None));

    /// <inheritdoc />
    public Task<UpdateApplyOutcome> ApplyUpdateAsync() => Task.FromResult(UpdateApplyOutcome.None);

    /// <inheritdoc />
    public Task<string?> ShowContextMenuAsync(IReadOnlyList<ContextMenuItem> items) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public ITrayHost Tray { get; } = new NullTrayHost();

    private sealed class NullTrayHost : ITrayHost
    {
        private TrayPrefs _prefs = TrayPrefs.Default;

        public event Action<TrayPrefs>? PrefsChanged;
        public event Action<TrayPanelState>? StateChanged { add { } remove { } }
        public event Action<bool>? VisibilityChanged { add { } remove { } }

        public Task<TrayPrefs> GetPrefsAsync() => Task.FromResult(_prefs);

        public Task<TrayPrefs> SetPrefsAsync(TrayPrefs prefs)
        {
            _prefs = prefs;
            PrefsChanged?.Invoke(prefs);
            return Task.FromResult(prefs);
        }

        public Task<TrayPanelState> GetStateAsync() => Task.FromResult(new TrayPanelState(false, null, null, false, null));
        public Task CommandAsync(TrayCommand command) => Task.CompletedTask;
        public void SetTitle(string title, string detail) { }
        public void Resize(double height) { }
    }
}
