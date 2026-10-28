using Astra.Desktop.Core;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

public class InstallJsonTests
{
    private static readonly Func<DateTimeOffset> Clock = () => DateTimeOffset.Parse("2026-10-26T10:00:00Z");

    [Fact]
    public void Updating_the_server_preserves_the_desktop_fields()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "install.json");
        File.WriteAllText(file, """{"serverPath":"/old","serverVersion":"0.1.0","desktopPath":"/Apps/Astra.app","desktopVersion":"0.1.0"}""");

        InstallJson.WithServer(InstallJson.Read(file), "/new", "0.2.0", Clock).Write(file);

        var back = InstallJson.Read(file)!;
        Assert.Equal(new InstallJson("/new", "0.2.0", "/Apps/Astra.app", "0.1.0", "2026-10-26T10:00:00.000Z"), back);
        // The narrow resolver view reads the same file.
        Assert.Equal(new InstallInfo("/new", "0.2.0"), InstallInfo.Parse(File.ReadAllText(file)));
    }

    [Fact]
    public void Without_a_version_the_existing_one_stays()
    {
        var r = InstallJson.WithServer(new InstallJson("/a", "0.1.0"), "/b", null, Clock);
        Assert.Equal(("/b", "0.1.0"), (r.ServerPath, r.ServerVersion));
    }

    [Fact]
    public void Clearing_the_server_keeps_desktop_fields_or_removes_the_file()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "install.json");
        InstallJson.ClearServer(file, new InstallJson("/s", "1", "/d", "2"), Clock);
        Assert.Equal(new InstallJson(null, null, "/d", "2", "2026-10-26T10:00:00.000Z"), InstallJson.Read(file));

        InstallJson.ClearServer(file, new InstallJson("/s", "1"), Clock);
        Assert.False(File.Exists(file));
        InstallJson.ClearServer(file, null, Clock); // nothing to remove is fine
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[1]")]
    public void Unreadable_files_are_null(string text)
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "install.json");
        File.WriteAllText(file, text);
        Assert.Null(InstallJson.Read(file));
        Assert.Null(InstallJson.Read(Path.Combine(t.Path, "missing.json")));
    }
}
