using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// The body of the model detail page (web <c>ModelDetailPage</c>): error alert, loading skeleton, or — in a column at most 1100 px wide,
/// two columns from a 1280 px window like the web's <c>xl:</c> — the basic fields, capabilities, provider prices and overrides on the left
/// and the price editor and billing simulator on the right.
/// </summary>
public sealed class ModelDetailView : UserControl
{
    private const double MaxWidthPx = 1100;
    private const double WideBreakpoint = 1280;

    private readonly ModelDetailPage _page;
    private readonly PriceSheet _priceSheet;
    private readonly StackPanel _host = new();
    private Grid _columns = new();
    private StackPanel _left = new();
    private StackPanel _right = new();
    private BillingSimulator? _simulator;
    private TextBlock? _unsaved;
    private IDisposable? _sizeSub;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public ModelDetailView(ModelDetailPage page)
    {
        _page = page;
        _priceSheet = new PriceSheet(page.Ctx, page.PriceKeys);
        Content = _host;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page.FormReplaced += Rebuild;
        _page.FormEdited += OnEdited;
        _page.PropertyChanged += OnPageChanged;
        _page.PriceKeys.PropertyChanged += OnKeysChanged;
        Loc.LanguageChanged += OnLanguage;
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _sizeSub = top.GetObservable(TopLevel.ClientSizeProperty).Subscribe(new ActionObserver<Size>(_ => ApplyColumns()));
            ApplyColumns();
        }
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _page.FormReplaced -= Rebuild;
        _page.FormEdited -= OnEdited;
        _page.PropertyChanged -= OnPageChanged;
        _page.PriceKeys.PropertyChanged -= OnKeysChanged;
        Loc.LanguageChanged -= OnLanguage;
        _sizeSub?.Dispose();
        _sizeSub = null;
    }

    private void OnLanguage() { }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelDetailPage.IsError)) Rebuild();
    }

    private void OnKeysChanged(object? sender, PropertyChangedEventArgs e) { }

    // ---------- states ----------

    private void Rebuild()
    {
        // the host keeps the price sheet; everything else is rebuilt
        _host.Children.Clear();
        if (_page.IsError)
        {
            _host.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _page.ErrorText });
            return;
        }
        if (_page.Detail is not { } model || _page.Form is not { } form)
        {
            _host.Children.Add(new LoadingState(5) { MaxWidth = MaxWidthPx, HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }

        _left = new StackPanel();
        _right = new StackPanel();
        _left.Children.Add(BasicGroup(model, form));
        _left.Children.Add(CapabilitiesGroup(model, form));
        _left.Children.Add(ProviderPrices(model));
        if (model.ProviderOverrides.Count > 0) _left.Children.Add(Overrides(model));
        _right.Children.Add(PricingSection(model, form));
        _right.Children.Add(SimulatorSection(model, form));

        _columns = new Grid { ColumnSpacing = 24 };
        _columns.Children.Add(_left);
        _columns.Children.Add(_right);
        ApplyColumns();
        // mx-auto max-w-[1100px]
        _columns.MaxWidth = MaxWidthPx;
        _columns.HorizontalAlignment = HorizontalAlignment.Stretch;
        _host.Children.Add(_columns);
        _host.Children.Add(_priceSheet);
    }

    /// <summary>Two columns when the window is at least 1280 px wide (Tailwind <c>xl</c> tests the viewport, not the column).</summary>
    private void ApplyColumns()
    {
        var wide = (TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 0) >= WideBreakpoint;
        _columns.ColumnDefinitions.Clear();
        _columns.RowDefinitions.Clear();
        if (wide)
        {
            _columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(_left, 0);
            Grid.SetRow(_left, 0);
            Grid.SetColumn(_right, 1);
            Grid.SetRow(_right, 0);
        }
        else
        {
            _columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _columns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _columns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(_left, 0);
            Grid.SetRow(_left, 0);
            Grid.SetColumn(_right, 0);
            Grid.SetRow(_right, 1);
        }
    }

    private void OnEdited()
    {
        if (_page.Detail is not { } model || _page.Form is not { } form) return;
        ApplySimulator(model, form);
    }

    // ---------- rows ----------

    /// <summary>The "modified" badge and, for a seeded model, the reset button of a field the user changed (web <c>marker</c>).</summary>
    private Control? Marker(ModelDetailDto model, string field)
    {
        if (!model.UserModifiedFields.Contains(field)) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(KitBadge.Create(Loc.T("models.modified"), Tone.Orange));
        if (model.Source == "seed")
        {
            var reset = PricingUi.Button(null, PricingUi.Look.Plain, LucideIconKind.RotateCcw, small: true, click: () => _ = _page.ResetFieldAsync(field), name: Loc.T("models.resetField"));
            ToolTip.SetTip(reset, Loc.T("models.resetField"));
            row.Children.Add(reset);
        }
        return row;
    }

    private static SettingsRow FieldRow(string label, Control? marker, Control editor)
    {
        var labelStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(PricingUi.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
        if (marker is not null) labelStack.Children.Add(marker);
        var row = new SettingsRow { Label = labelStack };
        row.Trailing.Add(editor);
        return row;
    }

    private Control BasicGroup(ModelDetailDto model, ModelForm form)
    {
        var group = new SettingsGroup { Title = Loc.T("models.section.basic"), Footer = model.Source == "seed" ? Loc.T("models.modifiedFooter") : null };
        group.Rows.Add(FieldRow(Loc.T("models.field.displayName"), Marker(model, "displayName"),
            PricingUi.TextBox(form.DisplayName, v => _page.Edit(f => f with { DisplayName = v }), width: 240, name: Loc.T("models.field.displayName"))));
        group.Rows.Add(FieldRow(Loc.T("models.field.vendor"), null,
            PricingUi.TextBox(form.Vendor, v => _page.Edit(f => f with { Vendor = v }), width: 240, name: Loc.T("models.field.vendor"))));
        group.Rows.Add(FieldRow(Loc.T("models.field.family"), Marker(model, "family"),
            PricingUi.TextBox(form.Family ?? "", v => _page.Edit(f => f.WithFamily(v)), width: 240, name: Loc.T("models.field.family"))));
        group.Rows.Add(FieldRow(Loc.T("models.field.aliases"), Marker(model, "aliases"),
            PricingUi.TextBox(form.AliasText, v => _page.Edit(f => f with { AliasText = v }), width: 240, mono: true, name: Loc.T("models.field.aliases"), placeholder: "a, b, c")));
        group.Rows.Add(FieldRow(Loc.T("models.field.contextWindow"), Marker(model, "contextWindow"),
            PricingUi.Number(form.ContextWindow, v => _page.Edit(f => f with { ContextWindow = v is { } d ? (long)d : null }), width: 144, name: Loc.T("models.field.contextWindow"), integer: true)));
        group.Rows.Add(FieldRow(Loc.T("models.field.maxOutputTokens"), Marker(model, "maxOutputTokens"),
            PricingUi.Number(form.MaxOutputTokens, v => _page.Edit(f => f with { MaxOutputTokens = v is { } d ? (long)d : null }), width: 144, name: Loc.T("models.field.maxOutputTokens"), integer: true)));
        var enabled = new Switch { IsChecked = form.Enabled, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(enabled, Loc.T("common.enabled"));
        enabled.IsCheckedChanged += (_, _) => _page.Edit(f => f with { Enabled = enabled.IsChecked == true });
        group.Rows.Add(FieldRow(Loc.T("common.enabled"), null, enabled));
        return group;
    }

    /// <summary>A group heading that holds controls (web passes a node as the <c>title</c>): text and an optional marker or action.</summary>
    private static Control Heading(string text, Control? trailing, bool actionRight)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(actionRight ? "*,Auto" : "Auto,*"), ColumnSpacing = 8, Margin = new Thickness(4, 0, 4, 8) };
        var title = PricingUi.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
        title.FontSize = KitStyle.Type12;
        row.Children.Add(title);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 1);
            row.Children.Add(trailing);
            if (!actionRight) trailing.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return row;
    }

    private Control CapabilitiesGroup(ModelDetailDto model, ModelForm form)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, RowSpacing = 12, Margin = new Thickness(16) };
        var i = 0;
        foreach (var (key, labelKey) in ModelCapabilityList.All)
        {
            var captured = key;
            var box = PricingUi.LabeledCheckbox(Loc.T(labelKey), ModelCapabilityList.Get(form.Capabilities, key) == true,
                on => _page.Edit(f => f.WithCapability(captured, on)));
            Grid.SetColumn(box, i % 2);
            Grid.SetRow(box, i / 2);
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(box);
            i++;
        }
        var group = new SettingsGroup();
        group.Rows.Add(grid);
        return new StackPanel { Children = { Heading(Loc.T("models.section.capabilities"), Marker(model, "capabilities"), actionRight: false), group } };
    }

    private Control ProviderPrices(ModelDetailDto model)
    {
        var add = PricingUi.Button(Loc.T("common.add"), PricingUi.Look.Plain, LucideIconKind.Plus, small: true, click: () => _priceSheet.Open(model, null));
        var group = new SettingsGroup { Footer = Loc.T("models.providerPricesFooter") };
        if (model.ProviderPrices.Count == 0)
            group.Rows.Add(new SettingsRow { Label = PricingUi.Muted12(Loc.T("models.noProviderPrices")) });
        foreach (var price in model.ProviderPrices)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var key = PricingUi.Text(price.PriceKey, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, mono: true);
            key.FontSize = 12.5;
            label.Children.Add(key);
            if (price.UserModified) label.Children.Add(KitBadge.Create(Loc.T("models.modified"), Tone.Orange));
            var row = new SettingsRow { Label = label, Detail = ModelDetailText.PriceDetail(price.Pricing, price.UpstreamModelId) };
            var captured = price;
            row.Trailing.Add(PricingUi.Button(null, PricingUi.Look.Plain, LucideIconKind.Pencil, small: true, click: () => _priceSheet.Open(model, captured), name: Loc.T("common.edit")));
            row.Trailing.Add(DeleteMorph.Create(Loc.T("common.delete"), Loc.T("models.deletePrice", ("key", price.PriceKey)), () => _page.DeletePriceAsync(captured.PriceKey)));
            group.Rows.Add(row);
        }
        return new StackPanel { Children = { Heading(Loc.T("models.section.providerPrices"), add, actionRight: true), group } };
    }

    private Control Overrides(ModelDetailDto model)
    {
        var group = new SettingsGroup { Title = Loc.T("models.section.overrides"), Footer = Loc.T("models.overridesFooter") };
        foreach (var o in model.ProviderOverrides)
        {
            var link = PricingUi.Text(o.ProviderName, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Accent);
            link.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var providerId = o.ProviderId;
            link.PointerPressed += (_, _) => _page.Ctx.Navigator.Navigate("ProviderDetail", providerId);
            group.Rows.Add(new SettingsRow { Label = link, Detail = ModelDetailText.OverrideDetail(o) });
        }
        return group;
    }

    // ---------- right column ----------

    private static Border Card(Control child)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), Padding = new Thickness(16), Child = child };
        Astra.Desktop.Components.Tokens.CardPanel.Themed(card, Astra.Desktop.Components.Tokens.CardPanel.SurfaceBrush);
        card.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    private Control PricingSection(ModelDetailDto model, ModelForm form)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        section.Children.Add(new SectionTitle(Loc.T("models.section.pricing"), Marker(model, "pricing")));
        var hint = PricingUi.Secondary12(Loc.T("models.pricingHint"));
        hint.Margin = new Thickness(4, 0, 4, 12);
        section.Children.Add(hint);
        var editor = new PricingEditor(form.Pricing, allowEmpty: true);
        editor.ValueChanged += (_, e) => _page.Edit(f => f with { Pricing = e.Value });
        section.Children.Add(Card(editor));
        return section;
    }

    private Control SimulatorSection(ModelDetailDto model, ModelForm form)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        section.Children.Add(new SectionTitle(Loc.T("models.section.simulator")));
        _simulator = new BillingSimulator(_page.Ctx);
        _unsaved = PricingUi.Muted12(Loc.T("models.simulatorUnsaved"));
        _unsaved.Margin = new Thickness(0, 12, 0, 0);
        ApplySimulator(model, form);
        section.Children.Add(Card(new StackPanel { Children = { _simulator, _unsaved } }));
        return section;
    }

    /// <summary>While the form is unsaved the simulator prices the working document, otherwise the saved model (web: <c>modelId={dirty ? undefined : m.id}</c>).</summary>
    private void ApplySimulator(ModelDetailDto model, ModelForm form)
    {
        if (_simulator is null || _unsaved is null) return;
        var dirty = _page.IsDirty;
        _simulator.Pricing = dirty ? form.Pricing : null;
        _simulator.ModelId = dirty ? null : model.Id;
        _unsaved.IsVisible = dirty;
    }
}

/// <summary>Adapts a delegate to <see cref="IObserver{T}"/> (Avalonia's observable subscription).</summary>
internal sealed class ActionObserver<T>(Action<T> next) : IObserver<T>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(T value) => next(value);
}
