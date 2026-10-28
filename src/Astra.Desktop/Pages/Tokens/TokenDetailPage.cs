using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>
/// One token's statistics (web <c>TokenDetailPage</c>): totals for a range, the trend, which models and providers served it — with
/// pool failovers and live member health — and its latest requests. The route parameter is the token id. The header shows the token's
/// name and masked key; a token that cannot be loaded (deleted, no such id) shows the error under the list's title instead.
/// </summary>
public sealed class TokenDetailPage : PageViewModel
{
    /// <summary>The recent-requests table shows this many rows.</summary>
    public const int RecentRows = 10;

    private readonly PageContext _ctx;
    private readonly StatsQueries _stats;
    private string _range = StatsRange.Days7;

    /// <summary>Creates the page for the token <paramref name="param"/>.</summary>
    public TokenDetailPage(PageContext context, string? param) : base(AppRoute.TokenDetail, param, context)
    {
        _ctx = context;
        Id = param ?? "";
        _stats = new StatsQueries(context.Api, context.Queries, LocalTime.Of(context.Clock).OffsetMinutes);
        Token = _stats.Token(Id);
        Providers = new ListQueries(context.Api, context.Queries).Providers();
        Observe(Token);
        Observe(Providers);
        Subscribe(Token, (_, e) =>
        {
            // Title, subtitle and header read only these; the header is rebuilt from scratch, so skip the fetch's other notifications.
            if (e.PropertyName is not ("Data" or "HasData" or "IsError")) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            Header();
        });
        Header();
        Leading();
    }

    /// <inheritdoc />
    protected override void OnActivated() => Leading();

    /// <summary>
    /// web <c>back</c> (to the token list). Pushed over the list the shell already draws its back button to the same place, so the page adds its
    /// own only when there is nothing to go back to (the ProviderDetail rule, plan §35).
    /// </summary>
    private void Leading() => SetHeaderLeading(_ctx.Navigator.CanGoBack
        ? null
        : Astra.Desktop.Components.Pricing.PricingUi.Button(null, Astra.Desktop.Components.Pricing.PricingUi.Look.Glass, LucideIconKind.ArrowLeft,
            click: () => _ctx.Navigator.Navigate(nameof(AppRoute.Tokens)), name: Loc.T("common.back")));

    /// <summary>The selected range changed: sections ask for their queries again.</summary>
    public event Action? RangeChanged;

    /// <summary>The token id.</summary>
    public string Id { get; }

    /// <summary>The token (polled every 10 s for its pool's health).</summary>
    public Query<TokenDto> Token { get; }

    /// <summary>The providers (names for the pool and the breakdown).</summary>
    public Query<List<ProviderDto>> Providers { get; }

    /// <summary>today | 7d | 30d | 90d; a token opens on 7 days.</summary>
    public string Range => _range;

    /// <summary>The page's wall clock.</summary>
    public LocalTime Now => LocalTime.Of(_ctx.Clock);

    /// <summary>The page clock.</summary>
    public DateTimeOffset Clock => _ctx.Clock.Now;

    /// <inheritdoc />
    public override string Title => Token.IsError ? Loc.T("nav.tokens") : Token.Data?.Name ?? Loc.T("nav.tokens");   // the list's title until the name arrives (the shell wants a non-empty one)

    /// <inheritdoc />
    public override string? Subtitle => Token.IsError ? null : Token.Data?.KeyMasked;

    /// <summary>Selects a range.</summary>
    public void SetRange(string range)
    {
        _range = range;
        RangeChanged?.Invoke();
    }

    /// <summary>The summary of the token in the current range.</summary>
    public Query<StatsSummaryDto> Summary() => _stats.Summary(_range, null, Id);

    /// <summary>The token's per-bucket series.</summary>
    public Query<List<TimeseriesPointDto>> Series() => _stats.Timeseries(_range, StatsRange.GroupBy(_range), "token", null, Id);

    /// <summary>The models that served the token.</summary>
    public Query<List<TopModelDto>> Models() => _stats.TopModels(_range, null, 20, Id);

    /// <summary>The providers that served the token, with failovers.</summary>
    public Query<List<ProviderStatDto>> ByProvider() => _stats.ProviderStats(_range, Id);

    /// <summary>The token's latest requests (independent of the range).</summary>
    public Query<RequestPageDto> Recent() => _stats.Requests(new RequestListQuery(null, Id, null, 1, RecentRows));

    /// <summary>Opens the request log filtered to this token.</summary>
    public void OpenLogs() => _ctx.Navigator.Push("Requests", "token=" + Uri.EscapeDataString(Id));

    /// <summary>Opens a request in the log.</summary>
    public void OpenRequest(string id) => _ctx.Navigator.Push("Requests", id);

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 3; i++)
        {
            await _ctx.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }

    /// <summary>The "view logs" button, shown once the token loaded (the error page has no actions).</summary>
    private void Header()
    {
        if (Token.IsError || !Token.HasData)
        {
            SetHeaderActions(null);
            return;
        }
        var button = new ArcButton
        {
            Variant = ButtonVariant.Secondary,
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
                Children = { new LucideIcon { Kind = LucideIconKind.ScrollText, Size = 14 }, new TextBlock { Text = Loc.T("tokens.detail.viewLogs"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
            },
        };
        button.Click += (_, _) => OpenLogs();
        SetHeaderActions(new ActionGroup(8, button));
    }
}
