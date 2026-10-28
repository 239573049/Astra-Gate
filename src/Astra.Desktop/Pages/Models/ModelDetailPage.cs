using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// A model's detail page (web <c>ModelDetailPage</c>, web/src/pages/ModelDetailPage.tsx): the editable form of the catalog entry, its
/// capability flags, the per-provider system prices, the price editor and the billing simulator. The form is the working copy of the saved
/// model (<see cref="Saved"/>): Save and Revert light up while they differ, a refetch (after saving, or an invalidation) replaces both, as the web's
/// <c>useEffect([base])</c> does. The route parameter is the model id.
/// </summary>
public sealed class ModelDetailPage : PageViewModel
{
    /// <summary>The zone dates are shown in; null is the machine's. The visual tests pin it to UTC like the web capture.</summary>
    public static TimeZoneInfo? DisplayZone { get; set; }

    private readonly PageContext _ctx;
    private readonly Query<ModelDetailDto> _model;
    private readonly Query<List<PriceKeyInfo>> _priceKeys;
    private ModelDetailDto? _shown;
    private bool _wasError;
    private ModelForm? _saved;
    private ModelForm? _form;
    private bool _saving;
    private readonly ArcButton _revert;
    private readonly ArcButton _save;
    private readonly ConfirmMorph _delete;

    /// <summary>Builds the page for the model id in <paramref name="param"/>.</summary>
    public ModelDetailPage(PageContext context, string? param) : base(AppRoute.ModelDetail, param, context)
    {
        _ctx = context;
        _model = ModelQueries.DetailQuery(context.Queries, context.Api, Id);
        _priceKeys = ModelQueries.PriceKeysQuery(context.Queries, context.Api);
        Observe(_model);
        Observe(_priceKeys);
        Load();

        Leading();
        _revert = PricingUi.Button(Loc.T("common.revert"), click: Revert);
        _save = PricingUi.Button(Loc.T("common.save"), PricingUi.Look.Primary, click: () => _ = SaveAsync());
        _delete = DeleteMorph.Create(Loc.T("common.delete"), Loc.T("models.deleteConfirm", ("id", Id)), async () =>
        {
            await _ctx.Api.DeleteAsync(ModelCatalog.DetailPath(Id));
            ModelQueries.Invalidate(_ctx.Queries);
            _ctx.Navigator.Navigate("Models");
        });
        UpdateHeader();
    }

    /// <summary>The model id (the route parameter, not escaped).</summary>
    public string Id => Param ?? "";

    /// <summary>The detail query (for the view).</summary>
    public Query<ModelDetailDto> Model => _model;

    /// <summary>The price-key query (the choices of the "add price" sheet).</summary>
    public Query<List<PriceKeyInfo>> PriceKeys => _priceKeys;

    /// <summary>The model as last loaded; null until then.</summary>
    public ModelDetailDto? Detail => _shown;

    /// <summary>The saved state of the form.</summary>
    public ModelForm? Saved => _saved;

    /// <summary>The working copy the person edits.</summary>
    public ModelForm? Form => _form;

    /// <summary>The page context (the views reach the API, toasts and navigation through it).</summary>
    public PageContext Ctx => _ctx;

    /// <summary>Whether the working copy differs from the saved one.</summary>
    public bool IsDirty => _form is not null && _saved is not null && _form.IsDirty(_saved);

    /// <inheritdoc />
    public override bool HoldsUnsavedWork => IsDirty;

    /// <summary>The model failed to load and nothing is shown.</summary>
    public bool IsError => _shown is null && _model.IsError;

    /// <summary>The load failure's message.</summary>
    public string? ErrorText => _model.Error?.Message;

    /// <summary>Raised when the form was replaced wholesale (first load, a refetch, Revert): the view rebuilds its inputs from <see cref="Form"/>.</summary>
    public event Action? FormReplaced;

    /// <summary>Raised when the working copy changed by an edit (the Save / Revert state and the simulator follow).</summary>
    public event Action? FormEdited;

    /// <inheritdoc />
    /// <remarks>
    /// While loading the web shows an empty title; the model id stands in here because the shell's <c>--smoke</c> check requires every
    /// page to have a title the moment it is built (a smoke run has no server, so nothing ever loads).
    /// </remarks>
    public override string Title => _shown?.DisplayName ?? (IsError ? Loc.T("nav.models") : Id);

