using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.Connect;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.Pages.Clients;

/// <summary>
/// The Clients page body in the desktop's compact layout (it no longer mirrors the web <c>ClientsPage</c> markup): the page fills the
/// shell's body (<see cref="ClientsPageViewModel.HostScrolls"/> is false) — the client list on the left (228 px, scrolling on its own),
/// the connect banner, the load state and the selected client's panel on the right, whose header and unapplied-changes bar stay put while
/// its tab scrolls. Loading shows a skeleton and a failed list an alert in the panel column. The sheets the view model asks for open here.
/// </summary>
public sealed class ClientsView : UserControl
{
    private readonly ClientsPageViewModel _vm;
    private readonly ClientListView _list = new();
    private readonly ContentControl _status = new();
    private readonly ContentControl _connect = new();
    private readonly ClientPanelView _panel;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="vm"/>.</summary>
    public ClientsView(ClientsPageViewModel vm)
    {
        _vm = vm;
        _panel = new ClientPanelView(vm);
        vm.Panel = _panel;
        var top = new StackPanel { Children = { _connect, _status } };
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(_panel);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Children.Add(_list);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;
        Render();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _vm.Clients.PropertyChanged += OnClients;
        _vm.SelectionChanged += OnSelection;
        _vm.EnableRequested += OnEnable;
        _vm.BackupsRequested += OnBackups;
        _vm.InstallRequested += OnInstall;
        _vm.ConnectRequested += OnConnectRequested;
        _vm.Connect.Status.PropertyChanged += OnClients;
        JoinLinkInbox.Arrived += OnInbox;
        Render();
        TakeInbox();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _vm.Clients.PropertyChanged -= OnClients;
        _vm.SelectionChanged -= OnSelection;
        _vm.EnableRequested -= OnEnable;
        _vm.BackupsRequested -= OnBackups;
        _vm.InstallRequested -= OnInstall;
        _vm.ConnectRequested -= OnConnectRequested;
        _vm.Connect.Status.PropertyChanged -= OnClients;
        JoinLinkInbox.Arrived -= OnInbox;
    }

    private bool _pending;

    private void OnClients(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Data" or "Status" or "Error") || _pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Render();
        });
    }

    private void OnSelection() => Render();

    private void Render()
    {
        var q = _vm.Clients;
        var loading = !q.HasData && !q.IsError;
        _connect.Content = ConnectBanner.Create(_vm.Connect.Status.Data, () => _vm.RequestConnect(null), () => _ = _vm.LeaveConnectAsync());
        _list.Show(q.Data, loading, _vm.SelectedKind, _vm.Select);
        if (loading) _status.Content = new LoadingState(4);
        else if (q.IsError && !q.HasData) _status.Content = ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message);
        else _status.Content = null;
        _panel.Rebuild();
    }

    private ClientSheetContext SheetContext() => new(_vm.Api, _vm.Context!.Feedback, CopyAsync, _vm.Zone);

    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            return false;
        }
    }

    // A join link the operating system handed to the app opens the connect sheet here (the app shows this page for it).
    private void OnInbox() => Dispatcher.UIThread.Post(TakeInbox);

    private void TakeInbox()
    {
        if (JoinLinkInbox.Take() is { } link) _vm.RequestConnect(link);
    }

    private void OnConnectRequested(JoinLink? link) =>
        _vm.OpenSheet = ConnectSheets.Show(this, new ConnectSheetContext(_vm.Connect, _vm.Api, _vm.Context!.Feedback), link);

    private void OnEnable(ClientInfoDto client, ClientDraft draft) => _vm.OpenSheet = ClientSheets.ShowEnable(this, SheetContext(), client, draft.EnableRequest());

    private void OnBackups(ClientInfoDto client) => _vm.OpenSheet = ClientSheets.ShowBackups(this, SheetContext(), client);

    private void OnInstall(ClientInfoDto client, InstallSheetMode mode)
    {
        _vm.OpenSheet = ClientSheets.ShowInstall(this, SheetContext(), client, mode);
        _vm.JobStarted();
    }
}
