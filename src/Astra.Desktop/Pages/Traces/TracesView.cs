using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Traces;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Traces;

/// <summary>
/// The Traces page body (pages/TracesPage.tsx): the view of the current location. The shell draws the header (title, subtitle, the
/// segmented control of the three views) from <see cref="TracesPageViewModel"/>.
/// </summary>
public sealed class TracesView : UserControl
{
    private readonly TracesPageViewModel _vm;
    private Window? _window;

    /// <summary>Builds the body for <paramref name="vm"/>.</summary>
    public TracesView(TracesPageViewModel vm)
    {
        _vm = vm;
        // web contentClassName "pt-2! pb-3!" over the page's pt-5: the shell's fixed cell pads 20 above and nothing below
        Margin = new Thickness(0, -12, 0, 12);
        Content = vm.View switch
        {
            TopologyViewModel t => new TopologyView(t),
            SessionsListViewModel l => new SessionsListView(l),
            SessionViewModel s => new SessionTraceView(s),
            RequestTraceViewModel r => new RequestTraceView(r),
            RecentRequestsViewModel r => new RecentRequestsView(r),
            _ => null,
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;
        _window.PropertyChanged += OnWindowChanged;
        SyncVisibility();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        _window = null;
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty) SyncVisibility();
    }

    /// <summary>The web closes the live stream while its tab is hidden; the window hidden or minimized is the desktop's equivalent.</summary>
    private void SyncVisibility()
    {
        if (_window is null) return;
        _vm.Visibility.Set(_window.IsVisible && _window.WindowState != WindowState.Minimized);
    }
}

/// <summary>A traces view: redraws when its view model changes or the language does; the palette follows the theme.</summary>
public abstract class TraceViewBase : UserControl
{
    private readonly TraceViewModel _model;
    private bool _pending;

    /// <summary>The root everything is drawn in (the palette watch lives here).</summary>
    protected Grid Root { get; } = new();

    /// <summary>Colours of the current theme.</summary>
    protected TracePalette Palette { get; private set; } = null!;