    /// <inheritdoc />
    public override string? Subtitle => _shown is { } m ? ModelDetailText.Subtitle(m, Loc.Language, DisplayZone) : null;

    /// <summary>Applies an edit to the working copy.</summary>
    public void Edit(Func<ModelForm, ModelForm> change)
    {
        if (_form is null) return;
        _form = change(_form);
        OnPropertyChanged(nameof(Form));
        OnPropertyChanged(nameof(IsDirty));
        UpdateHeader();
        FormEdited?.Invoke();
    }

    /// <summary>Drops the edits: the working copy is the saved one again.</summary>
    public void Revert()
    {
        _form = _saved;
        OnPropertyChanged(nameof(Form));
        OnPropertyChanged(nameof(IsDirty));
        UpdateHeader();
        FormReplaced?.Invoke();
    }

    /// <summary>Saves the working copy (<c>PUT /api/models/{id}</c>); a toast says whether it worked.</summary>
    public async Task SaveAsync()
    {
        if (_form is null || _saving) return;
        _saving = true;
        UpdateHeader();
        try
        {
            await _ctx.Api.PutAsync<ModelInput, ModelDto>(ModelCatalog.DetailPath(Id), _form.ToInput());
            ModelQueries.Invalidate(_ctx.Queries, Id);
            _ctx.Feedback.Success(Loc.T("common.saved"));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
        }
        finally
        {
            _saving = false;
            UpdateHeader();
        }
    }

    /// <summary>Restores one seeded field to its catalog value (<c>POST …/reset-field</c>).</summary>
    public async Task ResetFieldAsync(string field)
    {
        try
        {
            await _ctx.Api.PostAsync(ModelCatalog.DetailPath(Id) + "/reset-field", new ResetFieldInput(field));
            ModelQueries.Invalidate(_ctx.Queries, Id);
            _ctx.Feedback.Info(Loc.T("models.fieldReset"));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
        }
    }

    /// <summary>Deletes one provider price (<c>DELETE …/prices/{key}</c>); failures propagate to the confirm control, which shows them.</summary>
    public async Task DeletePriceAsync(string priceKey)
    {
        await _ctx.Api.DeleteAsync(ModelCatalog.DetailPath(Id) + "/prices/" + ApiClient.Enc(priceKey));
        ModelQueries.Invalidate(_ctx.Queries, Id);
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        Leading();
        // the shared query outlives the page: listen only while it is shown, and catch up with what arrived meanwhile
        _model.PropertyChanged += OnModelChanged;
        Load();
        _wasError = IsError;
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ErrorText));
    }

    /// <inheritdoc />
    protected override void OnDeactivated() => _model.PropertyChanged -= OnModelChanged;

    /// <summary>
    /// web <c>back</c> (to the catalog). The list opens a model with <c>Navigate</c>, so there is usually no history and the page draws it; pushed
    /// over another page (the shell then draws its own back button) it would be a second one, so it is left out (the ProviderDetail rule).
    /// </summary>
    private void Leading() => SetHeaderLeading(_ctx.Navigator.CanGoBack
        ? null
        : PricingUi.Button(null, PricingUi.Look.Glass, LucideIconKind.ArrowLeft, click: () => _ctx.Navigator.Navigate("Models"), name: Loc.T("common.back")));

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        FormReplaced?.Invoke();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Query<ModelDetailDto>.Data) or nameof(Query<ModelDetailDto>.HasData)) Load();
        // A fetch reports up to eight properties, and the view rebuilds the whole page on IsError: raise it only when it changed,
        // and the title / header only for the properties they read.
        if (e.PropertyName is not (nameof(Query<ModelDetailDto>.Data) or nameof(Query<ModelDetailDto>.HasData)
            or nameof(Query<ModelDetailDto>.IsError) or nameof(Query<ModelDetailDto>.Error))) return;
        if (IsError != _wasError)
        {
            _wasError = IsError;
            OnPropertyChanged(nameof(IsError));
        }
        OnPropertyChanged(nameof(ErrorText));
        RaiseTitle();
    }

    private void RaiseTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        UpdateHeader();
    }

    /// <summary>A new object from the query replaces the form (the web memoizes <c>base</c> on <c>model.data</c> and resets on it).</summary>
    private void Load()
    {
        if (!_model.HasData || _model.Data is not { } data || ReferenceEquals(data, _shown)) return;
        _shown = data;
        _saved = ModelForm.From(data);
        _form = _saved;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Form));
        OnPropertyChanged(nameof(IsDirty));
        FormReplaced?.Invoke();
        RaiseTitle();
    }

    private void UpdateHeader()
    {
        if (_revert is null) return; // before the controls exist (the first Load)
        if (_shown is null)
        {
            SetHeaderActions(null);
            return;
        }
        _revert.IsVisible = IsDirty;
        _save.IsEnabled = IsDirty;
        _save.IsLoading = _saving;
        _delete.IsVisible = _shown.Source == "user";
        if (HeaderActions is null) SetHeaderActions(new ActionGroup(8, _delete, _revert, _save));
    }
}

