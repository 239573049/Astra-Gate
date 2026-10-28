using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// The page header's right side (web <c>actions</c> of RequestsPage): the summed cost of every request matching the filters, the live RPM / TPM /
/// cache-hit pill of the trailing window (hidden until the first answer arrives), and the refresh button.
/// </summary>
public sealed class RequestHeaderActions : UserControl
{
    private readonly ActionGroup _group = new(8);
    private readonly ArcButton _refresh;
    private long? _totalNanos;
    private RateStatsDto? _rate;

    /// <summary>The actions; <paramref name="onRefresh"/> runs for the refresh button.</summary>
    public RequestHeaderActions(Action onRefresh)
    {
        _refresh = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new LucideIcon { Kind = LucideIconKind.RefreshCw, Size = 16 },
        };
        _refresh.Click += (_, _) => onRefresh();
        Content = _group;
        Rebuild();
    }

    /// <summary>Shows the page's current numbers: the total cost (null = no data yet), the live rate (null = none yet) and whether the list is refetching.</summary>
    public void Update(long? totalCostNanos, RateStatsDto? rate, bool fetching)
    {
        _totalNanos = totalCostNanos;
        _rate = rate;
        _refresh.IsLoading = fetching;
        Rebuild();
    }

    private void Rebuild()
    {
        var text = Loc.T("common.refresh");
        Tooltip.SetTip(_refresh, text);
        Avalonia.Automation.AutomationProperties.SetName(_refresh, text);
        _group.Children.Clear();
        if (_totalNanos is { } nanos) _group.Children.Add(TotalCost(nanos));
        if (_rate is { } rate) _group.Children.Add(LiveRate(rate));
        _group.Children.Add(_refresh);
    }

    private static Border Pill(Control content)
    {
        var pill = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(14, 6), Child = content, VerticalAlignment = VerticalAlignment.Center };
        pill.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderStrong);
        return pill;
    }

    private static Control Entry(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var l = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        l.FontSize = 10.5;
        l.VerticalAlignment = VerticalAlignment.Bottom;
        l.Margin = new Thickness(0, 0, 0, 1);
        var v = KitStyle.Text(value, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground).Num();
        v.FontSize = 12;
        v.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(l);
        row.Children.Add(v);
        return row;
    }

    private static Control TotalCost(long nanos)
    {
        var pill = Pill(Entry(Loc.T("requests.totalCost"), TraceFormat.Nanos(nanos)));
        Tooltip.SetTip(pill, Loc.T("requests.totalCost.hint"));
        Tooltip.SetSide(pill, TooltipSide.Bottom);
        return pill;
    }

    private static Control LiveRate(RateStatsDto rate)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(Entry(Loc.T("requests.rate.rpm"), RequestFormat.RateText(rate.Rpm)));
        row.Children.Add(Entry(Loc.T("requests.rate.tpm"), TraceFormat.Tokens(Math.Round(rate.Tpm, MidpointRounding.AwayFromZero), true)));
        row.Children.Add(Entry(Loc.T("usage.hitRate"), TraceFormat.Percent(rate.CacheHitRate)));
        var pill = Pill(row);
        Tooltip.SetTip(pill, Loc.T("requests.rate.hint", ("window", rate.WindowSeconds)));
        Tooltip.SetSide(pill, TooltipSide.Bottom);
        return pill;
    }
}