    /// <summary>Wraps <paramref name="model"/>.</summary>
    protected TraceViewBase(TraceViewModel model)
    {
        _model = model;
        Content = Root;
        PaletteWatch.Attach(Root, Redraw);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _model.Changed += OnChanged;
        Loc.LanguageChanged += OnLanguage;
        Redraw();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _model.Changed -= OnChanged;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage()
    {
        // the canvas chrome (hint, toolbar) is built once: build it again in the new language
        DropCanvas();
        OnChanged();
    }

    /// <summary>Forgets a kept canvas so the next draw builds a new one (a language change).</summary>
    protected virtual void DropCanvas() { }

    // Changes come in bursts (a poll answer plus live events): draw once per dispatcher turn.
    private void OnChanged()
    {
        if (_pending) return;
        _pending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Redraw();
        });
    }

    private void Redraw()
    {
        if (VisualRoot is null) return;
        Palette = TracePalette.For(this);
        Draw();
    }

    /// <summary>Draws the view from its model.</summary>
    protected abstract void Draw();

    private readonly FlexColumn _column = new();
    private readonly List<ContentControl> _slots = [];

    /// <summary>Shows one control as the whole view.</summary>
    protected void Show(Control child) => Rows(0, 12, child);

    /// <summary>
    /// Shows <paramref name="rows"/> as a column (web <c>flex flex-col gap-3</c>), the one at <paramref name="fill"/> taking the rest of the
    /// height (<c>flex-1</c>, -1 for none); null rows are hidden. Rows live in fixed slots, so a control shown again at the same index (the
    /// canvas) stays in the tree: no re-attach, no lost drag or animation.
    /// </summary>
    protected void Rows(int fill, double gap, params Control?[] rows)
    {
        if (_column.Parent is null) Root.Children.Add(_column);
        _column.Gap = gap;
        while (_slots.Count < rows.Length)
        {
            var slot = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            _slots.Add(slot);
            _column.Children.Add(slot);
        }
        for (var i = 0; i < _slots.Count; i++)
        {
            var row = i < rows.Length ? rows[i] : null;
            if (!ReferenceEquals(_slots[i].Content, row)) _slots[i].Content = row;
            _slots[i].IsVisible = row is not null;
        }
        _column.Fill = fill >= 0 && fill < _slots.Count ? _slots[fill] : null;
        _column.InvalidateMeasure();
    }

    /// <summary>A column of rows: auto rows, and the one at <paramref name="fill"/> takes the rest (web <c>flex flex-col gap-3</c> + <c>flex-1</c>).</summary>
    protected static Grid Column(int fill, double gap, params Control?[] rows)
    {
        var grid = new Grid { RowSpacing = gap };
        var i = 0;
        foreach (var row in rows)
        {
            if (row is null) continue;
            grid.RowDefinitions.Add(new RowDefinition(i == fill ? GridLength.Star : GridLength.Auto));
            Grid.SetRow(row, grid.RowDefinitions.Count - 1);
            grid.Children.Add(row);
            i++;
        }
        return grid;
    }

    /// <summary>A line of chips with controls pushed to the right (<c>flex flex-wrap items-center gap-2</c> + <c>ml-auto</c>).</summary>
    protected static Grid TopLine(Control left, Control? right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(left);
        if (right is not null)
        {
            right.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>The empty state (web <c>EmptyState</c>: px-6 py-12 around Arc's).</summary>
    protected static Control Empty(string title, Control? action = null) =>
        new Border { Padding = new Thickness(24, 48), Child = new EmptyStateView(title, null, null, action), VerticalAlignment = VerticalAlignment.Top };

    /// <summary>The loading lines (web <c>Spinner lines={6}</c>).</summary>
    protected static Control Loading() => new LoadingState(6) { VerticalAlignment = VerticalAlignment.Top };

    /// <summary>A segmented control (range / view pickers).</summary>
    protected static ControlledSegmented Segmented(IEnumerable<(string Value, string Label)> options, string value, string label, Action<string> chose)
    {
        var s = new ControlledSegmented(options, value) { VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(s, label);
        s.Chose += chose;
        return s;
    }
}

/// <summary>
/// A · the topology (topology.tsx <c>TopologyView</c>): headline chips, the live legend, the 活动流 toggle, the range picker, the canvas (each
/// provider's accounts as rows) and, beside it, the activity feed. Hovering a feed card highlights its path; while anything is in the live feed
/// the view redraws every second so a refusal stops bouncing and a switch note leaves the hub on time.
/// </summary>
public sealed class TopologyView : TraceViewBase
{
    private readonly TopologyViewModel _vm;
    private TraceCanvas? _canvas;
    private ActivityRailView? _rail;
    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
    private readonly ContentControl _main = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private IDisposable? _tick;

    /// <summary>Builds the view.</summary>
    public TopologyView(TopologyViewModel vm) : base(vm)
    {
        _vm = vm;
        _body.Children.Add(_main);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick = TraceTicker.Second(() =>
        {
            if (_vm.S.Live.List.Count > 0) _vm.Tick();
        });
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick?.Dispose();
        _tick = null;
    }

    /// <inheritdoc />
    protected override void DropCanvas()
    {
        _canvas = null;
        if (_rail is not null) _body.Children.Remove(_rail);
        _rail = null;
    }

    /// <inheritdoc />
    protected override void Draw()
    {
        var p = Palette;
        var s = _vm.S;
        var chips = TraceUi.Wrap(8, 8);
        if (_vm.Data is { } data)
        {
            var g = data.Gateway;
            chips.Children.Add(TraceUi.Chip(TraceUi.Dot(p.Green), TraceUi.Bold(TraceFormat.Num(g.Requests)),
                Loc.T("traces.topology.requestsIn", ("window", Loc.T("traces.range." + _vm.Range)))));
            chips.Children.Add(TraceUi.Chip(_vm.InFlight > 0 ? TraceUi.LivePulse() : null, Loc.T("traces.kv.inFlight"), TraceUi.Bold(_vm.InFlight.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            chips.Children.Add(TraceUi.Chip(Loc.T("traces.kv.errorRate"), TraceUi.Bold(TopologyPageLogic.ErrorRate(g))));
            chips.Children.Add(TraceUi.Chip(Loc.T("traces.kv.overheadP95"), TraceUi.Bold(TraceFormat.Us(g.OverheadP95Us))));
        }
        // max-lg:hidden — the legend gives way below 1024 px of window width
        var legend = TraceUi.HideBelowWindowWidth(TraceUi.Wrap(12, 4), TopologyPageLogic.ShowsLegend);
        legend.Children.Add(TraceUi.Text(Loc.T("traces.topology.legend"), 11.5, ArcDesignTokenKind.TextSecondary));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.cause.user"), LegendMark.Dot, TracePalette.Cyan));
        legend.Children.Add(HollowLegend(Loc.T("traces.cause.tool_result"), p.Green));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.live.legend.think"), LegendMark.Dot, p.Accent2));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.live.legend.write"), LegendMark.Dot, p.Accent));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.live.legend.tool"), LegendMark.Dot, p.Green));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.live.legend.retry"), LegendMark.Dot, p.Orange));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.live.legend.failed"), LegendMark.Dot, p.Red));
        chips.Children.Add(legend);
        var range = Segmented(TraceQueries.TopologyRanges.Select(r => (r, Loc.T("traces.range." + r))), _vm.Range, Loc.T("traces.range.label"), _vm.SetRange);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(RailToggle(p));
        right.Children.Add(range);

        Control body;
        if (_vm.IsLoading) body = Loading();
        else if (_vm.IsError) body = TraceUi.Error(_vm.Error);
        else if (_vm.IsEmpty)
        {
            var empty = TraceUi.Card(new Border
            {
                Padding = new Thickness(40),
                Child = new TextBlock { Text = Loc.T("traces.topology.empty"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
                    .Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary),
            });
            body = empty;
        }
        else
        {
            _canvas ??= CreateCanvas();
            _canvas.SetGraph(_vm.Graph);
            _canvas.SetSelected(_vm.Selected);
            _canvas.SetInspector(_vm.Inspector is { } m ? TraceInspectorView.Create(m, p, s.Open, s.ClientLabel) : null);
            body = _canvas;
        }
        if (!ReferenceEquals(_main.Content, body)) _main.Content = body;
        if (_vm.Rail)
        {
            if (_rail is null)
            {
                _rail = new ActivityRailView(new ActivityRailActions(_vm.NameOf, s.NowMs, _vm.Hover, id => s.Open(TraceRoutes.Request(id)), _vm.SetContent, s.ClientLabel))
                {
                    Width = 360,
                };
                Grid.SetColumn(_rail, 1);
                _body.Children.Add(_rail);
            }
            _rail.Update(_vm.Items, _vm.ContentOn, _vm.ContentReady, _vm.Hovered?.Id);
        }
        else if (_rail is not null)
        {
            _body.Children.Remove(_rail);
            _rail = null;
        }
        Rows(1, 12, TopLine(chips, right), _body);
    }

    /// <summary>The 活动流 toggle (topology.tsx: a 28 px button, pressed while the feed shows).</summary>
    private Border RailToggle(TracePalette p)
    {
        var on = _vm.Rail;
        var button = new Border
        {
            Height = 28, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(p.Separator), Background = on ? new SolidColorBrush(p.SurfaceSolid) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Focusable = true, VerticalAlignment = VerticalAlignment.Center,
            Child = TraceUi.Text(Loc.T("traces.activity.toggle"), 12, on ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextSecondary),
        };
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.T("traces.activity.toggle"));
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _vm.ToggleRail();
        };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            _vm.ToggleRail();
        };
        return button;
    }

    /// <summary>The tool-result legend entry: a hollow green dot (the capsule's outline).</summary>
    private static Control HollowLegend(string text, Color color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(TraceUi.Text(text, 11.5, ArcDesignTokenKind.TextSecondary));
        return row;
    }

    private TraceCanvas CreateCanvas()
    {
        var s = _vm.S;
        var canvas = new TraceCanvas("topology", new TraceLiveContext(s.Live, s.NowMs, id => s.Open(TraceRoutes.Request(id))), s.ClientLabel);
        canvas.SelectRequested += _vm.Select;
        return canvas;
    }
}

