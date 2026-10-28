using System.Diagnostics;
using Astra.Core;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Update;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using CoreThemeSource = Astra.Desktop.Core.Shell.ThemeSource;

namespace Astra.Desktop.Platform;

/// <summary>
/// The Avalonia app's <see cref="IDesktopHost"/>: what Electron's preload bridge gives the web pages (<c>window.__ASTRA__</c>,
/// plan avalonia-client §6), over the theme service, the login item, the OS "open" verb, the server supervisor and the updater.
/// The tray surface and the native context menu are plugged in by the tray runtime (<see cref="TrayHost"/>, <see cref="ContextMenu"/>,
/// Platform/Tray); until then — and in the smoke run — they answer like <see cref="NullDesktopHost"/>. Not supported yet: the desktop app's own
/// self-update and the macOS login item.
/// </summary>
public sealed class AvaloniaDesktopHost : IDesktopHost
{
    private readonly ThemeService _theme;
    private readonly AstraPaths _paths;
    private readonly LoginItemService _login;
    private readonly Func<UpdateCoordinator?> _updates;
    private readonly NullDesktopHost _unsupported = new();

    /// <param name="theme">Light / dark and accent.</param>
    /// <param name="paths">The data dir (logs folder).</param>
    /// <param name="login">The launch-at-login entry.</param>
    /// <param name="updates">The update coordinator, created once the server manager exists; null = no updates (smoke, tests).</param>
    public AvaloniaDesktopHost(ThemeService theme, AstraPaths paths, LoginItemService login, Func<UpdateCoordinator?> updates)
    {
        _theme = theme;
        _paths = paths;
        _login = login;
        _updates = updates;
        _theme.AppearanceChanged += () => AccentColorChanged?.Invoke(_theme.SystemAccentHex);
    }

    /// <summary>The connection that starts, restarts and reports on the server; set by the composition root once it exists.</summary>
    public ConnectionViewModel? Connection { get; set; }

    /// <inheritdoc />
    public string Platform => OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";

    /// <inheritdoc />
    public string Version => ServiceHost.AppVersion;

    /// <inheritdoc />
    public bool ApiVersionMismatch => Connection?.ApiVersionMismatch ?? false;

    /// <inheritdoc />
    public event Action<string?>? AccentColorChanged;

    /// <inheritdoc />
    public event Action<LoginItemState>? LoginItemChanged;

    /// <inheritdoc />
    public event Action<string>? MenuCommandReceived;

    /// <summary>Delivers a menu item / shortcut to whoever listens (the shell).</summary>
    public void RaiseMenuCommand(string command) => MenuCommandReceived?.Invoke(command);

    /// <inheritdoc />
    public Task<string?> GetAccentColorAsync() => Task.FromResult(_theme.SystemAccentHex);

    /// <inheritdoc />
    public async Task<bool> SetThemeSourceAsync(CoreThemeSource source)
    {
        await _theme.SetSourceAsync(source switch
        {
            CoreThemeSource.Light => Theme.ThemeSource.Light,
            CoreThemeSource.Dark => Theme.ThemeSource.Dark,
            _ => Theme.ThemeSource.System,
        });
        return true;
    }

    /// <inheritdoc />
    public Task<bool> OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) return Task.FromResult(false);
        return Task.FromResult(OsOpen.Open(path));
    }

    /// <inheritdoc />
    public Task<bool> RevealLogsAsync() => OpenPathAsync(_paths.LogsDir);

    /// <inheritdoc />
    public async Task<HostResult> StartServiceAsync()
        => Connection is null ? new HostResult(false, "Not connected to a service manager") : await Connection.StartNowAsync();

    /// <inheritdoc />
    public async Task<HostResult> RestartServiceAsync()
        => Connection is null ? new HostResult(false, "Not connected to a service manager") : await Connection.RestartAsync();

    /// <inheritdoc />
    public Task<LoginItemState> GetLoginItemAsync() => Task.FromResult(_login.Get());

    /// <inheritdoc />
    public Task<LoginItemState> SetLoginItemAsync(bool enabled)
    {
        var state = _login.Set(enabled);
        LoginItemChanged?.Invoke(state);
        return Task.FromResult(state);
    }

    /// <inheritdoc />
    public async Task<UpdateSummary> CheckForUpdateAsync()
        => _updates() is { } updates ? await updates.CheckAsync() : new UpdateSummary("Updates are not available in this run.", UpdateAction.None);

    /// <inheritdoc />
    public async Task<UpdateApplyOutcome> ApplyUpdateAsync()
    {
        var outcome = _updates() is { } updates ? await updates.ApplyAsync() : UpdateApplyOutcome.None;
        // Whoever applied it (Settings, the tray, the reminder), the tray's "update available" line and badge must not stay behind.
        if (outcome is UpdateApplyOutcome.Server or UpdateApplyOutcome.Desktop) UpdateApplied?.Invoke();
        return outcome;
    }

    /// <summary>An update was installed (the tray clears its "update available" state).</summary>
    public event Action? UpdateApplied;

    /// <summary>Shows a native context menu (macOS <c>NSMenu</c>, elsewhere a popup menu); set by the tray runtime.</summary>
    public Func<IReadOnlyList<ContextMenuItem>, Task<string?>>? ContextMenu { get; set; }

    /// <summary>The real tray (Platform/Tray <c>TrayController</c>); null until the tray runtime started.</summary>
    public ITrayHost? TrayHost { get; set; }

    /// <inheritdoc />
    public Task<string?> ShowContextMenuAsync(IReadOnlyList<ContextMenuItem> items)
        => ContextMenu is { } show ? show(items) : _unsupported.ShowContextMenuAsync(items);

    /// <inheritdoc />
    public ITrayHost Tray => TrayHost ?? _unsupported.Tray;
}

/// <summary>Hands a file or folder to the OS (Electron <c>shell.openPath</c>).</summary>
internal static class OsOpen
{
    public static bool Open(string path)
    {
        try
        {
            var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (OperatingSystem.IsMacOS()) info.FileName = "open";
            else if (OperatingSystem.IsWindows()) { info.FileName = "explorer.exe"; }
            else info.FileName = "xdg-open";
            info.ArgumentList.Add(path);
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
