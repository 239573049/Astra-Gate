using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// A subscription account's quota card (web SubscriptionQuotaCard.tsx): one bar per rate-limit window (used share, window length, reset
/// time) in a card, the plan / fetch-time caption and a prepaid balance above, Copilot's premium-request counts below. Windows without a
/// used share draw no bar; with none at all the card says so. The web's entrance animation of the bars is not reproduced (the captured
/// frames are the settled ones).
/// </summary>
internal sealed class SubscriptionQuotaCardView : UserControl
{
    /// <summary>Draws the card of <paramref name="quota"/>.</summary>
    public SubscriptionQuotaCardView(AccountQuota quota)
    {
        var model = QuotaViews.Card(quota);
        var stack = new StackPanel { Spacing = 12 };
        if (model.Caption is not null || model.Balance is not null)
        {
            var line = new WrapPanel { ItemSpacing = 12, LineSpacing = 4 };
            if (model.Caption is { } c) line.Children.Add(AccountsUi.Text(c, 11, ArcDesignTokenKind.TextSecondary));
            if (model.Balance is { } b) line.Children.Add(AccountsUi.Text(b, 11, ArcDesignTokenKind.TextSecondary));
            stack.Children.Add(line);
        }
        if (model.Bars.Count == 0) stack.Children.Add(AccountsUi.Text(Loc.T("providers.subscription.quotaNoWindows"), 12, ArcDesignTokenKind.TextMuted, wrap: true));
        foreach (var bar in model.Bars) stack.Children.Add(Bar(bar));
        if (model.Counts is { } counts) stack.Children.Add(AccountsUi.Text(counts, 10.5, ArcDesignTokenKind.TextMuted, wrap: true));
        // web: <div className="px-4 py-1"><div className="card ... p-3">
        Content = new Border { Padding = new Thickness(16, 4), Child = AccountsUi.Card(stack, new Thickness(12)) };
    }

    private static Control Bar(QuotaBar bar)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(AccountsUi.Text(bar.Label, 11.5, ArcDesignTokenKind.TextSecondary));
        if (bar.Hours is { } h) label.Children.Add(AccountsUi.Text($" · {h}h", 11.5, ArcDesignTokenKind.TextMuted));
        head.Children.Add(label);
        var pct = AccountsUi.Text($"{bar.Percent}%", 11.5, mono: true);
        Grid.SetColumn(pct, 1);
        head.Children.Add(pct);
        var stack = new StackPanel { Children = { head, AccountsUi.ProgressBar(bar.Percent, bar.Tone, bar.Label) } };
        if (bar.Reset is { } reset)
        {
            var r = AccountsUi.Text(reset, 10.5, ArcDesignTokenKind.TextMuted);
            r.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(r);
        }
        return stack;
    }
}
