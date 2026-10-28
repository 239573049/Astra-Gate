using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.Shell;

/// <summary>
/// The shell's data-driven status beyond the connection itself: the "clients still point at the old Astra" banner with its one-click update
/// (web ConnectionBanner.tsx, plan §7.1) and the sidebar's remote-access note (web AppShell.tsx <c>ServiceStatus</c> <c>remoteOn</c>).
/// Its queries share the pages' cache keys: <c>['clients','typed']</c> (<c>List&lt;ClientInfoDto&gt;</c>, as the overview reads it) and
/// <c>['tunnel']</c> (<c>TunnelStatusDto</c>, polled like <c>useTunnel</c>), so a page's change shows here at once and the other way round.
/// </summary>
public sealed partial class ShellStatusViewModel : ObservableObject
{
    private readonly PageContext _context;
    private readonly ConnectionViewModel _connection;
    private readonly Query<List<ClientInfoDto>> _clients;
    private readonly Query<TunnelStatusDto> _tunnel;
    private IDisposable? _tunnelObserver;
    private bool _updating;

    /// <summary>Watches the clients list now and the tunnel while the server is up (web <c>useTunnel(up)</c>).</summary>
    public ShellStatusViewModel(PageContext context, ConnectionViewModel connection)
    {
        _context = context;
        _connection = connection;
        _clients = context.Queries.Query(new QueryKey("clients", "typed"),
            async ct => await context.Api.GetAsync<List<ClientInfoDto>>("/api/clients", ct) ?? []);
        _tunnel = context.Queries.Query(new QueryKey("tunnel"),
            async ct => await context.Api.GetAsync<TunnelStatusDto>("/api/tunnel", ct) ?? throw new ApiException(0, "empty tunnel response"),
            new QueryOptions<TunnelStatusDto> { RefetchInterval = Astra.Desktop.Core.Settings.TunnelLogic.PollInterval });
        _clients.PropertyChanged += OnQuery;
        _tunnel.PropertyChanged += OnQuery;
        connection.PropertyChanged += OnConnection;
        _ = _clients.Activate();
        SyncTunnel();
    }

    /// <summary>Kinds of the enabled clients whose config is stale (none while the server is down).</summary>
    public IReadOnlyList<string> OutdatedKinds => ShellBanners.OutdatedKinds(_clients.HasData ? _clients.Data : null, _connection.IsDown);

    /// <summary>The banner is open.</summary>
    public bool HasOutdatedClients => OutdatedKinds.Count > 0;

    /// <summary>The banner's title (<c>banner.clientsOutdated</c>).</summary>
    public string OutdatedTitle => Loc.T("banner.clientsOutdated", ("names", ShellBanners.ClientNames(ClientList, OutdatedKinds)));

    /// <summary>The banner's button (<c>banner.clientsUpdate</c>).</summary>
    public string UpdateClientsText => Loc.T("banner.clientsUpdate");

    /// <summary>True while the update runs: the button shows its spinner.</summary>
    public bool IsUpdatingClients
    {
        get => _updating;
        private set => SetProperty(ref _updating, value);
    }

    /// <summary>Remote access is on (and the server up): the sidebar shows the accent "remote access on" link.</summary>
    public bool RemoteOn => ShellBanners.RemoteOn(_connection.IsConnected, _tunnel.HasData ? _tunnel.Data : null);

    /// <summary><c>shell.remoteOn</c>.</summary>
    public string RemoteOnText => Loc.T("shell.remoteOn");

    private List<ClientInfoDto>? ClientList => _clients.HasData ? _clients.Data : null;

    /// <summary>
    /// "一键更新" (web <c>useReapplyClients</c>): rewrites the stale configs, then the client and provider views refetch, and a toast names
    /// what was updated (<c>banner.clientsUpdated</c>); a failure is an error toast with the server's message.
    /// </summary>
    [RelayCommand]
    private async Task UpdateClientsAsync()
    {
        if (IsUpdatingClients) return;
        var all = ClientList;
        IsUpdatingClients = true;
        try
        {
            var result = await _context.Api.PostAsync<ReappliedDto>("/api/clients/reapply");
            // useClientMutation's onSuccess: everything that shows a client or a provider binding.
            _context.Queries.Invalidate("clients");
            _context.Queries.Invalidate("providers");
            _context.Queries.Invalidate("provider");
            _context.Queries.Invalidate("client-models");
            _context.Feedback.Success(Loc.T("banner.clientsUpdated", ("names", ShellBanners.ClientNames(all, result?.Updated ?? []))));
        }
        catch (ApiException e)
        {
            _context.Feedback.Error(e.Message);
        }
        finally
        {
            IsUpdatingClients = false;
        }
    }

    /// <summary>Re-reads the translated texts.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(OutdatedTitle));
        OnPropertyChanged(nameof(UpdateClientsText));
        OnPropertyChanged(nameof(RemoteOnText));
    }

    private void OnQuery(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Data" or "HasData")) return;
        RaiseAll();
    }

    private void OnConnection(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.IsDown))) return;
        SyncTunnel();
        RaiseAll();
    }

    private void SyncTunnel()
    {
        if (_connection.IsConnected) _tunnelObserver ??= _tunnel.Activate();
        else
        {
            _tunnelObserver?.Dispose();
            _tunnelObserver = null;
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(OutdatedKinds));
        OnPropertyChanged(nameof(HasOutdatedClients));
        OnPropertyChanged(nameof(OutdatedTitle));
        OnPropertyChanged(nameof(RemoteOn));
    }
}
