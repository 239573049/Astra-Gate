using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Shell;

public sealed class LoginItemServiceTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("astra-login-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool Throw;
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string command) { if (Throw) throw new UnauthorizedAccessException(); Values[name] = command; }
        public void Remove(string name) => Values.Remove(name);
    }

    private LoginItemService Linux(Dictionary<string, string>? env = null, bool packaged = true, string exec = "/opt/Astra/astra-desktop")
        => new(new LoginItemEnvironment(LoginOs.Linux, packaged, exec, _home, k => env?.GetValueOrDefault(k)));

    [Fact]
    public void Linux_entry_is_the_xdg_autostart_file_electron_writes()
    {
        var item = Linux();
        Assert.Equal(new LoginItemState(true, false), item.Get());

        Assert.Equal(new LoginItemState(true, true), item.Set(true));
        var file = Path.Combine(_home, ".config", "autostart", "io.astra.desktop.desktop");
        Assert.Equal(
            "[Desktop Entry]\nType=Application\nName=Astra\nComment=Local AI gateway\nExec=/opt/Astra/astra-desktop --hidden\nTerminal=false\nX-GNOME-Autostart-enabled=true\n",
            File.ReadAllText(file));

        Assert.Equal(new LoginItemState(true, false), item.Set(false));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Linux_honours_xdg_config_home_and_appimage()
    {
        var config = Path.Combine(_home, "cfg");
        var item = Linux(new() { ["XDG_CONFIG_HOME"] = config, ["APPIMAGE"] = "/home/me/Apps/Astra x.AppImage" });
        item.Set(true);
        var text = File.ReadAllText(Path.Combine(config, "autostart", "io.astra.desktop.desktop"));
        Assert.Contains("Exec=\"/home/me/Apps/Astra x.AppImage\" --hidden\n", text);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100%%")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\\\"b\"")]
    [InlineData("a$b", "\"a\\\\$b\"")]
    [InlineData("a\\b", "\"a\\\\\\\\b\"")]
    public void Exec_arguments_follow_the_desktop_entry_spec(string arg, string expected)
        => Assert.Equal(expected, LoginItemService.DesktopExecArg(arg));

    [Fact]
    public void An_entry_the_desktop_switched_off_does_not_count_and_a_moved_appimage_is_retargeted()
    {
        var file = Path.Combine(_home, ".config", "autostart", "io.astra.desktop.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[Desktop Entry]\nExec=/old/Astra --hidden\nX-GNOME-Autostart-enabled=false\n");
        var item = Linux(exec: "/new/Astra");
        Assert.False(item.Get().Enabled);

        item.RefreshLinuxEntry();
        var text = File.ReadAllText(file);
        Assert.Contains("Exec=/new/Astra --hidden", text);
        Assert.Contains("X-GNOME-Autostart-enabled=false", text);   // the switch-off is kept
        Assert.False(item.Get().Enabled);

        Assert.Null(LoginItemService.RetargetLinuxAutostart(text, ["/new/Astra", "--hidden"])); // already there
        Assert.Null(LoginItemService.RetargetLinuxAutostart("[Desktop Entry]\n", ["/x"]));      // no Exec line
    }

    [Fact]
    public void Windows_uses_the_run_key_value_Astra_with_the_hidden_argument()
    {
        var key = new FakeRunKey();
        var item = new LoginItemService(new LoginItemEnvironment(LoginOs.Windows, true, @"C:\Apps\Astra\Astra.exe", _home, _ => null, key));
        Assert.Equal(new LoginItemState(true, false), item.Get());

        Assert.Equal(new LoginItemState(true, true), item.Set(true));
        Assert.Equal("\"C:\\Apps\\Astra\\Astra.exe\" --hidden", key.Values["Astra"]);

        // Another install's entry is not ours.
        key.Values["Astra"] = "\"D:\\Other\\Astra.exe\" --hidden";
        Assert.False(item.Get().Enabled);

        item.Set(true);
        Assert.Equal(new LoginItemState(true, false), item.Set(false));
        Assert.Empty(key.Values);
    }

    [Fact]
    public void A_refused_write_leaves_the_state_unchanged()
    {
        var key = new FakeRunKey { Throw = true };
        var item = new LoginItemService(new LoginItemEnvironment(LoginOs.Windows, true, @"C:\a.exe", _home, _ => null, key));
        Assert.Equal(new LoginItemState(true, false), item.Set(true));
    }

    [Fact]
    public void Dev_runs_and_macos_are_unsupported_and_never_write()
    {
        Assert.Equal(new LoginItemState(false, false), Linux(packaged: false).Set(true));
        Assert.False(Directory.Exists(Path.Combine(_home, ".config")));
        var mac = new LoginItemService(new LoginItemEnvironment(LoginOs.MacOS, true, "/Applications/Astra.app/Contents/MacOS/Astra", _home, _ => null));
        Assert.Equal(new LoginItemState(false, false), mac.Set(true));
    }

    [Fact]
    public void The_login_item_starts_hidden()
    {
        Assert.True(LoginItemService.LaunchedAtLogin(["--hidden"]));
        Assert.False(LoginItemService.LaunchedAtLogin(["--smoke"]));
    }
}
