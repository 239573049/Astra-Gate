using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
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

/// <summary>Runs a write and turns a failure into the error toast (web <c>onError: (e) =&gt; toast(errorText(e), 'error')</c>).</summary>
internal static class PdRun
{
    /// <summary>True when <paramref name="work"/> succeeded.</summary>
    public static async Task<bool> TryAsync(PageContext ctx, Func<Task> work, ArcButton? busy = null)
    {
        if (busy is not null) busy.IsLoading = true;
        try
        {
            await work();
            return true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            ctx.Feedback.Error(e.Message);
            return false;
        }
        finally
        {
            if (busy is not null) busy.IsLoading = false;
        }
    }
}

/// <summary>
/// The models tab (web <c>ModelsTab</c>): the search box and the add / fetch-remote buttons, the provider's models as rows (link state,
/// price source, enable switch; a click opens the edit sheet), the model mapping group, and the three sheets.
/// </summary>
public sealed class ModelsTabView : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private readonly StackPanel _body = new();
    private readonly SearchField _search;
    private long? _selected;

    /// <summary>Builds the tab for <paramref name="provider"/>.</summary>
    public ModelsTabView(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        AddSheet = new AddModelsSheet(ctx, api);
        RemoteSheet = new RemoteModelsSheet(ctx, api, provider);
        MapSheet = new ModelMapSheet(ctx, api, provider);
        ModelSheet = new ProviderModelSheet(ctx, api, provider);
        ModelSheet.Closed += (_, _) => { _selected = null; Rebuild(); };

        _search = new SearchField { Width = 224, Placeholder = Loc.T("providers.searchModels"), IsLabelVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) => Rebuild();
        var buttons = PdUi.Inline(8,
            PdUi.Button(Loc.T("providers.addModels"), PdUi.Look.Glass, LucideIconKind.Plus, click: AddSheet.Show),
            PdUi.Button(Loc.T("providers.fetchRemote"), PdUi.Look.Primary, LucideIconKind.CloudDownload, click: RemoteSheet.Show));
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        DockPanel.SetDock(_search, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(_search);
        bar.Children.Add(buttons);
        Content = new Panel { Children = { new StackPanel { Children = { bar, _body } }, AddSheet, RemoteSheet, MapSheet, ModelSheet } };
        Rebuild();
    }

    /// <summary>The add-models sheet.</summary>
    public AddModelsSheet AddSheet { get; }

    /// <summary>The remote models sheet.</summary>
    public RemoteModelsSheet RemoteSheet { get; }

    /// <summary>The model-mapping sheet.</summary>
    public ModelMapSheet MapSheet { get; }

    /// <summary>The provider-model edit sheet.</summary>
    public ProviderModelSheet ModelSheet { get; }

    /// <summary>
    /// The provider changed (its model mapping lives in its settings): redraw the list in place. The tab stays, so the search text and an open
    /// sheet stay too, like the web's ModelsTab, which only re-renders.
    /// </summary>
    internal void ProviderChanged() => Rebuild();

    /// <summary>Opens the edit sheet of a model row (web <c>onSelect</c>).</summary>
    public void Select(ProviderModelDto m)
    {
        _selected = m.Id;
        ModelSheet.ShowFor(m);
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _api.Models.PropertyChanged += OnModels;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.Models.PropertyChanged -= OnModels;
    }

    private string _sig = "";

    private void OnModels(object? sender, PropertyChangedEventArgs e)
    {
        var models = _api.Models;
        var sig = $"{models.HasData}|{models.IsFetching && !models.HasData}|{models.DataUpdatedAt}";
        if (sig == _sig) return;
        _sig = sig;
        Rebuild();
        // an open edit sheet follows its row (web: pm is looked up in the fresh list)
        if (_selected is { } id && models.Data?.FirstOrDefault(m => m.Id == id) is { } fresh) ModelSheet.Follow(fresh);
    }

    private void Rebuild()
    {
        _body.Children.Clear();
        var q = _api.Models;
        if (!q.HasData)
        {
            if (q.IsError) _body.Children.Add(PdUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message));
            else _body.Children.Add(new LoadingState(4));
            return;
        }
        var models = q.Data ?? [];
        if (models.Count == 0)
        {
            var empty = PdUi.Card(new EmptyStateView(Loc.T("providers.noModels"), Loc.T("providers.noModelsDetail")));
            empty.Margin = new Thickness(0, 0, 0, 20);
            _body.Children.Add(empty);
        }
        var list = ProviderDetailLogic.FilterModels(models, _search.Text ?? "");
        if (list.Count > 0)
        {
            var group = new SettingsGroup { Footer = Loc.T("providers.modelsFooter") };
            foreach (var m in list) group.Rows.Add(ModelRow(m));
            _body.Children.Add(group);
        }
        _body.Children.Add(MapGroup(models));
    }

    private SettingsRow ModelRow(ProviderModelDto m)
    {
        // web: font-mono text-[12.5px] with no color of its own (the tab panel's text-secondary) unless disabled (muted).
        var id = PdUi.Text(m.ModelId, 12.5, m.Enabled ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.TextMuted, mono: true);
        id.LineHeight = 13 * 1.4;
        var label = PdUi.Inline(8, id);
        if (m.SystemModelId is null) label.Children.Add(PdUi.Badge(Loc.T("providers.unlinked"), Tone.Orange));
        var toggle = PdUi.Toggle(m.Enabled, on => _ = PdRun.TryAsync(_ctx, () => _api.UpdateModelAsync(m.Id, new JsonObject { ["enabled"] = on })), Loc.T("common.enabled"));
        var row = PdUi.Row(label, ProviderDetailLogic.ModelDetail(m), PricingSourceBadge.Create(m.PricingSource, m.PriceKey), toggle);
        row.IsSelected = m.Id == _selected;
        row.Invoked += (_, _) => Select(m);
        return row;
    }

    /// <summary>
    /// Model mapping (settings.model_map, web <c>ModelMapGroup</c>): a model id clients ask for → one of this provider's models. The gateway sends
    /// the mapped model upstream, and clients bound to several providers route the requested id here.
    /// </summary>
    private Control MapGroup(IReadOnlyList<ProviderModelDto> models)
    {
        var p = _api.Provider.Data ?? _provider;
        var map = ProviderDetailLogic.ModelMapOf(p.Settings);
        var known = models.Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        var group = new SettingsGroup { Title = Loc.T("providers.modelMap.title"), Footer = Loc.T("providers.modelMap.footer") };
        foreach (var (from, to) in map)
        {
            var label = PdUi.Inline(8,
                PdUi.Text(from, 12.5, ArcDesignTokenKind.TextSecondary, mono: true),
                PdUi.Icon(LucideIconKind.ArrowRight, 14, ArcDesignTokenKind.TextMuted),
                PdUi.Text(to, 12.5, known.Contains(to) ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.TextMuted, mono: true));
            if (!known.Contains(to)) label.Children.Add(PdUi.Badge(Loc.T("providers.modelMap.missingTarget"), Tone.Orange));
            var name = Loc.T("providers.modelMap.remove", ("from", from));
            ArcButton? remove = null;
            remove = PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.X, small: true, name: name, click: () => _ = PdRun.TryAsync(_ctx, async () =>
            {
                await _api.SetModelMapAsync(ProviderDetailLogic.WithoutMapping(map, from));
                _ctx.Feedback.Success(Loc.T("providers.modelMap.saved"));
            }, remove));
            group.Rows.Add(PdUi.Row(label, null, remove));
        }
        var add = new SettingsRow
        {
            Icon = PdUi.Icon(LucideIconKind.Plus, 16, ArcDesignTokenKind.TextSecondary),
            Label = PdUi.RowText(Loc.T("providers.modelMap.add"), 13),
            Detail = models.Count == 0 ? PdUi.RowText(Loc.T("providers.modelMap.needModels"), 11) : null,
        };
        if (models.Count > 0) add.Invoked += (_, _) => MapSheet.ShowFor(models);
        group.Rows.Add(add);
        return group;
    }
}

