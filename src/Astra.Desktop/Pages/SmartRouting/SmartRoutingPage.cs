using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.SmartRouting;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.SmartRouting;

/// <summary>
/// The Smart routing page (web <c>SmartRoutingPage</c>): a title and the settings form. The settings are polled every 30 s while the
/// page is shown (the live status: a judge being left alone, sessions holding a grade).
/// </summary>
public sealed class SmartRoutingPage : PageViewModel
{
    /// <summary>Creates the page over the app context.</summary>
    public SmartRoutingPage(PageContext context, string? param) : base(AppRoute.SmartRouting, param, context)
    {
        var api = new SmartRoutingApi(context.Api, context.Queries);
        Model = new SmartRoutingPageModel(api, context.Feedback);
        Observe(api.Settings);
        Observe(api.Models);
    }

    /// <summary>The settings form's model.</summary>
    public SmartRoutingPageModel Model { get; }

    /// <inheritdoc />
    public override bool HoldsUnsavedWork => Model.Form?.IsDirty == true;

    /// <summary>The clock the status times are read against.</summary>
    public DateTimeOffset Now => Context!.Clock.Now;

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("smartRouting.subtitle");
}
