using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Shell;

/// <summary>
/// The non-drawing rules of the shell's top banners and status card (web/src/components/layout/ConnectionBanner.tsx, AppShell.tsx
/// <c>ServiceStatus</c>, Page.tsx). The views only bind to what these return.
/// </summary>
public static class ShellBanners
{
    /// <summary>
    /// The enabled clients whose config still points at an old Astra address or setting (web <c>outdated</c>, plan §7.1): none while the
    /// server is down, since a client list from a server that is not running says nothing.
    /// </summary>
    public static IReadOnlyList<string> OutdatedKinds(IReadOnlyList<ClientInfoDto>? clients, bool down)
        => down || clients is null ? [] : clients.Where(c => c.ConfigOutdated).Select(c => c.Kind).ToList();

    /// <summary>
    /// Names for <paramref name="kinds"/>, comma separated (web <c>clientNames</c>): each kind's name as the server reports it in the list,
    /// else the kind itself.
    /// </summary>
    public static string ClientNames(IReadOnlyList<ClientInfoDto>? clients, IEnumerable<string> kinds)
        => string.Join(", ", kinds.Select(k => clients?.FirstOrDefault(c => c.Kind == k)?.Name ?? k));

    /// <summary>Remote access is on and the server is up: the sidebar always says so (web <c>remoteOn</c>), so it is never left on by accident.</summary>
    public static bool RemoteOn(bool connected, TunnelStatusDto? tunnel) => connected && tunnel?.Enabled == true;

    /// <summary>
    /// The sidebar toggle's tooltip (web Page.tsx): the label plus the desktop shortcut, <c>⌘⌥S</c> on macOS and <c>Ctrl+Alt+S</c> elsewhere.
    /// </summary>
    public static string ToggleTooltip(string label, bool mac) => $"{label} ({(mac ? "⌘⌥" : "Ctrl+Alt+")}S)";

    /// <summary>The rail's status dot tooltip (web <c>ServiceStatus</c> rail): the status, plus the remote-access note when it is on.</summary>
    public static string RailTooltip(string status, bool remoteOn, string remoteOnText) => remoteOn ? $"{status} · {remoteOnText}" : status;
}
