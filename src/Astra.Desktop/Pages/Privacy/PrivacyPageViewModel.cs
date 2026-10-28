using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Privacy;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Privacy;

/// <summary>The tabs of the Privacy page (web <c>TABS</c>; the URL segment is the lower-case name).</summary>
public enum PrivacyTab { Overview, Logs, Rules, Tester }

/// <summary>
/// Privacy guard (web/src/pages/PrivacyPage.tsx): overview, interception log, detection rules and the tester, four sub-pages
/// (<c>/privacy/:tab</c>). The tab is local state started from the route parameter.
/// </summary>
public sealed class PrivacyPageViewModel : PageViewModel
{
    private PrivacyTab _tab;

    /// <summary>Creates the page over the app context.</summary>
    public PrivacyPageViewModel(PageContext context, string? param) : base(AppRoute.Privacy, param, context)
    {
        Api = new PrivacyApi(context.Api, context.Queries, context.Clients);
        _tab = TabOf(param);
        Observe(Api.Policy);
    }

    /// <summary>The queries and writes of the page.</summary>
    public PrivacyApi Api { get; }

    /// <summary>The selected tab.</summary>
    public PrivacyTab Tab
    {
        get => _tab;
        set => SetProperty(ref _tab, value);
    }

    /// <summary>The tab a route segment names (anything unknown is the overview).</summary>
    public static PrivacyTab TabOf(string? segment) => segment switch
    {
        "logs" => PrivacyTab.Logs,
        "rules" => PrivacyTab.Rules,
        "tester" => PrivacyTab.Tester,
        _ => PrivacyTab.Overview,
    };

    /// <summary>The segment and catalog key suffix of a tab.</summary>
    public static string NameOf(PrivacyTab tab) => tab switch
    {
        PrivacyTab.Logs => "logs",
        PrivacyTab.Rules => "rules",
        PrivacyTab.Tester => "tester",
        _ => "overview",
    };

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("settings.privacy.footer");
}
