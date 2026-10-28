using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// One installed plugin (web <c>PluginRow</c>): its picture, name, version, kind badges and spec; move up/down for gateway middleware,
/// the enable switch and a menu (options, update, remove); error boxes; and the grey block of what it does (middleware counters, the client
/// an agent adds, the providers it signs in to).
/// </summary>
internal static class PluginRowView
{
    /// <summary>Builds the row for <paramref name="row"/>; <paramref name="index"/> / <paramref name="count"/> decide which move buttons are enabled.</summary>
    public static Control Create(PluginRowModel row, PluginsPageModel model, int index, int count)
    {
        var p = row.Plugin;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(16, 14) };
        // web: a disabled plugin is faded and its brand icon grayscale (opacity-70 / grayscale)
        if (!p.Enabled) grid.Opacity = 0.7;

        var icon = PluginIconView.Create(p.ShortName, p.Icon, row.Kind);
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);

        var body = new StackPanel { Spacing = 8 };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        body.Children.Add(Header(row, model, index, count));
        foreach (var alert in row.Alerts) body.Children.Add(PageUi.Alert(AlertTone.Danger, alert.Title, alert.Text));
        if (row.Details.Count > 0) body.Children.Add(Details(row, model));
        return grid;
    }

    private static Control Header(PluginRowModel row, PluginsPageModel model, int index, int count)
    {
        var p = row.Plugin;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };

        var name = new StackPanel { Spacing = 0 };
        var line = PageUi.Wrap(6, 4);
        var title = PageUi.Text(p.ShortName, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        title.FontSize = 14;
        title.LineHeight = 20; // the web's 1.45 body line height (here and below: 11 px → 16, 12 px → 17)
        line.Children.Add(title);
        if (row.VersionText is { } version) line.Children.Add(PageUi.Text(version, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, mono: true));
        foreach (var badge in row.Badges) line.Children.Add(PageUi.Badge(badge));
        name.Children.Add(line);
        var spec = PageUi.Text(p.Spec, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, mono: true);
        spec.LineHeight = 16;
        ToolTip.SetTip(spec, p.Spec);
        name.Children.Add(spec);
        header.Children.Add(name);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        if (row.HasMiddleware)
        {
            actions.Children.Add(PageUi.IconButton(LucideIconKind.ArrowUp, Loc.T("plugins.moveUp"), () => _ = model.MoveAsync(index, -1), enabled: index > 0));
            actions.Children.Add(PageUi.IconButton(LucideIconKind.ArrowDown, Loc.T("plugins.moveDown"), () => _ = model.MoveAsync(index, 1), enabled: index < count - 1));
        }
        var toggle = new Switch { IsChecked = p.Enabled, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T("plugins.enabled"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked is not { } on || on == p.Enabled) return;
            _ = Revert();
            async Task Revert()
            {
                if (!await model.SetEnabledAsync(p, on)) toggle.IsChecked = p.Enabled;
            }
        };
        actions.Children.Add(toggle);
        actions.Children.Add(Menu(row, model));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        return header;
    }

    private static Control Menu(PluginRowModel row, PluginsPageModel model)
    {
        var p = row.Plugin;
        var menu = new DropdownMenu { Label = Loc.T("common.more"), Alignment = MenuAlignment.End, Trigger = PageUi.IconButton(LucideIconKind.Ellipsis, Loc.T("common.more"), null, ControlSize.Medium) };
        // The trigger button is the anchor of the popup: it must not scale on press.
        if (menu.Trigger is TKCode.Arc.Button trigger) trigger.IsPressScaleEnabled = false;
        menu.Items.Add(new MenuEntry { Header = Loc.T("plugins.options"), Icon = PageUi.Icon(LucideIconKind.Settings2), Command = new RelayCommand(() => model.Editing = p) });
        if (row.CanUpdate)
            menu.Items.Add(new MenuEntry { Header = Loc.T("plugins.update"), Icon = PageUi.Icon(LucideIconKind.RefreshCw), Command = new RelayCommand(() => _ = model.UpdateAsync(p)) });
        menu.Items.Add(new MenuEntry
        {
            Header = Loc.T("common.delete"),
            Icon = PageUi.Icon(LucideIconKind.Trash2),
            IsDestructive = true,
            HasSeparatorBefore = true,
            Command = new RelayCommand(() => _ = model.RemoveAsync(p)),
        });
        menu.VerticalAlignment = VerticalAlignment.Center;
        return menu;
    }

    // Web: flex flex-col gap-1.5 rounded-lg bg-[var(--surface-muted)] px-3 py-2, each line a 56px label and its text.
    private static Control Details(PluginRowModel row, PluginsPageModel model)
    {
        var lines = new StackPanel { Spacing = 6 };
        foreach (var detail in row.Details)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*"), ColumnSpacing = 12 };
            var label = PageUi.Text(detail.Label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            label.FontSize = KitStyle.Type12;
            label.LineHeight = 17;
            label.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(label);
            var content = Content(detail, row.Plugin, model);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            lines.Children.Add(grid);
        }
        var box = new Border { Padding = new Thickness(12, 8), Child = lines };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        return box;
    }

    private static Control Content(PluginDetail detail, Astra.Api.Contracts.PluginDto plugin, PluginsPageModel model)
    {
        TextBlock Secondary(string text)
        {
            var t = PageUi.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
            t.FontSize = KitStyle.Type12;
            t.LineHeight = 17;
            return t;
        }

        switch (detail)
        {
            case MiddlewareDetail mw:
            {
                var stack = new StackPanel { Spacing = 0, Children = { Secondary(mw.Stats) } };
                if (mw.LastError is { } err)
                {
                    var t = PageUi.Text(err, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Danger, wrap: true);
                    t.FontSize = KitStyle.Type12;
                    t.LineHeight = 17;
                    stack.Children.Add(t);
                }
                return stack;
            }
            case AgentDetail agent:
                return PageUi.Link(agent.Text, () => model.OpenClient(agent.ClientKind));
            case ProvidesDetail provides:
            {
                var stack = new StackPanel { Spacing = 4 };
                var head = PageUi.Wrap(8, 2, Secondary(provides.Text), PageUi.Link(provides.OpenProvidersText, model.OpenProviders));
                stack.Children.Add(head);
                foreach (var served in provides.ServedBy)
                {
                    var warn = PageUi.Text(served.Text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Warning, wrap: true);
                    warn.FontSize = KitStyle.Type12;
                    warn.LineHeight = 17;
                    stack.Children.Add(PageUi.Wrap(8, 4, warn, PageUi.Button(served.ButtonText, () => _ = model.PreferAsync(plugin, served.Provider), size: ControlSize.Small)));
                }
                if (provides.PickedText is { } picked)
                {
                    var t = PageUi.Text(picked, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, wrap: true);
                    t.FontSize = KitStyle.Type12;
                    t.LineHeight = 17;
                    stack.Children.Add(t);
                }
                return stack;
            }
            default:
                return new Panel();
        }
    }
}
