using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Settings;

public class ListenLogicTests
{
    private static ListenDto Listen(string host = "127.0.0.1", string configured = "127.0.0.1", bool env = false, bool password = false, params string[] urls)
        => new(host, 17321, configured, env, password, [.. urls]);

    [Theory]
    [InlineData("127.0.0.1", ListenScope.Local)]
    [InlineData("localhost", ListenScope.Local)]
    [InlineData("::1", ListenScope.Local)]
    [InlineData("[::1]", ListenScope.Local)]
    [InlineData("0.0.0.0", ListenScope.Lan)]
    [InlineData("192.168.1.2", ListenScope.Lan)]
    public void Scope_follows_the_configured_host(string host, ListenScope scope) => Assert.Equal(scope, ListenLogic.ScopeOf(host));

    [Fact]
    public void Hosts_per_scope_are_loopback_and_wildcard()
    {
        Assert.Equal("127.0.0.1", ListenLogic.HostFor(ListenScope.Local));
        Assert.Equal("0.0.0.0", ListenLogic.HostFor(ListenScope.Lan));
    }

    [Fact]
    public void Detail_is_the_address_unless_something_decides_otherwise()
    {
        Loc.Language = "en";
        Assert.Equal("127.0.0.1:17321", ListenLogic.DetailText(Listen(), false, true));
        Assert.Equal("settings.listen.env", ListenLogic.Detail(Listen(env: true), false)!.Value.Key);
        Assert.Equal("settings.listen.localOnly", ListenLogic.Detail(Listen(), true)!.Value.Key);
        var pending = Listen(configured: "0.0.0.0");
        Assert.True(ListenLogic.Pending(pending));
        Assert.Equal("settings.listen.pending", ListenLogic.Detail(pending, false)!.Value.Key);
        Assert.Contains("·", ListenLogic.DetailText(pending, false, isDesktop: false));
        Assert.DoesNotContain("·", ListenLogic.DetailText(pending, false, isDesktop: true));
    }

    [Fact]
    public void Controls_hide_when_the_environment_or_another_device_decides()
    {
        Assert.True(ListenLogic.ControlsVisible(Listen(), false));
        Assert.False(ListenLogic.ControlsVisible(Listen(env: true), false));
        Assert.False(ListenLogic.ControlsVisible(Listen(), true));
        Assert.True(ListenLogic.RestartVisible(Listen(configured: "0.0.0.0"), true));
        Assert.False(ListenLogic.RestartVisible(Listen(configured: "0.0.0.0"), false));
    }

    [Fact]
    public void Password_row_shows_for_lan_or_when_the_network_has_addresses()
    {
        Assert.False(ListenLogic.PasswordRowVisible(Listen()));
        Assert.True(ListenLogic.PasswordRowVisible(Listen(configured: "0.0.0.0")));
        Assert.True(ListenLogic.PasswordRowVisible(Listen(urls: "http://192.168.1.2:17321")));
    }

    [Fact]
    public void Choosing_lan_asks_for_a_password_first_then_confirms_going_back_saves_at_once()
    {
        Assert.Equal(ListenLogic.Step.None, ListenLogic.Choose(Listen(), ListenScope.Local));
        Assert.Equal(ListenLogic.Step.AskPassword, ListenLogic.Choose(Listen(), ListenScope.Lan));
        Assert.Equal(ListenLogic.Step.ConfirmLan, ListenLogic.Choose(Listen(password: true), ListenScope.Lan));
        Assert.Equal(ListenLogic.Step.Save, ListenLogic.Choose(Listen(configured: "0.0.0.0", password: true), ListenScope.Local));
    }

    [Theory]
    [InlineData("12345678", "12345678", true, false)]
    [InlineData("1234567", "1234567", false, false)]
    [InlineData("12345678", "", false, false)]
    [InlineData("12345678", "1234", false, true)]
    public void New_password_needs_eight_characters_typed_twice(string password, string repeat, bool valid, bool mismatch)
    {
        Assert.Equal(valid, ListenLogic.PasswordValid(password, repeat));
        Assert.Equal(mismatch, ListenLogic.PasswordMismatch(password, repeat));
    }
}

public class StartupLogicTests
{
    private static AutostartDto Auto(bool supported = true, bool enabled = false, string mechanism = "launchd", string? unsupported = null, string? location = null, bool canChange = true)
        => new(supported, enabled, mechanism, unsupported, location, null, canChange);

    [Fact]
    public void Service_detail_explains_the_state()
    {
        Assert.Equal("settings.startup.unsupported.container", StartupLogic.ServiceDetail(Auto(false, mechanism: "none", unsupported: "container")).Key);
        Assert.Equal("settings.startup.unsupported.platform", StartupLogic.ServiceDetail(Auto(false, mechanism: "none")).Key);
        Assert.Equal("settings.startup.localOnly", StartupLogic.ServiceDetail(Auto(canChange: false)).Key);
        Assert.Equal("settings.startup.entry", StartupLogic.ServiceDetail(Auto(enabled: true, location: "~/x.plist")).Key);
        Assert.Equal("settings.startup.service.launchd", StartupLogic.ServiceDetail(Auto()).Key);
        Assert.Equal("settings.startup.service.systemd", StartupLogic.ServiceDetail(Auto(mechanism: "systemd")).Key);
        Assert.True(Loc.Has("settings.startup.service.registry"));
    }

    [Fact]
    public void Mac_footer_shows_for_launchd_or_the_mac_login_item()
    {
        Assert.True(StartupLogic.MacFooter(Auto(), false, false));
        Assert.True(StartupLogic.MacFooter(null, true, true));
        Assert.False(StartupLogic.MacFooter(Auto(mechanism: "systemd"), true, false));
    }

