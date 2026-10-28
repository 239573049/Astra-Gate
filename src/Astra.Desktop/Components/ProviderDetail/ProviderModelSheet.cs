using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The provider-model edit sheet (web <c>ProviderModelSheet</c>, 720 px): price source, the linked system model, the three inheritable fields
/// (display name, context, max output) with their origin badge and reset, and the price — inherited (shown read-only) or a custom price edited
/// with the shared <see cref="PricingEditor"/>. The footer deletes the row in place (Arc <see cref="ConfirmMorph"/>) or saves the draft.
/// </summary>
public sealed class ProviderModelSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private ProviderModelDraft? _draft;
    private ArcButton? _save;
    private IDisposable? _catalog;

    /// <summary>The sheet for <paramref name="provider"/>'s models.</summary>
    public ProviderModelSheet(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        SheetWidth = 720;
        Closed += (_, _) =>
        {
            _catalog?.Dispose();
            _catalog = null;
        };
    }

    /// <summary>The draft being edited (tests).</summary>
    public ProviderModelDraft? Draft => _draft;

    /// <summary>Opens for <paramref name="pm"/> (web <c>useEffect([pm])</c> resets the draft).</summary>
    public void ShowFor(ProviderModelDto pm)
    {
        _draft = ProviderModelDraft.From(pm);
        _catalog ??= _api.Catalog.Activate();
        _api.Catalog.PropertyChanged -= OnCatalog;
        _api.Catalog.PropertyChanged += OnCatalog;
        Show();
    }

    /// <summary>A refetched row replaces the draft (the web's effect runs on the new <c>pm</c> object).</summary>
    public void Follow(ProviderModelDto pm)
    {
        if (!IsOpen || _draft is null || _draft.Model.Id != pm.Id) return;
        _draft = ProviderModelDraft.From(pm);
        Build();
    }

    /// <summary>Applies an edit (scenes and the controls).</summary>
    internal void Edit(Func<ProviderModelDraft, ProviderModelDraft> change, bool rebuild = false)
    {
        if (_draft is null) return;
        _draft = change(_draft);
        if (rebuild) Build();
        else
        {
            if (_save is not null) _save.IsEnabled = _draft.IsDirty;
            foreach (var (key, panel) in _origins) FillOrigin(key, panel);
        }
    }

    private readonly Dictionary<string, StackPanel> _origins = [];

    private void OnCatalog(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Astra.Desktop.Core.Api.Query<List<ModelDto>>.Data)) Avalonia.Threading.Dispatcher.UIThread.Post(Build);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        if (_draft is not { } d) return;
        var pm = d.Model;
        Title = pm.ModelId;
        _origins.Clear();
        Description = _provider.Name;

        var badges = PdUi.Inline(8, PricingSourceBadge.Create(pm.PricingSource, pm.PriceKey));
        if (pm.Multiplier != 1 && pm.PricingSource != "provider_override") badges.Children.Add(PdUi.Badge("×" + pm.Multiplier.ToString(CultureInfo.InvariantCulture)));

        var options = new List<(string, string, string?)> { ("", Loc.T("providers.noLink"), null) };
        options.AddRange((_api.Catalog.Data ?? []).Select(m => (m.Id, m.Id, (string?)m.DisplayName)));
        var link = PdUi.Choice(options, d.Link, v => Edit(x => x with { Link = v }), name: Loc.T("providers.linkedModel"));
        link.HorizontalAlignment = HorizontalAlignment.Stretch;
        var linkHint = PdUi.Hint(Loc.T("providers.linkedModelHint"));
        linkHint.Margin = new Thickness(0, 8, 0, 0);
        var linked = new DetailSection { Title = Loc.T("providers.linkedModel"), Body = PdUi.Stack(0, link, linkHint) };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        AddField(grid, 0, "displayName", Loc.T("models.field.displayName"),
            PdUi.TextBox(d.Get("displayName")?.GetValue<string>(), v => Edit(x => x.With("displayName", v.Length > 0 ? JsonValue.Create(v) : null)), name: Loc.T("models.field.displayName"), placeholder: pm.Effective.DisplayName));
        AddField(grid, 1, "contextWindow", Loc.T("models.field.contextWindow"), NumberField("contextWindow", Loc.T("models.field.contextWindow"), pm.Effective.ContextWindow));
        AddField(grid, 2, "maxOutputTokens", Loc.T("models.field.maxOutputTokens"), NumberField("maxOutputTokens", Loc.T("models.field.maxOutputTokens"), pm.Effective.MaxOutputTokens));
        var overrides = new DetailSection { Title = Loc.T("providers.overrides"), Body = grid };

        var priceBody = new StackPanel();
        var priceHint = PdUi.Hint(d.CustomPrice ? Loc.T("providers.customPriceHint") : Loc.T("providers.inheritedPriceHint"));
        priceHint.Margin = new Thickness(0, 0, 0, 12);
        priceBody.Children.Add(priceHint);
        if (d.CustomPrice)
        {
            var editor = new PricingEditor(d.Get("pricing")?.DeepClone());
            editor.ValueChanged += (_, e) => Edit(x => x.With("pricing", e.Value?.DeepClone()));
            priceBody.Children.Add(PdUi.Card(editor, new Thickness(16)));
        }
        else if (pm.EffectivePricing is { } eff)
            priceBody.Children.Add(new CodeBlock { Code = PricingDocument.PrettyJson(eff), Language = "json", MaxLines = 10, CopyLabel = Loc.T("common.copy") });
        else priceBody.Children.Add(PdUi.Alert(AlertTone.Warning, Loc.T("pricingSource.none")));
        var custom = PdUi.LabeledToggle(d.CustomPrice, on => Edit(x => x.WithCustomPrice(on), rebuild: true), Loc.T("providers.customPrice"), captionFirst: true);
        var price = new DetailSection { Title = Loc.T("providers.price"), Action = custom, Body = priceBody };

        Content = PdUi.Stack(24, badges, linked, overrides, price);

        var delete = new ConfirmMorph
        {
            Label = Loc.T("providers.removeModelShort"),
            Icon = Lucide.Geometry(LucideIconKind.Trash2),
            Prompt = Loc.T("providers.removeModel", ("id", pm.ModelId)),
            ConfirmLabel = Loc.T("common.delete"),
            CancelLabel = Loc.T("common.cancel"),
            PendingLabel = Loc.T("common.deleting"),
            DoneLabel = Loc.T("common.deleted"),
            ErrorLabel = Loc.T("common.error"),
            RetryLabel = Loc.T("common.retry"),
            ConfirmAction = async () =>
            {
                await _api.DeleteModelAsync(pm.Id);
                Close();
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, click: () => _ = SaveAsync());
        _save.IsEnabled = d.IsDirty;
        // web: the delete morph sits at the left of the footer (mr-auto)
        var footer = new DockPanel { LastChildFill = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(delete, Dock.Left);
        var right = PdUi.Inline(8, PdUi.Button(Loc.T("common.cancel"), click: () => Close()), _save);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(delete);
        footer.Children.Add(right);
        Actions = footer;
    }

    private Control NumberField(string key, string name, long? effective)
    {
        var current = _draft!.Get(key) is JsonValue v && v.TryGetValue<long>(out var n) ? n : (long?)null;
        return PdUi.Number(current, value => Edit(x => x.With(key, value is { } d ? JsonValue.Create((long)d) : null)), null, name, integer: true,
            placeholder: effective?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>One inheritable field: caption, origin badge, reset (when overridden), the input (web <c>field</c>).</summary>
    private void AddField(Grid grid, int column, string key, string label, Control input)
    {
        var right = PdUi.Inline(4);
        _origins[key] = right;
        FillOrigin(key, right);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), MinHeight = 28 };
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        head.Children.Add(PdUi.Text(label, 13, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        var cell = PdUi.Stack(0, head, input);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>The origin badge and, for an override, the reset button (web <c>OriginBadge</c> + reset).</summary>
    private void FillOrigin(string key, StackPanel right)
    {
        right.Children.Clear();
        var d = _draft!;
        right.Children.Add(d.OriginOf(key) switch
        {
            FieldOrigin.Overridden => PdUi.Badge(Loc.T("origin.overridden"), Tone.Orange),
            FieldOrigin.Inherited => PdUi.Badge(Loc.T("origin.inherited")),
            _ => PdUi.Badge(Loc.T("origin.unset"), Tone.Red),
        });
        if (d.Get(key) is not null)
            right.Children.Add(PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.RotateCcw, small: true, name: Loc.T("origin.reset"), click: () => Edit(x => x.With(key, null), rebuild: true)));
    }

    private async Task SaveAsync()
    {
        if (_draft is not { } d) return;
        var ok = await PdRun.TryAsync(_ctx, () => _api.UpdateModelAsync(d.Model.Id, d.Body()), _save);
        if (!ok) return;
        _ctx.Feedback.Success(Loc.T("common.saved"));
        Close();
    }
}
