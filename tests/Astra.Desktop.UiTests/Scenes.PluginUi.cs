using System.Text.Json.Nodes;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Astra.Plugins.V2.Ui;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The v2 plugin UI scene set (design/plugin-v2 06 §5; tests/fixtures/plugin-ui): each scene in the component gallery, one alone
/// (route PluginGallery "scene:&lt;id&gt;", web /plugins/dev/gallery?scene=&lt;id&gt;; scripts/visual/steps/plugin-ui.mjs mirrors
/// the same names). By default each component's default scene and the composites; <c>ASTRA_VISUAL_PLUGIN_UI=all</c> adds every
/// state (variants, tones, sizes, loading, invalid, disabled), which roughly doubles the run.
/// </summary>
public static partial class Scenes
{
    /// <summary>A scene id ("Button/variant-primary", "_patterns/dashboard") as a scene name: plugin-ui__button-variant-primary.</summary>
    public static string PluginUiName(string id) =>
        "plugin-ui__" + id.Replace("_patterns/", "pattern-", StringComparison.Ordinal).Replace('/', '-').ToLowerInvariant();

    static partial void AddPluginUi(Dictionary<string, Func<Scene>> d)
    {
        var all = Environment.GetEnvironmentVariable("ASTRA_VISUAL_PLUGIN_UI") == "all";
        var scenes = UiGallery.Scenes();
        var json = scenes.ToJsonString();
        foreach (var scene in scenes.OfType<JsonObject>())
        {
            var id = scene["id"]!.GetValue<string>();
            var state = scene["state"]!.GetValue<string>();
            if (!all && scene["component"] is not null && state != "default") continue;
            d[PluginUiName(id)] = () =>
            {
                var shell = BuildShellWith(Fixtures().Override("/api/plugins/v2/ui/scenes", json), null);
                shell.Navigator.Navigate(AppRoute.PluginGallery, "scene:" + id);
                return new Scene(new ShellView { DataContext = shell }, async () =>
                {
                    await shell.Context.Queries.WhenIdleAsync();
                    await Task.Delay(300); // the gallery loads its scenes after it is shown
                    await shell.Context.Queries.WhenIdleAsync();
                });
            };
        }
    }
}
