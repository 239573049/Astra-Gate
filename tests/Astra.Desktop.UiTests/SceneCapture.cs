using Astra.Desktop.Core.Api;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;

namespace Astra.Desktop.UiTests;

/// <summary>Renders one scene in one theme to a PNG, the way TKCode.Arc's docshots does (the window's own rendered frame).</summary>
public static class SceneCapture
{
    public const int Width = 1440, Height = 900;
    private const int SettleFrames = 120; // about 16 ms each

    /// <summary>Output folder: <c>ASTRA_VISUAL_OUT</c>, else <c>&lt;repo&gt;/artifacts/visual/avalonia</c>.</summary>
    public static string OutDir { get; } = Environment.GetEnvironmentVariable("ASTRA_VISUAL_OUT") ?? Path.Combine(RepoRoot(), "artifacts", "visual", "avalonia");

    /// <summary>Device scale (the web side's <c>--scale</c> / <c>ASTRA_VISUAL_SCALE</c>); 1 = 1440x900 pixels.</summary>
    public static double Scale { get; } = double.TryParse(Environment.GetEnvironmentVariable("ASTRA_VISUAL_SCALE"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;

    public static string Capture(string scene, bool dark)
    {
        ApplyTheme(dark);
        // One recording per scene (the light pass; the dark one is served the same answers).
        if (!dark) VisualRecorder.Begin(scene);
        var built = Scenes.All[scene]();
        var window = new Window
        {
            Content = built.View,
            Width = Width,
            Height = Height,
            CanResize = false,
        };
        // Captured values are the settled ones: no entrance animations or counters (web side: reducedMotion + a 1.8 s settle).
        ArcMotion.SetLevel(window, ArcMotionLevel.Off);
        window.SetRenderScaling(Scale);
        window.Show();
        try
        {
            // The page's own signal, not a sleep: the frame is taken after its queries answered.
            if (built.Settled is not null) Await(built.Settled(), TimeSpan.FromSeconds(20));
            Pump(SettleFrames);
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"scene {scene}: the window rendered no frame");
            Directory.CreateDirectory(OutDir);
            var path = Path.Combine(OutDir, $"{scene}__{(dark ? "dark" : "light")}.png");
            frame.Save(path, new PngBitmapEncoderOptions());
            if (!dark) VisualRecorder.End(scene, window.Content, RepoRoot());
            return path;
        }
        finally
        {
            VisualRecorder.Stop();
            TearDown(window);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Stops what the scene left running. A closed window does not deactivate its pages, so their polling queries, live streams and
    /// timers kept posting to the UI thread for the rest of the run; with enough scenes behind it a later <see cref="Await"/> never got
    /// out of <c>RunJobs</c> and the run hung in a different scene each time.
    /// </summary>
    private static void TearDown(Window window)
    {
        var pages = new HashSet<PageViewModel>();
        var clients = new HashSet<QueryClient>();
        void Collect(object? context)
        {
            switch (context)
            {
                case ShellViewModel shell:
                    pages.Add(shell.Navigator.Current);
                    clients.Add(shell.Context.Queries);
                    break;
                case PageViewModel page:
                    pages.Add(page);
                    if (page.Context is { } ctx) clients.Add(ctx.Queries);
                    break;
            }
        }
        Collect(window.Content is StyledElement root ? root.DataContext : null);
        foreach (var visual in window.GetVisualDescendants())
            if (visual is StyledElement element) Collect(element.DataContext);
        foreach (var page in pages) page.Deactivate();
        foreach (var client in clients) client.Dispose();
    }

    /// <summary>Through the app's own <see cref="ThemeService"/> (same tokens as production), with the fixed accent.</summary>
    private static void ApplyTheme(bool dark)
    {
        var theme = AppServices.Current.Theme;
        var task = theme.SetSourceAsync(dark ? ThemeSource.Dark : ThemeSource.Light);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        Pump(5);
    }

    /// <summary>Waits for <paramref name="task"/> while keeping the UI thread (and its dispatcher posts) alive.</summary>
    public static void Await(Task task, TimeSpan timeout)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (limit.Elapsed > timeout) throw new TimeoutException($"the scene did not settle within {timeout.TotalSeconds:0} s");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }

    public static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Astra.sln"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
