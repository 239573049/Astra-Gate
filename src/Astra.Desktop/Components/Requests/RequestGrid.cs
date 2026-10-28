using System.ComponentModel;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Requests;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Requests;

/// <summary>One column of a <see cref="RequestGrid"/> (web <c>DataColumn</c>): a fixed pixel width, a sort value and a cell builder.</summary>
/// <param name="Key">Column key (the sort key).</param>
/// <param name="Header">Header text.</param>
/// <param name="Width">Width in px (web <c>width</c>); extra room is shared out in proportion (<see cref="TableColumns"/>).</param>
/// <param name="Numeric">Right-aligned with the sort arrow leading the label (web <c>data-numeric</c>).</param>
/// <param name="SortValue">The value the column sorts by.</param>
/// <param name="Cell">Builds the cell content from the row's freshest summary (rebuilt on a live update).</param>
public sealed record RequestGridColumn(string Key, string Header, double Width, bool Numeric, Func<RequestRowViewModel, object?> SortValue, Func<RequestRowViewModel, Control> Cell);

/// <summary>
/// ARC-GAP: Arc's <c>Table</c> draws every body row at a fixed 52 px and has no row activation; the web's request list (Arc's
/// SortableDataTable in web/src/components/arc/sortable-data-table, used by web/src/pages/RequestsPage.tsx) lets a row grow with its
/// content (the model cell's three lines, a wrapped badge), uses fixed pixel column widths and scrolls sideways when they do not fit.
/// This control reproduces that table: the 1 px <c>--border</c> frame on <c>--surface</c> with the control radius, the 44 px header
/// (13 px medium muted labels, 16 px inline padding, sort arrow that shows on hover and flips for descending, numeric columns
/// right-aligned with the arrow leading), body rows of at least 52 px (cells vertically centred, 16 px padding, <c>--border-subtle</c>
/// dividers, a 3 % foreground hover fill), the <c>--surface-muted</c> band under the sorted column, and the sort order of
/// <see cref="TableSort"/>. Beyond the web: a row is activated by a click or Enter / Space (<see cref="RowActivated"/>; the web's
/// model cell is a button), and Up / Down / Home / End move between rows. Rows are kept per request id, so a live update or a refetch
/// moves and redraws only what changed (each cell redraws itself from its row, see <see cref="RequestCell"/>).
/// </summary>
public sealed class RequestGrid : Border
{
    /// <summary>Web <c>.table td { height: 52px }</c>: the minimum row height, its 1 px divider included.</summary>
    public const double RowMinHeight = 52;

    /// <summary>Web <c>.table th { height: 44px }</c>, its 1 px rule included.</summary>
    public const double HeaderHeight = 44;

    /// <summary>Web <c>padding: 0 var(--space-4)</c>.</summary>
    public const double CellPadding = 16;

    private readonly GridPanel _panel;

