using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Providers;

/// <summary>What a provider card needs from the page: the writes, feedback and the two ways out (open, test).</summary>
internal sealed record ProviderCardServices(ProviderQueries Queries, IFeedback Feedback, Action<ProviderDto> Open, Action<ProviderDto> Test);

/// <summary>
/// One compact provider card (ProvidersPage.tsx <c>ProviderCard</c>, 116 px): the name row (icon, name, health dot, template-update badge,
/// model count, test button, enabled switch), the host with the credential or the balance, and the last week as <see cref="MiniBars"/> on the
/// shared scale with requests / success rate / cost. Protocols, bound clients and pricing live on the provider's page. The whole card opens
/// the provider; the switch, the test button and the bars do not.
/// </summary>
internal sealed class ProviderCardView : Border
{
    private readonly ProviderCardModel _model;
    private readonly ProviderCardServices _services;

    /// <summary>Builds the card for <paramref name="model"/>.</summary>
    public ProviderCardView(ProviderCardModel model, ProviderStatDto? stat, IReadOnlyList<string> labels, long max, ProviderCardServices services)
    {
        // CardPanel's look (web .card): translucent surface, 0.5 px edge, panel radius.
        BorderThickness = new Thickness(0.5);
        ClipToBounds = true;
        Padding = new Thickness(12, 10);
        Background = CardPanel.SurfaceBrush(null);
        ActualThemeVariantChanged += (_, _) => Background = CardPanel.SurfaceBrush(ActualThemeVariant);
        this.Token(BorderBrushProperty, ArcDesignTokenKind.Border).Token(CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        _model = model;
        _services = services;
        var p = model.Provider;
        Height = 116;
        if (!p.Enabled) Opacity = 0.6;
        var health = ProviderHealthRules.Of(p, stat);

        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 7 };
        rows.Children.Add(NameRow(p, health));
        var second = SecondRow(p);
        Grid.SetRow(second, 1);
        rows.Children.Add(second);
        var stats = StatsRow(stat, labels, max);
        Grid.SetRow(stats, 2);
        rows.Children.Add(stats);
        Child = rows;

        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this, p.Name);
        ToolTip.SetTip(this, p.Name);
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        PointerEntered += (_, _) => this.Token(BorderBrushProperty, ArcDesignTokenKind.BorderStrong);
        PointerExited += (_, _) => this.Token(BorderBrushProperty, ArcDesignTokenKind.Border);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Buttons and the switch mark their release handled, and the bars region stops it too (web stopClick).
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.GetPosition(this) is var pt && pt.X >= 0 && pt.Y >= 0 && pt.X <= Bounds.Width && pt.Y <= Bounds.Height) _services.Open(_model.Provider);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source == this && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            _services.Open(_model.Provider);
        }
    }

    private Control NameRow(ProviderDto p, ProviderHealth health)
    {
        var icon = BrandIcon.Provider(p.Name, p.Icon, 24, p.TemplateId);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var name = KitStyle.Text(p.Name, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        name.FontSize = 14;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var dot = new StatusDot(ProviderPalette.Tone(health)) { VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(dot, Loc.T(ProviderHealthRules.LabelKey(health)));
        var models = KitStyle.Text(_model.ModelCountText, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        models.FontSize = 11.5;
        models.VerticalAlignment = VerticalAlignment.Center;
        var test = ProviderUi.IconButton(LucideIconKind.FlaskConical, Loc.T("providers.test"), () => _services.Test(p));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { test, new ProviderEnabledSwitch(p, _services) } };

        // icon | name | dot | [badge] | (space) | models | actions
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        // The name may take what is left but no more, so the dot stays beside it.
        grid.ColumnDefinitions[1] = new ColumnDefinition(GridLength.Auto) { MaxWidth = 150 };
        Add(grid, icon, 0);
        Add(grid, name, 1);
        Add(grid, dot, 2);
        if (p.TemplateUpdateAvailable) Add(grid, KitBadge.Create(Loc.T("providers.templateUpdate"), Tone.Accent).With(b => b.VerticalAlignment = VerticalAlignment.Center), 3);
        Add(grid, models, 5);
        Add(grid, actions, 6);
        return grid;
    }

    private static void Add(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    // host · balance (or its failure) · else the credential
    private Control SecondRow(ProviderDto p)
    {
        var host = Small(_model.Host ?? "—", ArcDesignTokenKind.TextMuted);
        host.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        ToolTip.SetTip(host, p.Endpoints.FirstOrDefault()?.BaseUrl);
        TextBlock detail;
        var snapshot = QuotaText.Enabled(p) ? QuotaSnapshot.Parse(p.Quota) : null;
        if (QuotaText.Enabled(p) && QuotaText.Failure(snapshot) is { } f)
        {
            detail = Small(Loc.T("providers.quota.failed", ("error", QuotaText.FailureText(f))), ArcDesignTokenKind.Warning);
            ToolTip.SetTip(detail, f.Message);
        }
        else if (QuotaText.Enabled(p) && QuotaText.Summary(snapshot) is { } summary)
        {
            detail = Small(summary, ArcDesignTokenKind.TextSecondary);
            ToolTip.SetTip(detail, summary);
        }
        else
        {
            detail = Small(_model.Credential, ArcDesignTokenKind.TextMuted);
            if (_model.CredentialMono) detail.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        host.MaxWidth = 160;
        Add(grid, host, 0);
        Add(grid, Small("·", ArcDesignTokenKind.TextMuted), 1);
        Add(grid, detail, 2);
        return grid;
    }

    private Control StatsRow(ProviderStatDto? stat, IReadOnlyList<string> labels, long max)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent };
        grid.PointerReleased += (_, e) => e.Handled = true; // scrubbing the bars must not open the provider
        Add(grid, new MiniBars(_model.Requests, max, labels), 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        if (_model.TotalRequests > 0)
        {
            var count = Small(ProviderFormat.FormatTokens(_model.TotalRequests, true), ArcDesignTokenKind.Foreground);
            count.FontWeight = FontWeight.SemiBold;
            right.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 3,
                Children = { count, Small(Loc.T("providers.card.requestsUnit"), ArcDesignTokenKind.TextSecondary) },
            });
            if (stat is { Requests: > 0 } s)
            {
                var rate = Small(StatsFormat.Percent(s.SuccessRate), ProviderPalette.Success(s.SuccessRate));
                ToolTip.SetTip(rate, Loc.T("providers.col.successRate"));
                right.Children.Add(rate);
            }
            if (stat is { CostUsd: > 0 } c) right.Children.Add(Small(ProviderFormat.FormatUsd(c.CostUsd), ArcDesignTokenKind.TextSecondary));
            ToolTip.SetTip(right, Loc.T("providers.card.requests7d"));
        }
        else right.Children.Add(Small(Loc.T("providers.card.noRequests7d"), ArcDesignTokenKind.TextMuted));
        Add(grid, right, 1);
        return grid;
    }

    private static TextBlock Small(string text, ArcDesignTokenKind color)
    {
        var t = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);
        t.FontSize = 11.5;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }
}

/// <summary>A provider's enabled switch (web <c>ProviderEnabledSwitch</c>): writes at once and shows what the server kept when it refuses.</summary>
internal sealed class ProviderEnabledSwitch : ArcSwitch
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ArcSwitch);

    public ProviderEnabledSwitch(ProviderDto p, ProviderCardServices services)
    {
        IsChecked = p.Enabled;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(this, Loc.T("common.enabled"));
        IsCheckedChanged += async (_, _) =>
        {
            var enabled = IsChecked == true;
            if (enabled == p.Enabled) return;
            try
            {
                await services.Queries.UpdateAsync(p.Id, new JsonObject { ["enabled"] = enabled });
            }
            catch (Exception ex)
            {
                services.Feedback.Error(ex.Message);
                IsChecked = p.Enabled; // the server refused: show what it still has
            }
        };
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
