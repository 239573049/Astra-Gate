using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Skills;

/// <summary>
/// The Skills page (web <c>SkillsPage</c>): the model owns the query (polled while the page shows) and what must outlive a redraw — the tab,
/// the search text, the found Skills ticked for migration, the sheets asked for; the view draws.
/// </summary>
public sealed class SkillsPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public SkillsPage(PageContext context, string? param) : base(AppRoute.Skills, param, context)
    {
        Api = new SkillsApi(context.Api, context.Queries);
        Observe(Api.Library);
        var giveBack = Ui.Button(Loc.T("skills.returnAll"), () => ReturnAllRequested?.Invoke(), ButtonVariant.Secondary);
        var install = Ui.Button(Loc.T("skills.install"), () => InstallRequested?.Invoke(), ButtonVariant.Primary, LucideIconKind.Plus);
        SetHeaderActions(new ActionGroup(8, giveBack, install));
    }

    /// <summary>The Skills API (query and commands).</summary>
    public SkillsApi Api { get; }

    /// <summary>The feedback surface (toasts, confirmations).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <summary>The tab shown: <c>managed</c> or <c>found</c>.</summary>
    public string Tab { get; set; } = "managed";

    /// <summary>The search text of the managed list.</summary>
    public string Search { get; set; } = "";

    /// <summary>The found Skills ticked for migration; null until the user first touches it (then every one the skills CLI does not manage).</summary>
    public HashSet<string>? Selected { get; set; }

    /// <summary>The user asked for the install sheet.</summary>
    public event Action? InstallRequested;

    /// <summary>The user asked to give every Skill back.</summary>
    public event Action? ReturnAllRequested;

    /// <summary>The user asked for the migrate sheet for these found Skills.</summary>
    public event Action<IReadOnlyList<string>>? MigrateRequested;

    /// <summary>Opens the migrate sheet.</summary>
    public void BeginMigrate(IReadOnlyList<string> names) => MigrateRequested?.Invoke(names);

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("skills.subtitle");

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 2; i++)
        {
            await Context!.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }
}
