using System.Diagnostics;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Traces;

// ARC-GAP-11: Arc's WorkflowCanvas (TKCode.Arc.Workflow) draws its own fixed card chrome (title / description / run state; NodeTemplates
// only replace the inside), one edge style for the whole graph, no edge labels, no per-edge colour / width / dash / arrow, no choice of
// the side an edge leaves by, no dots driven by outside events; its Minimap and ZoomControls only target a WorkflowCanvas. The trace views
// need all of those (web: React Flow), so this canvas, its minimap and its toolbar are local. Upstream: EdgeLabel, per-edge style and
// handles, NodePulse / external run dots on WorkflowCanvas.

/// <summary>
/// The trace canvas (canvas.tsx <c>TraceCanvas</c> on React Flow): positioned card / hub nodes with bezier, flow and step edges, a dotted
/// background, pan (drag the background, wheel / trackpad scroll), zoom (⌘ / Ctrl + wheel, the toolbar; 25 % – 250 %), node dragging
/// (remembered under <see cref="LayoutKey"/>), click to select, a fitted first view, a minimap, the toolbar (zoom out / % / zoom in, fit,
/// reset layout, 详情 switch), the hint pill and the inspector panel shown while the switch is on and a node is selected. In-flight requests
/// ride the topology lines as dots (live.tsx <c>LiveDot</c>).
/// </summary>
public sealed class TraceCanvas : UserControl
{
    private readonly Func<string?, string> _clientLabel;
    private readonly TraceLiveContext? _live;
    private readonly Grid _root = new();
    private readonly Backdrop _backdrop;
    private readonly EdgeLayer _edges;
    private readonly WorldPanel _world;
    private readonly Minimap _minimap;
    private readonly Border _inspectorPanel;
    private readonly ContentControl _inspectorHost = new();
    private readonly TextBlock _zoomText;
    private readonly ArcSwitch _detailsSwitch;
    private readonly Dictionary<string, Pt> _moved;
    private readonly Dictionary<string, TraceNodeView> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Box> _boxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EdgeShape> _shapes = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TracePalette? _palette;
    private TraceGraph _graph = TraceGraph.Empty;
    private TraceGraph? _deferred;
    private string? _selected;
    private Control? _inspector;
    private Viewport _view = Viewport.Identity;
    private bool _fitted;
    private string? _focusId;
    private IDisposable? _frame;
    private bool _quietSwitch;

    /// <summary>Builds an empty canvas.</summary>
    /// <param name="layoutKey">Remember dragged positions under this key (the topology); otherwise they last while the canvas is open.</param>
    /// <param name="live">The live feed (dots on flow lines, the live line of in-flight cards); null when the view has none.</param>
    /// <param name="clientLabel">A client kind's display name (icon fallbacks).</param>
    public TraceCanvas(string? layoutKey, TraceLiveContext? live, Func<string?, string> clientLabel)
    {
        LayoutKey = layoutKey;
        _live = live;
        _clientLabel = clientLabel;
        _moved = TraceLayouts.Read(layoutKey);
        MinHeight = 420;
        ClipToBounds = true;

        _backdrop = new Backdrop(this);
        _edges = new EdgeLayer(this);
        _world = new WorldPanel(this);
        _minimap = new Minimap(this) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(15) };
        _root.Children.Add(_backdrop);
        _root.Children.Add(_edges);
        _root.Children.Add(_world);

        // top-left: the hint pill (Panel position="top-left", .glass rounded-full px-2.5 py-0.5 text-[11.5px])
        _root.Children.Add(Glass(new Thickness(10, 2), 999, TraceUi.Text(Loc.T("traces.canvas.hint"), 11.5, ArcDesignTokenKind.TextSecondary), HorizontalAlignment.Left, VerticalAlignment.Top));

