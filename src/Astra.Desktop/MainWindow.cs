using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;

namespace Astra.Desktop;

/// <summary>
/// Size, minimum size, remembered bounds and the macOS / Windows chrome are the Electron window's (desktop/src/main.ts,
/// desktop/src/shared/chrome.ts), applied by <see cref="WindowChrome"/> (plan §22); on Windows the content is wrapped with the
/// caption-buttons overlay (<see cref="WindowChrome.WithOverlay"/>).
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        Title = "Astra";
        Icon = new WindowIcon(ShellView.LoadLogo());
        // Before the shell view exists: it reads WindowChrome.Spec (rail width, drag region) while it is built.
        WindowChrome.Apply(this, AppServices.Current.Prefs);
        Content = WindowChrome.WithOverlay(this, new ShellView { DataContext = shell });
        DataContext = shell;
    }
}