/// <summary>The sessions list (session.tsx <c>SessionsList</c>): the client filter chip, the range picker, a row per session.</summary>
public sealed class SessionsListView : TraceViewBase
{
    private readonly SessionsListViewModel _vm;

    /// <summary>Builds the view.</summary>
    public SessionsListView(SessionsListViewModel vm) : base(vm) => _vm = vm;

    /// <inheritdoc />
    protected override void Draw()
    {
        var s = _vm.S;
        var left = TraceUi.Wrap(8, 8);
        if (_vm.Client is { } client)
        {
            var chip = TraceUi.Chip(BrandIcon.Client(client, 16, fallbackName: s.ClientLabel(client)), $"{s.ClientLabel(client)} ×");
            ((TextBlock)((StackPanel)chip.Child!).Children[^1]).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
            chip.Cursor = new Cursor(StandardCursorType.Hand);
            chip.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _vm.Set("client", null);
            };
            left.Children.Add(chip);
        }
        var range = Segmented(TraceQueries.SessionRanges.Select(r => (r, Loc.T("traces.range." + r))), _vm.Range, Loc.T("traces.range.label"), r => _vm.Set("range", r));

        Control body;
        if (_vm.IsLoading) body = Loading();
        else if (_vm.IsError) body = TraceUi.Error(_vm.Error);
        else if (_vm.Data is not { Count: > 0 } sessions) body = Empty(Loc.T("traces.sessions.empty"));
        else
        {
            var rows = new StackPanel();
            foreach (var session in sessions)
            {
                if (rows.Children.Count > 0) rows.Children.Add(TraceUi.Divider());
                rows.Children.Add(Row(session));
            }
            body = TraceUi.Card(rows);
            body.VerticalAlignment = VerticalAlignment.Top;
        }
        var scroll = new ScrollViewer { Content = Column(-1, 12, TopLine(left, range), body), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Show(scroll);
    }

