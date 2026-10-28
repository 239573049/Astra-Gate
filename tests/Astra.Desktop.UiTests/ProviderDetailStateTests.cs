using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.ProviderDetail;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.UiTests;

/// <summary>
/// What the provider detail page keeps across refetches (web: react-query structural sharing + React keeping component state), and the
/// sticky bars (web <c>position: sticky</c>), checked on the live controls.
/// </summary>
public class ProviderDetailStateTests
{
    private static (ProviderDetailPage Page, ProviderDetailView View, AtomWindow Window) Open(string id)
    {
        var fx = Scenes.Fixtures();
        var ctx = PageContext.Create(new ApiClient("http://fixture", fx.Handler()), AvaloniaUiDispatcher.Instance, clock: new FixedClock(fx.Now));
        var page = new ProviderDetailPage(ctx, id);
        page.Activate(); // the navigator does this when the page is shown: its queries start observing
        var view = new ProviderDetailView(page);
        var window = new AtomWindow { Content = view, Width = 1100, Height = 800 };
        window.Show();
        SceneCapture.Await(page.WhenSettledAsync(), TimeSpan.FromSeconds(20));
        SceneCapture.Pump(2);
        return (page, view, window);
    }

    [AvaloniaFact]
    public void An_identical_refetch_keeps_every_tab_and_the_typed_settings()
    {
        var (page, view, window) = Open("prov_openai");
        page.Tab = ProviderTab.Settings;
        SceneCapture.Pump(2);
        var settings = view.SettingsTab!;
        var models = view.ModelsTab!;
        settings.Set(f => f with { Name = "typed" });

        SceneCapture.Await(page.Api.Provider.RefetchAsync(), TimeSpan.FromSeconds(10));
        SceneCapture.Pump(2);

        Assert.Same(settings, view.SettingsTab);
        Assert.Same(models, view.ModelsTab);
        Assert.Equal("typed", view.SettingsTab!.Form.Name);
        page.Deactivate();
        window.Close();
    }

    [AvaloniaFact]
    public void A_changed_provider_keeps_the_models_and_api_key_tabs_and_resets_the_settings()
    {
        var (page, view, window) = Open("prov_openai");
        var settings = view.SettingsTab!;
        var models = view.ModelsTab!;
        var accounts = view.AccountsView!;

        page.Api.Provider.SetData(page.Provider! with { Name = "Renamed", HttpProxy = "http://127.0.0.1:7890" });
        SceneCapture.Pump(2);

        Assert.Same(models, view.ModelsTab);
        Assert.Same(accounts, view.AccountsView);
        Assert.NotSame(settings, view.SettingsTab);
        Assert.Equal("Renamed", view.SettingsTab!.Form.Name);
        page.Deactivate();
        window.Close();
    }

    [AvaloniaFact]
    public void A_sticky_bar_below_the_viewport_is_held_at_its_bottom_and_returns_when_scrolled_to()
    {
        var bar = new Border { Height = 30 };
        var sticky = new StickyBottom { Offset = 8, Child = bar };
        var content = new StackPanel { Children = { new Border { Height = 1000 }, sticky, new Border { Height = 200 } } };
        var viewer = new ScrollViewer { Content = content, VerticalAlignment = VerticalAlignment.Stretch };
        var window = new AtomWindow { Content = viewer, Width = 400, Height = 300 };
        window.Show();
        SceneCapture.Pump(3);

        // natural top 1000, viewport ~300 high: drawn 8 px above the viewport's bottom edge
        var viewport = viewer.Bounds.Height;
        Assert.Equal(Math.Round(viewport - 8 - 1030), sticky.Shift);

        viewer.Offset = new Avalonia.Vector(0, 1000); // its own place is now on screen
        SceneCapture.Pump(3);
        Assert.Equal(0, sticky.Shift);
        window.Close();
    }
}
