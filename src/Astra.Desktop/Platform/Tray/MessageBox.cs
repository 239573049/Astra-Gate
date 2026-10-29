using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Platform.Tray;

/// <summary>The kind of a <see cref="MessageBox"/> (Electron <c>showMessageBox</c>'s <c>type</c>): only the icon differs.</summary>
internal enum MessageBoxKind { Info, Warning, Error }

/// <summary>
/// A small modal message box of its own window (Electron's <c>dialog.showMessageBox</c> without a parent): the tray's update reminder, the
/// tray's check-for-updates result, the update-failed and API-mismatch warnings and tray action errors. Its own window, not a dialog in
/// the main one, because a tray app often has no visible window (login start, closed to the tray) — exactly when these appear.
/// Centered on the primary screen, always on top, out of the taskbar; Escape / Alt+F4 pick <c>cancelIndex</c>, Enter the default.
/// Frameless like the tray panel: no OS title bar, a transparent window whose rounded card (the tray panel's surface and border tokens, a
/// soft shadow) is the visible surface, dragged by its header. Built from Arc buttons and the tray panel's text styles; a plain Avalonia
/// window like the others (Arc's base theme gives it the overlay layer).
/// </summary>
internal sealed class MessageBox : Window
{
    private const double CardWidth = 420;
    /// <summary>Transparent margin around the card, room for its shadow.</summary>
    private const double ShadowRoom = 20;

    private readonly TaskCompletionSource<int> _result = new();
    private readonly int _cancelIndex;

    private MessageBox(MessageBoxKind kind, string message, string? detail, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        _cancelIndex = cancelIndex;
        Title = "Astra";
        Width = CardWidth + 2 * ShadowRoom;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var (glyph, tone) = kind switch
        {
            MessageBoxKind.Error => (LucideIconKind.CircleAlert, ArcDesignTokenKind.Danger),
            MessageBoxKind.Warning => (LucideIconKind.TriangleAlert, ArcDesignTokenKind.Warning),
            _ => (LucideIconKind.Info, ArcDesignTokenKind.Accent),
        };
        var icon = TrayUi.Icon(glyph, 22, tone);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Child = icon };
        badge.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        var head = TrayUi.Text(message, 14, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        head.TextTrimming = TextTrimming.None;
        head.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Spacing = 6, Children = { head } };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var body = new SelectableTextBlock { Text = detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            body.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            var notes = new Border
            {
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = new ScrollViewer { MaxHeight = 280, Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            };
            notes.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            text.Children.Add(notes);
        }
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        top.Children.Add(badge);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        // Electron (noLink: true) lays the buttons out right to left with the default first; here the default is the rightmost, primary one.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (var i = buttons.Count - 1; i >= 0; i--)
        {
            var index = i;
            var button = new TKCode.Arc.Button
            {
                Content = buttons[i],
                Variant = i == defaultIndex ? ButtonVariant.Primary : ButtonVariant.Secondary,
                Size = ControlSize.Medium,
            };
            button.Click += (_, _) => Finish(index);
            bar.Children.Add(button);
            if (i == defaultIndex) Opened += (_, _) => button.Focus();
        }
        var card = new Border
        {
            Child = new StackPanel { Children = { top, bar } },
            Padding = new Thickness(22, 22, 22, 18),
            Margin = new Thickness(ShadowRoom),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 10 32 0 #40000000"),
        };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderStrong);
        Content = card;

        // No title bar to drag: the card moves the window, except where a press selects text or hits a button.
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, card); v = v.GetVisualParent())
                if (v is TKCode.Arc.Button or SelectableTextBlock or ScrollViewer) return;
            BeginMoveDrag(e);
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(_cancelIndex);
            }
        };
        Closed += (_, _) => _result.TrySetResult(_cancelIndex);
    }

    private void Finish(int index)
    {
        _result.TrySetResult(index);
        Close();
    }

    /// <summary>Shows the box and returns the index of the button chosen (<paramref name="cancelIndex"/> when dismissed). Call on the UI thread.</summary>
    public static Task<int> ShowAsync(MessageBoxKind kind, string message, string? detail, IReadOnlyList<string> buttons, int defaultIndex = 0, int? cancelIndex = null)
    {
        // macOS: a menu-bar-only app is not frontmost, so its window would open behind other apps (Electron's bringAppToFront).
        if (OperatingSystem.IsMacOS()) MacApp.Activate();
        var box = new MessageBox(kind, message, detail, buttons, defaultIndex, cancelIndex ?? buttons.Count - 1);
        box.Show();
        box.Activate();
        return box._result.Task;
    }
}