    private Control Row(TraceSessionDto x)
    {
        var s = _vm.S;
        var p = Palette;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.6*,*,*,*,*"), ColumnSpacing = 12 };
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (!string.IsNullOrEmpty(x.ClientKind)) who.Children.Add(BrandIcon.Client(x.ClientKind, 26, fallbackName: s.ClientLabel(x.ClientKind)));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(TraceUi.Text(!string.IsNullOrEmpty(x.ClientKind) ? s.ClientLabel(x.ClientKind) : Loc.T("traces.topology.direct"), 12.5, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        names.Children.Add(TraceUi.Mono(x.SessionKey, 11, ArcDesignTokenKind.TextSecondary));
        who.Children.Add(names);
        grid.Children.Add(who);
        Cell(grid, 1, x.Active ? TraceUi.Text($"● {Loc.T("traces.sessions.active")}", 12.5, p.Green) : TraceUi.Text(s.DateTime(x.LastAtUtc), 12.5, ArcDesignTokenKind.TextSecondary));
        Cell(grid, 2, TraceUi.Text(Loc.T("traces.sessions.turnsCalls", ("turns", x.Turns), ("calls", x.Requests)), 12.5, ArcDesignTokenKind.Foreground));
        Cell(grid, 3, x.Errors > 0
            ? TraceUi.Text(Loc.T("traces.sessions.errors", ("count", x.Errors)), 12.5, p.Red)
            : TraceUi.Text(Loc.T("traces.sessions.errors", ("count", x.Errors)), 12.5, ArcDesignTokenKind.TextSecondary));
        Cell(grid, 4, TraceUi.Text(TraceFormat.Nanos(x.CostNanoUsd), 12.5, ArcDesignTokenKind.Foreground));
        return Clickable(grid, new Thickness(14, 10), () => _vm.Open(x.SessionKey));
    }

    internal static void Cell(Grid grid, int column, Control c)
    {
        Grid.SetColumn(c, column);
        grid.Children.Add(c);
    }

    /// <summary>A full-width row button with the muted hover fill.</summary>
    internal static Border Clickable(Control content, Thickness padding, Action click)
    {
        var row = new Border { Padding = padding, Child = content, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
        row.PointerEntered += (_, _) => row.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        row.PointerExited += (_, _) => { row.ClearValue(Border.BackgroundProperty); row.Background = Brushes.Transparent; };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            click();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            click();
        };
        return row;
    }
}

/// <summary>
/// One session (session.tsx <c>SessionView</c>): chips (client, the agent's status, turns and calls, providers, cost), the anomaly jump,
/// expand-all, the turn picker and the canvas / timeline switch; the legend; the view; the turn's time bar.
/// </summary>
public sealed class SessionTraceView : TraceViewBase
{
    private readonly SessionViewModel _vm;
    private TraceCanvas? _canvas;
    private string? _canvasTurn;
    private (string Id, int Seq)? _focused;
    private Border? _statusChip;
    private IDisposable? _second;

    /// <summary>Builds the view.</summary>
    public SessionTraceView(SessionViewModel vm) : base(vm) => _vm = vm;

    /// <inheritdoc />
    protected override void DropCanvas() => _canvas = null;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // the agent's "for how long" ticks once a second
        _second = TraceTicker.Second(UpdateStatusChip);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _second?.Dispose();
        _second = null;
    }

    /// <inheritdoc />
    protected override void Draw()
    {
        var s = _vm.S;
        var p = Palette;
        if (_vm.IsLoading)
        {
            Show(Loading());
            return;
        }
        if (_vm.IsError)
        {
            Show(TraceUi.Error(_vm.Error));
            return;
        }
        if (_vm.Data is not { } data)
        {
            Show(Empty(Loc.T("traces.session.notFound"), TraceUi.Link(Loc.T("traces.session.back"), _vm.Back)));
            return;
        }
        var state = _vm.State;
        var x = data.Session;
        var chips = TraceUi.Wrap(8, 8);
        chips.Children.Add(TraceUi.Chip(
            !string.IsNullOrEmpty(x.ClientKind) ? BrandIcon.Client(x.ClientKind, 16, fallbackName: s.ClientLabel(x.ClientKind)) : null,
            TraceUi.Bold(!string.IsNullOrEmpty(x.ClientKind) ? s.ClientLabel(x.ClientKind) : Loc.T("traces.topology.direct")),
            x.Active ? TraceUi.Text($"● {Loc.T("traces.sessions.active")}", 12, p.Green) : null));
        _statusChip = new Border();
        if (x.Active || state.Pending.Count > 0) chips.Children.Add(_statusChip);
        UpdateStatusChip();
        chips.Children.Add(TraceUi.Chip(Loc.T("traces.sessions.turnsCalls", ("turns", x.Turns), ("calls", x.Requests))));
        foreach (var id in x.ProviderIds)
            if (s.Brands.TryGetValue(id, out var b)) chips.Children.Add(TraceUi.Chip(BrandIcon.Provider(b.Name, b.Icon, 16), b.Name));
        chips.Children.Add(TraceUi.Chip(TraceUi.Bold(TraceFormat.Nanos(x.CostNanoUsd))));

        var graph = _vm.Graph;
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (graph.Anomalies.Count > 0)
        {
            var next = Pill(Loc.T("traces.session.nextAnomaly", ("count", graph.Anomalies.Count)), p.Red, TracePalette.Mix(p.Red, 0.45, p.Separator), _vm.NextAnomaly);
            right.Children.Add(next);
        }
        var groups = SessionPageLogic.GroupIds(graph);
        if (state.View == SessionView.Canvas && groups.Count > 0) right.Children.Add(Pill(Loc.T("traces.session.expandAll"), p.Accent, null, () => _vm.Expand(groups)));
        if (data.Turns.Count > 0 || state.LiveTurn is not null)
        {
            var picker = new ControlledSelect(_vm.TurnOptions.Select(o => (o.Value, o.Label)), state.TurnId, 230) { VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(picker, Loc.T("traces.session.turnPicker"));
            picker.Chose += _vm.SetTurn;
            right.Children.Add(picker);
        }
        right.Children.Add(Segmented([("canvas", Loc.T("traces.session.view.canvas")), ("timeline", Loc.T("traces.session.view.timeline"))],
            state.View == SessionView.Timeline ? "timeline" : "canvas", Loc.T("traces.session.viewLabel"), _vm.SetView));

        var legend = TraceUi.Wrap(16, 4);
        legend.Children.Add(TraceUi.Text(Loc.T("traces.session.turn", ("n", state.TurnIndex + 1)), 11.5, ArcDesignTokenKind.TextSecondary));
        if (state.View == SessionView.Canvas)
        {
            legend.Children.Add(TraceUi.Legend(Loc.T("traces.session.legend.exact"), LegendMark.Line, p.Label3));
            legend.Children.Add(TraceUi.Legend(Loc.T("traces.session.legend.inferred"), LegendMark.DashedLine, p.Accent2));
            legend.Children.Add(TraceUi.Legend(Loc.T("traces.session.legend.tool"), LegendMark.Pill, p.Label3));
        }
        else
            foreach (var k in new[] { PhaseKind.Wait, PhaseKind.Think, PhaseKind.Write, PhaseKind.Tool })
                legend.Children.Add(TraceUi.Legend(Loc.T("traces.phase." + k.ToString().ToLowerInvariant()), LegendMark.Swatch, p.Phase(k)));

        var inspectorModel = _vm.Inspector;
        Control? inspector = inspectorModel is null ? null : TraceInspectorView.Create(inspectorModel, p, s.Open, s.ClientLabel);
        Control view;
        if (state.Calls.Count == 0 && state.Pending.Count == 0) view = Empty(Loc.T("traces.session.empty"));
        else if (state.View == SessionView.Canvas)
        {
            // a new turn is a new layout: fit it again
            if (_canvas is null || _canvasTurn != state.TurnId)
            {
                _canvas = new TraceCanvas(null, new TraceLiveContext(s.Live, s.NowMs, _vm.OpenRequest), s.ClientLabel);
                _canvas.SelectRequested += _vm.Select;
                _canvas.SizeChanged += (_, e) => _vm.SetWidth(e.NewSize.Width);
                _canvasTurn = state.TurnId;
            }
            _canvas.SetGraph(graph);
            _canvas.SetSelected(_vm.Selected);
            _canvas.SetInspector(inspector);
            if (_vm.Focus is { } f && f != _focused)
            {
                _focused = f;
                _canvas.FocusNode(f.Id);
            }
            view = _canvas;
        }
        else
        {
            var timeline = TimelineBuilder.Build(state.Calls, state.Spans, s.T, state.Pending.Select(r => s.Live.Get(r.Id) ?? r).ToList(), s.NowMs());
            var tl = new TraceTimelineView(timeline, _vm.Selected, _vm.SelectRow, inspector, p);
            if (_vm.Focus is { } f && f != _focused)
            {
                _focused = f;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => tl.Reveal(f.Id));
            }
            view = tl;
        }

        var bar = _vm.TimeBar;
        Control? time = bar.Bar is { } tb ? TimeBarView.Create(tb, p) : null;
        Control? overhead = null;
        if (bar.Overhead is { } o)
        {
            overhead = TraceUi.Text(o, 11.5, ArcDesignTokenKind.TextSecondary);
            overhead.Margin = new Thickness(0, -4, 0, 0);
        }
        Rows(2, 12, TopLine(chips, right), legend, view, time, overhead);
    }

    /// <summary>The agent's status chip (session.tsx <c>AgentStatusChip</c>): what the call in flight or the last call left it doing, and since when.</summary>
    private void UpdateStatusChip()
    {
        if (_statusChip is null || Palette is null) return;
        var status = _vm.AgentStatus;
        _statusChip.IsVisible = status is not null;
        if (status is null) return;
        var p = Palette;
        var color = status.Kind switch
        {
            AgentStatusKind.Upstream or AgentStatusKind.Waiting => p.Phase(PhaseKind.Wait),
            AgentStatusKind.Thinking => p.Phase(PhaseKind.Think),
            AgentStatusKind.Writing => p.Phase(PhaseKind.Write),
            AgentStatusKind.Tool or AgentStatusKind.Tools => p.Phase(PhaseKind.Tool),
            AgentStatusKind.Idle => p.TextMuted,
            _ => p.Red,
        };
        var dot = TraceUi.Dot(color, 8);
        if (status.Busy && TraceUi.Animates(this)) dot.Opacity = 0.5 + 0.5 * Math.Abs(Math.Cos(Environment.TickCount64 / 1000.0 * Math.PI));
        var chip = TraceUi.Chip(dot, Loc.T("traces.agent.label"), TraceUi.Bold(status.Text(_vm.S.T)),
            status.Model is { } m ? TraceFormat.ShortModel(m) : null, TraceFormat.Since(_vm.S.NowMs() - status.Since));
        _statusChip.Child = chip;
    }

    private static Border Pill(string text, Color fg, Color? border, Action click)
    {
        var pill = new Border
        {
            Height = 28, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(border is null ? 0 : 1),
            BorderBrush = border is { } b ? new SolidColorBrush(b) : null, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = TraceUi.Text(text, 12, fg), VerticalAlignment = VerticalAlignment.Center,
        };
        var hover = new SolidColorBrush(border is null ? Colors.Transparent : TracePalette.WithAlpha(fg, 0.1));
        pill.PointerEntered += (_, _) =>
        {
            if (border is null) pill.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            else pill.Background = hover;
        };
        pill.PointerExited += (_, _) => { pill.ClearValue(Border.BackgroundProperty); pill.Background = Brushes.Transparent; };
        pill.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return pill;
    }
}

/// <summary>The most recent requests, to pick one to trace (request.tsx <c>RecentRequests</c>).</summary>
public sealed class RecentRequestsView : TraceViewBase
{
    private readonly RecentRequestsViewModel _vm;

