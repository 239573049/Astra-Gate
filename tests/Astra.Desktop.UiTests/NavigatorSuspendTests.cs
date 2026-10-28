using Astra.Desktop.Shell;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The window in the tray: the shown page stops observing its queries (no polling while nobody looks) and picks them up again when the
/// window comes back; a page navigated to meanwhile (a tray menu command) waits for the window as well.
/// </summary>
public class NavigatorSuspendTests
{
    [AvaloniaFact]
    public void A_suspended_navigator_keeps_its_page_inactive_until_resumed()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        var tokens = shell.Context.Tokens.Tokens;
        shell.Navigator.Navigate(AppRoute.Overview);
        Assert.True(tokens.IsObserved);

        shell.Navigator.SetSuspended(true);
        Assert.False(tokens.IsObserved);

        shell.Navigator.Navigate(AppRoute.Tokens);
        Assert.False(tokens.IsObserved);

        shell.Navigator.SetSuspended(false);
        Assert.True(tokens.IsObserved);
        Assert.Equal(AppRoute.Tokens, shell.Navigator.Current.Route);
    }
}
