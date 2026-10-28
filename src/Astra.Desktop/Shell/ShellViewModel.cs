using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Theme;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.Shell;

/// <summary>The window's view model: navigation, the connection and the sidebar state (web AppShell).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly DesktopPrefs _prefs;
    private readonly MenuCommandRouter _commands;
    private bool _sidebarCollapsed;

    /// <param name="prefs">Where the sidebar state is remembered (a detached <see cref="DesktopPrefs.InMemory"/> in tests: they must not write the user's file).</param>
    /// <param name="connection">The server connection (also what the sidebar's status card shows).</param>
    /// <param name="context">The app context; its navigator is replaced by this shell's (<see cref="Navigator"/>).</param>
    /// <param name="modules">The page modules (<see cref="AppModules.All"/>).</param>
    public ShellViewModel(DesktopPrefs prefs, ConnectionViewModel connection, PageContext context, IReadOnlyList<IPageModule> modules)
    {
        _prefs = prefs;
        _sidebarCollapsed = prefs.SidebarCollapsed;
        Connection = connection;
        Navigator = new Navigator(new PageRegistry(modules));
        Context = context with { Navigator = Navigator };
        Navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Navigator.CanGoBack)) OnPropertyChanged(nameof(CanGoBack));
        };
        _commands = new MenuCommandRouter(
            Navigator,
            () => IsSidebarCollapsed = !IsSidebarCollapsed,
            () => Context.Queries.Invalidate(),
            command => Navigator.Current.OnMenuCommand(command),
            () => Navigator.Current.Route.ToString());
        // Menu items and shortcuts reach the page host through the desktop host (web: onMenuCommand).
        Context.Host.MenuCommandReceived += command => Context.Queries.Dispatcher.Post(() => _commands.Handle(command));
        Navigator.Attach(Context);
        Status = new ShellStatusViewModel(Context, connection);
    }

    /// <summary>The clients-outdated banner and the sidebar's remote-access note.</summary>
    public ShellStatusViewModel Status { get; }

    public Navigator Navigator { get; }

    /// <summary>What every page of this shell is built with.</summary>
    public PageContext Context { get; }

    /// <summary>Runs a menu / shortcut command as if it came from the host (<see cref="Astra.Desktop.Core.Shell.MenuCommands"/>); false when nothing took it.</summary>
    public bool RunCommand(string command) => _commands.Handle(command);

    public ConnectionViewModel Connection { get; }

    public bool CanGoBack => Navigator.CanGoBack;

    /// <summary>Expanded sidebar or icon rail; remembered across runs like the web's <c>astra.sidebar</c>.</summary>
    public bool IsSidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (!SetProperty(ref _sidebarCollapsed, value)) return;
            _prefs.SidebarCollapsed = value;
            _prefs.Save();
            OnPropertyChanged(nameof(CollapseToggleText));
            OnPropertyChanged(nameof(CollapseToggleTooltip));
        }
    }

    public string CollapseToggleText => Loc.T(_sidebarCollapsed ? "shell.expandSidebar" : "shell.collapseSidebar");

    /// <summary>The toggle's tooltip: its label and the shortcut (web Page.tsx: <c>⌘⌥S</c> / <c>Ctrl+Alt+S</c> on the desktop).</summary>
    public string CollapseToggleTooltip => ShellBanners.ToggleTooltip(CollapseToggleText, OperatingSystem.IsMacOS());

    /// <summary>
    /// The sidebar card's "Start service" (web <c>ServiceStatus.start</c>): waits for the outcome and toasts a failure with its reason
    /// (<c>shell.startFailed</c> when there is none). The top banner's button only retries, like the web's.
    /// </summary>
    [RelayCommand]
    private async Task StartServiceAsync()
    {
        var result = await Context.Host.StartServiceAsync();
        if (!result.Ok) Context.Feedback.Error(string.IsNullOrEmpty(result.Error) ? Loc.T("shell.startFailed") : result.Error);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void GoBack() => Navigator.GoBack();

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CollapseToggleText));
        OnPropertyChanged(nameof(CollapseToggleTooltip));
        Connection.OnLanguageChanged();
        Status.OnLanguageChanged();
        Navigator.Current.OnLanguageChanged();
    }
}
