using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Astra.Desktop.Components.Kit;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// ARC-GAP: rows inside a grouped card that reorder by dragging a grip (web: dnd-kit <c>SortableContext</c> + <c>useSortable</c> in
/// <c>ProviderBindings</c>). Arc's <c>ReorderableList</c> draws every row as its own floating card with a 40 px handle and a position
/// number, which is not the web's look (rows separated by hairlines inside one card, a small grip glyph), so this does the drag itself:
/// pressing a row's grip and moving at least 4 px (dnd-kit's activation distance) lifts the row (surface fill, shadow, follows the
/// pointer) while the others slide out of its way; releasing calls <see cref="Reordered"/> with the old and new index. With the grip
/// focused, Up / Down move the row by one (dnd-kit's keyboard sensor needs Space first; one key press is enough here).
/// </summary>
public sealed class ReorderRows : UserControl
{
    private const double ActivationDistance = 4;

    private readonly StackPanel _stack = new();
    private readonly List<(Border Item, Control Grip)> _items = [];
    private int _from = -1;
    private int _target = -1;
    private Point _start;
    private bool _lifted;
    private IPointer? _pointer;

    /// <summary>Raised after a row was dropped at another place: (from, to).</summary>
    public event Action<int, int>? Reordered;

    /// <summary>When false the grips do nothing (a save is in flight, the client is not offered yet).</summary>
    public bool CanReorder { get; set; } = true;

    /// <summary>An empty list; add rows with <see cref="Add"/>.</summary>
    public ReorderRows() => Content = _stack;

    /// <summary>Appends <paramref name="row"/>; <paramref name="grip"/> (inside the row) starts a drag.</summary>
    public void Add(Control row, Control grip)
    {
        var index = _items.Count;
        var item = new Border { Child = row, BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0) };
        item.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _items.Add((item, grip));
        _stack.Children.Add(item);
        grip.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        grip.Focusable = true;
        grip.PointerPressed += (_, e) => OnPressed(item, e);
        grip.PointerMoved += (_, e) => OnMoved(e);
        grip.PointerReleased += (_, e) => OnReleased(e);
        grip.PointerCaptureLost += (_, _) => Cancel();
        grip.KeyDown += (_, e) => OnKey(item, e);
    }

    private int IndexOf(Border item) => _items.FindIndex(i => ReferenceEquals(i.Item, item));

    private void OnPressed(Border item, PointerPressedEventArgs e)
    {
        if (!CanReorder || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _from = IndexOf(item);
        _target = _from;
        _start = e.GetPosition(this);
        _lifted = false;
        _pointer = e.Pointer;
        e.Pointer.Capture((IInputElement?)e.Source ?? item);
        e.Handled = true;
    }

    private void OnMoved(PointerEventArgs e)
    {
        if (_from < 0 || !ReferenceEquals(e.Pointer, _pointer)) return;
        var dy = e.GetPosition(this).Y - _start.Y;
        if (!_lifted)
        {
            if (Math.Abs(dy) < ActivationDistance) return;
            Lift(true);
        }
        var moving = _items[_from].Item;
        moving.RenderTransform = new TranslateTransform(0, dy);

        // The drop slot: where the dragged row's centre lies among the other rows' centres.
        var centre = moving.Bounds.Center.Y + dy;
        var target = 0;
        for (var i = 0; i < _items.Count; i++)
            if (i != _from && _items[i].Item.Bounds.Center.Y < centre) target++;
        _target = Math.Clamp(target, 0, _items.Count - 1);

        // Slide the rows between the old and the new slot out of the way.
        var h = moving.Bounds.Height;
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _from) continue;
            double shift = 0;
            if (_target > _from && i > _from && i <= _target) shift = -h;
            else if (_target < _from && i >= _target && i < _from) shift = h;
            _items[i].Item.RenderTransform = shift == 0 ? null : new TranslateTransform(0, shift);
        }
    }

    private void OnReleased(PointerReleasedEventArgs e)
    {
        if (_from < 0 || !ReferenceEquals(e.Pointer, _pointer)) return;
        var (from, to, lifted) = (_from, _target, _lifted);
        e.Pointer.Capture(null);
        Reset();
        if (lifted && from != to) Reordered?.Invoke(from, to);
    }

    private void Cancel()
    {
        if (_from >= 0) Reset();
    }

    private void Reset()
    {
        if (_from >= 0 && _from < _items.Count && _lifted) Lift(false);
        foreach (var (item, _) in _items) item.RenderTransform = null;
        _from = -1;
        _target = -1;
        _lifted = false;
        _pointer = null;
    }

    private void Lift(bool on)
    {
        _lifted = on;
        var item = _items[_from].Item;
        item.ZIndex = on ? 10 : 0;
        if (on)
        {
            item.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface);
            item.BoxShadow = BoxShadows.Parse("0 10 15 -3 #26000000, 0 4 6 -4 #1A000000");
        }
        else
        {
            item.Background = null;
            item.BoxShadow = default;
        }
    }

    private void OnKey(Border item, KeyEventArgs e)
    {
        if (!CanReorder || e.Key is not (Key.Up or Key.Down)) return;
        var from = IndexOf(item);
        var to = e.Key == Key.Up ? from - 1 : from + 1;
        if (to < 0 || to >= _items.Count) return;
        e.Handled = true;
        Reordered?.Invoke(from, to);
    }
}
