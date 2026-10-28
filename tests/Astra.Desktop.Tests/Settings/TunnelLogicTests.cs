using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Tests.Settings;

public class TunnelLogicTests
{
    internal static TunnelStatusDto Status(
        bool enabled = false, string state = "off", string mode = "quick", string? hostname = null, bool hasToken = false, bool allowAdmin = false,
        bool passwordSet = false, string? blocker = null, string? error = null, DateTimeOffset? connectedAt = null, string? publicUrl = null,
        CloudflaredStatus? cf = null)
        => new(true, enabled, state, publicUrl, error, connectedAt, mode, hostname, hasToken, allowAdmin, "http://127.0.0.1:17321",
            cf ?? new CloudflaredStatus(null, null, false, null, null, null, null, false, false), new TunnelDownloadDto("auto", null, null),
            new TunnelChecksDto(passwordSet, blocker), []);

    [Fact]
    public void Polls_fast_while_coming_up_every_15s_while_running_and_slowly_while_off()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), TunnelLogic.PollInterval(Status(state: "starting")));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), TunnelLogic.PollInterval(Status(state: "downloading")));
        Assert.Equal(TimeSpan.FromSeconds(15), TunnelLogic.PollInterval(Status(enabled: true, state: "connected")));
        Assert.Equal(TimeSpan.FromSeconds(60), TunnelLogic.PollInterval(Status()));
        Assert.Equal(TimeSpan.FromSeconds(60), TunnelLogic.PollInterval(null));
    }

    [Fact]
    public void Blocker_names_what_is_missing_most_specific_first()
    {
        Assert.Equal("settings.remote.block.token", TunnelLogic.Blocker(Status(mode: "named"))!.Value.Key);
        Assert.Equal("settings.remote.block.hostname", TunnelLogic.Blocker(Status(mode: "named", hasToken: true))!.Value.Key);
        Assert.Equal("settings.remote.block.password", TunnelLogic.Blocker(Status(allowAdmin: true))!.Value.Key);
        Assert.Null(TunnelLogic.Blocker(Status(mode: "named", hasToken: true, hostname: "a.example.com")));
        Assert.Equal("the server says no", TunnelLogic.BlockerText(Status(blocker: "the server says no")));
    }

    [Theory]
    [InlineData("The token can no longer be read", "settings.remote.err.tokenLost")]
    [InlineData("error downloading cloudflared: boom", "settings.remote.err.download")]
    [InlineData("Checksum mismatch", "settings.remote.err.download")]
    [InlineData("Unauthorized: Invalid tunnel secret", "settings.remote.err.token")]
    [InlineData("the token is not valid", "settings.remote.err.token")]
    [InlineData("Unable to reach the origin service", "settings.remote.err.origin")]
    [InlineData("HTTP 429 Too Many Requests", "settings.remote.err.rate")]
    [InlineData("dial tcp: i/o timeout", "settings.remote.err.network")]
    [InlineData("something else entirely", "settings.remote.err.generic")]
    public void FriendlyError_reads_cloudflareds_wording_as_something_to_do(string raw, string key)
        => Assert.Equal(key, TunnelLogic.FriendlyError(raw, "http://127.0.0.1:1").Key);

    [Fact]
    public void Origin_errors_carry_the_origin_url()
    {
        var m = TunnelLogic.FriendlyError("origin server unreachable", "http://127.0.0.1:17321");
        Assert.Contains("http://127.0.0.1:17321", m.Text);
    }

    [Fact]
    public void Errors_show_only_when_enabled_and_down_except_origin_errors()
    {
        Assert.Null(TunnelLogic.ShownError(Status(enabled: false, error: "boom")));
        Assert.Equal("boom", TunnelLogic.ShownError(Status(enabled: true, state: "reconnecting", error: "boom")));
        Assert.Null(TunnelLogic.ShownError(Status(enabled: true, state: "connected", error: "boom")));
        Assert.Equal("origin server down", TunnelLogic.ShownError(Status(enabled: true, state: "connected", error: "origin server down")));
        Assert.True(TunnelLogic.ErrorIsWarning(Status(enabled: true, state: "connected")));
    }

    [Fact]
    public void State_text_counts_the_time_connected()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("settings.remote.connectedJustNow", TunnelLogic.StateText(Status(state: "connected", connectedAt: now.AddSeconds(-20)), now, "en").Key);
        var long_ = TunnelLogic.StateText(Status(state: "connected", connectedAt: now.AddMinutes(-12)), now, "en");
        Assert.Equal("settings.remote.connectedFor", long_.Key);
        Assert.Contains("12 minutes", long_.Text.Replace("zh", ""), StringComparison.Ordinal);
        var downloading = TunnelLogic.StateText(Status(state: "downloading", cf: new CloudflaredStatus(null, null, true, 0.4, null, "x", "github.com", false, false)), now, "en");
        Assert.Equal("settings.remote.state.downloadingFrom", downloading.Key);
        Assert.Equal("settings.remote.state.off", TunnelLogic.StateText(Status(), now, "en").Key);
    }

    [Fact]
    public void Tone_and_progress_follow_the_state()
    {
        Assert.Equal(DotTone.Green, TunnelLogic.Tone(Status(state: "connected")));
        Assert.Equal(DotTone.Neutral, TunnelLogic.Tone(Status()));
        Assert.Equal(DotTone.Orange, TunnelLogic.Tone(Status(state: "starting")));
        var dl = Status(state: "downloading", cf: new CloudflaredStatus(null, null, true, 0.456, null, "a", "github.com", false, false));
        Assert.Equal(46, TunnelLogic.ProgressPercent(dl));
        Assert.Null(TunnelLogic.ProgressPercent(Status()));
    }

    [Fact]
    public void Cloudflared_note_and_program_say_where_the_program_stands()
    {
        Assert.Equal("settings.remote.cloudflaredMissing", TunnelLogic.CloudflaredNote(Status(cf: new CloudflaredStatus(null, "2026.1", false, null, null, "asset", null, false, false)))!.Value.Key);
        Assert.Equal("settings.remote.cloudflaredUnsupported", TunnelLogic.CloudflaredNote(Status())!.Value.Key);
        Assert.Null(TunnelLogic.CloudflaredNote(Status(cf: new CloudflaredStatus("/bin/cf", "1", false, null, null, "a", null, false, false))));

        Assert.Equal("settings.remote.cf.fromEnv", TunnelLogic.Program(new CloudflaredStatus("/p", null, false, null, null, null, null, false, true)).Key);
        Assert.Equal("settings.remote.cf.installedProgram", TunnelLogic.Program(new CloudflaredStatus("/p", "1.2", false, null, null, null, null, true, false)).Key);
        Assert.Equal("settings.remote.cf.installedUnknown", TunnelLogic.Program(new CloudflaredStatus("/p", null, false, null, null, null, null, true, false)).Key);
        Assert.Equal("settings.remote.cf.downloaded", TunnelLogic.Program(new CloudflaredStatus("/p", "1.2", false, null, null, null, null, false, false)).Key);
        Assert.Equal("settings.remote.cf.notDownloaded", TunnelLogic.Program(new CloudflaredStatus(null, "1.2", false, null, null, "asset", null, false, false)).Key);
        Assert.Equal(DotTone.Red, TunnelLogic.ProgramTone(new CloudflaredStatus(null, null, false, null, null, null, null, false, false)));
    }

    [Fact]
    public void Mode_switch_stops_a_running_tunnel_only_when_named_is_incomplete()
    {
        Assert.True(TunnelLogic.ModeSwitchStops(Status(), "named"));
        Assert.False(TunnelLogic.ModeSwitchStops(Status(hasToken: true, hostname: "a.example.com"), "named"));
        Assert.False(TunnelLogic.ModeSwitchStops(Status(), "quick"));
    }

    [Fact]
    public void Named_form_is_dirty_when_a_token_is_typed_or_the_hostname_changed()
    {
        var s = Status(mode: "named", hostname: "a.example.com");
        Assert.False(TunnelLogic.ModeFormDirty(s, "", " a.example.com "));
        Assert.True(TunnelLogic.ModeFormDirty(s, " tok ", "a.example.com"));
        Assert.True(TunnelLogic.ModeFormDirty(s, "", "b.example.com"));
        var req = TunnelLogic.ModeFormRequest("  tok ", " b.example.com ");
        Assert.Equal("tok", req.Token);
        Assert.Equal("b.example.com", req.Hostname);
        Assert.Null(TunnelLogic.ModeFormRequest("", "b.example.com").Token);
    }

    [Fact]
    public void Copy_choices_are_openai_anthropic_and_key_page()
    {
        var c = TunnelLogic.CopyChoices("https://x.trycloudflare.com");
        Assert.Equal(["https://x.trycloudflare.com/v1", "https://x.trycloudflare.com", "https://x.trycloudflare.com/key"], c.Select(x => x.Value));
    }

    [Fact]
    public void Every_message_key_the_logic_returns_exists_in_the_catalog()
    {
        foreach (var key in new[] { "settings.remote.block.token", "settings.remote.err.origin", "settings.remote.connectedFor", "settings.remote.cf.source.custom.hint", "settings.remote.logWithVersion" })
            Assert.True(Loc.Has(key), key);
    }
}
