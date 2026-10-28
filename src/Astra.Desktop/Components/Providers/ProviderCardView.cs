using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Providers;

/// <summary>What a provider card needs from the page: the writes, feedback and the two ways out (open, test).</summary>
internal sealed record ProviderCardServices(ProviderQueries Queries, IFeedback Feedback, Action<ProviderDto> Open, Action<ProviderDto> Test);

/// <summary>
/// One provider card (ProvidersPage.tsx <c>ProviderCard</c>): Arc <see cref="Card"/> with a tinted media header (brand icon, state pill, host,
/// model count), title, protocol line, bound clients, the balance line, the last-week sparkline and a footer (credential, price, test button,
/// enabled switch). The whole card opens the provider; the footer controls, the balance line and the sparkline do not.
/// </summary>
internal sealed class ProviderCardView : Card
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Card);

    private readonly ProviderCardModel _model;
    private readonly ProviderCardServices _services;
    private readonly IReadOnlyList<string> _days;

    /// <summary>Builds the card for <paramref name="model"/>.</summary>
    public ProviderCardView(ProviderCardModel model, IReadOnlyList<string> days, decimal costUsd, ProviderCardServices services)
    {
        _model = model;
        _services = services;
        _days = days;
        var p = model.Provider;
        Title = model.Title;
        Description = model.Description;
        Media = BuildMedia(model);
        Meta = new TextBlock { Text = model.Credential, TextTrimming = TextTrimming.CharacterEllipsis }.With(t =>
        {
            if (model.CredentialMono) t.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        });
        Status = model.Pricing;
        Action = BuildAction();
        Content = BuildBody(costUsd);
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this, p.Name);
        ToolTip.SetTip(this, p.Name);
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The web card is <c>data-density="compact"</c> with Tailwind overrides on Arc's card parts (no media minimum height, 12 px padding, a
    /// one-line 15 px title, a two-line 12 px description, a 44 px footer). Arc's Avalonia card keeps those numbers in an internal token class, so
    /// the same overrides are applied as local values on the template parts. // ARC-GAP: a public "Compact" density on Card would replace this.
    /// </remarks>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Control>("PART_Zoom") is { } zoom) zoom.MinHeight = 0;
        if (e.NameScope.Find<Control>("PART_Body") is { } body) body.Margin = new Thickness(12);
        if (e.NameScope.Find<Control>("PART_Title") is { } title)
        {
            title.SetValue(TextBlock.FontSizeProperty, 15.0);
            title.SetValue(TextBlock.LineHeightProperty, 15.0 * 1.3); // web card.module.css .title line-height: 1.3
            title.Margin = new Thickness(0);
        }
        if (e.NameScope.Find<TextBlock>("PART_Description") is { } description)
        {
            description.FontSize = 12;
            description.LineHeight = 12 * 1.4;
            description.MaxLines = 2;
            description.TextTrimming = TextTrimming.CharacterEllipsis;
            description.MinHeight = 2 * description.LineHeight;
            description.Margin = new Thickness(0, 4, 0, 0); // web .card[data-density="compact"] .description { margin-top: 4px }
        }
        // The byline reserves a gap for an avatar slot this card never fills.
        if (e.NameScope.Find<Grid>("PART_Byline") is { } byline) byline.ColumnSpacing = 0;
        if (e.NameScope.Find<Control>("PART_Footer") is { } footer)
        {
            footer.MinHeight = 36;
            footer.Margin = new Thickness(0, 8, 0, 0);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Buttons and the switch mark their release handled, and the balance / sparkline regions stop it too (web stopClick).
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

    private static Control BuildMedia(ProviderCardModel m)
    {
        var p = m.Provider;
        var icon = BrandIcon.Provider(p.Name, p.Icon, 28);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        if (m.State == CardState.Disabled)
        {
            icon.Opacity = 0.5;
            icon.Effect = null; // ARC-GAP: no grayscale effect in Avalonia; the 50% opacity alone marks a disabled provider.
        }

        var tone = m.State switch { CardState.Disabled => Tone.Neutral, CardState.NoKey => Tone.Orange, _ => Tone.Green };
        var pill = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1), Padding = new Thickness(8, 2) };
        pill.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPill);
        pill.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface);
        pill.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new StatusDot(tone), KitStyle.Text(m.StateLabel, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary) },
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(icon);
        Grid.SetColumn(pill, 1);
        top.Children.Add(pill);

        var host = KitStyle.Text(m.Host ?? "—", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        host.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        ToolTip.SetTip(host, p.Endpoints.FirstOrDefault()?.BaseUrl);
        var models = KitStyle.Text(m.ModelCountText, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        models.HorizontalAlignment = HorizontalAlignment.Right;
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        bottom.Children.Add(host);
        Grid.SetColumn(models, 1);
        bottom.Children.Add(models);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6, Margin = new Thickness(12, 10) };
        grid.Children.Add(top);
        Grid.SetRow(bottom, 1);
        grid.Children.Add(bottom);
        // web: linear-gradient(135deg, accent 12%, transparent 70%) behind the header
        return new AccentTint { Child = grid };
    }

    private Control BuildAction()
    {
        var p = _model.Provider;
        var test = ProviderUi.IconButton(LucideIconKind.FlaskConical, Loc.T("providers.test"), () => _services.Test(p));
        var toggle = new ArcSwitch { IsChecked = p.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T("common.enabled"));
        toggle.IsCheckedChanged += async (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            if (enabled == p.Enabled) return;
            try
            {
                await _services.Queries.UpdateAsync(p.Id, new JsonObject { ["enabled"] = enabled });
            }
            catch (Exception ex)
            {
                _services.Feedback.Error(ex.Message);
                toggle.IsChecked = p.Enabled; // the server refused: show what it still has
            }
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { test, toggle } };
    }

    private Control BuildBody(decimal costUsd)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildClients());
        stack.Children.Add(BuildQuota());
        stack.Children.Add(BuildSparkline(costUsd));
        return stack;
    }

    // Fixed one-row slot (also when empty) so every card keeps the same height; overflow collapses into "+N".
    private Control BuildClients()
    {
        var p = _model.Provider;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Height = 20, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
        if (p.TemplateUpdateAvailable) row.Children.Add(KitBadge.Create(Loc.T("providers.templateUpdate"), Tone.Accent));
        foreach (var kind in _model.VisibleClients)
        {
            if (BrandIconCatalog.IsKnownClient(kind))
            {
                var icon = BrandIcon.Client(kind, 18);
                ToolTip.SetTip(icon, Astra.Desktop.Core.Overview.ClientCatalog.Name(kind)); // web title={clientName(k)}
                row.Children.Add(icon);
            }
            else row.Children.Add(KitBadge.Create(kind, Tone.Green));
        }
        if (_model.OverflowClients.Count > 0)
        {
            var more = KitStyle.Text("+" + _model.OverflowClients.Count, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            ToolTip.SetTip(more, string.Join(", ", _model.OverflowClients.Select(k => Astra.Desktop.Core.Overview.ClientCatalog.Name(k)))); // web CLIENT_META[k]?.name ?? k
            row.Children.Add(more);
        }
        return row;
    }

    // Balance / quota of the provider's usage query; its refresh button must not open the provider. Reserves its row when absent.
    private Control BuildQuota()
    {
        var p = _model.Provider;
        var slot = new Border { MinHeight = 32, Margin = new Thickness(0, 4, 0, 0) };
        slot.PointerReleased += (_, e) => e.Handled = true;
        if (!QuotaText.Enabled(p)) return slot;

        var snapshot = QuotaSnapshot.Parse(p.Quota);
        var summary = QuotaText.Summary(snapshot);
        var failure = QuotaText.Failure(snapshot);
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (summary is not null)
        {
            var s = KitStyle.Text(summary, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            s.FontSize = KitStyle.Type12;
            ToolTip.SetTip(s, summary);
            lines.Children.Add(s);
        }
        if (failure is { } f)
        {
            var text = Loc.T("providers.quota.failed", ("error", QuotaText.FailureText(f)));
            var w = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Warning);
            w.FontSize = KitStyle.Type12;
            ToolTip.SetTip(w, f.Message);
            lines.Children.Add(w);
        }
        if (summary is null && failure is null)
        {
            var never = KitStyle.Text(Loc.T("providers.quota.never"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            never.FontSize = KitStyle.Type12;
            lines.Children.Add(never);
        }
        var refresh = ProviderUi.IconButton(LucideIconKind.Gauge, Loc.T("providers.quota.refresh"));
        refresh.Click += async (_, _) =>
        {
            refresh.IsLoading = true;
            try
            {
                await _services.Queries.FetchQuotaAsync(p.Id);
            }
            catch (Exception ex)
            {
                _services.Feedback.Error(ex.Message);
            }
            finally
            {
                refresh.IsLoading = false;
            }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(lines);
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);
        slot.Child = row;
        return slot;
    }

    // Scrubbing the trend must not open the provider.
    private Control BuildSparkline(decimal costUsd)
    {
        var holder = new Border { Margin = new Thickness(0, 8, 0, 0) };
        holder.PointerReleased += (_, e) => e.Handled = true;
        var label = Loc.T("providers.card.requests7d");
        if (_model.TotalRequests > 0)
        {
            holder.Child = new Sparkline
            {
                Label = label,
                ItemsSource = _model.Requests.Select(r => (double)r).ToArray(),
                PointLabels = _days.Select(d => d[5..]).ToList(),
                Value = ProviderFormat.FormatTokens(_model.TotalRequests, true),
                Change = ProviderFormat.FormatUsd(costUsd),
                DrawingWidth = 320,
                DrawingHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            return holder;
        }

        // Same footprint as the sparkline, so cards with and without traffic line up.
        var empty = new Panel { Height = 24, Margin = new Thickness(0, 8, 0, 0) };
        var dash = new Avalonia.Controls.Shapes.Line { StartPoint = new Point(0, 0), EndPoint = new Point(100, 0), Stretch = Stretch.Fill, StrokeThickness = 1, StrokeDashArray = [4, 3], VerticalAlignment = VerticalAlignment.Bottom };
        dash.Token(Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.Border);
        empty.Children.Add(dash);
        empty.Children.Add(KitStyle.Text(Loc.T("providers.card.noRequests"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted).With(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        var title = KitStyle.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary);
        holder.Child = new StackPanel { Children = { title, empty } };
        return holder;
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

/// <summary>
/// A border painted with the accent fading out diagonally (web: <c>linear-gradient(135deg, accent 12%, transparent 70%)</c>), re-read when the
/// theme or accent changes. // ARC-GAP: Arc has no token that is "accent at 12% opacity"; AccentSubtle is a solid wash and would not fade.
/// </summary>
internal sealed class AccentTint : Border
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnTheme;
        Apply();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var c = ArcTokens.GetColor(this, ArcDesignTokenKind.Accent);
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb((byte)(0.12 * 255), c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.7) },
        };
    }
}

/// <summary>Whether the brand catalog knows a client kind (web <c>k in CLIENT_META</c>).</summary>
internal static class BrandIconCatalog
{
    public static bool IsKnownClient(string kind) => BrandCatalog.Client(kind) is not null;
}
