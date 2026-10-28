using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Settings;

/// <summary>Which machines may reach the gateway: this one only, or the whole network.</summary>
public enum ListenScope { Local, Lan }

/// <summary>
/// Settings › Gateway › Listen (web/src/components/ListenSettings.tsx): listen on 127.0.0.1 or on 0.0.0.0. The address goes to
/// config.json and applies at the next start, so a pending change offers a restart. LAN access needs an admin password.
/// </summary>
public static class ListenLogic
{
    /// <summary>The shortest admin password the server accepts.</summary>
    public const int MinPasswordLength = 8;

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1", "[::1]"];

    /// <summary>The scope a configured host means.</summary>
    public static ListenScope ScopeOf(string host) => LoopbackHosts.Contains(host) ? ListenScope.Local : ListenScope.Lan;

    /// <summary>The host config.json stores for a scope.</summary>
    public static string HostFor(ListenScope scope) => scope == ListenScope.Local ? "127.0.0.1" : "0.0.0.0";

    /// <summary>"host:port" of what the process is bound to.</summary>
    public static string Address(ListenDto s) => $"{s.Host}:{s.Port}";

    /// <summary>A change was saved that a restart has to apply.</summary>
    public static bool Pending(ListenDto s) => s.ConfiguredHost != s.Host;

    /// <summary>
    /// The row's detail line: the environment decides (ASTRA_HOST) / the request comes from another device / a change waits for a restart
    /// (the web adds "restart with astra restart" only where the page has no restart button) / the bound address.
    /// </summary>
    public static Msg? Detail(ListenDto s, bool fromOtherDevice)
    {
        if (s.HostFromEnvironment) return Msg.Of("settings.listen.env", ("address", Address(s)));
        if (fromOtherDevice) return Msg.Of("settings.listen.localOnly", ("address", Address(s)));
        if (Pending(s)) return Msg.Of("settings.listen.pending", ("host", s.ConfiguredHost));
        return null;
    }

    /// <summary>The detail as text, including the plain address and the web-only restart hint.</summary>
    public static string DetailText(ListenDto s, bool fromOtherDevice, bool isDesktop)
    {
        var msg = Detail(s, fromOtherDevice);
        if (msg is null) return Address(s);
        return Pending(s) && !s.HostFromEnvironment && !fromOtherDevice && !isDesktop
            ? $"{msg.Value.Text} · {new Msg("settings.listen.restartHint").Text}"
            : msg.Value.Text;
    }

    /// <summary>Whether the scope switch is offered: not when the environment or another device decides.</summary>
    public static bool ControlsVisible(ListenDto s, bool fromOtherDevice) => !s.HostFromEnvironment && !fromOtherDevice;

    /// <summary>Whether the restart button shows: a change is pending and the shell can restart the service.</summary>
    public static bool RestartVisible(ListenDto s, bool isDesktop) => Pending(s) && isDesktop;

    /// <summary>Whether the password row shows: LAN is chosen or other machines already have addresses.</summary>
    public static bool PasswordRowVisible(ListenDto s) => ScopeOf(s.ConfiguredHost) == ListenScope.Lan || s.NetworkUrls.Count > 0;

    /// <summary>What choosing a scope needs first.</summary>
    public enum Step
    {
        /// <summary>Already there.</summary>
        None,

        /// <summary>Save at once (back to this machine only).</summary>
        Save,

        /// <summary>LAN without an admin password: ask for one first.</summary>
        AskPassword,

        /// <summary>LAN with a password: confirm exposing the gateway.</summary>
        ConfirmLan,
    }

    /// <summary>The first step of choosing <paramref name="next"/>.</summary>
    public static Step Choose(ListenDto s, ListenScope next)
    {
        if (next == ScopeOf(s.ConfiguredHost)) return Step.None;
        if (next == ListenScope.Lan) return s.PasswordSet ? Step.ConfirmLan : Step.AskPassword;
        return Step.Save;
    }

    /// <summary>Whether the toast "saved, restart to apply" follows a save: only when the answer still differs from the bound host.</summary>
    public static bool SavedNeedsRestartToast(ListenDto saved) => Pending(saved);

    /// <summary>The new-password form is valid: at least 8 characters and typed twice the same.</summary>
    public static bool PasswordValid(string password, string repeat) => password.Length >= MinPasswordLength && password == repeat;

    /// <summary>The repeat field disagrees (shown once something is typed in it).</summary>
    public static bool PasswordMismatch(string password, string repeat) => repeat != "" && repeat != password;
}
