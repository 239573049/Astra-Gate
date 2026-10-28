using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// Add or edit one provider price of a model (web <c>PriceSheet</c> of web/src/pages/ModelDetailPage.tsx): the price key (a choice of the known
/// keys — those the model already has are disabled — or a custom one; fixed when editing), the optional upstream model id and a
/// <see cref="PricingEditor"/>. Save is enabled once a key is set; it puts the price and closes.
/// </summary>
public sealed class PriceSheet : UserControl
{
    private readonly PageContext _ctx;
    private readonly WideSheet _sheet = new() { SheetWidth = 680 };
    private readonly Query<List<PriceKeyInfo>> _priceKeys;
    private ModelDetailDto? _model;
    private bool _isNew;
    private string _priceKey = "";
    private string _upstream = "";
    private JsonNode? _pricing;
    private bool _saving;
    private ArcButton _save = null!;

    /// <summary>A closed sheet; <paramref name="priceKeys"/> is the shared <c>GET /api/price-keys</c> query.</summary>
    public PriceSheet(PageContext context, Query<List<PriceKeyInfo>> priceKeys)
    {
        _ctx = context;
        _priceKeys = priceKeys;
        Content = _sheet;
    }

    /// <summary>Whether the sheet is open.</summary>
    public bool IsOpen => _sheet.IsOpen;

    /// <summary>The underlying sheet (for the visual harness).</summary>
    public WideSheet Sheet => _sheet;

    /// <summary>Opens the sheet for a new price (<paramref name="editing"/> null) or for <paramref name="editing"/>.</summary>
    public void Open(ModelDetailDto model, ModelPriceDto? editing)
    {
        _model = model;
        _isNew = editing is null;
        if (editing is null)
        {
            _priceKey = "";
            _upstream = "";
            // the model's system price is the starting point of a new provider price (structuredClone), else an empty schedule
            _pricing = model.Pricing?.DeepClone() ?? PricingDocument.Empty().ToNode();
        }
        else
        {
            _priceKey = editing.PriceKey;
            _upstream = editing.UpstreamModelId ?? "";
            _pricing = editing.Pricing?.DeepClone();
        }
        Build();
        _sheet.IsOpen = true;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_model is not null && _sheet.IsOpen) Build();
    });

    private void Build()
    {
        var model = _model!;
        _sheet.Title = _isNew ? Loc.T("models.addPrice") : Loc.T("models.editPrice", ("key", _priceKey));
        _sheet.Description = Loc.T("models.priceSheetDetail");

        var taken = model.ProviderPrices.Select(p => p.PriceKey).ToHashSet(StringComparer.Ordinal);
        Control keyField;
        TextBox? custom = null;
        if (_isNew)
        {
            var options = (_priceKeys.Data ?? []).Select(k => new SelectOption(k.PriceKey, k.Label, isDisabled: taken.Contains(k.PriceKey)) { Meta = k.PriceKey }).ToList();
            var select = PricingUi.Choice(options, _priceKey, v => { _priceKey = v; Refresh(); if (custom is not null) custom.Text = v; }, placeholder: Loc.T("models.pickPriceKey"), name: Loc.T("providers.priceKey"));
            select.Label = Loc.T("providers.priceKey");
            select.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            keyField = select;
        }
        else
        {
            var key = PricingUi.TextBox(_priceKey, null, mono: true, name: Loc.T("providers.priceKey"), visibleLabel: true);
            key.IsEnabled = false;
            key.Width = double.NaN;
            keyField = key;
        }
        var upstream = PricingUi.TextBox(_upstream, v => _upstream = v, mono: true, name: Loc.T("models.upstreamId"), placeholder: model.Id, visibleLabel: true);
        upstream.Description = Loc.T("models.upstreamIdHint");
        upstream.Width = double.NaN;

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(PricingUi.Columns(2, keyField, upstream));
        if (_isNew)
        {
            // a key that is not in the list: typing here overrides the choice above (web: the second, label-less input)
            custom = PricingUi.TextBox(_priceKey, v => { _priceKey = v.Trim(); Refresh(); }, mono: true, name: Loc.T("models.customPriceKey"), placeholder: Loc.T("models.customPriceKey"));
            custom.Width = double.NaN;
            body.Children.Add(custom);
        }
        var editor = new PricingEditor(_pricing);
        editor.ValueChanged += (_, e) => { _pricing = e.Value; Refresh(); };
        body.Children.Add(editor);
        _sheet.Content = body;

        _save = PricingUi.Button(Loc.T("common.save"), PricingUi.Look.Primary, click: () => _ = SaveAsync());
        _sheet.Footer = [PricingUi.Button(Loc.T("common.cancel"), click: () => _sheet.IsOpen = false), _save];
        Refresh();
    }

    private void Refresh() => _save.IsEnabled = _priceKey.Length > 0 && _pricing is not null;

    private async Task SaveAsync()
    {
        if (_model is null || _pricing is null || _priceKey.Length == 0 || _saving) return;
        _saving = true;
        _save.IsLoading = true;
        try
        {
            var body = new ModelPriceInput(string.IsNullOrEmpty(_upstream) ? null : _upstream, _pricing.DeepClone());
            await _ctx.Api.PutAsync<ModelPriceInput, ModelPriceDto>(ModelCatalog.DetailPath(_model.Id) + "/prices/" + ApiClient.Enc(_priceKey), body);
            ModelQueries.Invalidate(_ctx.Queries, _model.Id);
            _ctx.Feedback.Success(Loc.T("common.saved"));
            _sheet.IsOpen = false;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
        }
        finally
        {
            _saving = false;
            _save.IsLoading = false;
        }
    }
}
