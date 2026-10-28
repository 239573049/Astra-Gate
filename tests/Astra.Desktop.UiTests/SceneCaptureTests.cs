using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

public class SceneCaptureTests
{
    /// <summary>Every registered scene x light/dark; <c>ASTRA_VISUAL_SCENE</c> (comma separated names or page prefixes) narrows it.</summary>
    public static IEnumerable<object[]> Cases()
    {
        var only = (Environment.GetEnvironmentVariable("ASTRA_VISUAL_SCENE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in Scenes.All.Keys.Order(StringComparer.Ordinal))
        {
            if (only.Length > 0 && !only.Any(o => name == o || name.StartsWith(o + "__", StringComparison.Ordinal))) continue;
            foreach (var theme in new[] { "light", "dark" }) yield return [name, theme];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Scene_renders(string scene, string theme)
    {
        var path = SceneCapture.Capture(scene, theme == "dark");
        var info = new FileInfo(path);
        Assert.True(info.Exists && info.Length > 2000, $"{path} is missing or empty");
        Console.WriteLine($"scene {scene}__{theme} -> {path}");
    }

    [Fact]
    public void Scene_names_are_page_and_state()
    {
        Assert.All(Scenes.All.Keys, name => Assert.Matches(@"^[a-z0-9-]+__[a-z0-9-]+$", name));
    }
}
