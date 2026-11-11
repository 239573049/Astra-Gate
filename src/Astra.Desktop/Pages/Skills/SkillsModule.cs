using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Skills;

/// <summary>The Skills route (<c>/skills</c>): the library Astra keeps and which clients see each Skill (plan resources-library).</summary>
public sealed class SkillsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Skills, (ctx, param) => new SkillsPage(ctx, param), vm => new SkillsView((SkillsPage)vm));
}