    [Fact]
    public void App_row_reports_unsupported_and_a_refused_change()
    {
        Assert.Equal("settings.startup.appUnsupported", StartupLogic.AppDetail(new LoginItemState(false, false)).Key);
        Assert.Equal("settings.startup.appHint", StartupLogic.AppDetail(new LoginItemState(true, false)).Key);
        Assert.False(StartupLogic.AppChangeSucceeded(new LoginItemState(true, false), true));
        Assert.True(StartupLogic.AppChangeSucceeded(new LoginItemState(true, true), true));
    }
}

public class UpdateLogicTests
{
    private static UpdateStatusDto Status(string? available = null, string? error = null, bool feed = true, DateTimeOffset? last = null)
        => new("0.11.7", available, last, null, error, "stable", true, feed);

    [Fact]
    public void Check_toast_says_failed_before_anything_else()
    {
        var failed = UpdateLogic.CheckToast(Status(available: "1.0.0", error: "offline"));
        Assert.True(failed.IsError);
        Assert.Equal("settings.updateCheckFailed", failed.Message.Key);
        Assert.Equal("settings.updateAvailable", UpdateLogic.CheckToast(Status(available: "1.0.0")).Message.Key);
        Assert.Equal("settings.upToDate", UpdateLogic.CheckToast(Status()).Message.Key);
    }

    [Theory]
    [InlineData(UpdateApplyOutcome.Server, "settings.updateApplied")]
    [InlineData(UpdateApplyOutcome.Desktop, "settings.updateDesktopRelaunch")]
    [InlineData(UpdateApplyOutcome.Download, "settings.updateDownloadOpened")]
    [InlineData(UpdateApplyOutcome.Downloading, "settings.updateDownloading")]
    [InlineData(UpdateApplyOutcome.None, "settings.upToDate")]
    public void Apply_toast_per_outcome(UpdateApplyOutcome outcome, string key) => Assert.Equal(key, UpdateLogic.ApplyToast(outcome).Key);

    [Fact]
    public void Only_a_server_update_refreshes_version_and_status()
    {
        Assert.True(UpdateLogic.ApplyRefreshes(UpdateApplyOutcome.Server));
        Assert.False(UpdateLogic.ApplyRefreshes(UpdateApplyOutcome.Desktop));
    }

    [Fact]
    public void The_hosts_check_decides_what_an_update_brings_once_it_ran()
    {
        // A packaged app behind a current server: the server says nothing, the app's own check finds its update.
        var app = new UpdateSummary("", UpdateAction.DesktopDmg, "0.13.3");
        Assert.Equal("0.13.3", UpdateLogic.AvailableVersion(Status(), app));
        Assert.Equal("settings.updateAvailable", UpdateLogic.CheckToast(Status(), app).Message.Key);
        // The host up to date wins over a server that would move alone; no host answer yet falls back to the server.
        Assert.Null(UpdateLogic.AvailableVersion(Status(available: "0.13.3"), new UpdateSummary("", UpdateAction.None)));
        Assert.Equal("0.13.3", UpdateLogic.AvailableVersion(Status(available: "0.13.3"), null));
        // A failed server feed fetch does not hide what the host found.
        Assert.Equal("settings.updateAvailable", UpdateLogic.CheckToast(Status(error: "offline"), app).Message.Key);
        Assert.Contains("0.13.3", UpdateLogic.CheckDetail(Status(), null, true, "en", app));
    }

    [Fact]
    public void Desktop_row_flags_a_server_on_another_version()
    {
        Loc.Language = "en";
        Assert.Equal("0.13.3", UpdateLogic.DesktopVersionDetail("0.13.3", "0.13.3"));
        Assert.Equal("0.13.3", UpdateLogic.DesktopVersionDetail("0.13.3", null));
        Assert.Equal("—", UpdateLogic.DesktopVersionDetail("", "0.13.3"));
        var mismatch = UpdateLogic.DesktopVersionDetail("0.13.0-preview.1", "0.13.3");
        Assert.StartsWith("0.13.0-preview.1 · ", mismatch);
        Assert.Contains("0.13.3", mismatch);
    }

    [Fact]
    public void Check_detail_prefers_failure_then_available_then_last_check()
    {
        Loc.Language = "en";
        Assert.Contains("boom", UpdateLogic.CheckDetail(Status(), "boom", true, "en"));
        Assert.Contains("1.0.0", UpdateLogic.CheckDetail(Status(available: "1.0.0"), null, true, "en"));
        var web = UpdateLogic.CheckDetail(Status(available: "1.0.0"), null, false, "en")!;
        Assert.Contains(Loc.T("settings.updateApplyHint"), web);
        Assert.Contains("2026", UpdateLogic.CheckDetail(Status(last: new DateTimeOffset(2026, 10, 26, 11, 25, 0, TimeSpan.Zero)), null, true, "en"));
        Assert.Null(UpdateLogic.CheckDetail(Status(), null, true, "en"));
    }

    [Fact]
    public void Auto_check_detail_flags_a_missing_feed_and_channel_prefers_the_saved_setting()
    {
        Assert.NotNull(UpdateLogic.AutoCheckDetail(Status(feed: false)));
        Assert.Null(UpdateLogic.AutoCheckDetail(Status()));
        Assert.Equal("preview", UpdateLogic.ChannelFor(true));
        Assert.Equal("stable", UpdateLogic.ChannelFor(false));
        Assert.Equal("stable", UpdateLogic.Channel(null, Status()));
    }
}
