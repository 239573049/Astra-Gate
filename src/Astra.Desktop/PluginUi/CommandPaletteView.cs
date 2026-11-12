using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using CommandPalette = Astra.Desktop.Core.Plugins.V2.CommandPalette;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// The command palette (design/plugin-v2 05 §8; web <c>plugin-ui/palette.tsx</c>): the app's pages, v2 plugin pages and the commands
/// plugins add to <c>command.palette</c>. Type to filter, ↑/↓ to choose, Enter to run. Also runs plugin keybindings.
/// </summary>
public static class CommandPaletteView
{
    /// <summary>Shows the palette over <paramref name="owner"/>.</summary>
    public static async Task ShowAsync(Visual owner, INavigator navigator, IFeedback feedback)
    {
        var pages = MenuCommandRouter.NavRoutes.Select(kv => (kv.Value, Loc.T("nav." + kv.Key)));
        var all = CommandPalette.Items(PluginShell.Api?.List.Data?.Contributions, pages);
        var shown = all;
        var active = 0;
        var list = new StackPanel { Spacing = 2 };
        var input = new Input { PlaceholderText = Loc.T("palette.placeholder"), Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(input, Loc.T("palette.placeholder"));
        var sheet = new Sheet { Title = Loc.T("palette.title") };
        var mac = OperatingSystem.IsMacOS();

        void Choose(PaletteItem? item)
        {
            if (item is null) return;
            sheet.Close();
            Run(item, navigator, feedback);
        }

        void Draw()
        {
            list.Children.Clear();
            if (shown.Count == 0) list.Children.Add(Ui.Note(Loc.T("palette.empty")));
            for (var i = 0; i < shown.Count; i++)
            {
                var item = shown[i];
                var row = new DockPanel { LastChildFill = true };
                if (item.Keybinding is { } key)
                {
                    var k = KitStyle.Text(CommandPalette.Label(key, mac), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
                    DockPanel.SetDock(k, Dock.Right);
                    row.Children.Add(k);
                }
                if (item.Group != "astra")
                {
                    var g = KitStyle.Text(item.Group, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
                    g.Margin = new Thickness(8, 0);
                    DockPanel.SetDock(g, Dock.Right);
                    row.Children.Add(g);
                    var icon = new LucideIcon { Kind = LucideIconKind.Puzzle, Size = 13, Margin = new Thickness(0, 0, 8, 0) };
                    DockPanel.SetDock(icon, Dock.Left);
                    row.Children.Add(icon);
                }
                row.Children.Add(KitStyle.Text(item.Label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
                var border = new Border { Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6), Child = row, Cursor = new Cursor(StandardCursorType.Hand) };
                if (i == active) border.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                var index = i;
                border.PointerPressed += (_, _) => Choose(shown[index]);
                list.Children.Add(border);
            }
        }

        input.TextChanged += (_, _) =>
        {
            shown = CommandPalette.Filter(all, input.Text);
            active = 0;
            Draw();
        };
        input.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down:
                    active = Math.Min(active + 1, Math.Max(shown.Count - 1, 0));
                    Draw();
                    e.Handled = true;
                    break;
                case Key.Up:
                    active = Math.Max(active - 1, 0);
                    Draw();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Choose(shown.Count > active ? shown[active] : null);
                    e.Handled = true;
                    break;
            }
        };
        Draw();
        sheet.Content = new StackPanel
        {
            Spacing = 10,
            Children = { input, new ScrollViewer { MaxHeight = 420, Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } },
        };
        sheet.Opened += (_, _) => Dispatcher.UIThread.Post(() => input.Focus());
        await sheet.ShowAsync(owner);
    }

    /// <summary>Runs a palette entry: shows its route, or runs the plugin's command.</summary>
    public static void Run(PaletteItem item, INavigator navigator, IFeedback feedback)
    {
        if (item.Target.Route is { } route)
        {
            navigator.Navigate(route, item.Target.Param);
            return;
        }
        if (PluginShell.Api is not { } api || item.Target.Plugin is not { } plugin || item.Target.Command is not { } command) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await api.RunCommandAsync(plugin, command, null);
                var text = result is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : Loc.T("pluginUi.commandRan", ("name", item.Label));
                Dispatcher.UIThread.Post(() => feedback.Success(text));
            }
            catch (Exception e) when (e is HttpRequestException or Core.Api.ApiException)
            {
                Dispatcher.UIThread.Post(() => feedback.Error(item.Label, e.Message));
            }
        });
    }

    /// <summary>
    /// A key press that is a plugin command's keybinding runs it — unless the app already uses the chord (the host wins, 05 §8).
    /// Hooked on the shell as a tunnelling handler.
    /// </summary>
    public static void OnKeyDown(KeyEventArgs e, INavigator navigator, IFeedback feedback)
    {
        if (e.Handled || CodeOf(e.Key) is not { } code) return;
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), alt = m.HasFlag(KeyModifiers.Alt), shift = m.HasFlag(KeyModifiers.Shift), meta = m.HasFlag(KeyModifiers.Meta);
        var mac = OperatingSystem.IsMacOS();
        // the app's own chords: Ctrl on Windows / Linux, ⌘ in the macOS menu
        if (ShortcutMap.CommandFor(code, mac ? meta : ctrl, alt, shift, mac ? ctrl : meta) is not null) return;
        if (CommandPalette.CommandFor(PluginShell.Api?.List.Data?.Contributions, code, ctrl, alt, shift, meta, mac) is not { } cmd) return;
        e.Handled = true;
        Run(new PaletteItem(cmd.Id, cmd.Title, cmd.Plugin, cmd.Keybinding, new PaletteTarget(null, null, cmd.Plugin, cmd.Id)), navigator, feedback);
    }

    /// <summary>The DOM code of letter and digit keys ("KeyG", "Digit3").</summary>
    public static string? CodeOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => "Key" + (char)('A' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => "Digit" + (char)('0' + (key - Key.D0)),
        Key.OemComma => "Comma",
        _ => null,
    };
}
