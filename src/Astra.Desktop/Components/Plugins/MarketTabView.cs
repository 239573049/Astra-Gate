using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// The market tab (web <c>MarketTab</c>): a search box and a kind filter over a grid of plugin cards — the community registry, or what a
/// search of the registry, npm and GitHub found — each with an Install button (which asks first) or the mark that it is installed.
/// The search box and filter are built once, so typing keeps its focus; only the results below are rebuilt.
/// </summary>
internal sealed class MarketTabView : LiveView
{
    private readonly PluginsPageModel _model;
    private readonly SearchField _search;
    private readonly SegmentedControl _kinds;
    private readonly Border _results = new();
    private IDisposable? _searchWatch;
    private string? _watching;
    private bool _syncing;

    /// <summary>Creates the tab over the page model.</summary>
    public MarketTabView(PluginsPageModel model)
    {
        _model = model;
        Watch(model.Api.Market, "Data", "Status");
        Watch(model.Api.Plugins, "Data");
        Watch(model, nameof(PluginsPageModel.DebouncedQuery), nameof(PluginsPageModel.Kind));

        var placeholder = Loc.T("plugins.searchPlaceholder");
        _search = new SearchField { Label = placeholder, IsLabelVisible = false, Placeholder = placeholder, MinWidth = 220, Text = model.SearchText };
        _search.TextChanged += (_, _) =>
        {
            if (!_syncing) model.SearchText = _search.Text ?? "";
        };
        _kinds = new SegmentedControl { VerticalAlignment = VerticalAlignment.Center };
        _kinds.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _kinds.SelectedValue is KindFilter kind) model.Kind = kind;
        };
        _kinds.ContainBringIntoView();
        Avalonia.Automation.AutomationProperties.SetName(_kinds, Loc.T("plugins.kindFilter"));

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        bar.Children.Add(_search);
        Grid.SetColumn(_kinds, 1);
        bar.Children.Add(_kinds);
        Content = new StackPanel { Spacing = 16, Children = { bar, _results } };
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        _syncing = true;
        try
        {
            _search.Label = _search.Placeholder = Loc.T("plugins.searchPlaceholder");
            RefreshKinds();
        }
        finally { _syncing = false; }
        _kinds.IsVisible = !_model.Searching;
        WatchSearch();
        _results.Child = _model.Searching ? Search() : Registry();
    }

    /// <inheritdoc />
    protected override void Detached()
    {
        _searchWatch?.Dispose();
        _searchWatch = null;
        _watching = null;
    }

    private void RefreshKinds()
    {
        var market = _model.Api.Market.Data;
        _kinds.ItemsSource = PluginMarketLogic.Filters.Select(f => new SegmentedOption(f, PluginMarketLogic.FilterText(market, f))).ToList();
        _kinds.SelectedValue = _model.Kind;
    }

    // The query of the current search is the one to follow; a new text is a new query.
    private void WatchSearch()
    {
        var wanted = _model.Searching ? _model.DebouncedQuery : null;
        if (wanted == _watching) return;
        _searchWatch?.Dispose();
        _searchWatch = null;
        _watching = wanted;
        if (wanted is null) return;
        _searchWatch = PageUi.On(_model.Api.Search(wanted), (_, e) =>
        {
            if (e.PropertyName is "Data" or "Status") _results.Child = _model.Searching ? Search() : Registry();
        });
    }

    private Control Search()
    {
        var q = _model.Api.Search(_model.DebouncedQuery);
        var stack = new StackPanel { Spacing = 16 };
        if (q.HasData && q.Data is { } found)
        {
            if (found.Count == 0)
                stack.Children.Add(PageUi.Card(new EmptyStateView(Loc.T("plugins.noResults"), icon: PageUi.Icon(LucideIconKind.Puzzle, 24)), new Thickness(24)));
            else stack.Children.Add(CardsOf(found.Select(r => PluginMarketLogic.CardOf(r, Installed()))));
        }
        else if (q.IsError) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
        else stack.Children.Add(new LoadingState(3));
        stack.Children.Add(PageUi.Note(Loc.T("plugins.searchNote")));
        return stack;
    }

    private Control Registry()
    {
        var q = _model.Api.Market;
        var stack = new StackPanel { Spacing = 16 };
        if (q.HasData && q.Data is { } market)
        {
            var locale = Loc.Language;
            if (q.IsError) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
            stack.Children.Add(CardsOf(PluginMarketLogic.Filter(market, _model.Kind).Select(l => PluginMarketLogic.CardOf(l, locale, Installed()))));
        }
        else if (q.IsError) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
        else stack.Children.Add(new LoadingState(4));
        stack.Children.Add(PageUi.Note(Loc.T("plugins.marketNote")));
        return stack;
    }

    private IReadOnlyList<PluginDto> Installed() => _model.Installed;

    private Control CardsOf(IEnumerable<ListingCardModel> cards)
    {
        var grid = new CardGrid();
        foreach (var card in cards) grid.Children.Add(Card(card));
        return grid;
    }

    // Web ListingCard: a card, 16px padding, gap 12: header (picture, name over spec), two lines of detail, tags and the action.
    private Control Card(ListingCardModel c)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var icon = PluginIconView.Create(c.Title, c.Icon, c.Kind);
        head.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = PageUi.Text(c.Title, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        title.FontSize = 14;
        title.LineHeight = 20; // the web's 1.45 body line height
        ToolTip.SetTip(title, c.Title);
        var subtitle = PageUi.Text(c.Subtitle, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, mono: true);
        subtitle.LineHeight = 16;
        ToolTip.SetTip(subtitle, c.Subtitle);
        names.Children.Add(title);
        names.Children.Add(subtitle);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);

        var detail = PageUi.Text(c.Detail ?? "", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
        detail.FontSize = KitStyle.Type12;
        detail.LineHeight = 19.5;
        detail.MaxLines = 2;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.MinHeight = 39;

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        // web `flex items-center gap-1.5` without wrapping: the tags stay on the action's line
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, ClipToBounds = true };
        foreach (var tag in c.Tags) tags.Children.Add(KitBadge.Create(tag));
        tags.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(tags);
        Control action;
        if (c.Installed)
        {
            var ok = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var check = PageUi.Icon(LucideIconKind.Check, 14);
            check.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Success);
            var word = PageUi.Text(Loc.T("plugins.installed"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Success);
            word.FontSize = KitStyle.Type12;
            ok.Children.Add(check);
            ok.Children.Add(word);
            action = ok;
        }
        else action = PageUi.Button(Loc.T("plugins.install"), () => _ = _model.InstallAsync(c.InstallSpec, c.InstallSource), ButtonVariant.Primary, ControlSize.Small);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        foot.Children.Add(action);

        var body = new StackPanel { Spacing = 12, Children = { head, detail, foot } };
        return PageUi.Card(body, new Thickness(16));
    }
}
