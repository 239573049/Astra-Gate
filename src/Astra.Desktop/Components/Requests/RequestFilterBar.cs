using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Requests;

/// <summary>One choice of a filter's multi-select: the query value and what it reads as.</summary>
/// <param name="Value">Client kind, token id, provider id, status or origin.</param>
/// <param name="Label">The name shown.</param>
public sealed record RequestFilterOption(string Value, string Label);

/// <summary>
/// The request-log filter bar (web <c>RequestFilters</c>): the model keyword search plus one multi-select per list filter, and a "clear filters"
/// button while any is set. The web's options carry brand icons for clients and providers; Arc's multi-select takes only a geometry or image as an
/// option icon, so the options here are text (Arc gap: option icon content). The origin filter shows only once remote access has been used.
/// Layout is the web's flex row: search basis 240 grow 2 (min 220), selects basis 9 rem grow 1, wrapping at the window width.
/// </summary>
public sealed class RequestFilterBar : UserControl
{
    private readonly FlexWrapPanel _panel = new() { Spacing = 8 };
    private readonly SearchField _search;
    private readonly Dictionary<RequestFilterId, MultiSelect> _selects = [];
    private readonly Dictionary<RequestFilterId, List<RequestFilterOption>> _options = [];
    private readonly ArcButton _clear;
    private bool _syncing;
    private bool _showOrigin;
    private string _model = "";
    private RequestFilterValues _filters = RequestFilterValues.Empty;

    /// <summary>The bar, empty.</summary>
    public RequestFilterBar()
    {
        _search = new SearchField { Size = ControlSize.Medium, IsLabelVisible = false, VerticalAlignment = VerticalAlignment.Center };
        FlexWrapPanel.SetBasis(_search, 240);
        FlexWrapPanel.SetGrow(_search, 2);
        _search.MinWidth = 220;
        _search.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _model = _search.Text ?? "";
            ModelChanged?.Invoke(_model);
        };
        foreach (var id in RequestFilterCatalog.Ids)
        {
            var select = new MultiSelect { MaxVisibleChips = 2, VerticalAlignment = VerticalAlignment.Center, MinWidth = 144 };
            select.TextSelector = o => ((RequestFilterOption)o!).Label;
            select.ValueSelector = o => ((RequestFilterOption)o!).Value;
            FlexWrapPanel.SetBasis(select, 144);
            FlexWrapPanel.SetGrow(select, 1);
            var captured = id;
            select.SelectionChanged += (_, _) =>
            {
                if (_syncing) return;
                FilterChanged?.Invoke(captured, select.SelectedValues.Select(v => (string)v!).ToList());
            };
            _selects[id] = select;
            _options[id] = [];
        }
        _clear = new ArcButton { Variant = ButtonVariant.Ghost, VerticalAlignment = VerticalAlignment.Center };
        _clear.Click += (_, _) => Cleared?.Invoke();
        Content = _panel;
        AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.Group);
        Rebuild();
    }

    /// <summary>The keyword changed by the user (not debounced; the page debounces).</summary>
    public event Action<string>? ModelChanged;

    /// <summary>One filter's picks changed by the user.</summary>
    public event Action<RequestFilterId, IReadOnlyList<string>>? FilterChanged;

    /// <summary>The "clear filters" button was pressed.</summary>
    public event Action? Cleared;

    /// <summary>Gives the keyword box focus (the page's <c>find</c> command).</summary>
    public void FocusSearch() => _search.Focus();

    /// <summary>Shows the page's current keyword and picks (no events).</summary>
    public void SetState(string model, RequestFilterValues filters, bool showOrigin)
    {
        _syncing = true;
        try
        {
            _model = model;
            _filters = filters;
            _showOrigin = showOrigin;
            if ((_search.Text ?? "") != model) _search.Text = model;
            foreach (var id in RequestFilterCatalog.Ids) SyncSelection(id);
            Rebuild();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Sets what a filter offers (clients, providers and tokens come from the server; statuses and origins are fixed).</summary>
    public void SetOptions(RequestFilterId id, IEnumerable<RequestFilterOption> options)
    {
        _syncing = true;
        try
        {
            _options[id] = options.ToList();
            _selects[id].ItemsSource = _options[id];
            SyncSelection(id);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Applies the language-dependent texts again.</summary>
    public void RefreshTexts()
    {
        _syncing = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSelection(RequestFilterId id)
    {
        var picked = _filters.Get(id);
        var select = _selects[id];
        var items = _options[id].Where(o => picked.Contains(o.Value)).Cast<object>().ToList();
        // Picks the list does not offer (a link carried a token id that no longer exists) cannot be shown as chips; they still filter.
        select.SelectedItems = items;
    }

    private void Rebuild()
    {
        var active = RequestFilterCatalog.HasFilters(_model, _filters);
        _search.Label = Loc.T("requests.searchModel");
        _search.Placeholder = Loc.T("requests.searchModel");
        _search.ClearText = Loc.T("common.clearSearch");
        AutomationProperties.SetName(_search, Loc.T("requests.searchModel"));
        AutomationProperties.SetName(this, Loc.T("requests.filters"));
        _panel.Children.Clear();
        _panel.Children.Add(_search);
        foreach (var id in RequestFilterCatalog.VisibleFields(RequestFilterCatalog.ShowOrigin(_showOrigin, false, _filters)))
        {
            var select = _selects[id];
            select.Placeholder = Loc.T(RequestFilterCatalog.PlaceholderKey(id));
            select.EmptyText = Loc.T("common.noResults");
            AutomationProperties.SetName(select, Loc.T(RequestFilterCatalog.LabelKey(id)));
            _panel.Children.Add(select);
        }
        if (active)
        {
            _clear.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new LucideIcon { Kind = LucideIconKind.X, Size = 16, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = Loc.T("requests.clearFilters"), VerticalAlignment = VerticalAlignment.Center } },
            };
            _panel.Children.Add(_clear);
        }
    }
}
