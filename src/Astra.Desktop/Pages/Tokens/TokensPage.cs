using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>
/// Tokens (web <c>TokensPage</c>): the shared gateway credentials with today's and lifetime usage, a row per token that expands into
/// its details, and the create / settings sheets. This model owns what outlives a redraw (which rows are open, which sheet was
/// asked for) and the commands (<see cref="TokenActions"/>); the view draws.
/// </summary>
public sealed class TokensPage : PageViewModel
{
    private readonly PageContext _ctx;
    private readonly ListQueries _lists;
    private readonly HashSet<string> _opened = [];
    private readonly HashSet<string> _closed = [];

    /// <summary>Creates the page over the app context.</summary>
    public TokensPage(PageContext context, string? param) : base(AppRoute.Tokens, param, context)
    {
        _ctx = context;
        _lists = new ListQueries(context.Api, context.Queries);
        Providers = _lists.Providers();
        Tunnel = _lists.Tunnel();
        Observe(context.Tokens.Tokens);
        Observe(context.Settings.Settings);
        Observe(Providers);
        Observe(Tunnel);
        Actions = new TokenActions(context.Api, context.Queries, context.Feedback, (key, args) => Loc.T(key, args));
        Accounts = id => _lists.Accounts(id);
        var add = Astra.Desktop.Components.Tokens.AccentButton.Create(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            Children = { new LucideIcon { Kind = LucideIconKind.Plus, Size = 14 }, new TextBlock { Text = Loc.T("tokens.add"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
        });
        add.Click += (_, _) => BeginCreate();
        SetHeaderActions(new ActionGroup(8, add));
    }

    /// <summary>The user asked for the create sheet.</summary>
    public event Action? CreateRequested;

    /// <summary>The user asked for a token's settings sheet.</summary>
    public event Action<TokenDto>? SettingsRequested;

    /// <summary>The token list (key ['tokens'], polled every 15 s while the page is up like the web).</summary>
    public Query<List<TokenDto>> Tokens => _ctx.Tokens.Tokens;

    /// <summary>The server settings: the gateway address for the hints and the copy buttons.</summary>
    public Query<SettingsDto> Settings => _ctx.Settings.Settings;

    /// <summary>The providers (the direct-provider names, the create sheet's options, the pool editor).</summary>
    public Query<List<ProviderDto>> Providers { get; }

    /// <summary>The remote-access state: with the tunnel up, the addresses to hand out are its.</summary>
    public Query<TunnelStatusDto> Tunnel { get; }

    /// <summary>The commands (create, update, reset, delete, copy).</summary>
    public TokenActions Actions { get; }

    /// <summary>The accounts of a provider (the pool editor's account select).</summary>
    public Func<string, Query<List<ProviderAccountDto>>> Accounts { get; }

    /// <summary>The page's zone and clock for the times it shows.</summary>
    public LocalTime Now => LocalTime.Of(_ctx.Clock);

    /// <summary>The page clock.</summary>
    public DateTimeOffset Clock => _ctx.Clock.Now;

    /// <summary>The public address of the tunnel when remote access is connected, else null (<c>remote</c> in the web page).</summary>
    public string? RemoteUrl => Tunnel.Data is { State: "connected" } t ? t.PublicUrl : null;

    // The shared token list does not poll by itself; the web's useTokens refetches every 15 s while its page is mounted.
    private System.Threading.Timer? _poll;

    /// <inheritdoc />
    protected override void OnActivated() => _poll = new System.Threading.Timer(_ => _ = Tokens.RefetchAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _poll?.Dispose();
        _poll = null;
    }

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("tokens.subtitle");

    /// <summary>
    /// Whether a row is open: the user's last choice, else open when the list holds a single token (web <c>defaultOpen</c>, which only
    /// applies when a row mounts).
    /// </summary>
    public bool IsOpen(string id, int tokenCount)
        => _opened.Contains(id) || (!_closed.Contains(id) && tokenCount == 1);

    /// <summary>Remembers a row's open state across redraws.</summary>
    public void SetOpen(string id, bool open)
    {
        if (open)
        {
            _opened.Add(id);
            _closed.Remove(id);
        }
        else
        {
            _opened.Remove(id);
            _closed.Add(id);
        }
    }

    /// <summary>Opens the create sheet.</summary>
    public void BeginCreate() => CreateRequested?.Invoke();

    /// <summary>Opens the settings sheet of <paramref name="token"/>.</summary>
    public void BeginEdit(TokenDto token) => SettingsRequested?.Invoke(token);

    /// <summary>Opens the detail page of a token.</summary>
    public void OpenDetail(string id) => _ctx.Navigator.Push("TokenDetail", id);

    /// <summary>Opens the request log filtered to a token (<c>/requests?token=&lt;id&gt;</c>): the Requests page reads the <c>token=</c> parameter.</summary>
    public void OpenLogs(string id) => _ctx.Navigator.Push("Requests", "token=" + Uri.EscapeDataString(id));

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 2; i++)
        {
            await _ctx.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }
}
