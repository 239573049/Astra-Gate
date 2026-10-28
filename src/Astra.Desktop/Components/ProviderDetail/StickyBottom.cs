using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// CSS <c>position: sticky; bottom: N</c> for a child of the shell's scroller (web: the provider settings' "unsaved" pill is
/// <c>sticky bottom-2</c>, the subscription accounts' bulk bar <c>sticky bottom-3</c>). The child keeps its place in the flow; while that place
/// lies below the visible part of the nearest <see cref="ScrollViewer"/>, it is drawn (and hit-tested) <see cref="Offset"/> pixels above the
/// viewport's bottom edge instead, over the content before it — exactly what a sticky element does. Scrolling it into view puts it back.
/// // ARC-GAP: Arc's ScrollArea has no sticky children; this shifts the child with a render transform on scroll and layout.
/// </summary>
public sealed class StickyBottom : Decorator
{
    private readonly TranslateTransform _shift = new();
    private ScrollViewer? _viewer;

    /// <summary>Creates the wrapper (drawn above the siblings before it).</summary>
    public StickyBottom()
    {
        RenderTransform = _shift;
        ZIndex = 10;
    }

    /// <summary>Distance from the viewport's bottom edge while stuck (web <c>bottom-2</c> = 8, <c>bottom-3</c> = 12).</summary>
    public double Offset { get; set; } = 8;

    /// <summary>The current shift (0 = in its own place; negative = stuck above the viewport's bottom). For tests.</summary>
    internal double Shift => _shift.Y;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewer = this.FindAncestorOfType<ScrollViewer>();
        if (_viewer is not null)
        {
            _viewer.ScrollChanged += OnScroll;
            _viewer.LayoutUpdated += OnLayout;
        }
        LayoutUpdated += OnLayout;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewer is not null)
        {
            _viewer.ScrollChanged -= OnScroll;
            _viewer.LayoutUpdated -= OnLayout;
        }
        LayoutUpdated -= OnLayout;
        _viewer = null;
        _shift.Y = 0;
    }

    private void OnScroll(object? sender, ScrollChangedEventArgs e) => Update();

    private void OnLayout(object? sender, EventArgs e) => Update();

    /// <summary>Recomputes the shift from the child's place in the flow and the viewport.</summary>
    internal void Update()
    {
        if (_viewer is null || !IsEffectivelyVisible || Bounds.Height <= 0)
        {
            Set(0);
            return;
        }
        // TranslatePoint includes the current render transform: take it out to get the place in the flow.
        if (this.TranslatePoint(default, _viewer) is not { } at)
        {
            Set(0);
            return;
        }
        var natural = at.Y - _shift.Y;
        var limit = _viewer.Bounds.Height - Offset;
        Set(Math.Min(0, limit - (natural + Bounds.Height)));
    }

    private void Set(double y)
    {
        // Whole pixels: a fractional shift would blur the text, and comparing them keeps LayoutUpdated from looping.
        y = Math.Round(y);
        if (_shift.Y != y) _shift.Y = y;
    }
}
