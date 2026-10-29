using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Avalonia's own menus have no control theme under Arc (its menus are its own controls): the tray icon's right-click menu, a
/// <c>MenuFlyoutPresenter</c> in a window of its own, measured 0 x 0 and never showed. KitStyles carries minimal themes for them.
/// </summary>
public class MenuThemeTests
{
    [AvaloniaTheory]
    [InlineData(typeof(MenuFlyoutPresenter))]
    [InlineData(typeof(ContextMenu))]
    [InlineData(typeof(MenuItem))]
    [InlineData(typeof(Separator))]
    public void Avalonia_menu_controls_have_a_theme(Type control)
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource(control, app.ActualThemeVariant, out var theme) && theme is Avalonia.Styling.ControlTheme);
    }

    [AvaloniaFact]
    public void The_tray_menu_presenter_is_drawn_with_its_items()
    {
        var presenter = new MenuFlyoutPresenter();
        presenter.Items.Add(new MenuItem { Header = "Open Astra" });
        presenter.Items.Add(new Separator());
        presenter.Items.Add(new MenuItem { Header = "Quit", IsChecked = true, ToggleType = MenuItemToggleType.CheckBox });
        var window = new Window { Content = presenter, Width = 300, Height = 160, WindowDecorations = WindowDecorations.None };
        window.Show();
        try
        {
            for (var i = 0; i < 5; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            Assert.NotNull(presenter.Template);
            Assert.True(presenter.Bounds.Width > 100 && presenter.Bounds.Height > 40, $"presenter measured {presenter.Bounds}");
            Assert.NotNull(window.CaptureRenderedFrame());
        }
        finally
        {
            window.Close();
        }
    }
}