/// <summary>Adds a model mapping (web <c>ModelMapSheet</c>): the requested id typed, the target picked from the provider's models.</summary>
public sealed class ModelMapSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private IReadOnlyList<ProviderModelDto> _models = [];
    private string _from = "";
    private string _to = "";
    private ArcButton? _save;

    /// <summary>The sheet for <paramref name="provider"/>.</summary>
    public ModelMapSheet(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
    }

    /// <summary>Opens empty over <paramref name="models"/>.</summary>
    public void ShowFor(IReadOnlyList<ProviderModelDto> models)
    {
        _models = models;
        _from = "";
        _to = "";
        Show();
    }

    /// <summary>Fills the form (scenes).</summary>
    internal void Fill(string from, string to)
    {
        _from = from;
        _to = to;
        Build();
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.modelMap.add");
        Description = Loc.T("providers.modelMap.addDetail");
        var from = PdUi.TextBox(_from, v => { _from = v; Update(); }, mono: true, name: Loc.T("providers.modelMap.from"), placeholder: "deepseek-v4.1", visibleLabel: true);
        var to = PdUi.Choice(_models.Select(m => (m.ModelId, m.ModelId)), _to.Length > 0 ? _to : null, v => { _to = v; Update(); }, name: Loc.T("providers.modelMap.to"), placeholder: Loc.T("providers.modelMap.toPlaceholder"));
        to.Label = Loc.T("providers.modelMap.to");
        to.HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = PdUi.Stack(12, from, to);
        InitialFocus = from; // web autoFocus
        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, click: () => _ = SaveAsync());
        Footer = [PdUi.Cancel(this), _save];
        Update();
    }

    private void Update()
    {
        var requested = _from.Trim();
        if (_save is not null) _save.IsEnabled = requested.Length > 0 && _to.Length > 0 && requested != _to;
    }

    private async Task SaveAsync()
    {
        var map = ProviderDetailLogic.ModelMapOf((_api.Provider.Data ?? _provider).Settings);
        var ok = await PdRun.TryAsync(_ctx, () => _api.SetModelMapAsync(ProviderDetailLogic.WithMapping(map, _from.Trim(), _to)), _save);
        if (!ok) return;
        Close();
        _ctx.Feedback.Success(Loc.T("providers.modelMap.saved"));
    }
}

