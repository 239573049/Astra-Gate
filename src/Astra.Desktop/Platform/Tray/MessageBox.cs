using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.Platform.Tray;

/// <summary>The kind of a <see cref="MessageBox"/> (Electron <c>showMessageBox</c>'s <c>type</c>): only the icon differs.</summary>
internal enum MessageBoxKind { Info, Warning, Error }

/// <summary>
/// A small modal message box of its own window (Electron's <c>dialog.showMessageBox</c> without a parent): the tray's update reminder, the
/// tray's check-for-updates result, the update-failed and API-mismatch warnings and tray action errors. Its own window, not a dialog in
/// the main one, because a tray app often has no visible window (login start, closed to the tray) — exactly when these appear.
/// Centered on the primary screen, always on top, out of the taskbar; Escape / the close box pick <c>cancelIndex</c>, Enter the default.
/// Built from Arc buttons and the tray panel's text styles; an AtomUI window like the others (plan avalonia-client §11).
/// </summary>
internal sealed class MessageBox : AtomWindow
{
    private readonly TaskCompletionSource<int> _result = new();
    private readonly int _cancelIndex;

    private MessageBox(MessageBoxKind kind, string message, string? detail, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        _cancelIndex = cancelIndex;
        Title = "Astra";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var (glyph, tone) = kind switch
        {
            MessageBoxKind.Error => (LucideIconKind.CircleAlert, ArcDesignTokenKind.Danger),
            MessageBoxKind.Warning => (LucideIconKind.TriangleAlert, ArcDesignTokenKind.Warning),
            _ => (LucideIconKind.Info, ArcDesignTokenKind.Accent),
        };
        var icon = TrayUi.Icon(glyph, 28, tone);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var head = TrayUi.Text(message, 14, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        head.TextTrimming = TextTrimming.None;
        head.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Spacing = 6, Children = { head } };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var body = new SelectableTextBlock { Text = detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            body.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            text.Children.Add(new ScrollViewer { MaxHeight = 320, Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        }
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        top.Children.Add(icon);
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
        var root = new StackPanel { Margin = new Thickness(20, 20, 20, 16), Children = { top, bar } };
        Content = root;

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
