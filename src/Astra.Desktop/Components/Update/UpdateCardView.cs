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
using ArcButton = TKCode.Arc.Button;
using ArcMarkdown = TKCode.Arc.Markdown;

namespace Astra.Desktop.Components.Update;

/// <summary>
/// The bottom-right update card (<see cref="UpdateCardModel"/>): a tinted status badge, title, optional version transition pill, subtitle,
/// scrollable Markdown release notes (<see cref="ArcMarkdown"/>), a progress bar with transfer stats, action buttons and a close (×).
/// One instance lives for the app; <see cref="Render"/> updates its parts in place (a download renders several times a second), only re-feeding
/// the Markdown document when the notes change and only rebuilding buttons when the button list changes. Hosted by <c>UpdateNotice</c> in the
/// main window's overlay layer or in a corner window of its own.
/// </summary>
internal sealed class UpdateCardView : Border
{
    /// <summary>Width when showing compact states (checking, progress, result, or an offer without notes).</summary>
    public const double CompactWidth = 380;

    /// <summary>Width when showing Markdown release notes and the reminder's three actions.</summary>
    public const double NotesWidth = 456;

    /// <summary>Default compact width alias.</summary>
    public const double CardWidth = CompactWidth;

    private readonly Border _badge = new()
    {
        Width = 36,
        Height = 36,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly LucideIcon _icon = TrayUi.Icon(LucideIconKind.Download, 18);
    private readonly TextBlock _title = TrayUi.Text(null, 14, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
    private readonly TextBlock _fromVersion = TrayUi.Text(null, 11.5, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
    private readonly TextBlock _toVersion = TrayUi.Text(null, 11.5, ArcDesignTokenKind.Accent, FontWeight.SemiBold);
    private readonly Border _versionPill;
    private readonly TextBlock _message = TrayUi.Text(null, 12.5, ArcDesignTokenKind.TextSecondary);
    private readonly Border _close;

    private readonly TextBlock _notesHeadingText = TrayUi.Text(null, 11.5, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
    private readonly Border _notesHeader;
    private readonly ArcMarkdown _markdown = new()
    {
        FontSize = 12.5,
        LineHeight = 19,
        BlockSpacing = 6,
        OpensLinks = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ScrollViewer _notesScroll;
    private readonly Border _notesBox;

    private readonly Border _track = new() { Height = 6, ClipToBounds = true, CornerRadius = new CornerRadius(3) };
    private readonly Border _fill = new()
    {
        CornerRadius = new CornerRadius(3),
        HorizontalAlignment = HorizontalAlignment.Left,
        RenderTransform = new TranslateTransform(),
    };
    private readonly DispatcherTimer _sweep = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private double _sweepAt;

    private readonly TextBlock _progressText = TrayUi.Text(null, 11.5, ArcDesignTokenKind.TextSecondary);
    private readonly StackPanel _leftButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _rightButtons = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Grid _footer;

    private ArcDesignTokenKind _badgeTone = ArcDesignTokenKind.Accent;
    private string? _lastNotes;
    private IReadOnlyList<UpdateCardButton> _shownButtons = [];

    /// <summary>The model on screen, or null before the first <see cref="Render"/>.</summary>
    public UpdateCardModel? Model { get; private set; }

    /// <summary>A button or the close (×) was pressed.</summary>
    public event Action<UpdateCardAction>? Acted;

    /// <summary>Builds the (empty) card.</summary>
    public UpdateCardView()
    {
        Width = CompactWidth;
        Padding = new Thickness(16, 14, 16, 14);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        // Kept within UpdateNotice.Gap (16 DIP) so UpdateCardWindow never clips the shadow at its transparent edges.
        BoxShadow = BoxShadows.Parse("0 8 24 0 #33000000");
        this.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(BorderBrushProperty, ArcDesignTokenKind.BorderStrong);
        AutomationProperties.SetAutomationId(this, "update-card");

        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _badge.Child = _icon;

        _title.TextTrimming = TextTrimming.None;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.LineHeight = 20;

        var arrow = TrayUi.Text("→", 11, ArcDesignTokenKind.TextMuted);
        _versionPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 0),
            IsVisible = false,
            Child = TrayUi.Row(5, _fromVersion, arrow, _toVersion),
        };
        _versionPill.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);

        _message.TextTrimming = TextTrimming.None;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.LineHeight = 18;

        var closeIcon = TrayUi.Icon(LucideIconKind.X, 14, ArcDesignTokenKind.TextSecondary);
        closeIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _close = TrayUi.Clickable(new Border { Width = 26, Height = 26, Child = closeIcon }, () =>
        {
            if (Model?.Dismiss is { } dismiss) Acted?.Invoke(dismiss);
        });
        _close.CornerRadius = new CornerRadius(7);
        _close.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(_close, "update-card-close");

        var headText = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _title, _versionPill, _message },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(_badge);
        Grid.SetColumn(headText, 1);
        header.Children.Add(headText);
        Grid.SetColumn(_close, 2);
        header.Children.Add(_close);

        AutomationProperties.SetAutomationId(_markdown, "update-card-notes");
        _notesHeader = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 7, 12, 6),
            Child = TrayUi.Row(6, TrayUi.Icon(LucideIconKind.ScrollText, 12, ArcDesignTokenKind.TextMuted), _notesHeadingText),
        };
        _notesHeader.Token(BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);

