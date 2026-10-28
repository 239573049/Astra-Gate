using System.Runtime.CompilerServices;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Error toasts as the web shows them (web/src/components/ui/overlays.tsx <c>FeedbackProvider</c>): not an Arc toast but a dismissible
/// danger <see cref="Alert"/> titled <c>common.error</c> with the message as its body, <c>w-[min(100%,26rem)]</c> at the bottom center
/// (<c>bottom-5</c>, <c>px-4</c>), selectable, gone after 8 s; at most three at a time (the web keeps the last three toasts). Success and
/// info toasts stay Arc toasts (<see cref="ToastPresenter"/>).
/// </summary>
public static class ErrorToasts
{
    /// <summary>How long an error stays (web: <c>setTimeout(dismiss, 8000)</c>).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    private const int MaxShown = 3;
    private static readonly ConditionalWeakTable<TopLevel, StackPanel> Stacks = new();

    /// <summary>Shows an error alert in <paramref name="owner"/>'s window. <paramref name="description"/> adds a second line under the message.</summary>
    public static Alert? Show(Visual owner, string message, string? description = null)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top || Column(owner, top) is not { } column) return null;
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(Line(message));
        if (!string.IsNullOrEmpty(description)) body.Children.Add(Line(description));
        var alert = new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), IsDismissible = true, IsOpen = true, Content = body };
        alert.Dismissed += (_, _) => column.Children.Remove(alert);
        while (column.Children.Count >= MaxShown) column.Children.RemoveAt(0);
        column.Children.Add(alert);
        DispatcherTimer.RunOnce(() => column.Children.Remove(alert), Lifetime);
        return alert;
    }

    private static Control Line(string text)
    {
        // "selectable" in the web: the message can be copied.
        var block = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        block.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeSM);
        return block;
    }

    private static StackPanel? Column(Visual owner, TopLevel top)
    {
        if (Stacks.TryGetValue(top, out var existing)) return existing;
        if ((OverlayLayer.GetOverlayLayer(owner) ?? OverlayLayer.GetOverlayLayer(top)) is not { } overlay) return null;
        var column = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 416,
            Margin = new Thickness(16, 0, 16, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        // The overlay is a canvas: a host sized to it lets the column hang from the window's bottom edge (as Arc's ToastHost does).
        // No background, so only the alerts take clicks.
        var host = new Panel { Children = { column } };
        void Fit(Rect b)
        {
            host.Width = b.Width > 0 ? b.Width : top.ClientSize.Width;
            host.Height = b.Height > 0 ? b.Height : top.ClientSize.Height;
            column.Width = Math.Max(0, Math.Min(416, host.Width - 32));
        }
        Fit(overlay.Bounds);
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty && e.NewValue is Rect bounds) Fit(bounds);
        };
        overlay.Children.Add(host);
        Stacks.Add(top, column);
        return column;
    }
}
