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
using Avalonia.LogicalTree;

namespace Astra.Desktop.UiTests;

/// <summary>
/// What the provider detail page keeps across refetches (web: react-query structural sharing + React keeping component state), and the
/// sticky bars (web <c>position: sticky</c>), checked on the live controls.
/// </summary>
public class ProviderDetailStateTests
{
    private static (ProviderDetailPage Page, ProviderDetailView View, Window Window) Open(string id, FixtureSet? fixtures = null)
    {
        var fx = fixtures ?? Scenes.Fixtures();
        var ctx = PageContext.Create(new ApiClient("http://fixture", fx.Handler()), AvaloniaUiDispatcher.Instance, clock: new FixedClock(fx.Now));
        var page = new ProviderDetailPage(ctx, id);
        page.Activate(); // the navigator does this when the page is shown: its queries start observing
        var view = new ProviderDetailView(page);
        var window = new Window { Content = view, Width = 1100, Height = 800 };
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
    public void The_remote_models_sheet_opens_again_after_it_was_closed()
    {
        // Issue #19: the second click on "fetch from upstream" crashed the app — the sheet's list panel was still the child of the first
        // opening's body when the second one added it to a new body ("already has a visual parent", thrown out of the click handler).
        const string remote = """[{"id":"gpt-5","displayName":"GPT-5","linkedSystemModelId":"gpt-5","alreadyAdded":false},{"id":"o3","displayName":null,"linkedSystemModelId":"o3","alreadyAdded":false}]""";
        var (page, view, window) = Open("prov_openai", Scenes.Fixtures().Override("/api/providers/prov_openai/remote-models", remote));
        var sheet = view.ModelsTab!.RemoteSheet;
        for (var i = 0; i < 3; i++)
        {
            sheet.Show();
            SceneCapture.Await(page.Context!.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
            SceneCapture.Pump(3);
            Assert.True(sheet.IsOpen);
            // every opening lists the upstream's models (a checkbox each), not a body left empty by the previous one
            var boxes = ((Control)sheet.Content!).GetLogicalDescendants().OfType<TKCode.Arc.Checkbox>()
                .Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).ToList();
            Assert.Equal(["gpt-5", "o3"], boxes);
            sheet.Close();
            SceneCapture.Pump(3);
            Assert.False(sheet.IsOpen);
        }
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
        var window = new Window { Content = viewer, Width = 400, Height = 300 };
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