/// <summary>Adds model ids by hand (web <c>AddModelsSheet</c>): one per line or comma separated; they link to system models on the server.</summary>
public sealed class AddModelsSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private string _text = "";
    private ArcButton? _add;

    /// <summary>The sheet.</summary>
    public AddModelsSheet(PageContext ctx, ProviderDetailApi api)
    {
        _ctx = ctx;
        _api = api;
    }

    /// <summary>Opens with an empty box (web <c>useEffect([open])</c>).</summary>
    public new void Show()
    {
        _text = "";
        base.Show();
    }

    /// <summary>Types into the box (scenes).</summary>
    internal void Type(string text)
    {
        _text = text;
        Build();
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.addModels");
        Description = Loc.T("providers.addModelsDetail");
        var area = PdUi.Area(_text, v => { _text = v; Update(); }, Loc.T("providers.modelIds"), 6 * 20 + 16, mono: true, placeholder: "deepseek-v4-pro\nclaude-sonnet-4-6");
        Content = area;
        InitialFocus = area; // web autoFocus
        _add = PdUi.Button("", PdUi.Look.Primary, click: () => _ = AddAsync());
        Footer = [PdUi.Cancel(this), _add];
        Update();
    }

    private void Update()
    {
        if (_add is null) return;
        var n = ProviderDetailLogic.ParseModelIds(_text).Count;
        _add.Content = Loc.T("providers.addSelected", ("count", n));
        _add.IsEnabled = n > 0;
    }

    private async Task AddAsync()
    {
        List<ProviderModelDto> added = [];
        var ok = await PdRun.TryAsync(_ctx, async () => added = await _api.AddModelsAsync(ProviderDetailLogic.ParseModelIds(_text)), _add);
        if (!ok) return;
        Close();
        _ctx.Feedback.Success(Loc.T("providers.modelsAdded", ("count", added.Count)));
    }
}