/// <summary>A destructive action that confirms in place (web <c>DeleteMorph</c>: Arc's confirm morph instead of a separate dialog).</summary>
internal static class DeleteMorph
{
    /// <summary>A morph whose idle button reads <paramref name="label"/>, asks <paramref name="prompt"/> and runs <paramref name="confirm"/>; an exception becomes the error state with a retry.</summary>
    public static ConfirmMorph Create(string label, string prompt, Func<Task> confirm)
    {
        var morph = new ConfirmMorph
        {
            Label = label,
            Icon = Lucide.Geometry(LucideIconKind.Trash2),
            Prompt = prompt,
            ConfirmLabel = Loc.T("common.delete"),
            CancelLabel = Loc.T("common.cancel"),
            PendingLabel = Loc.T("common.deleting"),
            DoneLabel = Loc.T("common.deleted"),
            ErrorLabel = Loc.T("common.error"),
            RetryLabel = Loc.T("common.retry"),
            ConfirmAction = confirm,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        DangerIdle(morph);
        return morph;
    }

    /// <summary>
    /// ARC-GAP: the web morph's idle face carries its danger tone (<c>confirm-morph.module.css</c>: <c>.root[data-tone="danger"] .surface</c> =
    /// danger 4% over the raised surface, no shadow, outline danger 18% over the border, red trigger text and icon); Arc's Avalonia theme
    /// draws every tone's idle face neutral. Local styles on the instance (they outrank the control theme) restore it for the idle state only.
    /// </summary>
    private static void DangerIdle(ConfirmMorph morph)
    {
        void Apply()
        {
            var dark = morph.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var danger = ArcTokens.GetColor(morph, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Danger);
            if (danger.A == 0) danger = dark ? Color.FromRgb(0xff, 0x45, 0x3a) : Color.FromRgb(0xff, 0x3b, 0x30);
            var raised = dark ? Color.FromRgb(0x2a, 0x2a, 0x2d) : Colors.White; // --surface-raised = --surface-solid
            var border = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(23, 0, 0, 0); // --separator
            var fill = Mix(danger, 0.04, raised);
            var edge = Mix(danger, 0.18, border);
            morph.Styles.Clear();
            morph.Styles.Add(Idle(x => x.Template().OfType<Border>().Name("PART_Pill"), (Border.BackgroundProperty, new SolidColorBrush(fill)), (Border.BoxShadowProperty, default(BoxShadows))));
            morph.Styles.Add(Idle(x => x.Template().OfType<Border>().Name("PART_Outline"), (Border.BorderBrushProperty, new SolidColorBrush(edge))));
            morph.Styles.Add(Idle(x => x.Template().OfType<Avalonia.Controls.Button>().Name("PART_Trigger"), (Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, new SolidColorBrush(danger))));
            morph.Styles.Add(Idle(x => x.Template().OfType<ArcGlyph>().Name("PART_TriggerIcon"), (ArcGlyph.ForegroundProperty, new SolidColorBrush(danger))));
        }
        Apply();
        morph.ActualThemeVariantChanged += (_, _) => Apply();
    }

    private static Style Idle(Func<Selector, Selector> part, params (AvaloniaProperty Property, object? Value)[] setters)
    {
        var style = new Style(x => part(x.OfType<ConfirmMorph>().Class(":idle")));
        foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
        return style;
    }

    /// <summary><c>color-mix(in oklab, a p, b)</c> including b's alpha (premultiplied, as browsers do).</summary>
    private static Color Mix(Color a, double p, Color b)
    {
        var (r, g, bl, alpha) = Astra.Desktop.Core.Overview.OklabMix.Mix((a.R, a.G, a.B, a.A / 255.0), p, (b.R, b.G, b.B, b.A / 255.0));
        return Color.FromArgb((byte)Math.Round(alpha * 255), r, g, bl);
    }
}
