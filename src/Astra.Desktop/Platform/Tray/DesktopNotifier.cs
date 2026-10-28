using System.Diagnostics;
using System.Runtime.Versioning;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Platform.Tray;

/// <summary>
/// A quiet, one-off desktop notification (Electron's <c>new Notification({ title, body, silent: true }).show()</c> in desktop/src/tray.ts
/// <c>notify</c>), used for the one-time "still running in the tray" notice when the main window is first closed.
/// <para>
/// Avalonia has no OS-notification API, so each platform gets the cheapest AOT-safe native route, and an in-app equivalent covers the rest:
/// <list type="bullet">
/// <item>macOS, packaged (a bundle identifier exists): <c>NSUserNotificationCenter</c> through the same blittable libobjc calls the menu-bar
/// item uses — the route Electron itself takes for an unauthorized, silent notification. Without a bundle (a dev run) the center is nil.</item>
/// <item>Linux: <c>notify-send</c> (libnotify, i.e. the freedesktop Notifications D-Bus service Electron talks to) when it is on PATH.</item>
/// <item>Windows and every fallback: <see cref="NoticeWindow"/>, a small frameless always-on-top card in the corner where the OS shows its own
/// notifications (bottom right; top right on macOS), closing itself after a few seconds. Windows toasts need an AppUserModelID registered by a
/// Start-menu shortcut plus WinRT activation, and the tray balloon needs the tray icon's own HWND, which Avalonia's <c>TrayIcon</c> does not expose.</item>
/// </list>
/// </para>
/// </summary>
internal static class DesktopNotifier
{
    /// <summary>
    /// Shows the notification; never throws (a notice that cannot be shown is just skipped). <paramref name="onClick"/> runs when the
    /// in-app notice is clicked (Electron's notification 'click'); the OS routes do not report clicks back.
    /// </summary>
    public static void Show(string title, string body, Action? onClick = null)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && MacNotify(title, body)) return;
            if (OperatingSystem.IsLinux() && NotifySend(title, body)) return;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Fall through to the in-app notice.
        }
        Dispatcher.UIThread.Post(() => NoticeWindow.Show(title, body, onClick));
    }

    [SupportedOSPlatform("macos")]
    private static bool MacNotify(string title, string body)
    {
        var bundle = ObjC.Send(ObjC.Class("NSBundle"), ObjC.Sel("mainBundle"));
        if (bundle == IntPtr.Zero || ObjC.Send(bundle, ObjC.Sel("bundleIdentifier")) == IntPtr.Zero) return false;
        var centerClass = ObjC.Class("NSUserNotificationCenter");
        if (centerClass == IntPtr.Zero) return false;
        var center = ObjC.Send(centerClass, ObjC.Sel("defaultUserNotificationCenter"));
        if (center == IntPtr.Zero) return false;
        var note = ObjC.New("NSUserNotification");
        try
        {
            ObjC.Send(note, ObjC.Sel("setTitle:"), ObjC.Str(title));
            ObjC.Send(note, ObjC.Sel("setInformativeText:"), ObjC.Str(body));
            // silent: true — no soundName.
            ObjC.Send(center, ObjC.Sel("deliverNotification:"), note);
        }
        finally
        {
            ObjC.Release(note);
        }
        return true;
    }

    private static bool NotifySend(string title, string body)
    {
        var exe = FindOnPath("notify-send");
        if (exe is null) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--app-name=Astra", "--urgency=low", title, body }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        return p is not null;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// The in-app notification card (see <see cref="DesktopNotifier"/>): 340 px wide, the tray panel's surface and border tokens, an icon, the
/// title and the message; a click or <see cref="Lifetime"/> closes it. It never takes focus (<c>ShowActivated = false</c>) — like an OS
/// notification it must not steal the keyboard from whatever the user switched to after closing the window.
/// </summary>
internal sealed class NoticeWindow : Window
{
    /// <summary>How long the card stays up.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    private const double CardWidth = 340;
    private const double Margin16 = 16;

    private NoticeWindow(string title, string body, Action? onClick)
    {
        Title = title;
        Width = CardWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var icon = TrayUi.Icon(LucideIconKind.Info, 18, ArcDesignTokenKind.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var heading = TrayUi.Text(title, 13, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        var message = TrayUi.Text(body, 12.5, ArcDesignTokenKind.TextSecondary);
        message.TextTrimming = TextTrimming.None;
        message.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Spacing = 4, Children = { heading, message } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var card = new Border
        {
            Child = row,
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6),
            BoxShadow = BoxShadows.Parse("0 6 24 0 #33000000"),
        };
        TrayUi.Fill(card, ArcDesignTokenKind.SurfaceRaised);
        card.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        Content = card;
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Close();
            onClick?.Invoke();
        };
    }

    /// <summary>Shows a card in the notification corner of the primary screen.</summary>
    public static void Show(string title, string body, Action? onClick = null)
    {
        var w = new NoticeWindow(title, body, onClick);
        if (onClick is not null) w.Cursor = new Cursor(StandardCursorType.Hand);
        w.Opened += (_, _) => w.Place();
        w.Show();
        DispatcherTimer.RunOnce(() => w.Close(), Lifetime);
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var margin = (int)(Margin16 * scale);
        var x = area.Right - width - margin;
        // macOS shows notifications top right, under the menu bar; Windows / Linux trays sit at the bottom.
        var y = OperatingSystem.IsMacOS() ? area.Y + margin : area.Bottom - height - margin;
        Position = new PixelPoint(x, y);
    }
}
