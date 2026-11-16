using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Library;

/// <summary>
/// The Library page (web <c>LibraryPage</c>): instructions, sub-agents, slash commands, hooks, environment variables and the projects they can go
/// into. The model owns the queries and the tab; the view draws.
/// </summary>
public sealed class LibraryPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public LibraryPage(PageContext context, string? param) : base(AppRoute.Library, param, context)
    {
        Api = new LibraryApi(context.Api, context.Queries);
        Observe(Api.For("instructions"));
    }

    /// <summary>The library API (queries and commands).</summary>
    public LibraryApi Api { get; }

    /// <summary>The feedback surface (toasts, confirmations).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <summary>The tab shown: a kind's URL segment, or <c>projects</c>.</summary>
    public string Tab { get; set; } = "instructions";

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("library.subtitle");

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
