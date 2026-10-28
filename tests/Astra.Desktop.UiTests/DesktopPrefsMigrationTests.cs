using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Desktop.Theme;

namespace Astra.Desktop.UiTests;

/// <summary><see cref="DesktopPrefs.Load(AstraPaths, IReadOnlyList{string})"/>: the first launch takes the Electron state over, later launches never again.</summary>
public sealed class DesktopPrefsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-prefs-mig-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private (AstraPaths Paths, string UserData) Setup(string electronJson)
    {
        var home = Path.Combine(_dir, "astra");
        var userData = Path.Combine(_dir, "electron");
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Combine(userData, "desktop-prefs.json"), electronJson);
        return (new AstraPaths(home), userData);
    }

    [Fact]
    public void First_launch_migrates_and_writes_electrons_keys_in_their_shape()
    {
        var (paths, userData) = Setup("""{ "closeHintShown": true, "tray": { "title": "tokens" }, "updateReminder": { "version": "0.11.7", "count": 1, "lastRemindedAt": 5, "skippedVersion": null } }""");

        var prefs = DesktopPrefs.Load(paths, [userData]);

        Assert.True(File.Exists(DesktopPrefs.PathIn(paths)));
        var written = JsonNode.Parse(File.ReadAllText(DesktopPrefs.PathIn(paths)))!.AsObject();
        Assert.True(written["closeHintShown"]!.GetValue<bool>());
        Assert.Equal("tokens", written["tray"]!["title"]!.GetValue<string>());
        Assert.Equal(8, written["tray"]!["sections"]!.AsObject().Count);
        Assert.Equal("""{"version":"0.11.7","count":1,"lastRemindedAt":5,"skippedVersion":null}""", written["updateReminder"]!.ToJsonString());
        Assert.Equal(ThemeSource.System, prefs.Theme);
        Assert.NotNull(prefs.GetExtra("tray"));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten_by_a_migration()
    {
        var (paths, userData) = Setup("""{ "closeHintShown": true }""");
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(DesktopPrefs.PathIn(paths), """{ "theme": "dark", "accent": "system" }""");

        var prefs = DesktopPrefs.Load(paths, [userData]);

        Assert.Equal(ThemeSource.Dark, prefs.Theme);
        Assert.Null(prefs.GetExtra("closeHintShown"));
    }

    [Fact]
    public void No_electron_state_leaves_defaults_and_no_file()
    {
        var paths = new AstraPaths(Path.Combine(_dir, "astra"));
        var prefs = DesktopPrefs.Load(paths, [Path.Combine(_dir, "missing")]);
        Assert.Equal(ThemeSource.System, prefs.Theme);
        Assert.False(File.Exists(DesktopPrefs.PathIn(paths)));
    }
}
