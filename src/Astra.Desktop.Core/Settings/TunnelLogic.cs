using System.Text.RegularExpressions;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Settings;

/// <summary>Colour of the status dot beside a state (web <c>Dot</c> tones).</summary>
public enum DotTone { Neutral, Green, Orange, Red }

/// <summary>
/// The decisions of Settings › Remote access that are not drawing (web/src/components/RemoteAccessSettings.tsx): why the tunnel cannot
/// start, how cloudflared's own error reads as something to do, the status text and dot, the poll interval, what the cloudflared
/// row says. Every message is a <see cref="Msg"/> with the web's catalog key.
/// </summary>
public static partial class TunnelLogic
{
    /// <summary>The docs page the named-tunnel form links to.</summary>
    public const string GuideUrl = "https://astra-gate.si/docs/guide/remote-access";

    /// <summary>The download sources of the cloudflared selector, in the web's order.</summary>
    public static readonly string[] DownloadSources = ["auto", "github", "astra", "custom"];

    private static readonly string[] BusyStates = ["downloading", "starting", "reconnecting"];

    /// <summary>web <c>useTunnel</c>: quickly while the tunnel is coming up, every 15 s while it runs, now and then while it is off.</summary>
    public static TimeSpan? PollInterval(TunnelStatusDto? s)
        => s is { } status && BusyStates.Contains(status.State) ? TimeSpan.FromMilliseconds(1500)
            : s?.Enabled == true ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60);

    /// <summary>Why the tunnel cannot start, in the UI's language; the server's own sentence is the fallback (web <c>blocker</c>).</summary>
    public static Msg? Blocker(TunnelStatusDto s)
    {
        if (s.Mode == "named" && !s.HasToken) return new Msg("settings.remote.block.token");
        if (s.Mode == "named" && string.IsNullOrEmpty(s.Hostname)) return new Msg("settings.remote.block.hostname");
        if (s.AllowAdmin && !s.Checks.PasswordSet) return new Msg("settings.remote.block.password");
        return s.Checks.Blocker is { } server ? new Msg(server) : null;
    }

    /// <summary>The blocker's text: the catalog sentence, or the server's own words when the "key" is not a catalog key.</summary>
    public static string? BlockerText(TunnelStatusDto s)
        => Blocker(s) is { } m ? (I18n.Loc.Has(m.Key) ? m.Text : m.Key) : null;

    [GeneratedRegex("can no longer be read", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenLost();

    [GeneratedRegex("downloading cloudflared|checksum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Download();

    [GeneratedRegex("unauthorized|invalid tunnel secret|token.*(invalid|not valid|malformed)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BadToken();

    [GeneratedRegex("origin service|origin server", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Origin();

    [GeneratedRegex("429|too many requests|rate.?limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Rate();

    [GeneratedRegex("dial|timeout|timed out|no such host|unreachable|quic|edge", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Network();

    /// <summary>cloudflared's own wording as something to do about it (web <c>friendlyTunnelError</c>); the raw message stays in the output.</summary>
    public static Msg FriendlyError(string raw, string originUrl)
    {
        if (TokenLost().IsMatch(raw)) return new Msg("settings.remote.err.tokenLost");
        if (Download().IsMatch(raw)) return new Msg("settings.remote.err.download");
        if (BadToken().IsMatch(raw)) return new Msg("settings.remote.err.token");
        if (Origin().IsMatch(raw)) return Msg.Of("settings.remote.err.origin", ("origin", originUrl));
        if (Rate().IsMatch(raw)) return new Msg("settings.remote.err.rate");
        if (Network().IsMatch(raw)) return new Msg("settings.remote.err.network");
        return new Msg("settings.remote.err.generic");
    }

    /// <summary>Whether the raw error is the kind that comes from single requests while the tunnel itself is fine.</summary>
    public static bool IsOriginError(string raw) => Origin().IsMatch(raw);

    /// <summary>The raw error to show, or null: only while remote access is on, and while down — except origin errors, which show anyway.</summary>
    public static string? ShownError(TunnelStatusDto s)
    {
        var raw = s.Enabled ? s.Error ?? s.Cloudflared.Error : null;
        return raw is not null && (s.State != "connected" || IsOriginError(raw)) ? raw : null;
    }

    /// <summary>Whether the shown error is a warning (the tunnel is up) rather than a failure.</summary>
    public static bool ErrorIsWarning(TunnelStatusDto s) => s.State == "connected";

    /// <summary>The dot of the status row.</summary>
    public static DotTone Tone(TunnelStatusDto s) => s.State == "connected" ? DotTone.Green : s.State == "off" ? DotTone.Neutral : DotTone.Orange;

    /// <summary>The status text beside the dot (connected for 12 minutes, downloading from github.com, ...).</summary>
    public static Msg StateText(TunnelStatusDto s, DateTimeOffset now, string language)
    {
        if (s.State == "connected" && s.ConnectedAt is { } at)
        {
            var since = (now - at).TotalMilliseconds;
            return since < 60_000
                ? new Msg("settings.remote.connectedJustNow")
                : Msg.Of("settings.remote.connectedFor", ("duration", SettingsFormat.Duration(since, language)));
        }
        if (s.State == "downloading" && !string.IsNullOrEmpty(s.Cloudflared.Source))
            return Msg.Of("settings.remote.state.downloadingFrom", ("source", s.Cloudflared.Source));
        return new Msg("settings.remote.state." + s.State);
    }

    /// <summary>The download percentage shown after the state while cloudflared downloads with a known size; null otherwise.</summary>
    public static int? ProgressPercent(TunnelStatusDto s)
        => s.Cloudflared is { Downloading: true, Progress: { } p } ? (int)Math.Round(p * 100, MidpointRounding.AwayFromZero) : null;

    /// <summary>The note under the switch while remote access is off and nothing blocks it: cloudflared is not at hand.</summary>
    public static Msg? CloudflaredNote(TunnelStatusDto s)
    {
        var cf = s.Cloudflared;
        if (cf.Downloading || !string.IsNullOrEmpty(cf.Path)) return null;
        return !string.IsNullOrEmpty(cf.Asset)
            ? Msg.Of("settings.remote.cloudflaredMissing", ("version", cf.Version ?? ""))
            : new Msg("settings.remote.cloudflaredUnsupported");
    }

    /// <summary>The program row's detail: which cloudflared runs (web <c>program</c>).</summary>
    public static Msg Program(CloudflaredStatus cf)
    {
        if (cf.FromEnvironment) return new Msg("settings.remote.cf.fromEnv");
        if (cf.Installed)
            return cf.Version is { } v ? Msg.Of("settings.remote.cf.installedProgram", ("version", v)) : new Msg("settings.remote.cf.installedUnknown");
        if (!string.IsNullOrEmpty(cf.Path)) return Msg.Of("settings.remote.cf.downloaded", ("version", cf.Version ?? ""));
        return !string.IsNullOrEmpty(cf.Asset)
            ? Msg.Of("settings.remote.cf.notDownloaded", ("version", cf.Version ?? ""))
            : new Msg("settings.remote.cloudflaredUnsupported");
    }

    /// <summary>The program row's dot: green when a program is at hand, neutral when it can be downloaded, red when it cannot.</summary>
    public static DotTone ProgramTone(CloudflaredStatus cf)
        => !string.IsNullOrEmpty(cf.Path) ? DotTone.Green : !string.IsNullOrEmpty(cf.Asset) ? DotTone.Neutral : DotTone.Red;

    /// <summary>The title of the log disclosure: with the cloudflared version when known.</summary>
    public static Msg LogTitle(TunnelStatusDto s)
        => !string.IsNullOrEmpty(s.Cloudflared.Path) && !string.IsNullOrEmpty(s.Cloudflared.Version)
            ? Msg.Of("settings.remote.logWithVersion", ("version", s.Cloudflared.Version))
            : new Msg("settings.remote.log");

    /// <summary>Whether switching to <paramref name="mode"/> while running stops the tunnel (a named tunnel not filled in yet).</summary>
    public static bool ModeSwitchStops(TunnelStatusDto s, string mode) => mode == "named" && (!s.HasToken || string.IsNullOrEmpty(s.Hostname));

    /// <summary>Whether the named-tunnel form has unsaved input.</summary>
    public static bool ModeFormDirty(TunnelStatusDto s, string token, string hostname)
        => token.Trim() != "" || hostname.Trim() != (s.Hostname ?? "");

    /// <summary>The body of the named-tunnel save: the hostname (null clears) and, only when typed, the token.</summary>
    public static TunnelConfigRequest ModeFormRequest(string token, string hostname)
        => new(null, hostname.Trim() is { Length: > 0 } h ? h : "", token.Trim() is { Length: > 0 } t ? t : null, null);

    /// <summary>Whether the custom-source address can be saved: typed, and different from what is stored.</summary>
    public static bool CustomSourceSavable(TunnelStatusDto s, string baseUrl, bool saving)
        => baseUrl.Trim() != "" && !saving && !(baseUrl.Trim() == (s.Download.DownloadBaseUrl ?? "") && s.Download.DownloadSource == "custom");

    /// <summary>The copy-menu entries of a connected tunnel: label key and the text copied.</summary>
    public static IReadOnlyList<(string LabelKey, string Value)> CopyChoices(string publicUrl)
        => [("settings.remote.copy.openai", publicUrl + "/v1"), ("settings.remote.copy.anthropic", publicUrl), ("settings.remote.copy.key", publicUrl + "/key")];
}
