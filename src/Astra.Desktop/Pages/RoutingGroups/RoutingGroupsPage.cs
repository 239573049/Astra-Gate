using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.RoutingGroups;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.RoutingGroups;

/// <summary>
/// The Routing groups page (web <c>RoutingGroupsPage</c>): the model owns the queries (the groups, and their live status polled every 10 s
/// while the page shows) and the sheet asked for; the view draws.
/// </summary>
public sealed class RoutingGroupsPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public RoutingGroupsPage(PageContext context, string? param) : base(AppRoute.RoutingGroups, param, context)
    {
        Api = new RoutingGroupsApi(context.Api, context.Queries);
        Observe(Api.Groups);
        Observe(Api.Status);
        Observe(Api.Models);
        var add = Ui.Button(Loc.T("routingGroups.add"), () => EditRequested?.Invoke(null), ButtonVariant.Primary, LucideIconKind.Plus);
        SetHeaderActions(new ActionGroup(8, add));
    }

    /// <summary>The groups' API (queries and commands).</summary>
    public RoutingGroupsApi Api { get; }

    /// <summary>The feedback surface (toasts, confirmations).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <summary>The user asked for the editor: a group to edit, or null for a new one.</summary>
    public event Action<RoutingGroupDto?>? EditRequested;

    /// <summary>Opens the editor of <paramref name="group"/>.</summary>
    public void BeginEdit(RoutingGroupDto group) => EditRequested?.Invoke(group);

    /// <summary>The clock the status times are read against.</summary>
    public DateTimeOffset Now => Context!.Clock.Now;

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("routingGroups.subtitle");

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
