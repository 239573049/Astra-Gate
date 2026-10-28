using System.ComponentModel;
using System.Windows.Input;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Astra.Desktop.Views;

/// <summary>
/// Sidebar footer (web AppShell.tsx <c>ServiceStatus</c>): service status, the gateway address, the remote-access link while the tunnel is on,
/// the community links and "Start service". On the icon rail: the links above the status dot, the globe beside it.
/// </summary>
public partial class ServiceStatusView : UserControl
{
    private const string GitHubUrl = "https://github.com/239573049/Astra-Gate";
    private const string DiscordUrl = "https://discord.gg/Qf6Jv4Rthe";

    // The 24x24 fill paths of the web's GithubIcon / DiscordIcon (web/src/components/layout/AppShell.tsx).
    private const string GitHubPath =
        "M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.1.79-.25.79-.56v-2c-3.2.7-3.87-1.37-3.87-1.37-.52-1.33-1.28-1.69-1.28-1.69-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.18 1.76 1.18 1.02 1.76 2.69 1.25 3.35.96.1-.74.4-1.25.73-1.54-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.28 1.18-3.09-.12-.29-.51-1.46.11-3.05 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.62 1.59.23 2.76.11 3.05.74.81 1.18 1.83 1.18 3.09 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5Z";
    private const string DiscordPath =
        "M20.32 4.37A19.8 19.8 0 0 0 15.43 2.85a.07.07 0 0 0-.08.04c-.21.38-.44.87-.6 1.26a18.3 18.3 0 0 0-5.5 0 12.6 12.6 0 0 0-.62-1.26.08.08 0 0 0-.08-.04A19.74 19.74 0 0 0 3.67 4.37a.07.07 0 0 0-.03.03C.53 9.05-.32 13.58.1 18.06a.08.08 0 0 0 .03.06 19.9 19.9 0 0 0 5.99 3.03.08.08 0 0 0 .08-.03c.46-.63.87-1.3 1.23-2a.08.08 0 0 0-.04-.1 13.1 13.1 0 0 1-1.87-.9.08.08 0 0 1 0-.13c.13-.09.25-.19.37-.29a.07.07 0 0 1 .08-.01c3.93 1.8 8.18 1.8 12.06 0a.07.07 0 0 1 .08.01c.12.1.24.2.37.29a.08.08 0 0 1 0 .13c-.6.35-1.22.65-1.87.9a.08.08 0 0 0-.04.1c.36.7.77 1.37 1.23 2a.08.08 0 0 0 .08.03 19.84 19.84 0 0 0 6-3.03.08.08 0 0 0 .03-.05c.5-5.18-.84-9.67-3.55-13.66a.06.06 0 0 0-.03-.03ZM8.02 15.33c-1.18 0-2.16-1.09-2.16-2.42s.96-2.42 2.16-2.42c1.21 0 2.18 1.1 2.16 2.42 0 1.33-.96 2.42-2.16 2.42Zm7.97 0c-1.18 0-2.16-1.09-2.16-2.42s.95-2.42 2.16-2.42c1.21 0 2.18 1.1 2.16 2.42 0 1.33-.95 2.42-2.16 2.42Z";

    public static readonly StyledProperty<bool> IsRailProperty = AvaloniaProperty.Register<ServiceStatusView, bool>(nameof(IsRail));

    private ShellStatusViewModel? _status;

    public ServiceStatusView()
    {
        InitializeComponent();
        GitHubIcon.Data = Geometry.Parse(GitHubPath);
        DiscordIcon.Data = Geometry.Parse(DiscordPath);
        RailGitHubIcon.Data = Geometry.Parse(GitHubPath);
        RailDiscordIcon.Data = Geometry.Parse(DiscordPath);
        GitHubLink.Click += (_, _) => Open(GitHubUrl);
        DiscordLink.Click += (_, _) => Open(DiscordUrl);
        RailGitHubLink.Click += (_, _) => Open(GitHubUrl);
        RailDiscordLink.Click += (_, _) => Open(DiscordUrl);
        RemoteLink.Click += (_, _) => OpenRemote?.Invoke();
        RailRemoteLink.Click += (_, _) => OpenRemote?.Invoke();
        ApplyTexts();
    }

    /// <summary>True while the sidebar is the icon rail: links, dot and globe only.</summary>
    public bool IsRail
    {
        get => GetValue(IsRailProperty);
        set => SetValue(IsRailProperty, value);
    }

    /// <summary>Opens Settings › Remote access (web <c>NavLink to="/settings/remote"</c>); set by the shell.</summary>
    public Action? OpenRemote { get; set; }

    /// <summary>The card's "Start service": the shell's command that toasts a failure (web <c>ServiceStatus.start</c>).</summary>
    public ICommand? StartCommand
    {
        set
        {
            if (value is not null) StartButton.Command = value;
        }
    }

    /// <summary>Where the remote-access state comes from (the shell's status view model).</summary>
    public ShellStatusViewModel? Status
    {
        get => _status;
        set
        {
            if (_status is not null) _status.PropertyChanged -= OnStatus;
            _status = value;
            if (_status is not null) _status.PropertyChanged += OnStatus;
            SyncRemote();
        }
    }

    /// <summary>Sets the card's fill (the web's <c>glass-tint 45%</c> over the wallpaper; follows light / dark, so the sidebar sets it).</summary>
    public void SetCardFill(IBrush fill) => Card.Background = fill;

    /// <summary>Re-reads the translated texts (links' tooltips, the remote line).</summary>
    public void ApplyTexts()
    {
        ToolTip.SetTip(GitHubLink, Loc.T("shell.github"));
        ToolTip.SetTip(DiscordLink, Loc.T("shell.discord"));
        ToolTip.SetTip(RailGitHubLink, Loc.T("shell.github"));
        ToolTip.SetTip(RailDiscordLink, Loc.T("shell.discord"));
        RemoteText.Text = Loc.T("shell.remoteOn");
        ToolTip.SetTip(RailRemoteLink, Loc.T("shell.remoteOn"));
        SyncRailTip();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ConnectionViewModel connection)
            connection.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ConnectionViewModel.StatusText)) SyncRailTip();
            };
        SyncRailTip();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsRailProperty) return;
        Card.IsVisible = !IsRail;
        Rail.IsVisible = IsRail;
    }

    private void OnStatus(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellStatusViewModel.RemoteOn) or nameof(ShellStatusViewModel.RemoteOnText)) SyncRemote();
    }

    private void SyncRemote()
    {
        var on = _status?.RemoteOn == true;
        RemoteLink.IsVisible = on;
        RailRemoteLink.IsVisible = on;
        SyncRailTip();
    }

    private void SyncRailTip()
    {
        // web: title={remoteOn ? `${status} · ${t('shell.remoteOn')}` : status}
        var status = (DataContext as ConnectionViewModel)?.StatusText ?? "";
        ToolTip.SetTip(RailStatus, ShellBanners.RailTooltip(status, _status?.RemoteOn == true, Loc.T("shell.remoteOn")));
    }

    private void Open(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(new Uri(url));
    }
}
