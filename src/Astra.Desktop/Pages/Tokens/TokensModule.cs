using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>The Tokens routes: the token list and a token's detail page (web <c>TokensPage</c> and <c>TokenDetailPage</c>).</summary>
public sealed class TokensModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
    {
        pages.Add(AppRoute.Tokens, (ctx, param) => new TokensPage(ctx, param), vm => new TokensView(vm));
        pages.Add(AppRoute.TokenDetail, (ctx, param) => new TokenDetailPage(ctx, param), vm => new TokenDetailView(vm));
    }
}
