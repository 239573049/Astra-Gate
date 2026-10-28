using Astra.Api.Contracts;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The usage breakdown shown on account cards, account details and a provider's API-key tab (web AccountUsageStats.tsx): requests, tokens
/// and cost, today and in total, as a small right-aligned table (first column 34 %). <paramref name="compact"/> is the card's 11 px variant
/// whose numbers are abbreviated ("12.3K", "$1.2K"); the full value is the tooltip. A missing usage shows dashes.
/// </summary>
public sealed class AccountUsageStatsView : UserControl
{
    private readonly bool _compact;
    private ProviderAccountUsageDto? _usage;

    private readonly ArcDesignTokenKind _valueColor;

    /// <summary>
    /// Creates the table for <paramref name="usage"/>. The numbers carry no color of their own in the web (they inherit): pass
    /// <see cref="AccountsUi.Inline"/> as <paramref name="valueColor"/> where the table sits inline in the provider page's tab panel.
    /// </summary>
    public AccountUsageStatsView(ProviderAccountUsageDto? usage, bool compact = false, ArcDesignTokenKind valueColor = ArcDesignTokenKind.Foreground)
    {
        _compact = compact;
        _valueColor = valueColor;
        _usage = usage;
        Build();
    }

    /// <summary>The usage shown; setting it redraws the numbers.</summary>
    public ProviderAccountUsageDto? Usage
    {
        get => _usage;
        set
        {
            if (Equals(_usage, value)) return;
            _usage = value;
            Build();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var size = _compact ? 11 : 12;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34*,33*,33*") };
        Avalonia.Automation.AutomationProperties.SetName(grid, Loc.T("providers.usage.title"));
        var u = _usage;
        var rows = new (string Label, long? Today, long? Total, Func<long, bool, string> Format)[]
        {
            (Loc.T("providers.usage.requests"), u?.Today.Requests, u?.Total.Requests, AccountRules.Count),
            (Loc.T("providers.usage.tokens"), u?.Today.Tokens, u?.Total.Tokens, AccountRules.Count),
            (Loc.T("providers.usage.cost"), u?.Today.CostNanoUsd, u?.Total.CostNanoUsd, AccountRules.Nanos),
        };
        for (var r = 0; r <= rows.Length; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Header: an empty first cell, then "today" / "total" (web thead, muted, pb-1).
        Add(grid, AccountsUi.Text(Loc.T("providers.usage.today"), size, ArcDesignTokenKind.TextMuted), 0, 1, new Thickness(0, 0, 0, 4), right: true);
        Add(grid, AccountsUi.Text(Loc.T("providers.usage.total"), size, ArcDesignTokenKind.TextMuted), 0, 2, new Thickness(0, 0, 0, 4), right: true);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            Add(grid, AccountsUi.Text(row.Label, size, ArcDesignTokenKind.TextSecondary), i + 1, 0, new Thickness(0, 2));
            Add(grid, Value(row.Today, row.Format, size), i + 1, 1, new Thickness(4, 2, 0, 2), right: true);
            Add(grid, Value(row.Total, row.Format, size), i + 1, 2, new Thickness(4, 2, 0, 2), right: true);
        }
        Content = grid;
    }

    private TextBlock Value(long? value, Func<long, bool, string> format, double size)
    {
        var text = AccountsUi.Text(value is { } v ? format(v, _compact) : "—", size, _valueColor, mono: true);
        if (value is { } full) ToolTip.SetTip(text, format(full, false));
        return text;
    }

    private static void Add(Grid grid, TextBlock cell, int row, int column, Thickness margin, bool right = false)
    {
        cell.Margin = margin;
        cell.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        cell.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>
    /// The note under a usage table (web <c>ProviderUsageHint</c>): what is counted, and which time zone "today" follows when the server
    /// says.
    /// </summary>
    public static TextBlock Hint(string? timeZone)
    {
        var text = Loc.T("providers.usage.hint") + (string.IsNullOrEmpty(timeZone) ? "" : " " + Loc.T("providers.usage.dayHint", ("zone", timeZone)));
        var block = AccountsUi.Note(text);
        block.LineHeight = 17.875; // leading-relaxed at 11 px
        return block;
    }
}
