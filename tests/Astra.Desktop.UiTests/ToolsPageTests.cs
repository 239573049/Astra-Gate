using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Tools;
using Astra.Desktop.Pages.Tools;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The tools page (linked update 34) drawn from the fixture set: one headless build, no window of the shell.</summary>
public class ToolsPageTests
{
    private static List<string> TextsOf(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    [AvaloniaFact]
    public void The_page_loads_the_settings_and_the_tool_list_and_asks_for_nothing_else()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Tools);
        var page = Assert.IsType<ToolsPage>(shell.Navigator.Current);
        var settled = page.WhenSettledAsync();
        SceneCapture.Await(Task.WhenAny(settled, Task.Delay(10000)), TimeSpan.FromSeconds(15));
        Assert.True(settled.IsCompleted);
        Assert.Equal(["tavily", "searxng"], page.Api.Settings.Data!.Search.Apis.Select(a => a.Vendor));
        Assert.Equal(["web_search", "web_fetch", "generate_image", "demo.stamp"], page.Api.Tools.Data!.Select(t => t.Name));
        Assert.Empty(fx.Unmatched);
    }

    [AvaloniaFact]
    public void The_view_shows_the_apis_with_masked_keys_the_rest_of_a_refused_key_and_the_plugin_tool()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Tools);
        var page = Assert.IsType<ToolsPage>(shell.Navigator.Current);
        SceneCapture.Await(Task.WhenAny(page.WhenSettledAsync(), Task.Delay(10000)), TimeSpan.FromSeconds(15));
        var window = new Window { Content = new ToolsView(page), Width = 1100, Height = SceneCapture.Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = TextsOf(window);
        foreach (var name in new[] { "Tavily", "SearXNG", "tvl…y-1", "tvl…y-2", "demo.stamp", "http://127.0.0.1:17321/mcp/astra" }) Assert.Contains(name, texts);
        Assert.Contains(Loc.T("tools.onBehalf.title"), texts);
        Assert.Contains(Loc.T("tools.fetch.title"), texts);
        Assert.Contains(Loc.T("tools.image.title"), texts);
        var badges = window.GetVisualDescendants().OfType<TKCode.Arc.Badge>().Select(b => b.Content?.ToString() ?? "").ToList();
        Assert.Contains(Loc.T("tools.search.rest.rate_limited"), badges);
        window.Close();
    }

    [Fact]
    public void Arrows_move_an_id_one_step_and_stop_at_the_ends()
    {
        Assert.Equal(["b", "a", "c"], ToolsApi.Moved(["a", "b", "c"], "b", -1));
        Assert.Equal(["a", "c", "b"], ToolsApi.Moved(["a", "b", "c"], "b", 1));
        Assert.Null(ToolsApi.Moved(["a", "b", "c"], "a", -1));
        Assert.Null(ToolsApi.Moved(["a", "b", "c"], "c", 1));
        Assert.Null(ToolsApi.Moved(["a"], "x", 1));
    }
}
