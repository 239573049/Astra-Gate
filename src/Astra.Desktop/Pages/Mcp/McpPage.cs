using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Mcp;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Mcp;

/// <summary>
/// The MCP gateway page (web <c>McpPage</c>): the model owns the queries (settings, servers, calls, polled while the page shows) and what
/// outlives a redraw (a sheet asked for); the view draws.
/// </summary>
public sealed class McpPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public McpPage(PageContext context, string? param) : base(AppRoute.Mcp, param, context)
    {
        Api = new McpApi(context.Api, context.Queries);
        ClientApi = new ClientMcpApi(context.Api, context.Queries);
        Tokens = context.Tokens.Tokens;
        Observe(Api.Settings);
        Observe(Api.Servers);
        Observe(Api.Calls);
        Observe(ClientApi.Entries);
        Observe(ClientApi.Servers);
        Observe(Tokens);
        var add = Ui.Button(Loc.T("mcp.servers.add"), () => EditRequested?.Invoke(null), ButtonVariant.Primary, LucideIconKind.Plus);
        SetHeaderActions(new ActionGroup(8, add));
    }

    /// <summary>The MCP gateway's API (queries and commands).</summary>
    public McpApi Api { get; }

    /// <summary>What the page writes into clients: the gateway entry and the user's own servers (issue #34).</summary>
    public ClientMcpApi ClientApi { get; }

    /// <summary>The tokens, for the "apply to clients" token picker.</summary>
    public Query<List<TokenDto>> Tokens { get; }

    /// <summary>The feedback surface (toasts, confirmations).</summary>
    public IFeedback Feedback => Context!.Feedback;

    /// <summary>The user asked for the server sheet: a server to edit, or null for a new one.</summary>
    public event Action<McpServerDto?>? EditRequested;

    /// <summary>Opens the sheet of <paramref name="server"/>.</summary>
    public void BeginEdit(McpServerDto server) => EditRequested?.Invoke(server);

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("mcp.subtitle");

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
