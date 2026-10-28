using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace Astra.Desktop.Platform;

/// <summary>
/// The menu commands and shortcuts of the Electron app (desktop/src/main.ts <c>buildAppMenu</c>, web/src/shell/commands.ts), sent to the
/// page host through <see cref="AvaloniaDesktopHost.RaiseMenuCommand"/> (the web's <c>onMenuCommand</c>). macOS gets a native menu bar whose
/// items carry the Cmd accelerators; Windows and Linux have no menu bar, the window handles Ctrl chords itself (<see cref="ShortcutMap"/>).
/// </summary>
public static class AppMenu
{
    /// <summary>Installs the menu / key handling for <paramref name="window"/>.</summary>
    public static void Install(Application app, Window window, AvaloniaDesktopHost host)
    {
        if (OperatingSystem.IsMacOS())
        {
            var actions = new WindowMenuActions(
                () => window.WindowState = WindowState.Minimized,
                () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
                () => window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen,
                op => Edit(window, op));
            NativeMenuItem? settings = null, aboutItem = null;
            NativeMenu? menu = null;
            void Apply()
            {
                var t = MenuLabels.For(Core.I18n.Loc.Language);
                var built = Build(t, host.RaiseMenuCommand, window.Close, () => _ = host.RevealLogsAsync(), actions);
                // The window's menu is exported once: Avalonia.Native binds its NSMenu to the first NativeMenu instance and a second
                // SetMenu with another instance throws "The menu being updated does not match" (a fatal error on the UI thread, seen
                // when the language was switched in Settings). So the menu is built once and relabelled in place afterwards —
                // item headers are observed by the exporter, the structure never changes.
                if (menu is null) NativeMenu.SetMenu(window, menu = built);
                else Relabel(menu, built);
                if (settings is not null) settings.Header = t.Settings;
                if (aboutItem is not null) aboutItem.Header = Extra.For(Core.I18n.Loc.Language).About;
            }
            Apply();
            // Avalonia supplies the application menu (About, Services, Hide..., Quit); Settings… (Cmd+,) goes into it like Electron's appMenu.
            if (NativeMenu.GetMenu(app) is { } appMenu)
            {
                settings = new NativeMenuItem(MenuLabels.For(Core.I18n.Loc.Language).Settings) { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
                settings.Click += (_, _) => host.RaiseMenuCommand(MenuCommands.Nav("settings"));
                // Avalonia's "About Avalonia" would say the wrong product: replaced by Electron's role 'about' (the standard about panel,
                // which reads the bundle's name, version and icon).
                var about = appMenu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header?.StartsWith("About", StringComparison.Ordinal) == true);
                if (about is not null)
                {
                    var i = appMenu.Items.IndexOf(about);
                    appMenu.Items.RemoveAt(i);
                    if (i < appMenu.Items.Count && appMenu.Items[i] is NativeMenuItemSeparator) appMenu.Items.RemoveAt(i);
                }
                aboutItem = new NativeMenuItem(Extra.For(Core.I18n.Loc.Language).About);
                aboutItem.Click += (_, _) => { if (OperatingSystem.IsMacOS()) Tray.MacApp.ShowAboutPanel(); };
                appMenu.Items.Insert(0, aboutItem);
                appMenu.Items.Insert(1, new NativeMenuItemSeparator());
                appMenu.Items.Insert(2, settings);
                appMenu.Items.Insert(3, new NativeMenuItemSeparator());
            }
            // Electron rebuilds its menu with the locale; here the UI language can change at run time (Settings › General).
            Core.I18n.Loc.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        }
        else window.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(e, host), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Ctrl chords on Windows / Linux; handled chords do not reach the focused control.</summary>
    internal static void OnKeyDown(KeyEventArgs e, AvaloniaDesktopHost host)
    {
        if (CodeFor(e.Key) is not { } code) return;
        var m = e.KeyModifiers;
        var command = ShortcutMap.CommandFor(code, m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Meta));
        if (command is null) return;
        e.Handled = true;
        host.RaiseMenuCommand(command);
    }

    /// <summary>The DOM <c>code</c> of the keys the shortcuts use (the web matches physical keys, not characters).</summary>
    internal static string? CodeFor(Key key) => key switch
    {
        >= Key.D1 and <= Key.D6 => "Digit" + (char)('1' + (key - Key.D1)),
        Key.F => "KeyF",
        Key.R => "KeyR",
        Key.N => "KeyN",
        Key.S => "KeyS",
        Key.OemComma => "Comma",
        _ => null,
    };

    /// <summary>What the Edit and Window menus do to the main window (Electron's roles <c>editMenu</c> / <c>windowMenu</c> / <c>togglefullscreen</c>).</summary>
    internal sealed record WindowMenuActions(Action Minimize, Action Zoom, Action ToggleFullScreen, Action<string> Edit);

