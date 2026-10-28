using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// The runtime tab (web <c>RuntimeTab</c>): two cards, Bun (where it is, its version, a download button) and the plugin host (running or
/// not, what each plugin provides or why it failed, the messages plugins showed lately), and a link to the community repository.
/// </summary>
internal sealed class RuntimeTabView : LiveView
{
    private readonly PluginsPageModel _model;

    /// <summary>Creates the tab over the page model.</summary>
    public RuntimeTabView(PluginsPageModel model)
    {
        _model = model;
        Watch(model.Api.Plugins, "Data");
        Watch(model.Api.Host, "Data");
        Watch(model, nameof(PluginsPageModel.BunPending));
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        var cards = new CardGrid { LargeColumns = 2, MediumColumns = 1, Gap = 16 };
        cards.Children.Add(BunCard(_model.Api.Plugins.Data?.Runtime));
        cards.Children.Add(HostCard(_model.Api.Host.Data));

        var link = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 16, 0, 0), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        var arrow = PageUi.Icon(LucideIconKind.ExternalLink, 12);
        arrow.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        var name = PageUi.Text("astragate-community/astra-gate-plugins", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        name.FontSize = KitStyle.Type12;
        link.Children.Add(arrow);
        link.Children.Add(name);
        link.PointerEntered += (_, _) => name.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        link.PointerExited += (_, _) => name.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        const string repo = "https://github.com/astragate-community/astra-gate-plugins";
        link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) _model.OpenExternal(repo);
        };
        // web: an <a>, so it is reachable with Tab and opens on Enter.
        link.Focusable = true;
        link.KeyDown += (_, e) =>
        {
            if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space)) return;
            e.Handled = true;
            _model.OpenExternal(repo);
        };
        Content = new StackPanel { Children = { cards, link } };
    }

    private static Control Title(string text, Badge? badge)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var title = PageUi.Text(text, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        title.FontSize = 14;
        grid.Children.Add(title);
        if (badge is not null)
        {
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        return grid;
    }

    private static TextBlock Detail(string text)
    {
        var t = PageUi.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
        t.FontSize = KitStyle.Type12;
        t.LineHeight = 19.5;
        return t;
    }

    private Control BunCard(BunStatusDto? bun)
    {
        var stack = new StackPanel { Spacing = 12 };
        var ready = bun is not null && !string.IsNullOrEmpty(bun.Path);
        stack.Children.Add(Title("Bun", bun is null ? null : KitBadge.Create(ready ? Loc.T("plugins.bunReady") : Loc.T("plugins.bunMissing"), ready ? Tone.Green : Tone.Neutral)));
        stack.Children.Add(Detail(Loc.T("plugins.bunDetail")));
        if (bun is not null)
        {
            var kv = new StackPanel { Spacing = 4 };
            if (ready) kv.Children.Add(new KeyValueRow(Loc.T("plugins.bunPath"), bun.Path, mono: true));
            if (!string.IsNullOrEmpty(bun.Version)) kv.Children.Add(new KeyValueRow(Loc.T("plugins.bunVersion"), bun.Version, mono: true));
            kv.Children.Add(new KeyValueRow(Loc.T("plugins.bunTarget"), bun.Target ?? Loc.T("plugins.bunNoBuild"), mono: true));
            stack.Children.Add(kv);
            if (!string.IsNullOrEmpty(bun.Error)) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("plugins.bunFailed"), bun.Error));
            if (!ready)
            {
                var label = bun.Downloading ? Loc.T("plugins.bunDownloading") : Loc.T("plugins.bunDownload", ("version", bun.Version ?? ""));
                var button = PageUi.Button(label, () => _ = _model.InstallBunAsync(), ButtonVariant.Primary, enabled: !bun.Downloading && !_model.BunPending && !string.IsNullOrEmpty(bun.Target));
                button.HorizontalAlignment = HorizontalAlignment.Left;
                button.IsLoading = bun.Downloading || _model.BunPending;
                stack.Children.Add(button);
            }
        }
        return PageUi.Card(stack, new Thickness(16));
    }

    private Control HostCard(PluginHostStatusDto? host)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Title(Loc.T("plugins.host"), host is null ? null : KitBadge.Create(host.Running ? Loc.T("plugins.hostRunning") : Loc.T("plugins.hostStopped"), host.Running ? Tone.Green : Tone.Neutral)));
        stack.Children.Add(Detail(Loc.T("plugins.hostDetail")));
        if (host is not null)
        {
            if (!string.IsNullOrEmpty(host.StartError)) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("plugins.hostStartFailed"), host.StartError));
            if (!string.IsNullOrEmpty(host.GoneReason)) stack.Children.Add(PageUi.Alert(AlertTone.Warning, Loc.T("plugins.hostGone"), host.GoneReason));
            if (host.Plugins.Count > 0)
            {
                var kv = new StackPanel { Spacing = 4 };
                foreach (var p in host.Plugins)
                {
                    var value = !string.IsNullOrEmpty(p.Error) ? Loc.T("plugins.hostPluginError", ("error", p.Error)) : p.Provides.Count > 0 ? string.Join(", ", p.Provides) : PluginFormat.Dash;
                    kv.Children.Add(new KeyValueRow(p.Spec, value, mono: true));
                }
                stack.Children.Add(kv);
            }
            if (host.Recent.Count > 0) stack.Children.Add(Recent(host.Recent));
        }
        return PageUi.Card(stack, new Thickness(16));
    }

    // Web: border-t pt-3, a 12px secondary heading, then one line per message: a dated badge in the message's tone and its text.
    private static Control Recent(IReadOnlyList<PluginHostMessageDto> messages)
    {
        var list = new StackPanel { Spacing = 6 };
        var heading = PageUi.Text(Loc.T("plugins.hostRecent"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
        heading.FontSize = KitStyle.Type12;
        list.Children.Add(heading);
        foreach (var m in messages)
        {
            var tone = m.Variant switch { "error" => Tone.Red, "warning" => Tone.Orange, "success" => Tone.Green, _ => Tone.Neutral };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            var badge = KitBadge.Create(PluginFormat.DateTime(m.At, Loc.Language), tone);
            badge.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(badge);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground).Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
            text.FontSize = KitStyle.Type12;
            if (!string.IsNullOrEmpty(m.Title)) text.Inlines!.Add(new Avalonia.Controls.Documents.Run(m.Title + " ") { FontWeight = FontWeight.Medium });
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(m.Message));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        var box = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = list };
        box.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
        return box;
    }
}
