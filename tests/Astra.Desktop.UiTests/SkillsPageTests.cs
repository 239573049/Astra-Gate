using Astra.Desktop.Core.I18n;
using Astra.Desktop.Pages.Skills;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The Skills page (plan resources-library) drawn from the fixture set: one headless build, no window.</summary>
public class SkillsPageTests
{
    private static List<string> TextsOf(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    private static IEnumerable<string> BadgeTexts(Control root) =>
        root.GetVisualDescendants().OfType<TKCode.Arc.Badge>().Select(b => b.Content?.ToString() ?? "");

    private static (SkillsPage Page, Window Window) Show(FixtureSet? fx = null, Action<SkillsPage>? before = null)
    {
        fx ??= Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Skills);
        var page = Assert.IsType<SkillsPage>(shell.Navigator.Current);
        SceneCapture.Await(Task.WhenAny(page.WhenSettledAsync(), Task.Delay(10000)), TimeSpan.FromSeconds(15));
        before?.Invoke(page);
        var window = new Window { Content = new SkillsView(page), Width = 1100, Height = SceneCapture.Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, window);
    }

    [AvaloniaFact]
    public void The_page_loads_its_one_query_and_asks_for_nothing_else()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Skills);
        var page = Assert.IsType<SkillsPage>(shell.Navigator.Current);
        Assert.Equal("Skills", page.Title);

        var settled = page.WhenSettledAsync();
        SceneCapture.Await(Task.WhenAny(settled, Task.Delay(10000)), TimeSpan.FromSeconds(15));
        Assert.True(settled.IsCompleted);
        Assert.Equal(["brand-writer", "code-review-zh", "db-migrate", "pdf-tools"], page.Api.Library.Data!.Skills.Select(s => s.Name).Order());
        Assert.Equal(4, page.Api.Library.Data.Found.Count);
        Assert.Empty(fx.Unmatched);
    }

    [AvaloniaFact]
    public void The_managed_tab_shows_each_skill_with_a_chip_per_client_and_the_banner_for_unmanaged_ones()
    {
        var (_, window) = Show();
        var texts = TextsOf(window);
        foreach (var name in new[] { "pdf-tools", "code-review-zh", "db-migrate", "brand-writer" }) Assert.Contains(name, texts);
        var badges = BadgeTexts(window).ToList();
        Assert.Contains("Claude Code", badges);                                          // on, own link
        Assert.Contains("Codex · " + Loc.T("skills.badge.shared"), badges);              // visible through the shared directory
        Assert.Contains(badges, b => b.StartsWith("Copilot CLI · " + Loc.T("skills.badge.copy"), StringComparison.Ordinal)); // a copy, edited
        Assert.Contains(Loc.T("skills.copyModified"), badges);
        Assert.Contains(texts, t => t.Contains(Loc.T("skills.foundBanner", ("n", 3)), StringComparison.Ordinal)); // three real folders are offered; the external link is not
        window.Close();
    }

    [AvaloniaFact]
    public void The_found_tab_lists_what_can_be_migrated_and_what_is_only_shown()
    {
        var (_, window) = Show(before: p => p.Tab = "found");
        var texts = TextsOf(window);
        foreach (var name in new[] { "review-helper", "sql-style", "frontend-design", "my-dotfiles-skill" }) Assert.Contains(name, texts);
        // The skills-CLI one is not ticked by default, so 2 of 3 real folders are.
        Assert.Contains(Loc.T("skills.found.selected", ("n", 2), ("total", 3)), texts);
        Assert.Contains(Loc.T("skills.found.kind.external"), BadgeTexts(window));
        window.Close();
    }

    [AvaloniaFact]
    public void An_empty_library_says_so_and_offers_nothing_to_search()
    {
        var fx = Scenes.Fixtures().Override("/api/library/skills",
            "{\"targets\":[],\"skills\":[],\"found\":[],\"libraryDir\":\"/h/.astra/library/skills\",\"sharedDir\":\"/h/.agents/skills\",\"canCreate\":true}");
        var (_, window) = Show(fx);
        Assert.Contains(Loc.T("skills.empty.title"), TextsOf(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Resources_group_holds_mcp_and_skills_in_the_sidebar()
    {
        var group = NavTable.Groups.Single(g => g.TitleKey == "nav.group.resources");
        Assert.Equal([AppRoute.Mcp, AppRoute.Skills], group.Items.Select(i => i.Route));
        Assert.DoesNotContain(NavTable.Groups.Single(g => g.TitleKey == "nav.group.config").Items, i => i.Route is AppRoute.Mcp or AppRoute.Skills);
    }
}