    /// <summary>The labels Electron's roles bring with them (macOS localizes them; here zh / en by the UI language, as <see cref="MenuLabels"/>).</summary>
    internal sealed record Extra(string Edit, string Undo, string Redo, string Cut, string Copy, string Paste, string SelectAll,
        string Window, string Minimize, string Zoom, string FullScreen, string About)
    {
        public static Extra For(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new("编辑", "撤销", "重做", "剪切", "拷贝", "粘贴", "全选", "窗口", "最小化", "缩放", "切换全屏幕", "关于 Astra")
            : new("Edit", "Undo", "Redo", "Cut", "Copy", "Paste", "Select All", "Window", "Minimize", "Zoom", "Toggle Full Screen", "About Astra");
    }

    /// <summary>
    /// The Edit menu's commands on the focused text box. The menu's key equivalents (Cmd+C …) take the keystroke before the text box sees
    /// it, so the menu has to perform them itself.
    /// </summary>
    private static void Edit(Window window, string op)
    {
        if (window.FocusManager?.GetFocusedElement() is not TextBox box) return;
        switch (op)
        {
            case "undo": box.Undo(); break;
            case "redo": box.Redo(); break;
            case "cut": box.Cut(); break;
            case "copy": box.Copy(); break;
            case "paste": box.Paste(); break;
            case "selectAll": box.SelectAll(); break;
        }
    }

    /// <summary>
    /// The macOS menu bar besides the application menu: File, Edit, View (pages, find, refresh, sidebar, full screen), Window, Help — the
    /// order of Electron's template (desktop/src/main.ts <c>buildAppMenu</c>). Edit / Window / full screen only with <paramref name="window"/>.
    /// </summary>
    internal static NativeMenu Build(MenuLabels t, Action<string> send, Action closeWindow, Action openLogs, WindowMenuActions? window = null)
    {
        NativeMenuItem Item(string header, string command, KeyGesture? gesture = null)
        {
            var item = new NativeMenuItem(header) { Gesture = gesture };
            item.Click += (_, _) => send(command);
            return item;
        }
        static KeyGesture Cmd(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, KeyModifiers.Meta | extra);

        var close = new NativeMenuItem(CloseLabel(t)) { Gesture = Cmd(Key.W) };
        close.Click += (_, _) => closeWindow();
        var file = new NativeMenuItem(t.File) { Menu = [Item(t.NewProvider, MenuCommands.NewProvider, Cmd(Key.N)), new NativeMenuItemSeparator(), close] };

        var view = new NativeMenu();
        var digit = Key.D1;
        foreach (var target in ShortcutMap.NavOrder)
            view.Items.Add(Item(t.Nav[target], MenuCommands.Nav(target), Cmd(digit++)));
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Item(t.Find, MenuCommands.Find, Cmd(Key.F)));
        view.Items.Add(Item(t.Refresh, MenuCommands.Refresh, Cmd(Key.R)));
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Item(t.ToggleSidebar, MenuCommands.ToggleSidebar, Cmd(Key.S, KeyModifiers.Alt)));

        var logs = new NativeMenuItem(t.OpenLogs);
        logs.Click += (_, _) => openLogs();

        if (window is null) return [file, new NativeMenuItem(t.View) { Menu = view }, new NativeMenuItem(t.Help) { Menu = [logs] }];

        var x = Extra.For(t.File == "文件" ? "zh" : "en");
        NativeMenuItem Act(string header, Action run, KeyGesture? gesture = null)
        {
            var item = new NativeMenuItem(header) { Gesture = gesture };
            item.Click += (_, _) => run();
            return item;
        }
        var edit = new NativeMenuItem(x.Edit)
        {
            Menu =
            [
                Act(x.Undo, () => window.Edit("undo"), Cmd(Key.Z)), Act(x.Redo, () => window.Edit("redo"), Cmd(Key.Z, KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Act(x.Cut, () => window.Edit("cut"), Cmd(Key.X)), Act(x.Copy, () => window.Edit("copy"), Cmd(Key.C)),
                Act(x.Paste, () => window.Edit("paste"), Cmd(Key.V)), Act(x.SelectAll, () => window.Edit("selectAll"), Cmd(Key.A)),
            ],
        };
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Act(x.FullScreen, window.ToggleFullScreen, new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control)));
        var windowMenu = new NativeMenuItem(x.Window) { Menu = [Act(x.Minimize, window.Minimize, Cmd(Key.M)), Act(x.Zoom, window.Zoom)] };
        return [file, edit, new NativeMenuItem(t.View) { Menu = view }, windowMenu, new NativeMenuItem(t.Help) { Menu = [logs] }];
    }

    /// <summary>
    /// Copies the headers of <paramref name="source"/> onto <paramref name="target"/>, item by item and submenu by submenu: both come from
    /// <see cref="Build"/> with the same shape, so the exported menu keeps its items (and their click handlers) and only the labels move.
    /// </summary>
    internal static void Relabel(NativeMenu target, NativeMenu source)
    {
        for (var i = 0; i < target.Items.Count && i < source.Items.Count; i++)
        {
            if (target.Items[i] is not NativeMenuItem to || source.Items[i] is not NativeMenuItem from) continue;
            to.Header = from.Header;
            if (to.Menu is { } sub && from.Menu is { } subSource) Relabel(sub, subSource);
        }
    }

    private static string CloseLabel(MenuLabels t) => t.File == "文件" ? "关闭窗口" : "Close Window";
}
