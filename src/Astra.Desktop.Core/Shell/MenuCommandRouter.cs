namespace Astra.Desktop.Core.Shell;

/// <summary>
/// Where a menu / shortcut command goes (web AppShell <c>useCommands</c> handler): <c>nav:*</c> shows a page, <c>toggle-sidebar</c>
/// folds the sidebar, <c>refresh</c> refetches everything, <c>settings:tray</c> opens Settings › Tray panel, <c>new-provider</c>
/// opens Providers with its new-provider flow; anything else (<c>find</c>) is offered to the current page.
/// </summary>
public sealed class MenuCommandRouter(INavigator navigator, Action toggleSidebar, Action refreshAll, Func<string, bool> pageCommand, Func<string?> currentRoute)
{
    /// <summary>Route parameter that asks Providers to open its new-provider flow (the web's <c>?new=1</c>).</summary>
    public const string NewProviderParam = "new";

    /// <summary><c>NavTarget</c> of web/src/shell/bridge.ts to the app's route names.</summary>
    public static readonly IReadOnlyDictionary<string, string> NavRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["overview"] = "Overview",
        ["requests"] = "Requests",
        ["traces"] = "Traces",
        ["clients"] = "Clients",
        ["tokens"] = "Tokens",
        ["providers"] = "Providers",
        ["models"] = "Models",
        ["plugins"] = "Plugins",
        ["privacy"] = "Privacy",
        ["smartRouting"] = "SmartRouting",
        ["settings"] = "Settings",
    };

    /// <summary>Handles <paramref name="command"/>; false when nothing took it (an unknown nav target, a page without the command).</summary>
    public bool Handle(string command)
    {
        if (command.StartsWith("nav:", StringComparison.Ordinal))
        {
            if (!NavRoutes.TryGetValue(command[4..], out var route)) return false;
            navigator.Navigate(route);
            return true;
        }
        switch (command)
        {
            case MenuCommands.ToggleSidebar:
                toggleSidebar();
                return true;
            case MenuCommands.SettingsTray:
                navigator.Navigate("Settings", "tray");
                return true;
            case MenuCommands.Refresh:
                refreshAll();
                return true;
            case MenuCommands.NewProvider:
                if (currentRoute() is "Providers") return pageCommand(command);
                navigator.Navigate("Providers", NewProviderParam);
                return true;
            default:
                return pageCommand(command);
        }
    }
}

/// <summary>
/// Keyboard shortcuts of Windows / Linux, a port of web/src/shell/commands.ts <c>commandForKey</c> for the desktop (non-mac) case:
/// Ctrl+1..6 pages, Ctrl+F find, Ctrl+R refresh, Ctrl+N new provider, Ctrl+, settings, Ctrl+Alt+S toggle the sidebar. On macOS the
/// native menu owns the same accelerators with Cmd (<see cref="MenuLabels"/>, the app's menu builder).
/// </summary>
public static class ShortcutMap
{
    /// <summary>The page order of Cmd/Ctrl+1..6 (web <c>NAV_ORDER</c>).</summary>
    public static readonly IReadOnlyList<string> NavOrder = ["overview", "requests", "clients", "providers", "models", "settings"];

    /// <summary>The command for a key chord, or null. <paramref name="code"/> is the physical key as the DOM names it ("Digit3", "KeyF", "Comma").</summary>
    public static string? CommandFor(string code, bool ctrl, bool alt, bool shift, bool meta)
    {
        if (!ctrl || meta) return null;
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '1' and <= '6' && !alt)
            return MenuCommands.Nav(NavOrder[code[5] - '1']);
        if (alt && code == "KeyS") return MenuCommands.ToggleSidebar;
        if (alt || shift) return null;
        return code switch
        {
            "KeyF" => MenuCommands.Find,
            "KeyR" => MenuCommands.Refresh,
            "KeyN" => MenuCommands.NewProvider,
            "Comma" => MenuCommands.Nav("settings"),
            _ => null,
        };
    }
}

/// <summary>The native menu's labels (desktop/src/shared/chrome.ts <c>menuLabels</c>): Chinese when the language is zh.</summary>
public sealed record MenuLabels(
    string File, string View, string Help, string Settings, string NewProvider, string Find, string Refresh, string ToggleSidebar, string OpenLogs,
    IReadOnlyDictionary<string, string> Nav)
{
    /// <summary>Labels for "zh" or any other language code (English).</summary>
    public static MenuLabels For(string? language)
    {
        var zh = language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
        string T(string z, string e) => zh ? z : e;
        return new MenuLabels(
            File: T("文件", "File"), View: T("显示", "View"), Help: T("帮助", "Help"), Settings: T("设置…", "Settings…"), NewProvider: T("新建提供商…", "New Provider…"),
            Find: T("搜索", "Find"), Refresh: T("刷新数据", "Reload Data"), ToggleSidebar: T("显示/隐藏侧边栏", "Toggle Sidebar"),
            OpenLogs: T("打开日志目录", "Open Logs Folder"),
            Nav: new Dictionary<string, string>
            {
                ["overview"] = T("概览", "Overview"),
                ["requests"] = T("请求日志", "Requests"),
                ["clients"] = T("客户端", "Clients"),
                ["providers"] = T("提供商", "Providers"),
                ["models"] = T("模型", "Models"),
                ["settings"] = T("设置", "Settings"),
            });
    }
}