    /// <summary>Builds the view.</summary>
    public RecentRequestsView(RecentRequestsViewModel vm) : base(vm) => _vm = vm;

    /// <inheritdoc />
    protected override void Draw()
    {
        if (_vm.IsLoading)
        {
            Show(Loading());
            return;
        }
        if (_vm.Items.Count == 0)
        {
            Show(Empty(Loc.T("traces.request.none")));
            return;
        }
        var s = _vm.S;
        var rows = new StackPanel();
        foreach (var r in _vm.Items)
        {
            if (rows.Children.Count > 0) rows.Children.Add(TraceUi.Divider());
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,*,90,80"), ColumnSpacing = 12 };
            grid.Children.Add(TraceUi.Text(s.DateTime(r.StartedAtUtc), 12.5, ArcDesignTokenKind.TextSecondary));
            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!string.IsNullOrEmpty(r.ClientKind)) who.Children.Add(BrandIcon.Client(r.ClientKind, 18, fallbackName: s.ClientLabel(r.ClientKind)));
            who.Children.Add(TraceUi.Text(!string.IsNullOrEmpty(r.ClientKind) ? s.ClientLabel(r.ClientKind) : Loc.T("traces.topology.direct"), 12.5, ArcDesignTokenKind.Foreground));
            SessionsListView.Cell(grid, 1, who);
            SessionsListView.Cell(grid, 2, TraceUi.Text(r.RequestedModel ?? TraceFormat.Dash, 12.5, ArcDesignTokenKind.Foreground));
            var status = r.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? r.Status;
            SessionsListView.Cell(grid, 3, r.Status == "success" ? TraceUi.Text(status, 12.5, ArcDesignTokenKind.TextSecondary) : TraceUi.Text(status, 12.5, Palette.Red));
            var ms = TraceUi.Text(TraceFormat.Ms(r.TotalMs), 12.5, ArcDesignTokenKind.Foreground);
            ms.HorizontalAlignment = HorizontalAlignment.Right;
            SessionsListView.Cell(grid, 4, ms);
            var id = r.Id;
            rows.Children.Add(SessionsListView.Clickable(grid, new Thickness(14, 8), () => _vm.Open(id)));
        }
        var card = TraceUi.Card(rows);
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(TraceUi.Text(Loc.T("traces.request.pick"), 12, ArcDesignTokenKind.TextSecondary));
        column.Children.Add(card);
        Show(new ScrollViewer { Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }
}

/// <summary>
/// One request (request.tsx <c>RequestTraceView</c>): status / route / model / routing / tries / duration / first token / cost chips, the
/// session link and "open in log", the in-flight note, the legend, the canvas and the time bar; the request log's detail sheet.
/// </summary>
public sealed class RequestTraceView : TraceViewBase
{
    private readonly RequestTraceViewModel _vm;
    private readonly Panel _sheetHost = new();
    private TraceCanvas? _canvas;
    private RequestDetailSheet? _sheet;

