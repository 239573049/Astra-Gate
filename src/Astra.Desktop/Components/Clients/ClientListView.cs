using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The master side of the Clients page (web <c>ClientList</c> / <c>ClientListItem</c>): a 232 px card of clients grouped by install state
/// ("installed · n", "not detected · n", "coming soon · n") in the catalog order; each row shows the client's mark, name, detected version
/// (and the update it can get), a spinner while an install runs, an update / shadowed badge, and an orange dot when drifted or a green
/// check when enabled. The selected row has the accent tint. (The web folds this into a horizontal strip below 768 px of content width;
/// the desktop window is never that narrow beside the sidebar's list, so only the column layout is built.)
/// </summary>
public sealed class ClientListView : UserControl
{
    private readonly StackPanel _stack = new();

    /// <summary>An empty list.</summary>
    public ClientListView()
    {
        var card = new Border { Padding = new Thickness(6), BorderThickness = new Thickness(0.5), Child = _stack, Width = 232, VerticalAlignment = VerticalAlignment.Top };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        Content = card;
    }

    /// <summary>Draws <paramref name="clients"/> (null = still loading) with <paramref name="selected"/> highlighted.</summary>
    public void Show(IReadOnlyList<ClientInfoDto>? clients, bool loading, string selected, Action<string> onSelect)
    {
        _stack.Children.Clear();
        if (loading) _stack.Children.Add(new LoadingState(6) { Margin = new Thickness(8) });
        foreach (var group in ClientsLogic.Groups(clients))
        {
            var heading = ClientUi.Text(Loc.T(group.TitleKey) + " · " + group.Items.Count, 11, ArcDesignTokenKind.TextMuted, FontWeight.Medium);
            heading.Margin = new Thickness(10, 8, 10, 4);
            // The web's body line height (1.45) on every text line here: 11 px → 16, 13 px → 19 (a 47 px row like the web's).
            heading.LineHeight = 16;
            _stack.Children.Add(heading);
            foreach (var c in group.Items) _stack.Children.Add(Item(c, c.Kind == selected, () => onSelect(c.Kind)));
        }
    }

    private static Control Item(ClientInfoDto client, bool active, Action onSelect)
    {
        var dim = ClientsLogic.GroupOf(client) != ClientGroup.Installed;
        var version = ClientsLogic.VersionOf(client);
        var shadowed = ClientsLogic.IsShadowed(client);
        var update = ClientsLogic.UpdateOf(client);
        var busy = client.Install?.Busy == true;
        var name = ClientCatalog.Name(client.Kind, client.Name);

        var icon = BrandIcon.Client(client.Kind, 24, fallbackName: client.Name);
        if (dim && !active) icon.Opacity = 0.6;

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = ClientUi.Text(name, 13, ArcDesignTokenKind.Foreground, active ? FontWeight.Medium : FontWeight.Normal);
        title.LineHeight = 19;
        texts.Children.Add(title);
        if (version is not null)
        {
            var line = new TextBlock { FontSize = 11, LineHeight = 16, TextTrimming = TextTrimming.CharacterEllipsis };
            var v = new Avalonia.Controls.Documents.Run(version);
            v.Token(Avalonia.Controls.Documents.TextElement.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            line.Inlines!.Add(v);
            if (update is not null)
            {
                var u = new Avalonia.Controls.Documents.Run(" → " + update);
                u.Token(Avalonia.Controls.Documents.TextElement.ForegroundProperty, ArcDesignTokenKind.Warning);
                line.Inlines.Add(u);
            }
            texts.Children.Add(line);
        }

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (busy) trailing.Children.Add(ClientUi.Spinner());
        if (update is not null && !busy) trailing.Children.Add(KitBadge.Create(Loc.T("clients.updateBadge"), Tone.Orange, Loc.T("clients.updateTo", ("version", update))));
        if (shadowed && !busy) trailing.Children.Add(KitBadge.Create(Loc.T("clients.shadowedBadge"), Tone.Orange, Loc.T("clients.shadowed.title", ("version", client.Install?.LatestVersion ?? ""))));
        if (client.Status == "drifted")
        {
            var dot = new StatusDot(Tone.Orange);
            ToolTip.SetTip(dot, Loc.T("clients.drifted"));
            trailing.Children.Add(dot);
        }
        else if (client.Enabled)
        {
            var check = ClientUi.Icon(LucideIconKind.Check, 14, ArcDesignTokenKind.Success);
            check.StrokeThickness = 2.5;
            Avalonia.Automation.AutomationProperties.SetName(check, Loc.T("clients.enabled"));
            trailing.Children.Add(check);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var row = new Border { Padding = new Thickness(10, 6), Child = grid, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
        row.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        Avalonia.Automation.AutomationProperties.SetName(row, name);
        if (!active)
        {
            row.PointerEntered += (_, _) => row.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        }
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) onSelect();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                onSelect();
            }
        };
        if (!active) return row;
        // web bg-[var(--accent-subtle)] = the accent at 14 % (Arc's AccentSubtle is a neutral wash, not the accent).
        var tint = new Border { Opacity = 0.14, IsHitTestVisible = false };
        tint.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        return new Panel { Children = { tint, row } };
    }
}
