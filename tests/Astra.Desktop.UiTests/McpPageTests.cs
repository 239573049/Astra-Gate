using Astra.Desktop.Core.I18n;
using Astra.Desktop.Pages.Mcp;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The MCP gateway page (plan mcp-gateway) drawn from the fixture set: one headless build, no window.</summary>
public class McpPageTests
{
    /// <summary>What is drawn: the view in a window (a row's label lives in its template, which only exists once the control is shown).</summary>
    private static List<string> TextsOf(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    [AvaloniaFact]
    public void The_page_loads_its_three_queries_and_asks_for_nothing_else()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Mcp);
        var page = Assert.IsType<McpPage>(shell.Navigator.Current);
        Assert.Equal("MCP 网关", page.Title);

        var settled = page.WhenSettledAsync();
        SceneCapture.Await(Task.WhenAny(settled, Task.Delay(10000)), TimeSpan.FromSeconds(15));
        Assert.True(settled.IsCompleted);
        Assert.True(page.Api.Settings.Data!.Enabled);
        Assert.Equal(["github", "docs", "notion"], page.Api.Servers.Data!.Select(s => s.Slug));
        Assert.Equal(4, page.Api.Calls.Data!.Count);
        Assert.Equal(["claude-code", "codex"], page.ClientApi.Entries.Data!.Select(e => e.Kind));
        Assert.Equal(["filesystem"], page.ClientApi.Servers.Data!.Select(e => e.Name));
        Assert.Empty(fx.Unmatched);
        // The OAuth state keeps the name the web uses: the contract pins it, not the C# property name.
        Assert.True(page.Api.Servers.Data![0].OAuth!.Authorized);
        Assert.False(page.Api.Servers.Data![2].OAuth!.Authorized);
    }

    [AvaloniaFact]
    public void The_view_shows_each_server_with_its_status_and_the_recent_calls()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Mcp);
        var page = Assert.IsType<McpPage>(shell.Navigator.Current);
        SceneCapture.Await(Task.WhenAny(page.WhenSettledAsync(), Task.Delay(10000)), TimeSpan.FromSeconds(15));

        var view = new McpView(page);
        var window = new Window { Content = view, Width = 1100, Height = SceneCapture.Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = TextsOf(window);
        Assert.Contains("http://127.0.0.1:17321/mcp", texts);                  // the address clients use
        foreach (var name in new[] { "GitHub", "Docs search", "Notion" }) Assert.Contains(name, texts);
        Assert.Contains(Loc.T("mcp.status.ok"), texts.Concat(BadgeTexts(window)));
        Assert.Contains(Loc.T("mcp.status.needs_auth"), texts.Concat(BadgeTexts(window)));
        Assert.Contains("github/search_issues", texts);                        // the calls: server and the upstream's own tool name
        Assert.Contains("docs/search", texts);
        // "apply to clients" and the user's own servers
        Assert.Contains(Loc.T("mcp.clients.title"), texts);
        Assert.Contains("Claude Code", texts);
        Assert.Contains(Loc.T("mcp.clients.usesToken", ("name", "team-shared")), texts);
        Assert.Contains(Loc.T("mcp.local.title"), texts);
        Assert.Contains("filesystem", texts);
        Assert.Contains("npx -y @modelcontextprotocol/server-filesystem /Users/dev/docs · " + Loc.T("mcp.local.envCount", ("count", 1)), texts);
        Assert.Contains(Loc.T("mcp.local.rewrite"), window.GetVisualDescendants().OfType<TKCode.Arc.Button>().Select(b => b.Content?.ToString() ?? ""));
        window.Close();
    }

    private static IEnumerable<string> BadgeTexts(Control root) =>
        root.GetVisualDescendants().OfType<TKCode.Arc.Badge>().Select(b => b.Content?.ToString() ?? "");
}
