using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tools;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Tools;

/// <summary>The tools page (web <c>ToolsPage</c>): the model owns the two queries; the view draws.</summary>
public sealed class ToolsPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public ToolsPage(PageContext context, string? param) : base(AppRoute.Tools, param, context)
    {
        Api = new ToolsApi(context.Api, context.Queries);
        Observe(Api.Settings);
        Observe(Api.Tools);
    }

    /// <summary>The tools API (queries and commands).</summary>
    public ToolsApi Api { get; }

    /// <summary>The feedback surface (toasts, confirmations).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("tools.subtitle");

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
