using Astra.Desktop.Components.SmartRouting;
using Avalonia.Controls;

namespace Astra.Desktop.Pages.SmartRouting;

/// <summary>The body of the Smart routing page: the settings form.</summary>
internal sealed class SmartRoutingView : UserControl
{
    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public SmartRoutingView(SmartRoutingPage page)
        => Content = new SmartRoutingFormView(page.Model, () => page.Now);
}
