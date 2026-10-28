using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Styling;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Motion;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// A status dot (web <c>Dot</c>: 8px, success / warning / danger / muted). Arc has no equivalent (plan inventory GAP-3), so this
/// is the kit's own: a token-colored circle that can pulse (opacity wave, only at Arc's Full motion level) for a live state.
/// </summary>
public sealed class StatusDot : Ellipse
{
    /// <summary>The color: Green / Orange / Red / Neutral (Accent uses the accent).</summary>
    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<StatusDot, Tone>(nameof(Tone), Tone.Neutral);

    /// <summary>Pulse while true (live / starting states).</summary>
    public static readonly StyledProperty<bool> IsPulsingProperty = AvaloniaProperty.Register<StatusDot, bool>(nameof(IsPulsing));

    private CancellationTokenSource? _pulse;

    /// <inheritdoc cref="ToneProperty"/>
    public Tone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    /// <inheritdoc cref="IsPulsingProperty"/>
    public bool IsPulsing { get => GetValue(IsPulsingProperty); set => SetValue(IsPulsingProperty, value); }

    /// <summary>An 8px dot.</summary>
    public StatusDot()
    {
        Width = 8;
        Height = 8;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;
        ApplyTone();
    }

    /// <summary>A dot of the given tone.</summary>
    public StatusDot(Tone tone) : this() => Tone = tone;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty) ApplyTone();
        else if (change.Property == IsPulsingProperty) UpdatePulse();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePulse();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopPulse();
    }

    private void ApplyTone()
    {
        var kind = Tone switch
        {
            Tone.Green => ArcDesignTokenKind.Success,
            Tone.Orange => ArcDesignTokenKind.Warning,
            Tone.Red => ArcDesignTokenKind.Danger,
            Tone.Accent => ArcDesignTokenKind.Accent,
            _ => ArcDesignTokenKind.TextMuted,
        };
        ArcTokens.Bind(this, FillProperty, kind);
    }

    private void UpdatePulse()
    {
        StopPulse();
        if (!IsPulsing || TopLevel.GetTopLevel(this) is null || !ArcMotion.IsFull(this)) return;
        _pulse = new CancellationTokenSource();
        var wave = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1600),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(OpacityProperty, 0.35) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } },
            },
        };
        _ = wave.RunAsync(this, _pulse.Token);
    }

    private void StopPulse()
    {
        _pulse?.Cancel();
        _pulse = null;
        Opacity = 1;
    }
}
