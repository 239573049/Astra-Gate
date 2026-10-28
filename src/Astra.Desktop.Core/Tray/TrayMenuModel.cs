using System.Text;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>A client as the tray menu needs it (desktop/src/shared/types.ts <c>GatewayClient</c>).</summary>
public sealed record TrayClient(string Kind, string Name, bool Enabled, string? ProviderId);

/// <summary>A provider as the tray menu needs it (<c>GatewayProvider</c>).</summary>
public sealed record TrayProvider(string Id, string Name, bool Enabled);

/// <summary>Everything the tray icon, its menu and the panel header show about the service (desktop/src/shared/trayMenu.ts <c>TrayState</c>).</summary>
/// <param name="Running">The service answers its health probe.</param>
/// <param name="Port">Its port, when known.</param>
/// <param name="ApiVersionMismatch">It speaks another API major.</param>
/// <param name="Clients">Clients (for per-client provider switching).</param>
/// <param name="Providers">Providers (the switch targets).</param>
/// <param name="UpdateAvailable">Non-null when an update check found something newer.</param>
/// <param name="Activity">A start / stop / restart the tray started and is still waiting on.</param>
/// <param name="OpenAtLogin">Launch-at-login state; null where unsupported.</param>
public sealed record TrayState(
    bool Running, int? Port, bool ApiVersionMismatch, IReadOnlyList<TrayClient> Clients, IReadOnlyList<TrayProvider> Providers,
    string? UpdateAvailable, TrayActivity? Activity, bool? OpenAtLogin)
{
    /// <summary>Before the first probe.</summary>
    public static TrayState Initial { get; } = new(false, null, false, [], [], null, null, null);

    /// <summary>The panel's view of it (<c>trayPanelState</c>).</summary>
    public TrayPanelState Panel => new(Running, Port, Activity, ApiVersionMismatch, UpdateAvailable);

    /// <summary>Change-detection key (<c>trayStateKey</c>): the menu is rebuilt only when this changes, so polling does not churn native menus.</summary>
    public string Key()
    {
        var b = new StringBuilder();
        b.Append(Running).Append('|').Append(Port).Append('|').Append(ApiVersionMismatch).Append('|').Append(UpdateAvailable).Append('|')
            .Append(Activity).Append('|').Append(OpenAtLogin).Append('|');
        foreach (var c in Clients) b.Append(c.Kind).Append(',').Append(c.Name).Append(',').Append(c.Enabled).Append(',').Append(c.ProviderId).Append(';');
        b.Append('|');
        foreach (var p in Providers) b.Append(p.Id).Append(',').Append(p.Name).Append(',').Append(p.Enabled).Append(';');
        return b.ToString();
    }
}

/// <summary>What a tray menu item does.</summary>
public enum TrayMenuVerb { None, OpenWindow, ShowPanel, Navigate, Start, Stop, Restart, CopyApiAddress, OpenInBrowser, OpenLogs, SwitchProvider, ToggleOpenAtLogin, CheckUpdates, Quit }

/// <summary>An item's action with its arguments (nav target; client kind + provider id; the login item's next state).</summary>
public sealed record TrayMenuAction(TrayMenuVerb Verb, string? Arg = null, string? Arg2 = null, bool Flag = false);

/// <summary>How an item draws.</summary>
public enum TrayMenuItemKind { Normal, Separator, Checkbox, Radio }

/// <summary>One native menu item (Electron's <c>MenuItemConstructorOptions</c>, the parts the tray uses).</summary>
public sealed record TrayMenuItem(
    string Label = "", TrayMenuAction? Action = null, bool Enabled = true, TrayMenuItemKind Kind = TrayMenuItemKind.Normal, bool Checked = false,
    string? Sublabel = null, IReadOnlyList<TrayMenuItem>? Submenu = null, bool QuitAccelerator = false)
{
    /// <summary>A separator.</summary>
    public static TrayMenuItem Separator { get; } = new(Kind: TrayMenuItemKind.Separator);
}

