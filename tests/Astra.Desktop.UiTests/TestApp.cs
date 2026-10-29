using Astra.Desktop;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using TKCode.Arc;
using TKCode.Arc.Fonts;

[assembly: AvaloniaTestApplication(typeof(Astra.Desktop.UiTests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Astra.Desktop.UiTests;

/// <summary>
/// Headless Skia host for the desktop app: the same builder chain as <c>Program.BuildAvaloniaApp</c> (Inter + Arc fonts,
/// AtomUI desktop controls, Arc, charts, blocks, the app's own theme service) with Avalonia's headless platform in place of
/// the OS one (plan avalonia-client §11, patterned on TKCode.Arc's tools/docshots).
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // The app reads and writes desktop-prefs.json under the data dir: never the developer's real ~/.astra.
        var home = Path.Combine(Path.GetTempPath(), $"astra-ui-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("ASTRA_HOME", home);
        // Deterministic prefs for every run: light theme, the web's default blue accent, zh.
        var prefs = AppServices.Current.Prefs;
        prefs.Theme = ThemeSource.Light;
        prefs.Accent = "#007aff";
        prefs.LastSystemAccent = "#007aff";
        prefs.SidebarCollapsed = false;
        Loc.Language = "zh";

        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .WithArcFonts()
            .UseArc(arc =>
            {
                arc.UseArcControls();
                arc.UseArcAdvancedCharts();
                arc.UseArcBlocks();
                AppServices.Current.Theme.Configure(arc);
            })
            .AfterSetup(_ => AppServices.Current.Theme.Install());
    }
}
