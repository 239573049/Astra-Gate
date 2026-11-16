using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Tools;

/// <summary>The built-in tools route (<c>/tools</c>): search APIs, search on behalf of models, web fetch, the image model and the tool list (linked update 34).</summary>
public sealed class ToolsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Tools, (ctx, param) => new ToolsPage(ctx, param), vm => new ToolsView((ToolsPage)vm));
}
