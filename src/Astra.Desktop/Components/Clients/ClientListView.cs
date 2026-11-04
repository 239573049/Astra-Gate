using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The master side of the Clients page in the desktop's compact layout (it no longer mirrors the web <c>ClientList</c>): a 228 px card that
/// fills the page's height — a search field and an All / Enabled / Needs-attention filter on top, then single-line 30 px rows in their own
/// scroll area, grouped by install state ("installed · n", "not detected · n", "coming soon · n") in the catalog order. A row shows the
/// client's mark, name, version (or "↑ latest" in the warning color when an update is available) and one trailing mark: a spinner while
/// an install runs, an orange dot when drifted or shadowed, a green check when enabled. The "not detected" group starts folded
/// (<see cref="ClientsLogic.IsCollapsible"/>); a search or a filter shows every match unfolded.
/// </summary>
public sealed class ClientListView : UserControl
{
    private readonly StackPanel _stack = new();
    private readonly SearchField _search;
    private readonly SegmentedControl _filter;
    // The folding groups the user opened (web ClientList's `open`); kept across redraws.
    private readonly HashSet<ClientGroup> _open = [];
    private ClientFilter _filterValue = ClientFilter.All;
    private string _query = "";
    private bool _syncing;
    private IReadOnlyList<ClientInfoDto>? _clients;
    private bool _loading;
    private string _selected = "";
    private Action<string> _onSelect = _ => { };

    /// <summary>An empty list.</summary>
    public ClientListView()
    {
        _search = new SearchField { Placeholder = Loc.T("clients.search"), Label = Loc.T("clients.search"), IsLabelVisible = false, Size = ControlSize.Small };
        _search.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _query = _search.Text ?? "";
            Redraw();
        };
        _filter = new SegmentedControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(_filter, Loc.T("clients.filter.label"));
        _filter.SelectionChanged += (_, _) =>
        {
            if (_syncing || _filter.SelectedValue is not ClientFilter f || f == _filterValue) return;
            _filterValue = f;
            Redraw();
        };
        var tools = new StackPanel { Spacing = 6, Margin = new Thickness(8, 8, 8, 6), Children = { _search, _filter } };
        var scroll = new ScrollViewer { Content = _stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _stack.Margin = new Thickness(0, 2, 0, 6);
        var line = new Border { Height = 1 };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
        var dock = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(line, Dock.Top);
        dock.Children.Add(tools);
        dock.Children.Add(line);
        dock.Children.Add(scroll);
        var card = new Border { BorderThickness = new Thickness(0.5), Child = dock, Width = 228, ClipToBounds = true };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        Content = card;
    }

    /// <summary>Draws <paramref name="clients"/> (null = still loading) with <paramref name="selected"/> highlighted.</summary>
    public void Show(IReadOnlyList<ClientInfoDto>? clients, bool loading, string selected, Action<string> onSelect)
    {
        _clients = clients;
        _loading = loading;
        _selected = selected;
        _onSelect = onSelect;
        Redraw();
    }

    private void Redraw()
    {
        SyncTools();
        _stack.Children.Clear();
        if (_loading) _stack.Children.Add(new LoadingState(6) { Margin = new Thickness(8) });
        var narrowed = _filterValue != ClientFilter.All || _query.Trim().Length > 0;
        var shown = 0;
        foreach (var group in ClientsLogic.Groups(_clients))
        {
            var items = group.Items.Where(c => ClientsLogic.Matches(c, _filterValue, _query)).ToList();
            if (items.Count == 0) continue;
            shown += items.Count;
            var title = Loc.T(group.TitleKey) + " · " + items.Count;
            var heading = ClientUi.Text(title, 11, ArcDesignTokenKind.TextMuted, FontWeight.Medium);
            var expanded = narrowed || ClientsLogic.IsExpanded(group, _selected, _open);
            if (ClientsLogic.IsCollapsible(group.Group) && !narrowed)
                _stack.Children.Add(GroupToggle(group.Group, title, heading, expanded));
            else
            {
                heading.Margin = new Thickness(14, 8, 14, 3);
                _stack.Children.Add(heading);
            }
            if (!expanded) continue;
            foreach (var c in items) _stack.Children.Add(Item(c, c.Kind == _selected, () => _onSelect(c.Kind)));
        }
        if (!_loading && _clients is { Count: > 0 } && shown == 0)
        {
            var empty = ClientUi.Text(Loc.T("clients.filter.empty"), 12, ArcDesignTokenKind.TextMuted);
            empty.Margin = new Thickness(14, 12);
            _stack.Children.Add(empty);
        }
    }

