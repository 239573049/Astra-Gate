using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Mcp;

/// <summary>The MCP gateway route (<c>/mcp</c>): the gateway's switch and address, the upstream servers and the recent calls (plan mcp-gateway).</summary>
public sealed class McpModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Mcp, (ctx, param) => new McpPage(ctx, param), vm => new McpView((McpPage)vm));
}