/// <summary>The tray menu's own labels (desktop/src/shared/trayMenu.ts <c>trayLabels</c>: Chinese for zh, English otherwise; not in the web catalog).</summary>
public sealed record TrayMenuLabels(bool Zh)
{
    private string T(string zh, string en) => Zh ? zh : en;

    /// <summary>Labels for a language code ("zh…" = Chinese).</summary>
    public static TrayMenuLabels For(string? language) => new(language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true);

#pragma warning disable CS1591 // one label each, named after Electron's
    public string Running(int? port) => Zh ? $"服务运行中 · 端口 {Port(port)}" : $"Service running on port {Port(port)}";
    public string Stopped => T("服务未运行", "Service not running");
    public string Starting => T("正在启动服务…", "Starting service…");
    public string Stopping => T("正在停止服务…", "Stopping service…");
    public string Restarting => T("正在重启服务…", "Restarting service…");
    public string Mismatch => T("API 版本不匹配，请运行 \"astra update\"", "API version mismatch — run \"astra update\"");
    public string UpdateAvailable => T("有可用更新…", "Update Available…");
    public string OpenWindow => T("打开 Astra", "Open Astra");
    public string ShowPanel => T("显示托盘面板", "Show Tray Panel");
    public string GoTo => T("前往", "Go To");
    public string Clients => T("客户端提供商", "Client Providers");
    public string NoProviders => T("未配置提供商", "No providers configured");
    public string DisabledProvider(string name) => Zh ? $"{name}（已停用）" : $"{name} (disabled)";
    public string Unbound => T("未绑定", "Not bound");
    public string StartService => T("启动服务", "Start Service");
    public string StopService => T("停止服务", "Stop Service");
    public string RestartService => T("重启服务", "Restart Service");
    public string CopyApiAddress => T("复制 API 地址", "Copy API Address");
    public string OpenInBrowser => T("在浏览器中打开", "Open in Browser");
    public string OpenLogs => T("打开日志目录", "Open Logs Folder");
    public string OpenAtLogin => T("登录时启动", "Launch at Login");
    public string CheckUpdates => T("检查更新…", "Check for Updates…");
    public string Quit => T("退出 Astra", "Quit Astra");
    public string TooltipRunning(int? port) => Zh ? $"Astra — 服务运行中（端口 {Port(port)}）" : $"Astra — running on port {Port(port)}";
    public string TooltipStopped => T("Astra — 服务未运行", "Astra — service not running");
    public string TooltipMismatch => T("Astra — API 版本不匹配", "Astra — API version mismatch");
    public string ClosedToTrayTitle => T("Astra 仍在运行", "Astra is still running");
    public string ClosedToTray(string platform) => platform == "darwin"
        ? T("窗口已关闭，网关继续在后台运行。点击菜单栏中的 Astra 图标可重新打开或退出。",
            "The window is closed but the gateway keeps running. Use the Astra icon in the menu bar to reopen or quit.")
        : T("窗口已关闭，网关继续在后台运行。点击托盘中的 Astra 图标可重新打开，右键可退出。",
            "The window is closed but the gateway keeps running. Click the Astra tray icon to reopen it, or right-click to quit.");
#pragma warning restore CS1591

    /// <summary>The label of an in-flight activity.</summary>
    public string Of(TrayActivity a) => a switch { TrayActivity.Starting => Starting, TrayActivity.Stopping => Stopping, _ => Restarting };

    private static string Port(int? port) => port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
}

/// <summary>
/// The tray / menu-bar menu as a platform-neutral model (desktop/src/shared/trayMenu.ts <c>buildTrayMenuTemplate</c>): status, window
/// shortcuts, per-client provider switching, service control and Quit. The platform layer turns it into an <c>NSMenu</c> (macOS) or an
/// Avalonia <c>NativeMenu</c> (Windows / Linux).
/// </summary>
public static class TrayMenuModel
{
    /// <summary>Hover text for the tray icon (<c>trayTooltip</c>).</summary>
    public static string Tooltip(TrayState state, TrayMenuLabels t)
    {
        if (state.Activity is { } a) return "Astra — " + t.Of(a);
        if (!state.Running) return t.TooltipStopped;
        if (state.ApiVersionMismatch) return t.TooltipMismatch;
        return t.TooltipRunning(state.Port);
    }

