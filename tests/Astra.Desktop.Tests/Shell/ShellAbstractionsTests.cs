using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Shell;

public class ShellAbstractionsTests
{
    [Fact]
    public async Task NullFeedback_answers_confirm_as_configured()
    {
        var f = new NullFeedback();
        Assert.True(await f.ConfirmAsync(new ConfirmRequest("Delete?", Destructive: true)));
        f.Answer = false;
        Assert.False(await f.ConfirmAsync(new ConfirmRequest("Delete?")));
        f.Success("ok"); f.Error("no"); f.Info("fyi");
    }

    [Fact]
    public void RecordingNavigator_keeps_a_back_stack_like_the_app_navigator()
    {
        var n = new RecordingNavigator();
        n.Navigate("Providers");
        n.Push("ProviderDetail", "p1");
        Assert.True(n.CanGoBack);
        Assert.Equal(("ProviderDetail", "p1"), n.Current);
        n.GoBack();
        Assert.Equal(("Providers", (string?)null), n.Current);
        Assert.False(n.CanGoBack);
        n.Push("Models");
        n.Navigate("Settings");
        Assert.False(n.CanGoBack);
    }

    [Fact]
    public async Task NullDesktopHost_login_item_is_unsupported_until_a_test_enables_it()
    {
        var host = new NullDesktopHost();
        Assert.Equal(new LoginItemState(false, false), await host.SetLoginItemAsync(true));
        host.SetLoginSupported(true);
        LoginItemState? seen = null;
        host.LoginItemChanged += s => seen = s;
        Assert.Equal(new LoginItemState(true, true), await host.SetLoginItemAsync(true));
        Assert.Equal(new LoginItemState(true, true), seen);
    }

    [Fact]
    public async Task NullDesktopHost_tray_prefs_round_trip_and_default_matches_electron()
    {
        var host = new NullDesktopHost();
        var prefs = await host.Tray.GetPrefsAsync();
        Assert.Equal(TrayTitleMode.Requests, prefs.Title);
        Assert.Equal(8, prefs.Sections.Count);
        Assert.True(prefs.Sections[TraySection.Quota]);
        Assert.False(prefs.Sections[TraySection.Clients]);
        TrayPrefs? changed = null;
        host.Tray.PrefsChanged += p => changed = p;
        var next = prefs with { Title = TrayTitleMode.Cost };
        Assert.Equal(next, await host.Tray.SetPrefsAsync(next));
        Assert.Equal(next, changed);
        Assert.Equal(next, await host.Tray.GetPrefsAsync());
    }

    [Fact]
    public async Task NullDesktopHost_does_nothing_visible()
    {
        var host = new NullDesktopHost();
        Assert.Null(await host.GetAccentColorAsync());
        Assert.Equal(UpdateAction.None, (await host.CheckForUpdateAsync()).Action);
        Assert.Equal(UpdateApplyOutcome.None, await host.ApplyUpdateAsync());
        Assert.Null(await host.ShowContextMenuAsync([new ContextMenuItem("a", "A")]));
        string? cmd = null;
        host.MenuCommandReceived += c => cmd = c;
        host.RaiseMenuCommand(MenuCommands.Nav("providers"));
        Assert.Equal("nav:providers", cmd);
    }
}
