using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// The "new model" sheet (web <c>CreateModelSheet</c> of web/src/pages/ModelsPage.tsx): id, display name, vendor, context window and the
/// two base prices of a user-created catalog model. Create is enabled once an id and a vendor are typed; on success the sheet closes, the
/// id and name are cleared (the other fields keep their values, like the web) and <c>onCreated</c> gets the new id.
/// </summary>
public sealed class CreateModelSheet : UserControl
{
    private readonly PageContext _ctx;
    private readonly Action<string> _onCreated;
    private readonly CreateModelDraft _draft = new();
    private readonly WideSheet _sheet = new() { SheetWidth = 460 };
    private Input _id = null!;
    private Input _name = null!;
    private ArcButton _create = null!;
    private bool _pending;

    /// <summary>A closed sheet over the app context.</summary>
    public CreateModelSheet(PageContext context, Action<string> onCreated)
    {
        _ctx = context;
        _onCreated = onCreated;
        Content = _sheet;
        // once, here: Build runs again on a language change and would add a second handler
        _sheet.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        Build();
    }

    /// <summary>Opens or closes the sheet.</summary>
    public bool IsOpen
    {
        get => _sheet.IsOpen;
        set => _sheet.IsOpen = value;
    }

    /// <summary>Raised when the sheet closes (the page clears its <c>IsCreating</c> flag).</summary>
    public event EventHandler? Closed;

    /// <summary>The underlying sheet (for the visual harness).</summary>
    public WideSheet Sheet => _sheet;

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

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        _sheet.Title = Loc.T("models.new");
        _sheet.Description = Loc.T("models.newDetail");

        _id = PricingUi.TextBox(_draft.Id, v => { _draft.Id = v; Refresh(); }, mono: true, name: Loc.T("models.field.id"), placeholder: "my-model-1", visibleLabel: true);
        _id.Width = double.NaN;
        _id.Description = Loc.T("models.field.idHint");
        _name = PricingUi.TextBox(_draft.DisplayName, v => _draft.DisplayName = v, name: Loc.T("models.field.displayName"), visibleLabel: true);
        _name.Width = double.NaN;
        var vendor = PricingUi.TextBox(_draft.Vendor, v => { _draft.Vendor = v; Refresh(); }, name: Loc.T("models.field.vendor"), placeholder: "openai", visibleLabel: true);
        vendor.Width = double.NaN;
        var context = PricingUi.Number(_draft.ContextWindow, v => _draft.ContextWindow = v is { } d ? (long)d : null, name: Loc.T("models.field.contextWindow"), integer: true, visibleLabel: true);
        var input = PricingUi.Number(_draft.InputPrice, v => _draft.InputPrice = v, name: Loc.T("models.field.inputPrice"), visibleLabel: true);
        var output = PricingUi.Number(_draft.OutputPrice, v => _draft.OutputPrice = v, name: Loc.T("models.field.outputPrice"), visibleLabel: true);
        foreach (var box in new Control[] { context, input, output })
        {
            box.Width = double.NaN;
            box.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }

        _sheet.Content = new StackPanel
        {
            Spacing = 16,
            Children = { _id, PricingUi.Columns(2, _name, vendor), PricingUi.Columns(3, context, input, output) },
        };
        _create = PricingUi.Button(Loc.T("common.create"), PricingUi.Look.Primary, click: () => _ = SubmitAsync());
        _sheet.Footer = [PricingUi.Button(Loc.T("common.cancel"), click: () => IsOpen = false), _create];
        Refresh();
    }

    private void Refresh() => _create.IsEnabled = _draft.CanSubmit;

    private async Task SubmitAsync()
    {
        var body = _draft.ToInput();
        if (body is null || _pending) return;
        _pending = true;
        _create.IsLoading = true;
        try
        {
            var created = await _ctx.Api.PostAsync<ModelInput, ModelDto>("/api/models", body);
            ModelQueries.Invalidate(_ctx.Queries, created?.Id);
            IsOpen = false;
            _draft.ResetAfterCreate();
            _id.Text = "";
            _name.Text = "";
            if (created is not null) _onCreated(created.Id);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
        }
        finally
        {
            _pending = false;
            _create.IsLoading = false;
            Refresh();
        }
    }
}
