using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.Tests.V2;

public class UiGalleryTests
{
    [Fact]
    public void Every_component_has_scenes_and_every_scene_is_a_clean_tree()
    {
        var scenes = UiGallery.Scenes();
        var problems = scenes.Where(s => s!["warnings"]!.AsArray().Count > 0)
            .Select(s => s!["id"] + ": " + string.Join("; ", s["warnings"]!.AsArray())).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        var covered = scenes.Select(s => s!["component"]?.GetValue<string>()).OfType<string>().ToHashSet();
        var missing = UiSchema.Default.Names.Where(c => !covered.Contains(c)).ToList();
        Assert.Empty(missing);
    }
}