    /// <summary>Builds the view.</summary>
    public RequestTraceView(RequestTraceViewModel vm) : base(vm)
    {
        _vm = vm;
        Root.Children.Add(_sheetHost);
    }

    /// <inheritdoc />
    protected override void DropCanvas() => _canvas = null;

    /// <inheritdoc />
    protected override void Draw()
    {
        var s = _vm.S;
        var p = Palette;
        if (_vm.IsLoading)
        {
            Show(Loading());
            return;
        }
        if (_vm.IsError)
        {
            Show(TraceUi.Error(_vm.Error));
            return;
        }
        if (_vm.Data is not { } data)
        {
            Show(Empty(Loc.T("traces.request.notFound"), TraceUi.Link(Loc.T("traces.request.pick"), _vm.Back)));
            return;
        }
        var r = data.Request;
        var brand = _vm.Brand;
        var chips = TraceUi.Wrap(8, 8);
        var statusColor = r.Status == "success" ? p.Green : data.InFlight ? p.Orange : p.Red;
        chips.Children.Add(TraceUi.Chip(TraceUi.Dot(statusColor), TraceUi.Bold(data.InFlight ? Loc.T("traces.request.pending") : r.Status),
            r.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        chips.Children.Add(TraceUi.Chip(
            !string.IsNullOrEmpty(r.ClientKind) ? BrandIcon.Client(r.ClientKind, 16, fallbackName: s.ClientLabel(r.ClientKind)) : null,
            (!string.IsNullOrEmpty(r.ClientKind) ? s.ClientLabel(r.ClientKind) : Loc.T("traces.topology.direct")) + " →",
            brand is not null ? BrandIcon.Provider(brand.Name, brand.Icon, 16) : null,
            TraceUi.Bold(brand?.Name ?? r.ProviderName ?? TraceFormat.Dash)));
        chips.Children.Add(TraceUi.Chip(r.RequestedModel ?? TraceFormat.Dash));
        if (_vm.Decision is { } d)
            chips.Children.Add(TraceUi.Chip(Loc.T("traces.node.routing") + " ·", TraceUi.Bold(d.Grade ?? TraceFormat.Dash),
                d.Source is not null ? "· " + TraceAnalysis.RouteSourceLabel(d.Source, s.T) : null));
        if (_vm.Attempts > 1) chips.Children.Add(TraceUi.Chip(Loc.T("traces.session.tries", ("count", _vm.Attempts))));
        chips.Children.Add(TraceUi.Chip(Loc.T("traces.kv.duration"), TraceUi.Bold(TraceFormat.Ms(r.TotalMs))));
        if (r.TtftMs is > 0) chips.Children.Add(TraceUi.Chip(Loc.T("traces.kv.firstToken"), TraceUi.Bold(TraceFormat.Ms(r.TtftMs))));
        chips.Children.Add(TraceUi.Chip(TraceUi.Bold(TraceFormat.Nanos(r.CostNanoUsd))));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!string.IsNullOrEmpty(r.SessionKey)) right.Children.Add(TraceUi.Link($"{Loc.T("traces.request.openSession")} →", _vm.OpenSession));
        var log = new ArcButton { Content = Loc.T("traces.openInLog"), Size = ControlSize.Small, Variant = ButtonVariant.Secondary, VerticalAlignment = VerticalAlignment.Center };
        log.Click += (_, _) => _vm.SetLogOpen(true);
        right.Children.Add(log);

        Control? inFlight = data.InFlight
            ? TraceUi.Card(TraceUi.Text(Loc.T("traces.request.inFlight"), 12.5, ArcDesignTokenKind.TextSecondary, wrap: true), new Thickness(12))
            : null;
        var legend = TraceUi.Wrap(16, 4);
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.request.legend.request"), LegendMark.Line, p.Label3));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.request.legend.response"), LegendMark.Line, p.Accent));
        legend.Children.Add(TraceUi.Legend(Loc.T("traces.request.legend.failed"), LegendMark.DashedLine, p.Red));