        // bottom-left: the toolbar
        _zoomText = TraceUi.Text("100%", 12, ArcDesignTokenKind.TextSecondary);
        _detailsSwitch = new ArcSwitch { IsChecked = TraceInspectorSwitch.On, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_detailsSwitch, Loc.T("traces.canvas.details"));
        _detailsSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_quietSwitch) TraceInspectorSwitch.Set(_detailsSwitch.IsChecked == true);
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bar.Children.Add(ToolButton(Loc.T("traces.canvas.zoomOut"), Icon(LucideIconKind.Minus, 16), false, ZoomOut));
        bar.Children.Add(ToolButton(Loc.T("traces.canvas.fit"), _zoomText, true, FitView));
        bar.Children.Add(ToolButton(Loc.T("traces.canvas.zoomIn"), Icon(LucideIconKind.Plus, 16), false, ZoomIn));
        bar.Children.Add(Separator());
        var fit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        fit.Children.Add(Icon(LucideIconKind.Maximize, 14));
        fit.Children.Add(TraceUi.Text(Loc.T("traces.canvas.fit"), 12, ArcDesignTokenKind.Foreground));
        bar.Children.Add(ToolButton(Loc.T("traces.canvas.fit"), fit, true, FitView));
        bar.Children.Add(ToolButton(Loc.T("traces.canvas.resetLayout"), TraceUi.Text(Loc.T("traces.canvas.resetLayout"), 12, ArcDesignTokenKind.Foreground), true, ResetLayout));
        bar.Children.Add(Separator());
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(_detailsSwitch);
        details.Children.Add(TraceUi.Text(Loc.T("traces.canvas.details"), 12, ArcDesignTokenKind.Foreground));
        bar.Children.Add(details);
        _root.Children.Add(Glass(new Thickness(4), 10, bar, HorizontalAlignment.Left, VerticalAlignment.Bottom));

        _root.Children.Add(_minimap);

        // top-right: the inspector panel (w-[300px], max-h-[calc(100%-170px)], surface-solid, 14 px corners, deep shadow, close button)
        var close = ToolButton(Loc.T("traces.canvas.close"), Icon(LucideIconKind.X, 16), false, () => TraceInspectorSwitch.Set(false));
        close.Width = close.Height = 24;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -4, -4, 0);
        var inner = new Panel();
        inner.Children.Add(_inspectorHost);
        inner.Children.Add(close);
        _inspectorPanel = new Border
        {
            Width = 300, Margin = new Thickness(12), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 12, Blur = 32, Color = Color.FromArgb(71, 0, 0, 0) }),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(14), Child = inner }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        _root.Children.Add(_inspectorPanel);

        var frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true, Child = _root };
        // canvas.tsx: bg-[var(--surface)], the translucent material (WebMaterial; the theme's Surface token is opaque). Its border is the palette's separator.
        Requests.WebMaterial.Apply(frame, edge: false);
        Content = frame;
        PaletteWatch.Attach(_root, Repaint);
        _frameBorder = frame;
        PointerWheelChanged += OnWheel;
    }

    private readonly Border _frameBorder;

    /// <summary>The key dragged positions are remembered under (null: only while the canvas is open).</summary>
    public string? LayoutKey { get; }

    /// <summary>The current pan / zoom.</summary>
    public Viewport View => _view;

    /// <summary>A node was clicked (or a step label naming one): the page decides what selecting means (a folded group opens instead).</summary>
    public event Action<string?>? SelectRequested;

    /// <summary>Shows a new graph; positions the user dragged nodes to are kept (a graph that arrives mid-drag waits for the drop).</summary>
    public void SetGraph(TraceGraph graph)
    {
        if (_drag is not null)
        {
            _deferred = graph;
            return;
        }
        _graph = graph;
        Rebuild();
    }

    /// <summary>The selected node (accent ring).</summary>
    public void SetSelected(string? id)
    {
        _selected = id;
        foreach (var (key, view) in _nodes) view.SetSelected(key == id);
        SyncInspector();
    }

    /// <summary>The details of the selected node, shown while the 详情 switch is on.</summary>
    public void SetInspector(Control? content)
    {
        _inspector = content;
        _inspectorHost.Content = content;
        SyncInspector();
    }

    /// <summary>Centres the view on a node at a readable zoom (the session's "next anomaly"; canvas.tsx <c>focus</c>).</summary>
    public void FocusNode(string id)
    {
        _focusId = id;
        ApplyFocus();
    }

    /// <summary><c>fitView({ padding: 0.18, maxZoom: 1, duration: 200 })</c> (the toolbar's fit buttons).</summary>
    public void FitView()
    {
        if (_boxes.Count == 0 || Bounds.Width <= 0) return;
        AnimateTo(Viewport.Fit(WorldBounds(), Bounds.Width, Bounds.Height), FitMs);
    }

    /// <summary>Zooms in one step around the centre (<c>zoomIn({ duration: 150 })</c>).</summary>
    public void ZoomIn() => AnimateTo(_view.ZoomAround(new Pt(Bounds.Width / 2, Bounds.Height / 2), _view.Zoom * Viewport.ZoomStep), ZoomMs);

    /// <summary>Zooms out one step around the centre (<c>zoomOut({ duration: 150 })</c>).</summary>
    public void ZoomOut() => AnimateTo(_view.ZoomAround(new Pt(Bounds.Width / 2, Bounds.Height / 2), _view.Zoom / Viewport.ZoomStep), ZoomMs);

    /// <summary>Forgets dragged positions and fits the view again (canvas.tsx <c>resetLayout</c>: new positions, then an animated fit).</summary>
    public void ResetLayout()
    {
        _moved.Clear();
        TraceLayouts.Write(LayoutKey, _moved);
        Rebuild();
        // after the arrange that measures the boxes at their home positions (web: requestAnimationFrame)
        Avalonia.Threading.Dispatcher.UIThread.Post(FitView, Avalonia.Threading.DispatcherPriority.Background);
    }

    // the durations canvas.tsx passes (ms)
    private const double ZoomMs = 150;
    private const double FitMs = 200;
    private const double FocusMs = 300;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TraceInspectorSwitch.Changed += OnSwitch;
        OnSwitch();
        if (_live is not null) _live.Store.ListChanged += OnLive;
        Repaint();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TraceInspectorSwitch.Changed -= OnSwitch;
        if (_live is not null) _live.Store.ListChanged -= OnLive;
        _frame?.Dispose();
        _frame = null;
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _inspectorPanel.MaxHeight = Math.Max(120, e.NewSize.Height - 170);
    }

    private void OnSwitch()
    {
        _quietSwitch = true;
        _detailsSwitch.IsChecked = TraceInspectorSwitch.On;
        _quietSwitch = false;
        SyncInspector();
    }

    private void OnLive()
    {
        _edges.InvalidateVisual();
        SyncAnimation();
    }

    private void SyncInspector() => _inspectorPanel.IsVisible = TraceInspectorSwitch.On && _selected is not null && _inspector is not null;

    private void Repaint()
    {
        if (VisualRoot is null) return;
        _palette = TracePalette.For(this);
        _frameBorder.BorderBrush = new SolidColorBrush(_palette.Separator);
        _inspectorPanel.Background = new SolidColorBrush(_palette.SurfaceSolid);
        _inspectorPanel.BorderBrush = new SolidColorBrush(_palette.Separator);
        foreach (var child in _root.Children)
            if (child is Border { Tag: "glass" } g)
            {
                g.Background = new SolidColorBrush(_palette.Glass);
                g.BorderBrush = new SolidColorBrush(_palette.GlassEdge);
            }
        Rebuild();
    }

    private void Rebuild()
    {
        if (_palette is null) return;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        _world.Children.Clear();
        _nodes.Clear();
        _labels.Clear();
        foreach (var edge in _graph.Edges)
        {
            var label = EdgeLabel(edge);
            if (label is null) continue;
            _labels[edge.Id] = label;
            _world.Children.Add(label);
        }
        foreach (var node in _graph.Nodes)
        {
            keep.Add(node.Id);
            var view = new TraceNodeView(node, _palette, node.Id == _selected, _live, _clientLabel);
            AttachDrag(view);
            _nodes[node.Id] = view;
            _world.Children.Add(view);
        }
        foreach (var gone in _moved.Keys.Where(k => !keep.Contains(k)).ToList())
            if (LayoutKey is null) _moved.Remove(gone);
        _world.InvalidateMeasure();
        _edges.InvalidateVisual();
        _minimap.InvalidateVisual();
        SyncAnimation();
    }

    private Pt PositionOf(TraceNode n) => _moved.TryGetValue(n.Id, out var p) ? p : new Pt(n.X, n.Y);

    private Box WorldBounds()
    {
        Box? all = null;
        foreach (var b in _boxes.Values) all = all is { } a ? a.Union(b) : b;
        return all ?? new Box(0, 0, 0, 0);
    }

    /// <summary>A view the user moved to (pan, wheel, minimap): ends any transition and applies at once.</summary>
    private void SetView(Viewport view)
    {
        _tween = null;
        ApplyView(view);
    }

    /// <summary>
    /// Moves to <paramref name="target"/> over <paramref name="ms"/> (React Flow's <c>duration</c>: d3-zoom's smooth zoom, see
    /// <see cref="ViewportTween"/>), one step per rendered frame. Jumps when motion is off (visual tests) or the canvas is not shown.
    /// </summary>
    private void AnimateTo(Viewport target, double ms)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || !TraceUi.Animates(this) || Bounds.Width <= 0 || target == _view)
        {
            SetView(target);
            return;
        }
        _tween = new ViewportTween(_view, target, Bounds.Width, Bounds.Height);
        _tweenMs = ms;
        _tweenClock.Restart();
        var tween = _tween;
        void Step(TimeSpan _)
        {
            if (!ReferenceEquals(tween, _tween)) return;
            var f = _tweenClock.Elapsed.TotalMilliseconds / _tweenMs;
            ApplyView(tween.At(f));
            if (f >= 1)
            {
                _tween = null;
                return;
            }
            (TopLevel.GetTopLevel(this) ?? top).RequestAnimationFrame(Step);
        }
        top.RequestAnimationFrame(Step);
    }

    private ViewportTween? _tween;
    private double _tweenMs;
    private readonly Stopwatch _tweenClock = new();

    private void ApplyView(Viewport view)
    {
        _view = view;
        _zoomText.Text = $"{Math.Round(view.Zoom * 100)}%";
        _world.InvalidateArrange();
        _edges.InvalidateVisual();
        _backdrop.InvalidateVisual();
        _minimap.InvalidateVisual();
    }

    private void ApplyFocus()
    {
        if (_focusId is null || !_boxes.TryGetValue(_focusId, out var b) || Bounds.Width <= 0) return;
        // setCenter(…, { zoom: max(zoom, 0.9), duration: 300 })
        AnimateTo(Viewport.Center(new Pt(b.X + b.Width / 2, b.Y + b.Height / 2), Math.Max(_view.Zoom, 0.9), Bounds.Width, Bounds.Height), FocusMs);
        _focusId = null;
    }

    /// <summary>Runs the frame clock while something moves (dots, an animated edge, a breathing card) and motion is on.</summary>
    private void SyncAnimation()
    {
        var needed = VisualRoot is not null && TraceUi.Animates(this)
            && (_graph.Edges.Any(e => e.Animated || e.Flow is { Requests.Count: > 0 }) || _nodes.Values.Any(n => n.Animated));
        if (needed && _frame is null)
            _frame = TraceTicker.Frame(() =>
            {
                var t = _clock.Elapsed.TotalSeconds;
                foreach (var n in _nodes.Values)
                    if (n.Animated) n.Animate(t);
                _edges.InvalidateVisual();
            });
        else if (!needed && _frame is not null)
        {
            _frame.Dispose();
            _frame = null;
        }
    }

    private double AnimationSeconds => _frame is null ? 0 : _clock.Elapsed.TotalSeconds;

    // ------------------------------------------------------------------ edge labels

    private Control? EdgeLabel(TraceEdge edge)
    {
        var p = _palette!;
        if (edge.Type == EdgeType.Step && edge.Step is { Label: { Length: > 0 } } step)
        {
            // .trace-step-label: tools on top, the gap under them; a long tool list is cut off, the time never is
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var hasTools = !string.IsNullOrEmpty(step.Tools);
            if (hasTools)
            {
                var tools = TraceUi.Text(step.Tools, 11, p.Label);
                tools.HorizontalAlignment = HorizontalAlignment.Center;
                tools.LineHeight = 14;
                col.Children.Add(tools);
            }
            if (!string.IsNullOrEmpty(step.Gap))
            {
                var gap = TraceUi.Text(step.Gap, 11, p.Label2);
                gap.HorizontalAlignment = HorizontalAlignment.Center;
                gap.LineHeight = hasTools ? 14 : 18;
                col.Children.Add(gap);
            }
            var border = new Border
            {
                MaxWidth = 120, Padding = hasTools ? new Thickness(9, 2) : new Thickness(8, 0), CornerRadius = new CornerRadius(hasTools ? 9 : 999),
                Background = new SolidColorBrush(p.SurfaceSolid), Child = col,
            };
            // 0.5 px dashed edge at label 30 % (a Border cannot dash)
            var holder = new Panel { Tag = edge.Id, Background = Brushes.Transparent };
            holder.Children.Add(border);
            holder.Children.Add(new RoundedDashedOutline(TracePalette.WithAlpha(p.Label, 0.3), hasTools ? 9 : -1) { IsHitTestVisible = false });
            ToolTip.SetTip(holder, step.Label);
            if (step.SelectId is { } select)
            {
                holder.Cursor = new Cursor(StandardCursorType.Hand);
                holder.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(holder).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    SelectRequested?.Invoke(select);
                };
            }
            return holder;
        }
        if (!string.IsNullOrEmpty(edge.Label))
        {
            // .trace-edge-label: a pill on surface-solid with a 0.5 px separator edge, 11.5 px label-2 text, 18 px line
            var text = TraceUi.Text(edge.Label, 11.5, p.Label2);
            text.LineHeight = 18;
            return new Border
            {
                Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0.5),
                Background = new SolidColorBrush(p.SurfaceSolid), BorderBrush = new SolidColorBrush(p.Separator), Child = text, IsHitTestVisible = false,
                Tag = edge.Id,
            };
        }
        return null;
    }

    // ------------------------------------------------------------------ dragging, selecting, panning

    private sealed record DragState(TraceNodeView View, Point Start, Pt Origin, IPointer Pointer)
    {
        public bool Moved { get; set; }
    }

    private DragState? _drag;

    private void AttachDrag(TraceNodeView view)
    {
        view.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _drag = new DragState(view, point.Position, PositionOf(view.Node), e.Pointer);
            e.Pointer.Capture(view);
        };
        view.PointerMoved += (_, e) =>
        {
            if (_drag is not { } d || d.View != view) return;
            var at = e.GetPosition(this);
            var dx = at.X - d.Start.X;
            var dy = at.Y - d.Start.Y;
            if (!d.Moved && Math.Abs(dx) + Math.Abs(dy) < 2) return;
            d.Moved = true;
            _moved[view.Node.Id] = new Pt(d.Origin.X + dx / _view.Zoom, d.Origin.Y + dy / _view.Zoom);
            _world.InvalidateArrange();
            _edges.InvalidateVisual();
            _minimap.InvalidateVisual();
        };
        view.PointerReleased += (_, e) =>
        {
            if (_drag is not { } d || d.View != view) return;
            _drag = null;
            e.Pointer.Capture(null);
            if (d.Moved) TraceLayouts.Write(LayoutKey, _moved);
            else SelectRequested?.Invoke(view.Node.Id);
            if (_deferred is { } next)
            {
                _deferred = null;
                SetGraph(next);
            }
        };
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // trackpad / wheel pans; ⌘ or Ctrl + wheel (and pinch) zooms (canvas.tsx: panOnScroll, zoomActivationKeyCode Meta / Control)
        e.Handled = true;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            var at = e.GetPosition(this);
            SetView(_view.ZoomAround(new Pt(at.X, at.Y), _view.Zoom * Math.Pow(2, e.Delta.Y * 0.25)));
            return;
        }
        SetView(_view.Pan(e.Delta.X * 25, e.Delta.Y * 25));
    }

    // ------------------------------------------------------------------ chrome helpers

    private static LucideIcon Icon(LucideIconKind kind, double size)
    {
        // colored by the inherited foreground, like Arc's own glyphs
        return new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Border Glass(Thickness padding, double radius, Control child, HorizontalAlignment h, VerticalAlignment v) => new()
    {
        Tag = "glass", Padding = padding, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(0.5), Child = child,
        HorizontalAlignment = h, VerticalAlignment = v, Margin = new Thickness(15),
        BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 16, Color = Color.FromArgb(20, 0, 0, 0) }),
    };

    private static Border Separator()
    {
        var line = new Border { Width = 1, Height = 16, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    /// <summary>A toolbar button (canvas.tsx <c>ToolbarButton</c>): 28 px, 7 px corners, muted hover fill.</summary>
    private static Border ToolButton(string label, Control content, bool wide, Action onClick)
    {
        var button = new Border
        {
            Height = 28, MinWidth = 28, CornerRadius = new CornerRadius(7), Padding = wide ? new Thickness(8, 0) : default, Background = Brushes.Transparent,
            Child = content, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true,
        };
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.PointerEntered += (_, _) => button.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        button.PointerExited += (_, _) => { button.ClearValue(Border.BackgroundProperty); button.Background = Brushes.Transparent; };
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return button;
    }

    // ------------------------------------------------------------------ layers

    /// <summary>
    /// The nodes and edge labels, arranged at their screen positions and scaled by the zoom (React Flow's viewport transform; arranging
    /// in screen space keeps every child inside the panel for hit testing). Arrange also measures the node boxes the edges attach to.
    /// </summary>
    private sealed class WorldPanel(TraceCanvas canvas) : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children) child.Measure(Size.Infinity);
            return default;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var c = canvas;
            c._boxes.Clear();
            foreach (var node in c._graph.Nodes)
            {
                if (!c._nodes.TryGetValue(node.Id, out var view)) continue;
                var at = c.PositionOf(node);
                var size = view.DesiredSize;
                c._boxes[node.Id] = new Box(at.X, at.Y, node.Width ?? size.Width, node.Height ?? size.Height);
            }
            if (!c._fitted && c._boxes.Count > 0 && finalSize.Width > 0)
            {
                c._fitted = true;
                c._view = Viewport.Fit(c.WorldBounds(), finalSize.Width, finalSize.Height);
                c._zoomText.Text = $"{Math.Round(c._view.Zoom * 100)}%";
                c._backdrop.InvalidateVisual();
                c._minimap.InvalidateVisual();
            }
            if (c._focusId is not null) Avalonia.Threading.Dispatcher.UIThread.Post(c.ApplyFocus);
            var view0 = c._view;
            c._shapes.Clear();
            foreach (var edge in c._graph.Edges)
                if (c.ShapeOf(edge) is { } shape) c._shapes[edge.Id] = shape;
            foreach (var node in c._graph.Nodes)
            {
                if (!c._nodes.TryGetValue(node.Id, out var view) || !c._boxes.TryGetValue(node.Id, out var box)) continue;
                var s = view0.ToScreen(new Pt(box.X, box.Y));
                view.RenderTransformOrigin = RelativePoint.TopLeft;
                view.RenderTransform = new ScaleTransform(view0.Zoom, view0.Zoom);
                view.Arrange(new Rect(s.X, s.Y, box.Width, box.Height));
            }
            foreach (var (id, label) in c._labels)
            {
                if (!c._shapes.TryGetValue(id, out var shape))
                {
                    label.Arrange(default);
                    continue;
                }
                var size = label.DesiredSize;
                var s = view0.ToScreen(shape.Label);
                label.RenderTransformOrigin = RelativePoint.Center;
                label.RenderTransform = new ScaleTransform(view0.Zoom, view0.Zoom);
                label.Arrange(new Rect(s.X - size.Width / 2, s.Y - size.Height / 2, size.Width, size.Height));
            }
            c._edges.InvalidateVisual();
            return finalSize;
        }
    }

    private EdgeShape? ShapeOf(TraceEdge edge)
    {
        if (!_boxes.TryGetValue(edge.Source, out var s) || !_boxes.TryGetValue(edge.Target, out var t)) return null;
        var a = EdgeGeometry.HandlePoint(s, edge.SourceHandle);
        var b = EdgeGeometry.HandlePoint(t, edge.TargetHandle);
        return edge.Type == EdgeType.Step ? EdgeGeometry.Step(a, edge.SourceHandle, b, edge.TargetHandle, edge.Step?.Wrap)
            : EdgeGeometry.Bezier(a, edge.SourceHandle, b, edge.TargetHandle);
    }

    /// <summary>The dotted background (React Flow <c>Background variant="dots" gap=20 size=1</c>, label at 16 %); a pan by dragging it.</summary>
    private sealed class Backdrop : Control
    {
        private readonly TraceCanvas _c;
        private Point? _panFrom;
        private Viewport _panStart;

        public Backdrop(TraceCanvas canvas)
        {
            _c = canvas;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (_c._palette is not { } p) return;
            var v = _c._view;
            var gap = 20 * v.Zoom;
            var r = 0.5 * v.Zoom;
            if (gap < 3) return;
            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, gap, gap)) });
            tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(TracePalette.WithAlpha(p.Label, 0.16)), Geometry = new EllipseGeometry(new Rect(gap / 2 - r, gap / 2 - r, 2 * r, 2 * r)) });
            var ox = ((v.X % gap) + gap) % gap - gap / 2;
            var oy = ((v.Y % gap) + gap) % gap - gap / 2;
            var brush = new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                DestinationRect = new RelativeRect(ox, oy, gap, gap, RelativeUnit.Absolute),
            };
            context.FillRectangle(brush, new Rect(Bounds.Size));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _panFrom = e.GetPosition(this);
            _panStart = _c._view;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_panFrom is not { } from) return;
            var at = e.GetPosition(this);
            _c.SetView(_panStart.Pan(at.X - from.X, at.Y - from.Y));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _panFrom = null;
            e.Pointer.Capture(null);
        }
    }

    /// <summary>
    /// The edges and the live dots on them: bezier / step paths with React Flow's closed arrow marker, dashed failed / inferred legs, the
    /// moving dash of an animated (in-flight) edge, and per in-flight request a dot going out or coming back, or a burst where it ended.
    /// Hit-tests only the dots (click opens the request, hover shows what it waits on) and clickable step edges.
    /// </summary>
    private sealed class EdgeLayer : Control, ICustomHitTest
    {
        private readonly TraceCanvas _c;
        private readonly List<(Point At, double R, string Id, string Title)> _dots = [];
        private readonly List<(IReadOnlyList<Point> Line, string Select)> _clickable = [];
        private readonly Dictionary<string, double> _bursts = new(StringComparer.Ordinal);

        public EdgeLayer(TraceCanvas canvas)
        {
            _c = canvas;
            ClipToBounds = true;
        }

        public bool HitTest(Point point) => DotAt(point) is not null || EdgeAt(point) is not null;

        private (Point At, double R, string Id, string Title)? DotAt(Point p)
        {
            foreach (var d in _dots)
                if ((d.At - p).X * (d.At - p).X + (d.At - p).Y * (d.At - p).Y <= (d.R + 3) * (d.R + 3)) return d;
            return null;
        }

        private string? EdgeAt(Point p)
        {
            foreach (var (line, select) in _clickable)
                for (var i = 1; i < line.Count; i++)
                    if (DistanceToSegment(p, line[i - 1], line[i]) <= 8) return select;
            return null;
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            var ab = b - a;
            var len = ab.X * ab.X + ab.Y * ab.Y;
            var t = len <= 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len, 0, 1);
            var q = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
            return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var at = e.GetPosition(this);
            if (DotAt(at) is { } dot)
            {
                e.Handled = true;
                _c._live?.OpenRequest(dot.Id);
            }
            else if (EdgeAt(at) is { } select)
            {
                e.Handled = true;
                _c.SelectRequested?.Invoke(select);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var dot = DotAt(e.GetPosition(this));
            Cursor = dot is not null || EdgeAt(e.GetPosition(this)) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            ToolTip.SetTip(this, dot?.Title);
            ToolTip.SetIsOpen(this, dot is not null);
        }

        public override void Render(DrawingContext context)
        {
            _dots.Clear();
            _clickable.Clear();
            if (_c._palette is not { } p) return;
            var v = _c._view;
            var seconds = _c.AnimationSeconds;
            var seenBursts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in _c._graph.Edges)
            {
                if (!_c._shapes.TryGetValue(edge.Id, out var shape)) continue;
                var color = p.Edge(edge.Tone);
                var width = edge.StrokeWidth * v.Zoom;
                var screen = shape.Flatten().Select(pt => ToPoint(v.ToScreen(pt))).ToList();
                var geometry = PathGeometryOf(shape, v);
                DashStyle? dash = null;
                if (edge.Animated) dash = new DashStyle([5 / edge.StrokeWidth, 5 / edge.StrokeWidth], (10 * (1 - (seconds / 0.5 % 1))) / edge.StrokeWidth);
                else if (edge.Dashed) dash = new DashStyle([5 / edge.StrokeWidth, 4 / edge.StrokeWidth], 0);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(color), width, dash, PenLineCap.Flat, PenLineJoin.Round), geometry);
                if (edge.Arrow) DrawArrow(context, shape, color, edge.StrokeWidth, v);
                if (edge.Step?.SelectId is { } select) _clickable.Add((screen, select));
                if (edge.Flow is { } flow && _c._live is { } live) DrawDots(context, edge, flow, shape, live, p, v, seconds, seenBursts);
            }
            foreach (var gone in _bursts.Keys.Where(k => !seenBursts.Contains(k)).ToList()) _bursts.Remove(gone);
        }

        private void DrawDots(DrawingContext context, TraceEdge edge, FlowData flow, EdgeShape shape, TraceLiveContext live, TracePalette p,
            Viewport v, double seconds, HashSet<string> seenBursts)
        {
            var now = live.NowMs();
            foreach (var id in flow.Requests)
            {
                if (live.Get(id) is not { } r) continue;
                var (mode, slow, dur) = TopologyPageLogic.Dot(r, flow.Role, now);
                if (mode == TopologyPageLogic.DotMode.Hidden) continue;
                var title = TopologyPageLogic.DotTitle(r, TraceTexts.FromLoc, now);
                var surface = new Pen(new SolidColorBrush(p.SurfaceSolid), 1.5 * v.Zoom);
                if (mode == TopologyPageLogic.DotMode.Burst)
                {
                    // a burst at the agent (or at the provider when the upstream turned it down): r 5, scale 1 → 3.2, fading out over 0.9 s
                    var key = edge.Id + "|" + id;
                    seenBursts.Add(key);
                    if (!_bursts.TryGetValue(key, out var start)) _bursts[key] = start = seconds;
                    var k = _c._frame is null ? 0 : Math.Clamp((seconds - start) / 0.9, 0, 1);
                    if (k >= 1) continue;
                    var end = ToPoint(v.ToScreen(flow.Role == "client" ? shape.Start : shape.End));
                    var radius = 5 * (1 + 2.2 * k) * v.Zoom;
                    var failed = TraceAnalysis.LivePhaseOf(r) == LivePhase.Failed;
                    var fill = TracePalette.WithAlpha(failed ? p.Red : p.Green, 0.95 * (1 - k));
                    context.DrawEllipse(new SolidColorBrush(fill), null, end, radius, radius);
                    _dots.Add((end, radius, id, title));
                    continue;
                }
                var back = mode == TopologyPageLogic.DotMode.Back;
                var frac = ((seconds + TopologyPageLogic.DotOffset(id) * dur) / dur) % 1;
                var at = ToPoint(v.ToScreen(shape.PointAt(back ? 1 - frac : frac)));
                var color = slow ? p.Orange : back ? p.Green : p.Accent;
                var rr = 4.5 * v.Zoom;
                context.DrawEllipse(new SolidColorBrush(color), surface, at, rr, rr);
                _dots.Add((at, rr, id, title));
            }
        }

        /// <summary>React Flow's <c>arrowclosed</c> marker: 16 × 16 in stroke-width units over a 20-unit box, tip at the path's end.</summary>
        private static void DrawArrow(DrawingContext context, EdgeShape shape, Color color, double strokeWidth, Viewport v)
        {
            var unit = 16 * strokeWidth / 20 * v.Zoom;
            var dir = shape.EndDirection();
            var tip = ToPoint(v.ToScreen(shape.End));
            var nx = -dir.Y;
            var ny = dir.X;
            var back = new Point(tip.X - dir.X * 5 * unit, tip.Y - dir.Y * 5 * unit);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(tip, true);
                ctx.LineTo(new Point(back.X + nx * 4 * unit, back.Y + ny * 4 * unit));
                ctx.LineTo(new Point(back.X - nx * 4 * unit, back.Y - ny * 4 * unit));
                ctx.EndFigure(true);
            }
            var brush = new SolidColorBrush(color);
            context.DrawGeometry(brush, new Pen(brush, unit, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        }

        private static StreamGeometry PathGeometryOf(EdgeShape shape, Viewport v)
        {
            var g = new StreamGeometry();
            using var ctx = g.Open();
            ctx.BeginFigure(ToPoint(v.ToScreen(shape.Start)), false);
            foreach (var s in shape.Segments)
            {
                switch (s.Kind)
                {
                    case SegmentKind.Line: ctx.LineTo(ToPoint(v.ToScreen(s.P3))); break;
                    case SegmentKind.Quad: ctx.QuadraticBezierTo(ToPoint(v.ToScreen(s.P1)), ToPoint(v.ToScreen(s.P3))); break;
                    default: ctx.CubicBezierTo(ToPoint(v.ToScreen(s.P1)), ToPoint(v.ToScreen(s.P2)), ToPoint(v.ToScreen(s.P3))); break;
                }
            }
            ctx.EndFigure(false);
            return g;
        }
    }

    private static Point ToPoint(Pt p) => new(p.X, p.Y);

    /// <summary>
    /// The minimap (React Flow <c>MiniMap pannable zoomable</c>, 200 × 150 at the bottom right): the nodes as rounded blocks, the area
    /// outside the view masked, the view outlined in the accent; drag it to pan, wheel to zoom.
    /// </summary>
    private sealed class Minimap : Control
    {
        private const double W = 200;
        private const double H = 150;
        private readonly TraceCanvas _c;
        private Point? _from;
        private Viewport _start;
        private double _scale = 1;

        public Minimap(TraceCanvas canvas)
        {
            _c = canvas;
            Width = W;
            Height = H;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            Avalonia.Automation.AutomationProperties.SetName(this, Loc.T("traces.canvas.minimap"));
        }

        public override void Render(DrawingContext context)
        {
            if (_c._palette is not { } p) return;
            var rect = new Rect(0, 0, W, H);
            var clip = context.PushClip(new RoundedRect(rect, 10));
            context.FillRectangle(new SolidColorBrush(p.SurfaceSolid), rect);
            var visible = _c._view.Visible(_c.Bounds.Width, _c.Bounds.Height);
            var bounds = _c._boxes.Count == 0 ? visible : _c.WorldBounds().Union(visible);
            var viewScale = Math.Max(bounds.Width / W, bounds.Height / H);
            if (viewScale <= 0 || double.IsNaN(viewScale)) viewScale = 1;
            var vw = viewScale * W;
            var vh = viewScale * H;
            var offset = 5 * viewScale;
            var x0 = bounds.X - (vw - bounds.Width) / 2 - offset;
            var y0 = bounds.Y - (vh - bounds.Height) / 2 - offset;
            var k = W / (vw + 2 * offset);
            _scale = 1 / k;
            Rect Map(Box b) => new((b.X - x0) * k, (b.Y - y0) * k, b.Width * k, b.Height * k);
            var nodeBrush = new SolidColorBrush(TracePalette.WithAlpha(p.Label, 0.28));
            foreach (var b in _c._boxes.Values)
            {
                var r = Map(b);
                context.DrawRectangle(nodeBrush, null, r, 10 * k, 10 * k);
            }
            var view = Map(visible);
            var canvasColor = p.Dark ? Color.FromRgb(0x16, 0x16, 0x18) : Color.FromRgb(0xee, 0xf0, 0xf5);
            var mask = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(rect), new RectangleGeometry(view));
            context.DrawGeometry(new SolidColorBrush(TracePalette.WithAlpha(canvasColor, 0.55)), null, mask);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(p.Accent), 2), view);
            clip.Dispose();
            context.DrawRectangle(null, new Pen(new SolidColorBrush(p.Separator), 0.5), rect.Deflate(0.25), 10, 10);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _from = e.GetPosition(this);
            _start = _c._view;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_from is not { } from) return;
            var at = e.GetPosition(this);
            // dragging the view rectangle moves the view the same way over the world
            _c.SetView(_start.Pan(-(at.X - from.X) * _scale * _start.Zoom, -(at.Y - from.Y) * _scale * _start.Zoom));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _from = null;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            e.Handled = true;
            var c = _c;
            c.SetView(c._view.ZoomAround(new Pt(c.Bounds.Width / 2, c.Bounds.Height / 2), c._view.Zoom * Math.Pow(2, e.Delta.Y * 0.25)));
        }
    }
}

/// <summary>A 0.5 px dashed rounded outline (a step label; a Border cannot dash); a negative radius draws a pill.</summary>
internal sealed class RoundedDashedOutline(Color color, double radius) : Control
{
    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size).Deflate(0.25);
        var rad = radius < 0 ? r.Height / 2 : radius;
        context.DrawRectangle(null, new Pen(new SolidColorBrush(color), 0.5, new DashStyle([5, 4], 0)), r, rad, rad);
    }
}
