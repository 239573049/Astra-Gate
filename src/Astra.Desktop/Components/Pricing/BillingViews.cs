using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Pricing;

/// <summary>Web <c>PricingSourceBadge</c>: where a bill's prices came from (provider override, provider price, official default, none).</summary>
public static class PricingSourceBadge
{
    /// <summary>The badge for a <c>pricingSource</c> value (<c>provider_override</c>, <c>provider_price</c>, <c>system_default</c>, anything else is "none").</summary>
    public static Badge Create(string? source, string? priceKey = null)
    {
        var (text, tone) = BillingSimulation.SourceBadge(source, priceKey);
        return KitBadge.Create(text, tone switch { "orange" => Tone.Orange, "accent" => Tone.Accent, "neutral" => Tone.Neutral, _ => Tone.Red });
    }
}

/// <summary>
/// The item table and description of a computed bill (web <c>BillingResultView</c>): the total, where the price came from, a sortable
/// table of items and the server's explanation. Shared by the simulator and, later, the request detail view.
/// </summary>
public sealed class BillingResultView : UserControl
{
    /// <summary>The bill shown; set again to show another.</summary>
    public static readonly StyledProperty<BillingResultDto?> ResultProperty = AvaloniaProperty.Register<BillingResultView, BillingResultDto?>(nameof(Result));

    /// <inheritdoc cref="ResultProperty"/>
    public BillingResultDto? Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }

    /// <summary>An empty view.</summary>
    public BillingResultView() { }

    /// <summary>A view of <paramref name="result"/>.</summary>
    public BillingResultView(BillingResultDto result) => Result = result;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResultProperty) Rebuild();
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

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (Result is not { } result)
        {
            Content = null;
            return;
        }
        var stack = new StackPanel { Spacing = 12 };

        // total + source badge (+ "unpriced")
        var head = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var total = PricingUi.Text(PriceFormat.Nanos(result.TotalNanos), ArcDesignTokenKind.TypeXL, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        total.FontSize = 20;
        total.FontFeatures = FontFeatureCollection.Parse("tnum");
        head.Children.Add(total);
        head.Children.Add(PricingSourceBadge.Create(result.PricingSource, result.PriceKey));
        if (!result.Priced) head.Children.Add(KitBadge.Create(Loc.T("billing.unpriced"), Tone.Red));
        stack.Children.Add(head);

        var rows = BillingSimulation.Rows(result);
        if (rows.Count > 0) stack.Children.Add(Table(rows));

        if (!string.IsNullOrEmpty(result.Description))
        {
            var description = new SelectableTextBlock { Text = result.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 12 * 1.625 };
            description.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            stack.Children.Add(description);
        }
        Content = stack;
    }

    // The web folds the table only under a 620 px VIEWPORT (a media query), never because its column is narrow: a table in a 500 px card
    // keeps its columns and scrolls sideways below 560 px. Arc folds by the table's own width, so the breakpoint is switched off.
    private static SortableDataTable Table(List<BillingSimulation.ItemRow> rows)
    {
        var table = new SortableDataTable { Caption = Loc.T("billing.type"), ItemsSource = rows, MaxHeight = 360 }.WithoutCompactLayout();
        Astra.Desktop.Components.Tokens.Tables.WebSurface(table);
        table.Columns.Add(new DataTableColumn(Loc.T("billing.type"), r => ((BillingSimulation.ItemRow)r).TypeText) { Key = "type", SortValueSelector = r => ((BillingSimulation.ItemRow)r).Item.TokenType });
        table.Columns.Add(new DataTableColumn(Loc.T("billing.tokens"), r => ((BillingSimulation.ItemRow)r).TokensText) { Key = "tokens", IsNumeric = true, Width = new GridLength(120), SortValueSelector = r => ((BillingSimulation.ItemRow)r).Item.Tokens });
        table.Columns.Add(new DataTableColumn(Loc.T("billing.unitPrice"), r => ((BillingSimulation.ItemRow)r).UnitPriceText) { Key = "unitPrice", IsNumeric = true, Width = new GridLength(140), SortValueSelector = r => ((BillingSimulation.ItemRow)r).Item.UnitPrice });
        table.Columns.Add(new DataTableColumn(Loc.T("billing.tier"), r => ((BillingSimulation.ItemRow)r).Item.Tier) { Key = "tier", Width = new GridLength(100) });
        table.Columns.Add(new DataTableColumn(Loc.T("billing.cost"), r => ((BillingSimulation.ItemRow)r).CostText) { Key = "cost", IsNumeric = true, Width = new GridLength(110), SortValueSelector = r => ((BillingSimulation.ItemRow)r).Item.CostNanos });
        return table;
    }
}