        Control? canvas = null;
        if (data.Spans.Count > 0)
        {
            if (_canvas is null)
            {
                _canvas = new TraceCanvas(null, null, s.ClientLabel);
                _canvas.SelectRequested += _vm.Select;
            }
            _canvas.SetGraph(_vm.Graph);
            _canvas.SetSelected(_vm.Selected);
            _canvas.SetInspector(_vm.Inspector is { } m ? TraceInspectorView.Create(m, p, s.Open, s.ClientLabel) : null);
            canvas = _canvas;
        }
        Control? time = _vm.TimeBar is { } tb ? TimeBarView.Create(tb, p) : null;
        SyncSheet();
        Rows(canvas is null ? -1 : 3, 12, TopLine(chips, right), inFlight, legend, canvas, time);
    }

    private void SyncSheet()
    {
        if (_vm.LogOpen && _sheet is null)
        {
            var s = _vm.S;
            var detail = new RequestDetailViewModel(s.Ctx, _vm.Id);
            var sheet = new RequestDetailSheet(detail, new RequestTableContext { Clock = s.Ctx.Clock, Provider = _ => null, TimeZone = s.TimeZone },
                id => s.Open(TraceRoutes.Request(id)));
            sheet.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_sheet, sheet)) return;
                sheet.Detach();
                detail.Dispose();
                _sheetHost.Children.Remove(sheet);
                _sheet = null;
                _vm.SetLogOpen(false);
            };
            _sheet = sheet;
            _sheetHost.Children.Add(sheet);
            sheet.IsOpen = true;
        }
    }
}

