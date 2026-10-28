using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Avalonia.Controls;

namespace Astra.Desktop.Platform.Tray;

/// <summary>
/// Pop-up menus: the bridge's <c>showContextMenu</c> (the tray panel's client switch) and, on Windows / Linux, the tray menu opened from the
/// panel's "more" button (Avalonia cannot pop a tray icon's own menu there). macOS uses a native <c>NSMenu</c> at the mouse like Electron's
/// <c>Menu.popup</c>; elsewhere an Avalonia <see cref="ContextMenu"/> on the given control.
/// </summary>
internal static class ContextMenus
{
    /// <summary>Shows <paramref name="items"/>; resolves with the chosen id, or null when dismissed.</summary>
    public static Task<string?> ShowAsync(IReadOnlyList<ContextMenuItem> items, Control? owner)
    {
        if (OperatingSystem.IsMacOS())
        {
            string? picked = null;
            using var menu = MacMenu.Build(items, id => picked = id);
            // Runs the menu's tracking loop; the choice is known when it returns.
            menu.PopUpAtMouse();
            return Task.FromResult(picked);
        }
        if (owner is null) return Task.FromResult<string?>(null);
        var done = new TaskCompletionSource<string?>();
        var context = new ContextMenu();
        foreach (var item in items)
        {
            if (item.Separator)
            {
                context.Items.Add(new Separator());
                continue;
            }
            var entry = new MenuItem { Header = item.Label, IsEnabled = item.Enabled };
            if (item.Checkbox)
            {
                entry.ToggleType = MenuItemToggleType.CheckBox;
                entry.IsChecked = item.Checked;
            }
            var id = item.Id;
            entry.Click += (_, _) => done.TrySetResult(id);
            context.Items.Add(entry);
        }
        context.Closed += (_, _) => done.TrySetResult(null);
        context.Open(owner);
        return done.Task;
    }

    /// <summary>The tray menu as a popup menu on <paramref name="owner"/> (Windows / Linux "more" button); resolves when it closed.</summary>
    public static Task ShowTrayMenuAsync(IReadOnlyList<TrayMenuItem> items, Control owner, Action<TrayMenuAction> onSelect)
    {
        var done = new TaskCompletionSource();
        var context = new ContextMenu();
        foreach (var entry in Build(items, onSelect)) context.Items.Add(entry);
        context.Closed += (_, _) => done.TrySetResult();
        context.Open(owner);
        return done.Task;
    }

    private static IEnumerable<Control> Build(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuAction> onSelect)
    {
        foreach (var item in items)
        {
            if (item.Kind == TrayMenuItemKind.Separator)
            {
                yield return new Separator();
                continue;
            }
            var entry = new MenuItem { Header = item.Sublabel is { } sub ? $"{item.Label} · {sub}" : item.Label, IsEnabled = item.Enabled };
            if (item.Kind is TrayMenuItemKind.Checkbox or TrayMenuItemKind.Radio)
            {
                entry.ToggleType = item.Kind == TrayMenuItemKind.Radio ? MenuItemToggleType.Radio : MenuItemToggleType.CheckBox;
                entry.IsChecked = item.Checked;
            }
            if (item.Submenu is { } children)
                foreach (var child in Build(children, onSelect)) entry.Items.Add(child);
            else if (item.Action is { } action) entry.Click += (_, _) => onSelect(action);
            yield return entry;
        }
    }
}