/// <summary>
/// The upstream's model list (web <c>RemoteModelsSheet</c>, 560 px): search, tick the ones to add (already added ones are marked), select all,
/// and on an upstream 401/403 the key-alternative hint.
/// </summary>
public sealed class RemoteModelsSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private readonly HashSet<string> _checked = [];
    private readonly StackPanel _list = new();
    private string _search = "";
    private ArcButton? _add;
    private ArcButton? _all;
    private IDisposable? _watch;

    /// <summary>The sheet for <paramref name="provider"/>.</summary>
    public RemoteModelsSheet(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        SheetWidth = 560;
        Opened += (_, _) =>
        {
            _api.RemoteModels.Enabled = true;
            _watch ??= _api.RemoteModels.Activate();
            _api.RemoteModels.PropertyChanged += OnRemote;
        };
        Closed += (_, _) =>
        {
            _api.RemoteModels.PropertyChanged -= OnRemote;
            _watch?.Dispose();
            _watch = null;
            _api.RemoteModels.Enabled = false;
        };
    }

    /// <summary>Opens with nothing ticked.</summary>
    public new void Show()
    {
        _checked.Clear();
        _search = "";
        base.Show();
        // the query is enabled by Opened; make sure it also runs when Opened came before the attach
        _api.RemoteModels.Enabled = true;
        _watch ??= _api.RemoteModels.Activate();
    }

    /// <summary>Ticks a model (scenes).</summary>
    internal void Tick(string id)
    {
        _checked.Add(id);
        RebuildList();
    }

    private void OnRemote(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(RebuildList);

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.fetchRemote");
        Description = Loc.T("providers.fetchRemoteDetail");
        var search = new SearchField { Placeholder = Loc.T("providers.searchModels"), IsLabelVisible = false, Text = _search, Margin = new Thickness(0, 0, 0, 12) };
        search.TextChanged += (_, _) => { _search = search.Text ?? ""; RebuildList(); };
        Content = new StackPanel { Children = { search, _list } };
        _all = PdUi.Button(Loc.T("common.selectAll"), PdUi.Look.Plain, click: () =>
        {
            foreach (var m in ProviderDetailLogic.FilterRemote(_api.RemoteModels.Data, _search).Where(m => !m.AlreadyAdded)) _checked.Add(m.Id);
            RebuildList();
        });
        _add = PdUi.Button("", PdUi.Look.Primary, click: () => _ = AddAsync());
        // web: "select all" sits at the left of the footer (mr-auto)
        var footer = new DockPanel { LastChildFill = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(_all, Dock.Left);
        var right = PdUi.Inline(8, PdUi.Cancel(this), _add);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(_all);
        footer.Children.Add(right);
        Actions = footer;
        RebuildList();
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        var q = _api.RemoteModels;
        if (q.IsLoading || !q.HasData && !q.IsError) _list.Children.Add(new LoadingState(4));
        if (q.IsError)
        {
            _list.Children.Add(PdUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message));
            if (q.Error is ApiException api)
            {
                var status = api.Details?["httpStatus"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : (int?)null;
                var hint = new KeyAlternativeHint(_ctx, _api, _api.Provider.Data ?? _provider, status) { Margin = new Thickness(0, 12, 0, 0) };
                _list.Children.Add(hint);
            }
        }
        var list = ProviderDetailLogic.FilterRemote(q.Data, _search);
        if (list.Count > 0 || q.HasData)
        {
            var rows = list.Select(m =>
            {
                var detail = m.LinkedSystemModelId is { } l ? Loc.T("providers.linksTo", ("id", l)) : Loc.T("providers.noLink");
                var label = PdUi.Text(m.Id, 12, ArcDesignTokenKind.Foreground, mono: true);
                if (m.AlreadyAdded) return (Control)PdUi.Row(label, detail, PdUi.Badge(Loc.T("providers.added")));
                var box = new Checkbox { IsChecked = _checked.Contains(m.Id), VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(box, m.Id);
                box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) _checked.Add(m.Id); else _checked.Remove(m.Id); Update(); };
                var row = PdUi.Row(label, detail, box);
                row.Invoked += (_, _) => box.IsChecked = box.IsChecked != true;
                return row;
            }).ToList();
            var card = PdUi.Card(PdUi.Divided(rows));
            card.Margin = new Thickness(0, list.Count > 0 && q.IsError ? 12 : 0, 0, 0);
            if (rows.Count > 0) _list.Children.Add(card);
        }
        if (_all is not null) _all.IsEnabled = list.Count > 0;
        Update();
    }

    private void Update()
    {
        if (_add is null) return;
        _add.Content = Loc.T("providers.addSelected", ("count", _checked.Count));
        _add.IsEnabled = _checked.Count > 0;
    }

    private async Task AddAsync()
    {
        List<ProviderModelDto> added = [];
        var ok = await PdRun.TryAsync(_ctx, async () => added = await _api.AddModelsAsync(_checked.ToList()), _add);
        if (!ok) return;
        Close();
        _ctx.Feedback.Success(Loc.T("providers.modelsAdded", ("count", added.Count)));
    }
}
