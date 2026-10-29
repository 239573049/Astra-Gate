using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
namespace Astra.Desktop.UiTests;
/// <summary>The shell shows the current page's header actions (regression: the bound presenter stayed empty, so the provider detail page had no test / more menu).</summary>
public class ShellHeaderActionsTests
{
    [AvaloniaFact]
    public void A_pushed_page_shows_its_header_actions_in_the_toolbar()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        shell.Navigator.Navigate(AppRoute.Providers);
        var window = new MainWindow(shell);
        try
        {
            window.Show();
            SceneCapture.Pump(10);
            shell.Navigator.Push(AppRoute.ProviderDetail, "prov_openai");
            SceneCapture.Pump(30);
            System.Threading.Thread.Sleep(500);
            SceneCapture.Pump(30);
            var host = ((ShellView)window.Content!).FindControl<ContentPresenter>("HeaderActionsHost")!;
            Assert.NotNull(host.Child);
        }
        finally { window.Close(); }
    }
}
