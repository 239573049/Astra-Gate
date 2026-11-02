using Astra.Api.Contracts;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The strip above the client panel while this machine is connected to a unified gateway: whose token it is, where the gateway is, how many
/// clients go through it, and the two things to do (connect more clients, disconnect). Nothing is shown while no gateway is connected;
/// the page's header button is the way in then.
/// </summary>
public static class ConnectBanner
{
    /// <summary>The banner for <paramref name="status"/>, or null when no gateway is connected (or the status has not loaded).</summary>
    public static Control? Create(ConnectStatusDto? status, Action more, Action leave)
    {
        if (status is not { Connected: true }) return null;
        var title = PdUi.Inline(8,
            PdUi.Badge(Loc.T("connect.banner.title"), Astra.Desktop.Components.Kit.Tone.Green),
            PdUi.Text(status.TokenName, 13, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        var gateway = PdUi.Text(status.GatewayUrl, 12, ArcDesignTokenKind.TextSecondary, mono: true);
        var detail = PdUi.Text(status.Clients.Count == 0 ? Loc.T("connect.banner.none") : Loc.T("connect.banner.clients", ("n", status.Clients.Count)),
            12, ArcDesignTokenKind.TextSecondary);
        var left = PdUi.Stack(4, title, gateway, detail);
        if (status.Error is { Length: > 0 } error)
            left.Children.Add(PdUi.Text(Loc.T("connect.banner.error", ("error", error)), 11, ArcDesignTokenKind.TextMuted, wrap: true));
        var buttons = PdUi.Inline(8, PdUi.Button(Loc.T("connect.banner.manage"), PdUi.Look.Glass, click: more), PdUi.Button(Loc.T("connect.leave"), PdUi.Look.Glass, click: leave));
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        var card = PdUi.Card(row);
        card.Margin = new Thickness(0, 0, 0, 16);
        return card;
    }
}
