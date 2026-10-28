using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using AtomWindow = AtomUI.Desktop.Controls.Window;
using Avalonia.Controls;

namespace Astra.Desktop;

/// <summary>
/// Must be AtomUI's Window, not Avalonia's: Arc controls that open a popup (the data grid, selects, dialogs) throw
/// "Unable to create IPopupImpl and no overlay layer" on a plain Avalonia window (plan avalonia-client §11).
/// Size, minimum size, remembered bounds and the macOS / Windows chrome are the Electron window's (desktop/src/main.ts,
/// desktop/src/shared/chrome.ts), applied by <see cref="WindowChrome"/> (plan §22).
/// </summary>
public sealed class MainWindow : AtomWindow
{
    public MainWindow(ShellViewModel shell)
    {
        Title = "Astra";
        Icon = new WindowIcon(ShellView.LoadLogo());
        // Before the shell view exists: it reads WindowChrome.Spec (rail width, drag region) while it is built.
        WindowChrome.Apply(this, AppServices.Current.Prefs);
        Content = new ShellView { DataContext = shell };
        DataContext = shell;
    }
}
