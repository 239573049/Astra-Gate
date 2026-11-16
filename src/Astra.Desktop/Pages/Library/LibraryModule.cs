using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Library;

/// <summary>The Library route (<c>/library</c>): instructions, sub-agents, commands, hooks and environment variables written into the clients' own files.</summary>
public sealed class LibraryModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Library, (ctx, param) => new LibraryPage(ctx, param), vm => new LibraryView((LibraryPage)vm));
}
