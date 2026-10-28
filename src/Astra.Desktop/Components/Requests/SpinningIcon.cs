using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Motion;

namespace Astra.Desktop.Components.Requests;

/// <summary>A Lucide icon that turns while it is on screen (web <c>animate-spin</c>: the pending status badge); still under reduced motion.</summary>
public sealed class SpinningIcon : LucideIcon
{
    private CancellationTokenSource? _spin;

    /// <summary>A spinning icon.</summary>
    public SpinningIcon()
    {
        RenderTransformOrigin = RelativePoint.Center;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Stop();
        if (!ArcMotion.IsFull(this)) return;
        _spin = new CancellationTokenSource();
        var turn = new Animation
        {
            Duration = TimeSpan.FromSeconds(1),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360.0) } },
            },
        };
        RenderTransform = new RotateTransform();
        _ = turn.RunAsync(this, _spin.Token);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    private void Stop()
    {
        _spin?.Cancel();
        _spin = null;
    }
}
