using System.Text.Json.Nodes;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>Issue #22: the overview's filters never overlap, also when the client list arrives after the toolbar was first laid out.</summary>
public class OverviewToolbarLayoutTests
{
    [AvaloniaFact]
    public void Clients_answering_after_the_first_layout_do_not_push_the_filters_over_the_range_switch()
    {
        var fixtures = Scenes.Fixtures().Override("/api/clients", "[]");
        var shell = Scenes.BuildShell(fixtures);
        shell.Navigator.Navigate(AppRoute.Overview);
        var window = new Window { Content = new ShellView { DataContext = shell }, Width = 1280, Height = SceneCapture.Height };
        window.Show();
        try
        {
            SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(20));
            SceneCapture.Pump(20);

            // The window does not change size from here on, so only the data can bring the toolbar to reconsider.
            fixtures.Override("/api/clients", EveryClientInstalled());
            shell.Context.Queries.Invalidate("clients");
            SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(20));
            SceneCapture.Pump(20);

            var range = Named(window, Loc.T("overview.range"));
            var token = Named(window, Loc.T("overview.tokenFilter"));
            var tokenRight = token.TranslatePoint(new Point(token.Bounds.Width, 0), window)!.Value.X;
            var rangeLeft = range.TranslatePoint(new Point(0, 0), window)!.Value.X;
            Assert.True(tokenRight <= rangeLeft, $"the token filter ends at {tokenRight}, the range switch starts at {rangeLeft}");
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Named(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().First(c => AutomationProperties.GetName(c) == name);

    /// <summary>One copy of a fixture client per kind the filter knows, all of them detected.</summary>
    private static string EveryClientInstalled()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "clients.json");
        var sample = ((JsonArray)JsonNode.Parse(File.ReadAllText(path))!)[0]!;
        var list = new JsonArray();
        foreach (var kind in ClientCatalog.Order)
        {
            var client = sample.DeepClone().AsObject();
            client["kind"] = kind;
            client["detection"]!["installed"] = true;
            list.Add(client);
        }
        return list.ToJsonString();
    }
}
