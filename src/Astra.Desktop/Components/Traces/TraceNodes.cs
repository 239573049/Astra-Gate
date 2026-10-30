using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TKCode.Arc.DesignTokens;
using TraceTone = Astra.Desktop.Core.Traces.Tone;

namespace Astra.Desktop.Components.Traces;

/// <summary>
/// What the canvases need of the live request feed: the in-flight rows (cards that read what their call is doing, dots on the topology
/// lines), the clock and how to open a request's trace (live.tsx <c>useLiveRequest</c> / <c>useNow</c> / the dot's <c>navigate</c>).
/// </summary>
/// <param name="Store">The page's live rows.</param>
/// <param name="NowMs">The page clock in Unix ms.</param>
/// <param name="OpenRequest">Opens a request's trace.</param>
public sealed record TraceLiveContext(LiveRequestStore Store, Func<double> NowMs, Action<string> OpenRequest)
{
    /// <summary>A live row by id.</summary>
    public RequestSummaryDto? Get(string id) => Store.Get(id);
}

/// <summary>
/// One node of a trace canvas (nodes.tsx <c>CardNodeView</c> / <c>HubNodeView</c>, styled as flow.css <c>.trace-node</c> /
/// <c>.trace-hub</c>): brand icon + title + secondary lines, tone borders, the dashed tool pill, the folded-group stack, the failed /
/// retried / slow flag, the live badge, the breathing in-flight card and the busy hub. Sizes are the model's (the web's pixels).
/// </summary>
public sealed class TraceNodeView : Panel
{
    // A provider card's geometry is fixed so its lines can end on a row before the card is measured (flow.css .trace-provider / .trace-row):
    // 1 px border, 8 px padding, a 34 px header, 8 px gap, then 30 px rows 4 px apart.
    private const double ProviderTop = 1 + 8;
    private const double ProviderHeader = 34;
    private const double ProviderGap = 8;
    private const double RowHeight = 30;
    private const double RowGap = 4;

    /// <summary>Where row <paramref name="index"/>'s line ends on a provider card: its vertical centre, from the card's top (the left border is the x).</summary>
    public static double RowCenterY(int index) => ProviderTop + ProviderHeader + ProviderGap + index * (RowHeight + RowGap) + RowHeight / 2;

    private static Bitmap? _logo;
    private readonly Border _card;
    private readonly TracePalette _palette;
    private bool _selected;
    private bool _hover;

    /// <summary>The node drawn.</summary>
    public TraceNode Node { get; }

    /// <summary>Builds the node.</summary>
    public TraceNodeView(TraceNode node, TracePalette palette, bool selected, TraceLiveContext? live, Func<string?, string> clientLabel)
    {
        Node = node;
        _palette = palette;
        _selected = selected;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        if (node.Kind == NodeKind.Hub)
        {
            Width = 96;
            Height = 96;
            // the shadow sits on an unclipped frame; the picture is clipped to the corners inside it
            _card = new Border
            {
                Width = 96, Height = 96, CornerRadius = new CornerRadius(24), Background = Brushes.White,
                Child = new Border
                {
                    CornerRadius = new CornerRadius(24), ClipToBounds = true,
                    Child = new Image { Source = Logo(), Stretch = Stretch.UniformToFill, IsHitTestVisible = false },
                },
            };
            Children.Add(_card);
            // the switch a request just made, or where one was just sent (nodes.tsx .trace-hub-note), above the logo
            if (node.Note is { } note)
            {
                var warn = note.Tone == "warn";
                var color = warn ? palette.Orange : palette.Accent;
                var label = TraceUi.Text(note.Text, 11.5, color, FontWeight.Medium);
                label.TextTrimming = TextTrimming.None;
                label.Measure(Size.Infinity);
                // wider than the 96 px hub: an explicit width (a negative margin alone would arrange it at 0 − margins), centred over the logo
                var width = Math.Ceiling(label.DesiredSize.Width) + 24;
                Children.Add(new Border
                {
                    Width = width, Height = 22, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(warn ? TracePalette.Mix(palette.Orange, 0.1, palette.SurfaceSolid) : palette.SurfaceSolid),
                    BorderBrush = new SolidColorBrush(TracePalette.Mix(color, warn ? 0.65 : 0.55, Colors.Transparent)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness((96 - width) / 2, -30, (96 - width) / 2, 0),
                    BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 14, Color = Color.FromArgb(41, 0, 0, 0) }),
                    Child = label, IsHitTestVisible = false,
                });
            }
            Restyle(0);
            return;
        }