        _notesScroll = new ScrollViewer
        {
            MaxHeight = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Padding = new Thickness(12, 10),
                Child = _markdown,
            },
        };
        var notesBody = new StackPanel { Children = { _notesHeader, _notesScroll } };
        _notesBox = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            IsVisible = false,
            Child = notesBody,
        };
        _notesBox.Token(BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);

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
        AttachedToVisualTree += (_, _) =>
        {
            ApplyBadgeTint();
            UpdateSweep();
        };
        DetachedFromVisualTree += (_, _) => _sweep.Stop();
        ActualThemeVariantChanged += (_, _) => ApplyBadgeTint();

        _progressText.VerticalAlignment = VerticalAlignment.Center;
        var footerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _progressText, _leftButtons },
        };
        _footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _footer.Children.Add(footerLeft);
        Grid.SetColumn(_rightButtons, 1);
        _footer.Children.Add(_rightButtons);

        Child = new StackPanel
        {
            Spacing = 12,
            Children = { header, _notesBox, _track, _footer },
        };
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
        _badgeTone = tone;
        ApplyBadgeTint();

        _title.Text = model.Title;
        AutomationProperties.SetName(this, model.Title);

        var showVersions = !string.IsNullOrEmpty(model.CurrentVersion) && !string.IsNullOrEmpty(model.Version);
        _versionPill.IsVisible = showVersions;
        if (showVersions)
        {
            _fromVersion.Text = FormatVersion(model.CurrentVersion!);
            _toVersion.Text = FormatVersion(model.Version!);
        }

        _message.Text = model.Message;
        _message.IsVisible = !string.IsNullOrEmpty(model.Message);

        var hasNotes = !string.IsNullOrEmpty(model.Notes);
        Width = hasNotes ? NotesWidth : CompactWidth;
        _notesBox.IsVisible = hasNotes;
        _notesHeader.IsVisible = hasNotes && !string.IsNullOrEmpty(model.NotesHeading);
        if (hasNotes && !string.IsNullOrEmpty(model.NotesHeading)) _notesHeadingText.Text = model.NotesHeading;
        if (!string.Equals(_lastNotes, model.Notes, StringComparison.Ordinal))
        {
            _lastNotes = model.Notes;
            _markdown.Text = model.Notes ?? string.Empty;
            _notesScroll.Offset = Vector.Zero;
        }

        _track.IsVisible = model.ShowProgress;
        LayoutFill();
        UpdateSweep();

        var hasProgressText = !string.IsNullOrEmpty(model.ProgressText);
        _progressText.Text = model.ProgressText;
        _progressText.IsVisible = hasProgressText;

        _close.IsVisible = model.Dismiss is not null;
        if (!_shownButtons.SequenceEqual(model.Buttons)) RebuildButtons(model.Buttons);
        _footer.IsVisible = model.Buttons.Count > 0 || hasProgressText;
    }

    /// <summary>The Markdown document control hosting the release notes (tests).</summary>
    internal ArcMarkdown MarkdownControl => _markdown;

    /// <summary>The release notes panel is visible (tests).</summary>
    internal bool NotesShown => _notesBox.IsVisible;

    /// <summary>The bar shows (tests).</summary>
    internal bool ProgressShown => _track.IsVisible;

    /// <summary>The bar slides (an unknown share) rather than fills (tests).</summary>
    internal bool Indeterminate => Model is { ShowProgress: true, Fraction: null };

    /// <summary>The fill's width as a share of the track (tests).</summary>
    internal double FillShare => _track.Bounds.Width > 0 && !Indeterminate ? _fill.Width / _track.Bounds.Width : 0;

    private static string FormatVersion(string version)
        => version.StartsWith('v') || version.StartsWith('V') ? version : "v" + version;

    private void ApplyBadgeTint()
    {
        var c = ArcTokens.GetColor(this, _badgeTone);
        _badge.Background = new SolidColorBrush(Color.FromArgb((byte)(0.12 * 255), c.R, c.G, c.B));
        _badge.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(0.24 * 255), c.R, c.G, c.B));
    }

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
        _leftButtons.Children.Clear();
        _rightButtons.Children.Clear();

        // Low-priority dismissive action ("Skip This Version") sits separated on the left; primary action rightmost.
        for (var i = buttons.Count - 1; i >= 0; i--)
        {
            var b = buttons[i];
            var variant = b.Primary
                ? ButtonVariant.Primary
                : b.Action is UpdateCardAction.Skip or UpdateCardAction.Background
                    ? ButtonVariant.Ghost
                    : ButtonVariant.Secondary;
            var button = new ArcButton
            {
                Content = b.Label,
                Variant = variant,
                Size = ControlSize.Small,
            };
            AutomationProperties.SetAutomationId(button, "update-card-" + b.Action.ToString().ToLowerInvariant());
            button.Click += (_, _) => Acted?.Invoke(b.Action);
            if (b.Action == UpdateCardAction.Skip) _leftButtons.Children.Add(button);
            else _rightButtons.Children.Add(button);
        }

        _leftButtons.IsVisible = _leftButtons.Children.Count > 0;
        _rightButtons.IsVisible = _rightButtons.Children.Count > 0;
    }

    /// <summary>Test seam: presses the button for <paramref name="action"/> when the card shows one.</summary>
    internal bool Press(UpdateCardAction action)
    {
        if (!_shownButtons.Any(b => b.Action == action)) return false;
        Acted?.Invoke(action);
        return true;
    }
}
