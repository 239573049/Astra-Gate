using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
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
/// The Clients page body (web <c>ClientsPage</c> markup): master-detail centred in at most 1200 px — the client list on the left (232 px), the
/// selected client's panel on the right, capped at 760 px and centred in the space the list leaves. Loading shows a skeleton and a failed
/// list an alert in the panel column, like the web. The sheets the view model asks for open here.
/// </summary>
public sealed class ClientsView : UserControl
{
    private readonly ClientsPageViewModel _vm;
    private readonly ClientListView _list = new();
    private readonly ContentControl _status = new();
    private readonly ClientPanelView _panel;
    private bool _wired;
    private ScrollViewer? _scroller;

    /// <summary>Builds the body for <paramref name="vm"/>.</summary>
    public ClientsView(ClientsPageViewModel vm)
    {
        _vm = vm;
        _panel = new ClientPanelView(vm);
        vm.Panel = _panel;
        var right = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _status, _panel } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20, MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Children.Add(_list);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;
        _list.RenderTransform = new Avalonia.Media.TranslateTransform();
        _list.VerticalAlignment = VerticalAlignment.Top; // its height is the card's, which is how far it can travel
        _list.ZIndex = 1;
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
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _offset = _scroller.GetObservable(ScrollViewer.OffsetProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<Vector>(_ => Stick()));
        Render();
    }

    private IDisposable? _offset;

    // web: the list is `@3xl:sticky @3xl:top-0` — it stays where it is while the page scrolls, until the page's bottom pushes it up.

    private void Stick()
    {
        if (_scroller is null || _list.RenderTransform is not Avalonia.Media.TranslateTransform shift) return;
        var room = Math.Max(0, (Content as Control)!.Bounds.Height - _list.Bounds.Height);
        shift.Y = Math.Clamp(_scroller.Offset.Y, 0, room);
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
        _offset?.Dispose();
        _offset = null;
        _scroller = null;
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

    private void OnEnable(ClientInfoDto client, ClientDraft draft) => _vm.OpenSheet = ClientSheets.ShowEnable(this, SheetContext(), client, draft.EnableRequest());

    private void OnBackups(ClientInfoDto client) => _vm.OpenSheet = ClientSheets.ShowBackups(this, SheetContext(), client);

    private void OnInstall(ClientInfoDto client, InstallSheetMode mode)
    {
        _vm.OpenSheet = ClientSheets.ShowInstall(this, SheetContext(), client, mode);
        _vm.JobStarted();
    }
}