/// <summary>
/// "What would this cost?" (web <c>BillingSimulator</c>): prices a usage against a saved model (optionally at one AI provider) or against
/// a pricing document that may not be saved yet. Set <see cref="ModelId"/> and/or <see cref="Pricing"/> (the model detail page passes
/// the model id while its form is saved, and the working pricing while it is edited); the box shows the server's answer in a
/// <see cref="BillingResultView"/>. Talks to <c>POST /api/billing/simulate</c> through the page context's <c>ApiClient</c>.
/// </summary>
public sealed class BillingSimulator : UserControl
{
    /// <summary>The saved model to price; null prices <see cref="Pricing"/> alone.</summary>
    public static readonly StyledProperty<string?> ModelIdProperty = AvaloniaProperty.Register<BillingSimulator, string?>(nameof(ModelId));

    /// <summary>A pricing document to price (the unsaved edit); null prices the saved model.</summary>
    public static readonly StyledProperty<JsonNode?> PricingProperty = AvaloniaProperty.Register<BillingSimulator, JsonNode?>(nameof(Pricing));

    private readonly PageContext _context;
    private readonly Dictionary<string, decimal?> _tokens = BillingSimulation.DefaultTokens();
    private string _providerId = "";
    private string _serviceTier = "";
    private DateOnly? _date;
    private TimeOnly? _time;
    private BillingResultDto? _result;
    private string? _error;
    private bool _running;
    private IDisposable? _providersToken;
    private readonly StackPanel _output = new() { Spacing = 12 };
    private Select? _providerSelect;
    private TKCode.Arc.Button? _run;

    /// <inheritdoc cref="ModelIdProperty"/>
    public string? ModelId { get => GetValue(ModelIdProperty); set => SetValue(ModelIdProperty, value); }

    /// <inheritdoc cref="PricingProperty"/>
    public JsonNode? Pricing { get => GetValue(PricingProperty); set => SetValue(PricingProperty, value); }