    /// <summary>The menu for <paramref name="state"/> on <paramref name="platform"/> ("darwin" | "win32" | "linux").</summary>
    public static List<TrayMenuItem> Build(TrayState state, TrayMenuLabels t, string platform, string? version, MenuLabels nav)
    {
        var mac = platform == "darwin";
        var busy = state.Activity is not null;
        var items = new List<TrayMenuItem>
        {
            // Status block.
            new(string.IsNullOrEmpty(version) ? "Astra" : $"Astra {version}", Enabled: false),
            new(state.Activity is { } a ? t.Of(a) : state.Running ? t.Running(state.Port) : t.Stopped, Enabled: false),
        };
        if (state.Running && state.ApiVersionMismatch) items.Add(new(t.Mismatch, Enabled: false));
        if (state.UpdateAvailable is not null) items.Add(new(t.UpdateAvailable, new(TrayMenuVerb.CheckUpdates)));

        // Window.
        items.Add(TrayMenuItem.Separator);
        items.Add(new(t.OpenWindow, new(TrayMenuVerb.OpenWindow)));
        if (platform == "linux") items.Add(new(t.ShowPanel, new(TrayMenuVerb.ShowPanel)));
        items.Add(new(t.GoTo, Submenu: ShortcutMap.NavOrder.Select(target => new TrayMenuItem(nav.Nav[target], new(TrayMenuVerb.Navigate, target))).ToList()));

        // Per-client provider switching (needs the server).
        var enabled = state.Clients.Where(c => c.Enabled).ToList();
        if (enabled.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
            items.Add(new(t.Clients, Enabled: false));
            foreach (var client in enabled)
            {
                var current = state.Providers.FirstOrDefault(p => p.Id == client.ProviderId);
                var currentName = current?.Name ?? t.Unbound;
                var sub = state.Providers.Select(p => new TrayMenuItem(
                    p.Enabled ? p.Name : t.DisabledProvider(p.Name), new(TrayMenuVerb.SwitchProvider, client.Kind, p.Id),
                    Kind: TrayMenuItemKind.Radio, Checked: client.ProviderId is not null && client.ProviderId == p.Id)).ToList();
                if (sub.Count == 0) sub.Add(new(t.NoProviders, Enabled: false));
                // macOS 14.4+ renders a native sublabel; elsewhere the binding goes inline.
                items.Add(new(mac ? client.Name : $"{client.Name} · {currentName}", Enabled: state.Running && !busy, Sublabel: mac ? currentName : null, Submenu: sub));
            }
        }

        // Service control.
        items.Add(TrayMenuItem.Separator);
        if (state.Running)
        {
            items.Add(new(t.StopService, new(TrayMenuVerb.Stop), Enabled: !busy));
            items.Add(new(t.RestartService, new(TrayMenuVerb.Restart), Enabled: !busy));
        }
        else items.Add(new(t.StartService, new(TrayMenuVerb.Start), Enabled: !busy));
        var reachable = state.Running && state.Port is not null;
        items.Add(new(t.CopyApiAddress, new(TrayMenuVerb.CopyApiAddress), Enabled: reachable));
        items.Add(new(t.OpenInBrowser, new(TrayMenuVerb.OpenInBrowser), Enabled: reachable));

        // App.
        items.Add(TrayMenuItem.Separator);
        if (state.OpenAtLogin is { } on)
            items.Add(new(t.OpenAtLogin, new(TrayMenuVerb.ToggleOpenAtLogin, Flag: !on), Kind: TrayMenuItemKind.Checkbox, Checked: on));
        items.Add(new(t.CheckUpdates, new(TrayMenuVerb.CheckUpdates)));
        items.Add(new(t.OpenLogs, new(TrayMenuVerb.OpenLogs)));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(t.Quit, new(TrayMenuVerb.Quit), QuitAccelerator: mac));
        return items;
    }
}