/// <summary>
/// A vertical stack that skips hidden children, puts <see cref="Gap"/> between the shown ones and gives <see cref="Fill"/> the height the
/// others leave (CSS <c>flex flex-col gap-n</c> with one <c>flex-1 min-h-0</c> child); without a fill child the rest stays empty below.
/// </summary>
internal sealed class FlexColumn : Panel
{
    public double Gap { get; set; } = 12;

    public Control? Fill { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        double used = Gap * Math.Max(0, shown.Count - 1), width = 0;
        foreach (var c in shown.Where(c => c != Fill))
        {
            c.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            used += c.DesiredSize.Height;
            width = Math.Max(width, c.DesiredSize.Width);
        }
        if (Fill is { IsVisible: true } fill)
        {
            var rest = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : Math.Max(0, availableSize.Height - used);
            fill.Measure(new Size(availableSize.Width, rest));
            used += double.IsInfinity(rest) ? fill.DesiredSize.Height : rest;
            width = Math.Max(width, fill.DesiredSize.Width);
        }
        return new Size(width, double.IsInfinity(availableSize.Height) ? used : Math.Min(used, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        var fixedHeight = shown.Where(c => c != Fill).Sum(c => c.DesiredSize.Height) + Gap * Math.Max(0, shown.Count - 1);
        var fillHeight = Math.Max(0, finalSize.Height - fixedHeight);
        double y = 0;
        foreach (var c in shown)
        {
            var h = c == Fill ? fillHeight : c.DesiredSize.Height;
            c.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h + Gap;
        }
        return finalSize;
    }
}