        var d = node.Card ?? new CardData { Title = node.Id };
        if (node.Width is { } w) Width = w;
        if (node.Height is { } h) Height = h;
        _card = new Border
        {
            CornerRadius = new CornerRadius(d.Pill ? 999 : 12),
            BorderThickness = new Thickness(d.Pill ? 0 : 1),
            Padding = d.Pill ? new Thickness(14, 6) : new Thickness(10, 8),
            Opacity = d.Tone == TraceTone.Idle ? 0.75 : 1,
        };
        var body = new Panel();
        body.Children.Add(node.Kind == NodeKind.Provider ? ProviderContent(d, node.Rows ?? []) : Content(d, live, clientLabel));
        _card.Child = body;
        if (d.Dim) Opacity = 0.32;
        Children.Add(_card);
        // a tool run by the agent: a dashed pill (a Border cannot dash, so the edge is drawn over it)
        if (d.Pill) Children.Add(new DashedOutline(palette.Separator, 3) { IsHitTestVisible = false });
        if (d.Flag is { } flag)
        {
            var bg = flag.Tone == TraceTone.Error ? palette.Red : TracePalette.Mix(palette.Orange, 0.85, Colors.Black);
            Children.Add(new Border
            {
                Height = 18, Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(bg),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, -9, 0, 0),
                Child = TraceUi.Text(flag.Text, 10.5, Colors.White, FontWeight.SemiBold), IsHitTestVisible = false,
            });
        }
        if (d.Live is > 0 and var count)
        {
            var text = Loc.T("traces.live.badge", ("count", count));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(TraceUi.LivePulse());
            row.Children.Add(TraceUi.Text(text, 10.5, palette.Accent, FontWeight.SemiBold));
            Children.Add(new Border
            {
                Height = 18, Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(palette.SurfaceSolid), BorderBrush = new SolidColorBrush(TracePalette.Mix(palette.Accent, 0.45, palette.Separator)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -9, 10, 0),
                Child = row, IsHitTestVisible = false,
            });
        }
        PointerEntered += (_, _) => { _hover = true; Restyle(0); };
        PointerExited += (_, _) => { _hover = false; Restyle(0); };
        Restyle(0);
    }

    /// <summary>Whether the node animates (an in-flight card breathes, a busy hub too).</summary>
    public bool Animated => Node.Kind == NodeKind.Hub ? Node.Busy : Node.Card?.Pending is not null;

    /// <summary>Selects or deselects the node (the accent ring).</summary>
    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        Restyle(0);
    }

    /// <summary>One animation frame: <paramref name="seconds"/> drives the breathing ring (1.6 s ease-in-out, flow.css <c>trace-pending</c> / <c>trace-breathe</c>).</summary>
    public void Animate(double seconds) => Restyle(Animated ? (1 - Math.Cos(seconds / 1.6 * 2 * Math.PI)) / 2 : 0);

    private void Restyle(double breathe)
    {
        var p = _palette;
        var shadows = new List<BoxShadow>();
        if (Node.Kind == NodeKind.Hub)
        {
            shadows.Add(new BoxShadow { Spread = _selected ? 2 : 0.5, Color = _selected ? p.Accent : p.Separator });
            if (Node.Busy && breathe > 0) shadows.Add(new BoxShadow { Spread = 6 * breathe, Color = TracePalette.WithAlpha(p.Accent, 0.3) });
            shadows.Add(new BoxShadow { OffsetY = 8, Blur = 24, Color = Color.FromArgb(_selected ? (byte)56 : (byte)46, 0, 0, 0) });
            _card.BoxShadow = new BoxShadows(shadows[0], shadows.Skip(1).ToArray());
            return;
        }
        var d = Node.Card!;
        var border = p.Separator;
        if (d.Tone == TraceTone.Warn) border = TracePalette.Mix(p.Orange, 0.6, p.Separator);
        else if (d.Tone == TraceTone.Error) border = TracePalette.Mix(p.Red, 0.6, p.Separator);
        else if (d.Tone == TraceTone.Ok) border = TracePalette.Mix(p.Green, 0.45, p.Separator);
        if (d.Pending is not null) border = TracePalette.Mix(p.Accent, 0.6, p.Separator);
        if (_hover) border = TracePalette.Mix(p.Accent, 0.5, p.Separator);
        _card.BorderBrush = new SolidColorBrush(border);
        _card.Background = new SolidColorBrush(d.Pill ? p.SurfaceMuted : d.Tone == TraceTone.Sub ? TracePalette.Mix(p.Accent2, 0.12, p.SurfaceSolid) : p.SurfaceSolid);
        if (d.Group)
        {
            // a folded run of steps: a little stack (flow.css .trace-node.is-group)
            shadows.Add(new BoxShadow { OffsetX = 4, OffsetY = 4, Spread = -1, Color = p.SurfaceSolid });
            shadows.Add(new BoxShadow { OffsetX = 4, OffsetY = 4, Spread = 0, Color = p.Separator });
            shadows.Add(new BoxShadow { OffsetX = 8, OffsetY = 8, Spread = -2, Color = p.SurfaceSolid });
            shadows.Add(new BoxShadow { OffsetX = 8, OffsetY = 8, Spread = -1, Color = p.Separator });
        }
        else if (_selected)
        {
            shadows.Add(new BoxShadow { Spread = 2, Color = p.Accent });
            shadows.Add(new BoxShadow { OffsetY = 8, Blur = 22, Color = Color.FromArgb(56, 0, 0, 0) });
        }
        else
        {
            if (d.Pending is not null && breathe > 0) shadows.Add(new BoxShadow { Spread = 4 * breathe, Color = TracePalette.WithAlpha(p.Accent, 0.22) });
            if (!d.Pill) shadows.Add(new BoxShadow { OffsetY = 1, Blur = 2, Color = Color.FromArgb(20, 0, 0, 0) });
        }
        _card.BoxShadow = shadows.Count == 0 ? default : new BoxShadows(shadows[0], shadows.Skip(1).ToArray());
    }

    private Control Content(CardData d, TraceLiveContext? live, Func<string?, string> clientLabel)
    {
        var size = d.Stacked ? 28 : 26;
        Control? icon = d.Client is { Length: > 0 } client ? BrandIcon.Client(client, size, fallbackName: clientLabel(client))
            : d.Provider is { } prov ? BrandIcon.Provider(prov.Name, prov.Icon, size) : null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(TraceUi.Text(d.Title, 12.5, ArcDesignTokenKind.Foreground, FontWeight.SemiBold));
        foreach (var line in d.Lines) text.Children.Add(TraceUi.Text(line, 11.5, ArcDesignTokenKind.TextSecondary));
        if (d.Status is { } status)
        {
            // what the client's agent is doing right now (.trace-status): thinking accent-2, writing accent, tools green, retrying orange
            var color = status.Tone switch
            {
                "think" => _palette.Accent2,
                "write" => _palette.Accent,
                "tool" or "run" => _palette.Green,
                "warn" => _palette.Orange,
                _ => _palette.Label2,
            };
            var line = TraceUi.Text(status.Text, 11.5, color, FontWeight.Medium);
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(line);
        }
        if (d.Pending is { } id && live is not null) text.Children.Add(new PendingLineView(live, id, _palette));
        if (icon is null) return text;
        if (d.Stacked)
        {
            var col = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(icon);
            col.Children.Add(text);
            return col;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>
    /// A provider's card (nodes.tsx <c>ProviderNodeView</c>): brand, name and status line, then one row per account (or its API key) — status
    /// dot, name, requests on it now, its cooldown / remaining quota — lit accent while a request is served there and red right after one was
    /// refused there. The geometry is fixed (<see cref="RowCenterY"/>) so the hub's line to each row ends level with it.
    /// </summary>
    private Control ProviderContent(CardData d, IReadOnlyList<AccountRow> rows)
    {
        var p = _palette;
        var header = new Grid { Height = ProviderHeader, ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        if (d.Provider is { } prov)
        {
            var icon = BrandIcon.Provider(prov.Name, prov.Icon, 26);
            icon.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = TraceUi.Text(d.Title, 12.5, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        foreach (var line in d.Lines)
        {
            var l = TraceUi.Text(line, 11.5, ArcDesignTokenKind.TextSecondary);
            l.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(l);
        }
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        var list = new StackPanel { Spacing = RowGap };
        foreach (var row in rows)
        {
            var tone = row.Status == "ok" ? "ok" : row.Status == "cooldown" ? "warn" : "off";
            var bg = row.Hit == "on" ? TracePalette.Mix(p.Accent, 0.14, p.SurfaceMuted)
                : row.Hit == "fail" ? TracePalette.Mix(p.Red, 0.14, p.SurfaceMuted)
                : p.SurfaceMuted;
            var edge = row.Hit == "on" ? TracePalette.WithAlpha(p.Accent, 0.6) : row.Hit == "fail" ? TracePalette.WithAlpha(p.Red, 0.65) : Colors.Transparent;
            var cells = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
            cells.Children.Add(TraceUi.Dot(tone == "ok" ? p.Green : tone == "warn" ? p.Orange : p.Label3));
            var name = TraceUi.Text(row.Name, 12, ArcDesignTokenKind.Foreground);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            cells.Children.Add(name);
            if (row.Live > 0)
            {
                var badge = new Border
                {
                    MinWidth = 16, Height = 16, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(p.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = row.Live.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                Grid.SetColumn(badge, 2);
                cells.Children.Add(badge);
            }
            var detail = TraceUi.Text(row.Detail, 11, tone == "ok" ? p.Label2 : p.Orange);
            detail.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(detail, 3);
            cells.Children.Add(detail);
            var rowBorder = new Border
            {
                Height = RowHeight, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(bg), BorderBrush = new SolidColorBrush(edge), Child = cells, Opacity = row.Dim && !d.Dim ? 0.32 : 1,
            };
            ToolTip.SetTip(rowBorder, $"{row.Name} · {row.Detail}");
            list.Children.Add(rowBorder);
        }
        var stack = new StackPanel { Spacing = ProviderGap };
        stack.Children.Add(header);
        stack.Children.Add(list);
        return stack;
    }

    /// <summary>
    /// The hub's picture. The web shows <c>assets/logo-mark.png</c>, which the desktop project does not link (only <c>logo.png</c>, the
    /// same drawing with a dark rim); the rim is cropped away.
    /// </summary>
    private static IImage Logo()
    {
        _logo ??= ShellView.LoadLogo();
        var inset = 12;
        return new CroppedBitmap(_logo, new PixelRect(inset, inset, _logo.PixelSize.Width - 2 * inset, _logo.PixelSize.Height - 2 * inset));
    }
}

/// <summary>
/// A session card's live line (live.tsx <c>PendingLine</c>): what its call is doing and for how long, with the stage dot (thinking =
/// accent-2, writing = accent, tool = green, done green, failed red, else muted; pulsing while in progress).
/// </summary>
public sealed class PendingLineView : StackPanel
{
    private readonly TraceLiveContext _live;
    private readonly string _id;
    private readonly TracePalette _palette;
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stage = TraceUi.Text("", 11.5, ArcDesignTokenKind.TextSecondary);
    private readonly TextBlock _elapsed = TraceUi.Text("", 11.5, ArcDesignTokenKind.TextSecondary);
    private IDisposable? _tick;
    private IDisposable? _pulse;
    private bool _pulsing;

    /// <summary>Builds the line for request <paramref name="id"/>.</summary>
    public PendingLineView(TraceLiveContext live, string id, TracePalette palette)
    {
        _live = live;
        _id = id;
        _palette = palette;
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        Children.Add(_dot);
        Children.Add(_stage);
        Children.Add(_elapsed);
        Update();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _live.Store.RowChanged += OnRow;
        _tick = TraceTicker.Second(Update);
        Update();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _live.Store.RowChanged -= OnRow;
        _tick?.Dispose();
        _tick = null;
        _pulse?.Dispose();
        _pulse = null;
    }

    private void OnRow(string id)
    {
        if (id == _id) Update();
    }

    private void Update()
    {
        var r = _live.Get(_id);
        IsVisible = r is not null;
        if (r is null) return;
        var phase = TraceAnalysis.LivePhaseOf(r);
        var stage = r.Stage?.Split(':')[0];
        var color = phase == LivePhase.Failed ? _palette.Red
            : phase == LivePhase.Done ? _palette.Green
            : stage switch
            {
                "thinking" => _palette.Accent2,
                "writing" => _palette.Accent,
                "tool" => _palette.Green,
                _ => TracePalette.WithAlpha(_palette.Label3, 0.8),
            };
        _dot.Fill = new SolidColorBrush(color);
        _stage.Text = TraceAnalysis.StageText(r, TraceTexts.FromLoc);
        _elapsed.Text = TraceFormat.Ms(TraceAnalysis.Elapsed(r, _live.NowMs()));
        var pulsing = phase is not (LivePhase.Done or LivePhase.Failed);
        if (pulsing == _pulsing && (_pulse is not null || !pulsing)) return;
        _pulsing = pulsing;
        _pulse?.Dispose();
        _pulse = null;
        _dot.Opacity = 1;
        if (pulsing && TraceUi.Animates(this))
        {
            var start = Environment.TickCount64;
            _pulse = TraceTicker.Frame(() => _dot.Opacity = 1 - 0.75 * (1 - Math.Cos((Environment.TickCount64 - start) / 1200.0 * 2 * Math.PI)) / 2);
        }
    }
}
