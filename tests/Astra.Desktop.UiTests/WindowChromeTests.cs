using Astra.Desktop.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Astra.Desktop.UiTests;

public class WindowChromeTests
{
    [Fact]
    public void The_three_platforms_get_the_electron_shells_chrome()
    {
        // desktop/src/shared/chrome.ts windowChromeOptions: macOS hiddenInset with the traffic lights in the sidebar, Windows hidden +
        // overlay (the shell draws the caption buttons, the toolbar keeps their width free), Linux the system title bar and no drag region.
        var mac = ChromeSpec.For(DesktopOs.MacOS, dark: false);
        Assert.True(mac.HidesManagedTitleBar);
        Assert.True(mac.NativeTrafficLights);
        Assert.True(mac.HasAppDragRegion);
        Assert.Equal(78, mac.SidebarRail);
        Assert.Equal(0, mac.CaptionOverlayWidth);

        var win = ChromeSpec.For(DesktopOs.Windows, dark: true);
        Assert.True(win.HidesManagedTitleBar);
        Assert.False(win.NativeTrafficLights);
        Assert.True(win.HasAppDragRegion);
        Assert.Equal(68, win.SidebarRail);
        Assert.Equal(ChromeSpec.CaptionButtonsReserve, win.CaptionOverlayWidth);
        Assert.True(3 * ChromeSpec.CaptionButtonWidth <= win.CaptionOverlayWidth);
        Assert.Equal("#1C1C1E", win.Background);

        var linux = ChromeSpec.For(DesktopOs.Linux, dark: false);
        Assert.False(linux.HidesManagedTitleBar);
        Assert.False(linux.HasAppDragRegion);
        Assert.Equal("#F2F2F7", linux.Background);
    }

    [AvaloniaFact]
    public void The_windows_caption_buttons_carry_their_roles_follow_the_window_state_and_drive_it()
    {
        var window = new AtomUI.Desktop.Controls.Window { Width = 900, Height = 600 };
        var buttons = new CaptionButtons(window);
        window.Content = buttons;
        window.Show();
        try
        {
            Assert.Equal(
                [WindowDecorationsElementRole.MinimizeButton, WindowDecorationsElementRole.MaximizeButton, WindowDecorationsElementRole.CloseButton],
                buttons.Buttons.Select(WindowDecorationProperties.GetElementRole));
            Assert.All(buttons.Buttons, b => Assert.Equal(ChromeSpec.CaptionButtonWidth, b.Width));
            Assert.All(buttons.Buttons, b => Assert.Equal(ChromeSpec.Toolbar, b.Height));
            Assert.False(buttons.ShowsRestore);

            buttons.Invoke(CaptionButtons.Kind.Maximize);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.True(buttons.ShowsRestore);
            buttons.Invoke(CaptionButtons.Kind.Maximize);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.False(buttons.ShowsRestore);

            // Electron hides the overlay in full screen.
            window.WindowState = WindowState.FullScreen;
            Assert.False(buttons.IsVisible);
            window.WindowState = WindowState.Normal;
            Assert.True(buttons.IsVisible);

            var closed = false;
            window.Closed += (_, _) => closed = true;
            buttons.Invoke(CaptionButtons.Kind.Close);
            Assert.True(closed);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }
}
