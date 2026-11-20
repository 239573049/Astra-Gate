using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Astra.Desktop.Core.Tray;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Update;

/// <summary>
/// The bottom-right update card (<see cref="UpdateCardModel"/>): an icon badge, the title and a line under it, release notes, a progress bar with
/// its percent / bytes, the buttons and a close (×). One instance lives for the app; <see cref="Render"/> updates its parts in place (a download
/// renders several times a second), and only the button row is rebuilt, when its buttons change. Hosted by <c>UpdateNotice</c> in the main
/// window's overlay layer or in a corner window of its own. The bar is drawn here (a 6 px track and its fill, like the quota bars): a known
/// share fills from the left, an unknown one is a segment sliding across the track, animated only while the card is on screen.
/// </summary>
internal sealed class UpdateCardView : Border
{
    /// <summary>The card's width (DIP).</summary>
    public const double CardWidth = 360;

    private readonly Border _badge = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top };
    private readonly LucideIcon _icon = TrayUi.Icon(LucideIconKind.Download, 18);
    private readonly TextBlock _title = TrayUi.Text(null, 13.5, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
    private readonly TextBlock _message = TrayUi.Text(null, 12.5, ArcDesignTokenKind.TextSecondary);
    private readonly SelectableTextBlock _notes = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border _notesBox;
    private readonly Border _track = new() { Height = 6, ClipToBounds = true, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 2) };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform() };
    private readonly DispatcherTimer _sweep = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private double _sweepAt;
    private readonly TextBlock _progressText = TrayUi.Text(null, 11.5, ArcDesignTokenKind.TextSecondary);
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _close;
    private IReadOnlyList<UpdateCardButton> _shownButtons = [];

    /// <summary>The model on screen, or null before the first <see cref="Render"/>.</summary>
    public UpdateCardModel? Model { get; private set; }

    /// <summary>A button or the close (×) was pressed.</summary>
    public event Action<UpdateCardAction>? Acted;

    /// <summary>Builds the (empty) card.</summary>
    public UpdateCardView()
    {
        Width = CardWidth;
        Padding = new Thickness(14, 12, 12, 12);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 8 28 0 #33000000");
        this.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(BorderBrushProperty, ArcDesignTokenKind.Border);
        AutomationProperties.SetAutomationId(this, "update-card");

        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _badge.Child = _icon;
        _title.TextTrimming = TextTrimming.None;
        _title.TextWrapping = TextWrapping.Wrap;
        _message.TextTrimming = TextTrimming.None;
        _message.TextWrapping = TextWrapping.Wrap;
        _notes.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        _notesBox = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new ScrollViewer { MaxHeight = 120, Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        _notesBox.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        _track.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        _fill.Token(BackgroundProperty, ArcDesignTokenKind.Accent);
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => LayoutFill();
        // Not the (interval, priority, callback) constructor: that one starts the timer.
        _sweep.Tick += (_, _) =>
        {
            _sweepAt = (_sweepAt + 0.025) % 1.35;
            LayoutFill();
        };
        AttachedToVisualTree += (_, _) => UpdateSweep();
        DetachedFromVisualTree += (_, _) => _sweep.Stop();
        _progressText.VerticalAlignment = VerticalAlignment.Center;

        var closeIcon = TrayUi.Icon(LucideIconKind.X, 14, ArcDesignTokenKind.TextSecondary);
        closeIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _close = TrayUi.Clickable(new Border { Width = 24, Height = 24, Child = closeIcon }, () =>
        {
            if (Model?.Dismiss is { } dismiss) Acted?.Invoke(dismiss);
        });
        _close.CornerRadius = new CornerRadius(6);
        _close.VerticalAlignment = VerticalAlignment.Top;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        head.Children.Add(_title);
        Grid.SetColumn(_close, 1);
        head.Children.Add(_close);
        // The percent / bytes on the left of the buttons' row.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(_progressText);
        Grid.SetColumn(_buttons, 1);
        footer.Children.Add(_buttons);
        var text = new StackPanel { Spacing = 6, Children = { head, _message, _notesBox, _track, footer } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(_badge);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Child = row;
    }

    /// <summary>Shows <paramref name="model"/>.</summary>
    public void Render(UpdateCardModel model)
    {
        Model = model;
        var (glyph, tone) = model.Tone switch
        {
            UpdateCardTone.Success => (LucideIconKind.CircleCheck, ArcDesignTokenKind.Success),
            UpdateCardTone.Danger => (LucideIconKind.CircleAlert, ArcDesignTokenKind.Danger),
            UpdateCardTone.Busy => (LucideIconKind.RefreshCw, ArcDesignTokenKind.Accent),
            _ => (LucideIconKind.Download, ArcDesignTokenKind.Accent),
        };
        _icon.Kind = glyph;
        _icon.Token(LucideIcon.ForegroundProperty, tone);
        _badge.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);

        _title.Text = model.Title;
        AutomationProperties.SetName(this, model.Title);
        _message.Text = model.Message;
        _message.IsVisible = !string.IsNullOrEmpty(model.Message);
        _notes.Text = model.Notes;
        _notesBox.IsVisible = !string.IsNullOrEmpty(model.Notes);

        _track.IsVisible = model.ShowProgress;
        LayoutFill();
        UpdateSweep();
        _progressText.Text = model.ProgressText;
        _progressText.IsVisible = !string.IsNullOrEmpty(model.ProgressText);

        _close.IsVisible = model.Dismiss is not null;
        if (!_shownButtons.SequenceEqual(model.Buttons)) RebuildButtons(model.Buttons);
    }

    /// <summary>The bar shows (tests).</summary>
    internal bool ProgressShown => _track.IsVisible;

    /// <summary>The bar slides (an unknown share) rather than fills (tests).</summary>
    internal bool Indeterminate => Model is { ShowProgress: true, Fraction: null };

    /// <summary>The fill's width as a share of the track (tests).</summary>
    internal double FillShare => _track.Bounds.Width > 0 && !Indeterminate ? _fill.Width / _track.Bounds.Width : 0;

    private void LayoutFill()
    {
        var width = _track.Bounds.Width;
        var shift = (TranslateTransform)_fill.RenderTransform!;
        if (Indeterminate)
        {
            // A third of the track, entering on the left and leaving on the right.
            _fill.Width = width * 0.35;
            shift.X = (_sweepAt - 0.35) * width;
        }
        else
        {
            _fill.Width = width * (Model?.Fraction ?? 0);
            shift.X = 0;
        }
    }

    private void UpdateSweep()
    {
        var run = Indeterminate && TopLevel.GetTopLevel(this) is not null;
        if (run && !_sweep.IsEnabled) _sweep.Start();
        else if (!run && _sweep.IsEnabled) _sweep.Stop();
    }

    private void RebuildButtons(IReadOnlyList<UpdateCardButton> buttons)
    {
        _shownButtons = buttons;
        _buttons.Children.Clear();
        _buttons.IsVisible = buttons.Count > 0;
        // The primary action sits rightmost, like the message box.
        for (var i = buttons.Count - 1; i >= 0; i--)
        {
            var b = buttons[i];
            var button = new TKCode.Arc.Button
            {
                Content = b.Label,
                Variant = b.Primary ? ButtonVariant.Primary : ButtonVariant.Ghost,
                Size = ControlSize.Small,
            };
            AutomationProperties.SetAutomationId(button, "update-card-" + b.Action.ToString().ToLowerInvariant());
            button.Click += (_, _) => Acted?.Invoke(b.Action);
            _buttons.Children.Add(button);
        }
    }

    /// <summary>Test seam: presses the button for <paramref name="action"/> when the card shows one.</summary>
    internal bool Press(UpdateCardAction action)
    {
        if (!_shownButtons.Any(b => b.Action == action)) return false;
        Acted?.Invoke(action);
        return true;
    }
}
