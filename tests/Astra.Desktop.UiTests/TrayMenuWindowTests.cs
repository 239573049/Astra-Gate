using System.Reflection;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Platform.Tray;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The Windows tray icon's right-click menu (issue #18). Avalonia.Win32 draws it itself: <c>TrayIconImpl.OnRightClicked</c> shows a
/// <c>TrayPopupRoot</c> window (SizeToContent) holding a <c>TrayIconMenuFlyoutPresenter</c> over the <see cref="NativeMenu"/>'s items. Both
/// types are private, but they are plain managed controls, so the headless platform builds Avalonia's own window here exactly as the right
/// click does (no Win32 call involved), under the app's real theme, with the menu the app builds for Windows. Up to 0.13.1 Avalonia's menu
/// controls had no theme under Arc and this window measured 0 x 0: the right click showed nothing.
/// </summary>
public class TrayMenuWindowTests
{
    private static readonly Assembly Win32 = Assembly.Load("Avalonia.Win32");

    private static List<TrayMenuItem> WindowsMenu()
        => TrayMenuModel.Build(new TrayState(true, 17321, false, [], [], null, null, true), TrayMenuLabels.For("en"), "win32", "1.2.3", MenuLabels.For("en"));

    /// <summary><c>TrayIconImpl.OnRightClicked</c> (Avalonia 12.1.4), minus the cursor lookup.</summary>
    private static (Window Root, MenuFlyoutPresenter Presenter) RightClick(NativeMenu menu)
    {
        var presenter = (MenuFlyoutPresenter)Activator.CreateInstance(Win32.GetType("Avalonia.Win32.TrayIconImpl+TrayIconMenuFlyoutPresenter", true)!, true)!;
        presenter.ItemsSource = menu.Items;
        var root = (Window)Activator.CreateInstance(Win32.GetType(TrayMenuWatch.MenuWindowType, true)!, true)!;
        root.Name = "AvaloniaTrayPopupRoot_Astra";
        root.WindowDecorations = WindowDecorations.None;
        root.SizeToContent = SizeToContent.WidthAndHeight;
        root.Background = null;
        root.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        root.Content = presenter;
        root.Position = new PixelPoint(400, 300);
        root.Show();
        Pump();
        return (root, presenter);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Takes the KitStyles menu theme away for the duration of <paramref name="body"/> (what 0.13.1 shipped with).</summary>
    private static void WithoutMenuTheme(Action body)
    {
        var kit = Application.Current!.Styles.OfType<KitStyles>().Single();
        var theme = kit.Resources[typeof(MenuFlyoutPresenter)];
        kit.Resources.Remove(typeof(MenuFlyoutPresenter));
        try
        {
            body();
        }
        finally
        {
            kit.Resources[typeof(MenuFlyoutPresenter)] = theme;
        }
    }

    [AvaloniaFact]
    public void Avalonias_tray_menu_window_shows_every_item_of_the_windows_menu()
    {
        var model = WindowsMenu();
        var chosen = new List<TrayMenuAction>();
        var menu = AvaloniaTrayIcon.Build(model, chosen.Add);
        var (root, presenter) = RightClick(menu);
        try
        {
            Assert.True(TrayMenuWatch.IsMenuWindow(root));
            Assert.True(TrayMenuWatch.HasContent(root));
            Assert.True(root.ClientSize.Width > 150 && root.ClientSize.Height > 300, $"tray menu window is {root.ClientSize}");
            var items = presenter.GetRealizedContainers().ToList();
            Assert.Equal(model.Count, items.Count);
            Assert.All(items, c => Assert.True(c is TemplatedControl { Template: not null } && c.Bounds.Width > 100, $"{c.GetType().Name} {c.Bounds}"));
            var quit = Assert.IsAssignableFrom<MenuItem>(items[^1]);
            Assert.Equal("Quit Astra", quit.Header);

            // Avalonia's container forwards its click to the NativeMenuItem, whose handler is the tray's action.
            quit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(TrayMenuVerb.Quit, Assert.Single(chosen).Verb);
        }
        finally
        {
            root.Close();
        }
    }

    [AvaloniaFact]
    public void Without_a_menu_theme_the_window_is_empty_and_the_watch_hands_over_to_the_fallback()
    {
        var fallbacks = 0;
        TrayMenuWatch.Install(() => fallbacks++);
        WithoutMenuTheme(() =>
        {
            var (root, _) = RightClick(AvaloniaTrayIcon.Build(WindowsMenu(), _ => { }));
            Assert.False(TrayMenuWatch.HasContent(root));
            Pump();
            Assert.Equal(1, fallbacks);
            Assert.False(root.IsVisible);
        });

        // Themed, the watch leaves Avalonia's menu alone.
        var (themed, _) = RightClick(AvaloniaTrayIcon.Build(WindowsMenu(), _ => { }));
        try
        {
            Pump();
            Assert.Equal(1, fallbacks);
            Assert.True(themed.IsVisible);
        }
        finally
        {
            themed.Close();
        }
    }

    [AvaloniaFact]
    public void The_watch_ignores_other_windows()
    {
        var window = new Window { SizeToContent = SizeToContent.WidthAndHeight };
        Assert.False(TrayMenuWatch.IsMenuWindow(window));
    }
}