    // The filter's labels carry the counts, so they are rebuilt with the list (and after a language change).
    private void SyncTools()
    {
        _syncing = true;
        try
        {
            var all = _clients ?? [];
            _search.Placeholder = _search.Label = Loc.T("clients.search");
            _filter.ItemsSource = new List<SegmentedOption>
            {
                new(ClientFilter.All, Loc.T("clients.filter.all", ("count", all.Count(c => ClientsLogic.GroupOf(c) == ClientGroup.Installed)))),
                new(ClientFilter.Enabled, Loc.T("clients.filter.enabled", ("count", all.Count(c => c.Enabled)))),
                new(ClientFilter.Attention, Loc.T("clients.filter.attention", ("count", all.Count(ClientsLogic.NeedsAttention)))),
            };
            _filter.SelectedValue = _filterValue;
        }
        finally { _syncing = false; }
    }

    /// <summary>The heading of a folding group (web: the chevron button): a click opens or closes it and redraws the list.</summary>
    private Control GroupToggle(ClientGroup group, string title, TextBlock heading, bool expanded)
    {
        var chevron = ClientUi.Icon(expanded ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight, 12, ArcDesignTokenKind.TextMuted);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { chevron, heading } };
        var row = new Border
        {
            Padding = new Thickness(10, 7, 10, 4), Margin = new Thickness(4, 0), Child = line, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Focusable = true,
        };
        row.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        Avalonia.Automation.AutomationProperties.SetName(row, Loc.T(expanded ? "clients.group.collapse" : "clients.group.expand", ("group", title)));
        row.PointerEntered += (_, _) => row.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        void Toggle()
        {
            if (!_open.Remove(group)) _open.Add(group);
            Redraw();
        }
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) Toggle();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                Toggle();
            }
        };
        return row;
    }

    private static Control Item(ClientInfoDto client, bool active, Action onSelect)
    {
        var dim = ClientsLogic.GroupOf(client) != ClientGroup.Installed;
        var version = ClientsLogic.VersionOf(client);
        var shadowed = ClientsLogic.IsShadowed(client);
        var update = ClientsLogic.UpdateOf(client);
        var busy = client.Install?.Busy == true;
        var name = ClientCatalog.Name(client.Kind, client.Name);

        var icon = BrandIcon.Client(client.Kind, 18, fallbackName: client.Name);
        if (dim && !active) icon.Opacity = 0.6;

        var title = ClientUi.Text(name, 12.5, ArcDesignTokenKind.Foreground, active ? FontWeight.Medium : FontWeight.Normal);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;

        TextBlock? meta = null;
        if (update is not null)
        {
            meta = ClientUi.Text("↑ " + update, 11, ArcDesignTokenKind.Warning);
            ToolTip.SetTip(meta, Loc.T("clients.updateTo", ("version", update)));
        }
        else if (version is not null) meta = ClientUi.Text(version, 11, ArcDesignTokenKind.TextMuted);
        if (meta is not null)
        {
            meta.VerticalAlignment = VerticalAlignment.Center;
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            meta.MaxWidth = 76;
        }

        // One trailing mark, 14 px wide so names and versions line up.
        Control? mark = null;
        if (busy) mark = ClientUi.Spinner(12);
        else if (client.Status == "drifted" || shadowed || client.Repairs is { Count: > 0 })
        {
            var dot = new StatusDot(Tone.Orange);
            ToolTip.SetTip(dot, client.Status == "drifted" ? Loc.T("clients.drifted") : shadowed ? Loc.T("clients.shadowedBadge") : Loc.T("clients.repair.title", ("name", name)));
            mark = dot;
        }
        else if (client.Enabled)
        {
            var check = ClientUi.Icon(LucideIconKind.Check, 13, ArcDesignTokenKind.Success);
            check.StrokeThickness = 2.5;
            Avalonia.Automation.AutomationProperties.SetName(check, Loc.T("clients.enabled"));
            mark = check;
        }
        var markBox = new Border { Width = 14, Child = mark, VerticalAlignment = VerticalAlignment.Center };
        if (mark is not null) mark.HorizontalAlignment = HorizontalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(icon);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        if (meta is not null)
        {
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);
        }
        Grid.SetColumn(markBox, 3);
        grid.Children.Add(markBox);

        var row = new Border
        {
            Height = 30, Padding = new Thickness(8, 0), Margin = new Thickness(6, 0), Child = grid,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true,
        };
        row.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        Avalonia.Automation.AutomationProperties.SetName(row, name);
        if (!active)
        {
            row.PointerEntered += (_, _) => row.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        }
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) onSelect();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                onSelect();
            }
        };
        if (!active) return row;
        // The accent at 14 % (Arc's AccentSubtle is a neutral wash, not the accent).
        var tint = new Border { Opacity = 0.14, IsHitTestVisible = false, Margin = row.Margin };
        tint.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        return new Panel { Children = { tint, row } };
    }
}