    /// <summary>A simulator over the app context.</summary>
    public BillingSimulator(PageContext context)
    {
        _context = context;
        Build();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // the provider list is the shared query ['providers'] (web useProviders): observed while this control is on screen
        _providersToken = _context.Providers.Providers.Activate();
        _context.Providers.Providers.PropertyChanged += OnProvidersChanged;
        Loc.LanguageChanged += OnLanguage;
        FillProviders();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _context.Providers.Providers.PropertyChanged -= OnProvidersChanged;
        _providersToken?.Dispose();
        _providersToken = null;
        Loc.LanguageChanged -= OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // the provider box only exists for a saved model (web: `modelId && <Select …/>`)
        if (change.Property == ModelIdProperty) Build();
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private void OnProvidersChanged(object? sender, PropertyChangedEventArgs e) => FillProviders();

    private void FillProviders()
    {
        if (_providerSelect is null) return;
        var options = new List<SelectOption> { new("", Loc.T("simulator.official")) };
        foreach (var p in BillingSimulation.ProviderOptions(_context.Providers.Providers.Data))
            options.Add(new SelectOption(p.Id, p.Name) { Meta = p.Detail });
        _providerSelect.ItemsSource = options;
        _providerSelect.SelectedValue = options.Any(o => Equals(o.Value, _providerId)) ? _providerId : "";
    }

    private void Build()
    {
        var root = new StackPanel { Spacing = 12 };

        var grid = new AutoFillGrid { MinColumnWidth = 200, ColumnSpacing = 20, RowSpacing = 8 };
        foreach (var type in PricingDocument.TokenTypes)
        {
            var box = PricingUi.Number(_tokens.GetValueOrDefault(type), v => _tokens[type] = v, width: 112, name: type, integer: true, minimum: 0);
            var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            cell.Children.Add(PricingUi.Text(type, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, mono: true));
            Grid.SetColumn(box, 1);
            cell.Children.Add(box);
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);

        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var left = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        _providerSelect = null;
        if (!string.IsNullOrEmpty(ModelId))
        {
            _providerSelect = PricingUi.Choice([new SelectOption("", Loc.T("simulator.official"))], _providerId, v => _providerId = v, width: 192, name: Loc.T("simulator.official"));
            left.Children.Add(_providerSelect);
        }
        left.Children.Add(PricingUi.TextBox(_serviceTier, v => _serviceTier = v, width: 112, name: Loc.T("simulator.serviceTier"), placeholder: Loc.T("simulator.serviceTier")));
        left.Children.Add(RequestTimeFields());
        controls.Children.Add(left);
        _run = PricingUi.Button(Loc.T("simulator.run"), PricingUi.Look.Primary, LucideIconKind.Calculator, click: () => _ = RunAsync());
        _run.IsLoading = _running;
        Grid.SetColumn(_run, 1);
        controls.Children.Add(_run);
        root.Children.Add(controls);

        if (_output.Parent is Panel old) old.Children.Remove(_output);
        root.Children.Add(_output);
        RenderOutput();
        Content = root;
        FillProviders();
    }

    private void RenderOutput()
    {
        _output.Children.Clear();
        if (_error is not null) _output.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _error });
        if (_result is not null) _output.Children.Add(new BillingResultView(_result));
    }

    /// <summary>
    /// The request time (web: one <c>datetime-local</c> input, 208 px): Arc's date picker and a 24-hour time picker side by side, minute steps
    /// like the browser's field. ARC-GAP: Arc has no combined date-time picker.
    /// </summary>
    private Control RequestTimeFields()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(Loc.Language == "zh" ? "zh-CN" : "en-US");
        var date = new TKCode.Arc.DatePicker
        {
            SelectedDate = _date, Width = 136, Culture = culture, FirstDayOfWeek = DayOfWeek.Monday, VerticalAlignment = VerticalAlignment.Center,
            Placeholder = Loc.T("simulator.pickDate"), EmptyStatusText = Loc.T("simulator.pickDate"),
            SelectedStatusFormat = Loc.T("simulator.selectedDate", ("date", "{0}")), ClearText = Loc.T("simulator.clearDate"),
        };
        Avalonia.Automation.AutomationProperties.SetName(date, Loc.T("simulator.time"));
        ToolTip.SetTip(date, Loc.T("simulator.time"));
        date.SelectedDateChanged += (_, _) => _date = date.SelectedDate;
        var time = new TKCode.Arc.TimePicker
        {
            SelectedTime = _time, Width = 104, MinuteStep = 1, ClockFormat = TimePickerClockFormat.TwentyFourHour, Culture = culture,
            Placeholder = Loc.T("simulator.pickTime"), VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(time, Loc.T("simulator.time"));
        time.SelectedTimeChanged += (_, _) => _time = time.SelectedTime;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { date, time } };
    }

    private async Task RunAsync()
    {
        if (_running) return;
        var when = BillingSimulation.RequestTime(_date, _time, TimeZoneInfo.Local);
        var request = BillingSimulation.BuildRequest(ModelId, Pricing, _providerId, _tokens, _serviceTier, when, Loc.Language);
        _running = true;
        if (_run is not null) _run.IsLoading = true;
        try
        {
            _result = await _context.Api.PostAsync<BillingSimulateRequest, BillingResultDto>("/api/billing/simulate", request);
            _error = null;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _error = e.Message;
            _result = null;
        }
        finally
        {
            _running = false;
            if (_run is not null) _run.IsLoading = false;
        }
        RenderOutput();
    }
}