    /// <summary>A table of <paramref name="columns"/>; <paramref name="emptyMessage"/> shows when there are no rows.</summary>
    public RequestGrid(IReadOnlyList<RequestGridColumn> columns, string caption, string emptyMessage)
    {
        _panel = new GridPanel(this, columns, emptyMessage);
        AutomationProperties.SetName(this, caption);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        // The web's translucent --surface and --border (see WebMaterial: the theme's Surface / Border tokens are opaque greys).
        WebMaterial.Apply(this);
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _panel,
        };
    }

    /// <summary>The rows (any order; shown in the current sort order, else in this order).</summary>
    public IReadOnlyList<RequestRowViewModel> ItemsSource
    {
        get => _panel.Items;
        set => _panel.SetItems(value);
    }

    /// <summary>The sorted column's key, or null (input order).</summary>
    public string? SortKey => _panel.SortKey;

    /// <summary>Whether the sort is descending.</summary>
    public bool SortDescending => _panel.Descending;

    /// <summary>Sorts by <paramref name="key"/> (web <c>sortBy</c> sets the direction itself; this is for code and tests).</summary>
    public void SortBy(string? key, bool descending = false) => _panel.SetSort(key, descending);

    /// <summary>The rows in display order.</summary>
    public IReadOnlyList<RequestRowViewModel> DisplayedRows => _panel.Ordered;

    /// <summary>A row was clicked or activated from the keyboard.</summary>
    public event Action<RequestRowViewModel>? RowActivated;

    internal void Activate(RequestRowViewModel row) => RowActivated?.Invoke(row);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // The panel sits in a sideways ScrollViewer (unbounded width), so it is told the container's width to fill.
        _panel.Viewport = availableSize.Width - BorderThickness.Left - BorderThickness.Right;
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The header and body rows; draws the sorted column's band behind them.</summary>
    internal sealed class GridPanel : Panel
    {
        public static readonly StyledProperty<IBrush?> BandBrushProperty = AvaloniaProperty.Register<GridPanel, IBrush?>(nameof(BandBrush));
        public static readonly StyledProperty<IBrush?> RuleBrushProperty = AvaloniaProperty.Register<GridPanel, IBrush?>(nameof(RuleBrush));
        public static readonly StyledProperty<IBrush?> DividerBrushProperty = AvaloniaProperty.Register<GridPanel, IBrush?>(nameof(DividerBrush));
        public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<GridPanel, IBrush?>(nameof(LabelBrush));
        public static readonly StyledProperty<IBrush?> FocusBrushProperty = AvaloniaProperty.Register<GridPanel, IBrush?>(nameof(FocusBrush));

        private readonly RequestGrid _owner;
        private readonly Painter _painter;
        private readonly HeaderRow _header;
        private readonly TextBlock _empty;
        private readonly Dictionary<string, BodyRow> _rows = new(StringComparer.Ordinal);
        private IReadOnlyList<RequestRowViewModel> _items = [];
        private List<BodyRow> _ordered = [];
        private double _viewport;

        public GridPanel(RequestGrid owner, IReadOnlyList<RequestGridColumn> columns, string emptyMessage)
        {
            _owner = owner;
            Columns = columns;
            Geometry = new (double, double)[columns.Count];
            this.Token(BandBrushProperty, ArcDesignTokenKind.SurfaceMuted).Token(RuleBrushProperty, ArcDesignTokenKind.Border)
                .Token(DividerBrushProperty, ArcDesignTokenKind.BorderSubtle).Token(LabelBrushProperty, ArcDesignTokenKind.Foreground)
                .Token(FocusBrushProperty, ArcDesignTokenKind.FocusRing);
            _painter = new Painter(DrawBand);
            Children.Add(_painter);
            _header = new HeaderRow(this);
            Children.Add(_header);
            // Web .empty: a 120 px muted centred line.
            _empty = KitStyle.Text(emptyMessage, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(_empty);
        }

        public IBrush? BandBrush => GetValue(BandBrushProperty);
        public IBrush? RuleBrush => GetValue(RuleBrushProperty);
        public IBrush? DividerBrush => GetValue(DividerBrushProperty);
        public IBrush? LabelBrush => GetValue(LabelBrushProperty);
        public IBrush? FocusBrush => GetValue(FocusBrushProperty);

        public IReadOnlyList<RequestGridColumn> Columns { get; }

        /// <summary>Left edge and width of each column at the current width.</summary>
        public (double X, double Width)[] Geometry { get; private set; }

        public double TableWidth { get; private set; }

        public string? SortKey { get; private set; }

        public bool Descending { get; private set; }

        public IReadOnlyList<RequestRowViewModel> Items => _items;

        public IReadOnlyList<RequestRowViewModel> Ordered => _ordered.Select(r => r.Row).ToList();

        public double Viewport
        {
            get => _viewport;
            set
            {
                if (Math.Abs(_viewport - value) < 0.01) return;
                _viewport = value;
                InvalidateMeasure();
            }
        }

        public void SetItems(IReadOnlyList<RequestRowViewModel> items)
        {
            _items = items;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                keep.Add(item.Id);
                if (_rows.TryGetValue(item.Id, out var row)) row.Row = item;
                else
                {
                    row = new BodyRow(this, item);
                    _rows[item.Id] = row;
                    Children.Add(row);
                }
            }
            foreach (var gone in _rows.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                Children.Remove(_rows[gone]);
                _rows.Remove(gone);
            }
            Reorder();
        }

        public void SetSort(string? key, bool descending)
        {
            SortKey = key;
            Descending = descending;
            _header.SyncSort();
            Reorder();
            _painter.InvalidateVisual();
        }

        /// <summary>Web <c>sortBy</c>: a new column starts ascending, the sorted one flips between ascending and descending.</summary>
        public void ToggleSort(string key) => SetSort(key, SortKey == key && !Descending);

        /// <summary>A row's values changed (live update): its place in a sorted table may have too.</summary>
        public void RowChanged()
        {
            if (SortKey is not null) Reorder();
        }

        private void Reorder()
        {
            var column = Columns.FirstOrDefault(c => c.Key == SortKey);
            var rows = _items.Select(i => _rows[i.Id]).ToList();
            var ordered = TableSort.Order(rows, column is null ? null : r => column.SortValue(r.Row), Descending);
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].IsLast = i == ordered.Count - 1;
                KeyboardNavigation.SetTabIndex(ordered[i], i + 1);
            }
            _ordered = ordered;
            _empty.IsVisible = ordered.Count == 0;
            InvalidateMeasure();
        }

        public void Move(BodyRow from, int delta, bool absolute = false)
        {
            if (_ordered.Count == 0) return;
            var index = absolute ? delta : _ordered.IndexOf(from) + delta;
            index = Math.Clamp(index, 0, _ordered.Count - 1);
            var target = _ordered[index];
            target.Focus(NavigationMethod.Directional);
            target.BringIntoView();
        }

        public void Activate(RequestRowViewModel row) => _owner.Activate(row);

        protected override Size MeasureOverride(Size availableSize)
        {
            var (total, geometry) = TableColumns.Layout(Columns.Select(c => c.Width).ToList(), _viewport);
            Geometry = geometry;
            TableWidth = total;
            var height = 0d;
            _header.Measure(new Size(total, HeaderHeight));
            height += HeaderHeight;
            foreach (var row in _ordered)
            {
                row.Measure(new Size(total, double.PositiveInfinity));
                height += row.DesiredSize.Height;
            }
            if (_empty.IsVisible)
            {
                _empty.Measure(new Size(total, double.PositiveInfinity));
                height += 120;
            }
            return new Size(total, height);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BandBrushProperty) _painter.InvalidateVisual();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _header.Arrange(new Rect(0, 0, TableWidth, HeaderHeight));
            var y = HeaderHeight;
            foreach (var row in _ordered)
            {
                row.Arrange(new Rect(0, y, TableWidth, row.DesiredSize.Height));
                y += row.DesiredSize.Height;
            }
            if (_empty.IsVisible) _empty.Arrange(new Rect(0, y + (120 - _empty.DesiredSize.Height) / 2, TableWidth, _empty.DesiredSize.Height));
            var size = new Size(TableWidth, Math.Max(finalSize.Height, y + (_empty.IsVisible ? 120 : 0)));
            _painter.Arrange(new Rect(size));
            _painter.InvalidateVisual();
            return size;
        }

        // Web .band: a still tint under the sorted column, header included.
        private void DrawBand(DrawingContext context, Size size)
        {
            var index = Columns.ToList().FindIndex(c => c.Key == SortKey);
            if (index >= 0 && BandBrush is { } band)
                context.FillRectangle(band, new Rect(Geometry[index].X, 0, Geometry[index].Width, size.Height));
        }
    }

    /// <summary>The header row: one sort control per column over a 1 px <c>--border</c> rule.</summary>
    internal sealed class HeaderRow : Panel
    {
        private readonly GridPanel _grid;
        private readonly List<HeaderCell> _cells = [];

        private readonly Painter _painter;

        public HeaderRow(GridPanel grid)
        {
            _grid = grid;
            _painter = new Painter((context, size) =>
            {
                if (_grid.RuleBrush is { } rule) context.FillRectangle(rule, new Rect(0, size.Height - 1, size.Width, 1));
            });
            Children.Add(_painter);
            foreach (var column in grid.Columns)
            {
                var cell = new HeaderCell(grid, column);
                _cells.Add(cell);
                Children.Add(cell);
            }
        }

        public void SyncSort()
        {
            foreach (var cell in _cells) cell.Sync();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            for (var i = 0; i < _cells.Count; i++) _cells[i].Measure(new Size(_grid.Geometry[i].Width, HeaderHeight - 1));
            return new Size(availableSize.Width, HeaderHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            for (var i = 0; i < _cells.Count; i++) _cells[i].Arrange(new Rect(_grid.Geometry[i].X, 0, _grid.Geometry[i].Width, HeaderHeight - 1));
            _painter.Arrange(new Rect(finalSize));
            _painter.InvalidateVisual();
            return finalSize;
        }
    }

    /// <summary>
    /// A header sort control (web <c>.sortButton</c>): label in 13 px medium, muted (foreground once sorted or hovered), and the 16 px
    /// arrow — hidden, a 60 % muted hint on hover, full foreground when sorted, turned over for descending.
    /// </summary>
    internal sealed class HeaderCell : Border
    {
        private readonly GridPanel _grid;
        private readonly RequestGridColumn _column;
        private readonly TextBlock _label;
        private readonly LucideIcon _arrow;
        private bool _hover;

        public HeaderCell(GridPanel grid, RequestGridColumn column)
        {
            _grid = grid;
            _column = column;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            Background = Brushes.Transparent;
            Padding = new Thickness(CellPadding, 0);
            _label = KitStyle.Text(column.Header, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted, FontWeight.Medium);
            _arrow = new LucideIcon { Kind = LucideIconKind.ArrowUp, Size = 16, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center };
            var inner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = column.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            // Right-aligned columns lead with the arrow so the label lines up with the digits below it.
            if (column.Numeric) { inner.Children.Add(_arrow); inner.Children.Add(_label); }
            else { inner.Children.Add(_label); inner.Children.Add(_arrow); }
            Child = inner;
            AutomationProperties.SetName(this, column.Header);
            PointerEntered += (_, _) => { _hover = true; Sync(); };
            PointerExited += (_, _) => { _hover = false; Sync(); };
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left && new Rect(Bounds.Size).Contains(e.GetPosition(this))) _grid.ToggleSort(_column.Key);
            };
            KeyDown += OnKey;
            Sync();
        }

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space) { _grid.ToggleSort(_column.Key); e.Handled = true; }
        }

        public void Sync()
        {
            var active = _grid.SortKey == _column.Key;
            var lit = active || _hover;
            _label.Token(TextBlock.ForegroundProperty, lit ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextMuted);
            _arrow.Token(TemplatedControl.ForegroundProperty, active ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextMuted);
            _arrow.Opacity = active ? 1 : _hover ? 0.6 : 0;
            _arrow.RenderTransform = active && _grid.Descending ? new RotateTransform(180) : null;
        }
    }

    /// <summary>A body row: the cells of one request at their column slots, at least 52 px tall, growing with its tallest cell.</summary>
    internal sealed class BodyRow : Panel
    {
        private const double ClickSlop = 6;
        private readonly GridPanel _grid;
        private readonly RequestCell[] _cells;
        private RequestRowViewModel _row;
        private bool _hover;
        private bool _focusVisible;
        private bool _last;
        private Point? _press;
        private readonly Painter _painter;

        public BodyRow(GridPanel grid, RequestRowViewModel row)
        {
            _grid = grid;
            _row = row;
            _painter = new Painter(Draw);
            Children.Add(_painter);
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            Background = Brushes.Transparent;
            DataContext = row;
            row.PropertyChanged += OnRowChanged;
            _cells = grid.Columns.Select(c => new RequestCell(c.Cell)
            {
                // Web: text cells truncate inside their slot; numeric cells are nowrap and spill to the left when wider (e.g. "316.2 tok/s").
                ClipToBounds = !c.Numeric,
                HorizontalContentAlignment = c.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            }).ToArray();
            foreach (var cell in _cells) Children.Add(cell);
            PointerEntered += (_, _) => { _hover = true; _painter.InvalidateVisual(); };
            PointerExited += (_, _) => { _hover = false; _painter.InvalidateVisual(); };
            GotFocus += (_, e) => { _focusVisible = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional; _painter.InvalidateVisual(); };
            LostFocus += (_, _) => { _focusVisible = false; _painter.InvalidateVisual(); };
        }

        public RequestRowViewModel Row
        {
            get => _row;
            set
            {
                if (ReferenceEquals(value, _row)) return;
                _row.PropertyChanged -= OnRowChanged;
                _row = value;
                _row.PropertyChanged += OnRowChanged;
                DataContext = value;
            }
        }

        public bool IsLast
        {
            get => _last;
            set
            {
                if (_last == value) return;
                _last = value;
                _painter.InvalidateVisual();
            }
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => _grid.RowChanged();

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            _press = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_press is not { } start) return;
            _press = null;
            var at = e.GetPosition(this);
            // A click, not a drag (text selection, a scroll) and released over the same row.
            if (Math.Abs(at.X - start.X) > ClickSlop || Math.Abs(at.Y - start.Y) > ClickSlop || !new Rect(Bounds.Size).Contains(at)) return;
            _grid.Activate(_row);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            switch (e.Key)
            {
                case Key.Enter or Key.Space: _grid.Activate(_row); break;
                case Key.Down: _grid.Move(this, 1); break;
                case Key.Up: _grid.Move(this, -1); break;
                case Key.Home: _grid.Move(this, 0, absolute: true); break;
                case Key.End: _grid.Move(this, int.MaxValue / 2, absolute: true); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var tallest = 0d;
            for (var i = 0; i < _cells.Length; i++)
            {
                var slot = _grid.Columns[i].Numeric ? double.PositiveInfinity : Math.Max(0, _grid.Geometry[i].Width - 2 * CellPadding);
                _cells[i].Measure(new Size(slot, double.PositiveInfinity));
                tallest = Math.Max(tallest, _cells[i].DesiredSize.Height);
            }
            return new Size(availableSize.Width, Math.Max(RowMinHeight, Math.Ceiling(tallest) + 1));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var content = finalSize.Height - 1;
            for (var i = 0; i < _cells.Length; i++)
            {
                var (x, w) = _grid.Geometry[i];
                var h = Math.Min(_cells[i].DesiredSize.Height, content);
                var slot = Math.Max(0, w - 2 * CellPadding);
                var cw = _grid.Columns[i].Numeric ? Math.Max(slot, _cells[i].DesiredSize.Width) : slot;
                _cells[i].Arrange(new Rect(x + CellPadding + slot - cw, (content - h) / 2, cw, h));
            }
            _painter.Arrange(new Rect(finalSize));
            _painter.InvalidateVisual();
            return finalSize;
        }

        private void Draw(DrawingContext context, Size size)
        {
            var box = new Rect(size);
            // Web hover: color-mix(in oklab, var(--foreground) 3%, cell) — the foreground at 3 % over the cell (and the sorted band).
            if (_hover && _grid.LabelBrush is ISolidColorBrush fg)
            {
                var c = fg.Color;
                context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * 0.03), c.R, c.G, c.B)), box);
            }
            if (!_last && _grid.DividerBrush is { } divider) context.FillRectangle(divider, new Rect(0, box.Height - 1, box.Width, 1));
            if (_focusVisible && IsFocused && _grid.FocusBrush is { } ring)
                context.DrawRectangle(new Pen(ring, 2), box.Deflate(2), 6);
        }
    }

    /// <summary>Draws behind its siblings (the first child of a panel); Avalonia 12's <c>Panel.Render</c> is sealed.</summary>
    internal sealed class Painter : Control
    {
        private readonly Action<DrawingContext, Size> _draw;

        public Painter(Action<DrawingContext, Size> draw)
        {
            _draw = draw;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _draw(context, Bounds.Size);
    }
}
