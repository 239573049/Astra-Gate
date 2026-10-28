using Astra.Desktop.Pages.Overview;
using Astra.Desktop.Pages.Providers;
using Astra.Desktop.Pages.Tokens;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The shell's page host follows every navigation. Regression: <c>PageHost</c> used to bind <c>Content</c> to <c>Navigator.Current</c>; once the
/// presenter showed a page through its template it took the page view model as DataContext, the binding then resolved against the page, and the
/// first page's view stayed on screen for good while the header still changed.
/// </summary>
public class ShellNavigationTests
{
    [AvaloniaFact]
    public void The_page_host_shows_each_page_navigated_to()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        shell.Navigator.Navigate(AppRoute.Tokens);
        var window = new MainWindow(shell);
        try
        {
            window.Show();
            SceneCapture.Pump(10);
            var host = ((ShellView)window.Content!).FindControl<ContentPresenter>("PageHost")!;
            Assert.IsType<TokensView>(host.Child);

            shell.Navigator.Navigate(AppRoute.Overview);
            SceneCapture.Pump(10);
            Assert.Same(shell.Navigator.Current, host.Content);
            Assert.IsType<OverviewView>(host.Child);

            shell.Navigator.Navigate(AppRoute.Providers);
            SceneCapture.Pump(10);
            Assert.Same(shell.Navigator.Current, host.Content);
            Assert.IsType<ProvidersView>(host.Child);

            // back to the first again (a page pushed and popped as well)
            shell.Navigator.Navigate(AppRoute.Tokens);
            SceneCapture.Pump(10);
            Assert.IsType<TokensView>(host.Child);
        }
        finally
        {
            window.Close();
        }
    }
}
