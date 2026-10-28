using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Providers;
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
/// The settings tab (web <c>SettingsTab</c>): basic info, auth scheme, endpoints with the preferred-protocol chips, billing (price key and
/// multiplier), advanced (proxy, extra headers, the adapter settings JSON) and read-only info. Edits go to a working copy
/// (<see cref="ProviderSettingsForm"/>); while it differs from the saved provider an "unsaved" pill offers Revert and Save, and Save sends only
/// the changed fields. Rows that change shape (endpoints, headers, chips) rebuild their group; text edits never rebuild their own input.
/// </summary>
public sealed class SettingsTabView : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private readonly ProviderSettingsForm _base;
    private ProviderSettingsForm _form;
    private readonly StackPanel _root = new();
    private ContentControl _endpoints = new();
    private ContentControl _headers = new();
    private readonly Border _pill = new() { HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 4, 4, 4), CornerRadius = new CornerRadius(999), Margin = new Thickness(0, 0, 0, 8) };
    // web: sticky bottom-2 — the pill's own 8 px bottom margin is the gap to the viewport's edge while it is stuck
    private readonly StickyBottom _sticky = new() { Offset = 0 };
    private ArcButton? _save;
    private IDisposable? _keys;

    /// <summary>Builds the tab for <paramref name="provider"/>.</summary>
    public SettingsTabView(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        _base = _form = ProviderSettingsForm.From(provider);
        EndpointSheet = new AddEndpointSheet(e => Set(f => f with { Endpoints = [.. f.Endpoints, e] }, endpoints: true));
        HeaderSheet = new AddHeaderSheet((n, v) => Set(f => f.AddHeader(n, v), headers: true));
        Content = new Panel { Children = { _root, EndpointSheet, HeaderSheet } };
        Build();
    }

    /// <summary>The working copy.</summary>
    public ProviderSettingsForm Form => _form;

    /// <summary>The form differs from the provider as loaded.</summary>
    public bool IsDirty => _form.IsDirty(_base);

    /// <summary>The add-endpoint sheet.</summary>
    public AddEndpointSheet EndpointSheet { get; }

    /// <summary>The add-header sheet.</summary>
    public AddHeaderSheet HeaderSheet { get; }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keys = _api.PriceKeys.Activate();
        _api.PriceKeys.PropertyChanged += OnKeys;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.PriceKeys.PropertyChanged -= OnKeys;
        _keys?.Dispose();
        _keys = null;
    }

    private void OnKeys(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_api.PriceKeys.Data)) Build();
    }

    /// <summary>Applies an edit; the flags rebuild the parts whose rows depend on it.</summary>
    internal void Set(Func<ProviderSettingsForm, ProviderSettingsForm> change, bool endpoints = false, bool headers = false, bool all = false)
    {
        _form = change(_form);
        if (all) Build();
        else
        {
            if (endpoints) _endpoints.Content = EndpointsGroup();
            if (headers) _headers.Content = HeadersEditor();
        }
        UpdatePill();
    }

    private void Build()
    {
        _root.Children.Clear();
        // fresh hosts: the old group rows still hold the previous ones
        _endpoints = new ContentControl();
        _headers = new ContentControl();
        var basic = new SettingsGroup { Title = Loc.T("providers.section.basic") };
        basic.Rows.Add(PdUi.Row(Loc.T("providers.name"), null, PdUi.TextBox(_form.Name, v => Set(f => f with { Name = v }), 256, name: Loc.T("providers.name"))));
        basic.Rows.Add(PdUi.Row(Loc.T("common.enabled"), null, PdUi.Toggle(_form.Enabled, on => Set(f => f with { Enabled = on }), Loc.T("common.enabled"))));
        basic.Rows.Add(PdUi.Row(Loc.T("providers.notes"), null, PdUi.TextBox(_form.Notes, v => Set(f => f with { Notes = v.Length > 0 ? v : null }), 256, name: Loc.T("providers.notes"))));
        _root.Children.Add(basic);

        // API key and subscription accounts live on their own tab; only the scheme is here.
        var auth = new SettingsGroup { Title = Loc.T("providers.section.auth") };
        auth.Rows.Add(PdUi.Row(Loc.T("providers.authScheme"), null,
            PdUi.Choice(ProviderProtocols.AuthOptions, _form.AuthScheme, v => Set(f => f with { AuthScheme = v }), 192, Loc.T("providers.authScheme"))));
        _root.Children.Add(auth);

        _endpoints.Content = EndpointsGroup();
        _root.Children.Add(_endpoints);

        var billing = new SettingsGroup { Title = Loc.T("providers.section.billing"), Footer = Loc.T("providers.billingFooter") };
        billing.Rows.Add(PdUi.Row(Loc.T("providers.priceKey"), Loc.T("providers.priceKeyHint"),
            PdUi.Choice(ProviderDetailLogic.PriceKeyOptions(_provider, _api.PriceKeys.Data), _form.PriceKeyChoice,
                v => Set(f => f with { PriceKey = ProviderSettingsForm.PriceKeyOf(v) }), 224, Loc.T("providers.priceKey"))));
        billing.Rows.Add(PdUi.Row(Loc.T("providers.multiplier"), Loc.T("providers.multiplierHint"),
            PdUi.Number(_form.PriceMultiplier, v => Set(f => f with { PriceMultiplier = v ?? 1 }), 112, Loc.T("providers.multiplier"), minimum: 0)));
        _root.Children.Add(billing);

        var advanced = new SettingsGroup { Title = Loc.T("providers.section.advanced") };
        advanced.Rows.Add(PdUi.Row(Loc.T("providers.proxy"), Loc.T("providers.proxyHint"),
            PdUi.TextBox(_form.HttpProxy, v => Set(f => f with { HttpProxy = v.Length > 0 ? v : null }), 256, mono: true, name: Loc.T("providers.proxy"), placeholder: "http://127.0.0.1:7890")));
        _headers.Content = HeadersEditor();
        advanced.Rows.Add(PdUi.Pad(_headers));
        if (_provider.Settings.Count > 0)
        {
            var area = PdUi.Area(PricingDocument.PrettyJson(_form.Settings), null, Loc.T("providers.adapterSettings"), 4 * 20 + 16, mono: true);
            area.LostFocus += (_, _) =>
            {
                try
                {
                    if (JsonNode.Parse(area.Text ?? "") is JsonObject o) Set(f => f with { Settings = o });
                    else _ctx.Feedback.Error(Loc.T("common.invalidJson"));
                }
                catch (JsonException)
                {
                    _ctx.Feedback.Error(Loc.T("common.invalidJson"));
                }
            };
            advanced.Rows.Add(PdUi.Pad(area));
        }
        _root.Children.Add(advanced);

        var info = new SettingsGroup { Title = Loc.T("providers.section.info") };
        var kv = PdUi.Stack(0, new KeyValueRow("ID", _provider.Id));
        if (!string.IsNullOrEmpty(_provider.AdapterId)) kv.Children.Add(new KeyValueRow(Loc.T("providers.adapter"), _provider.AdapterId));
        kv.Children.Add(new KeyValueRow(Loc.T("providers.boundClients"), ProviderDetailLogic.BoundClients(_provider.BoundClients)));
        info.Rows.Add(PdUi.Pad(kv));
        _root.Children.Add(info);

        BuildPill();
        _sticky.Child ??= _pill;
        _root.Children.Add(_sticky);
        UpdatePill();
    }

    private Control EndpointsGroup()
    {
        var group = new SettingsGroup { Title = Loc.T("providers.section.endpoints"), Footer = Loc.T("providers.endpointsFooter") };
        for (var i = 0; i < _form.Endpoints.Count; i++)
        {
            var index = i;
            var e = _form.Endpoints[i];
            var protocol = PdUi.Choice(ProviderProtocols.Options, e.Protocol, v => Avalonia.Threading.Dispatcher.UIThread.Post(() => Set(f => f.WithEndpoint(index, x => x with { Protocol = v }), endpoints: true)), 192, Loc.T("providers.protocol"));
            var url = PdUi.TextBox(e.BaseUrl, v => Set(f => f.WithEndpoint(index, x => x with { BaseUrl = v })), mono: true, name: Loc.T("providers.baseUrl"));
            url.MinWidth = 240;
            var full = PdUi.LabeledToggle(e.FullUrl == true, on => Set(f => f.WithEndpoint(index, x => x with { FullUrl = on })), Loc.T("providers.fullUrl"));
            var delete = PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.Trash2, small: true, name: Loc.T("common.delete"), click: () => Set(f => f.WithoutEndpoint(index), endpoints: true));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(url, 1);
            Grid.SetColumn(full, 2);
            Grid.SetColumn(delete, 3);
            line.Children.Add(protocol);
            line.Children.Add(url);
            line.Children.Add(full);
            line.Children.Add(delete);
            group.Rows.Add(PdUi.Pad(line));
        }
        group.Rows.Add(PdUi.Pad(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = PdUi.Button(Loc.T("providers.addEndpoint"), PdUi.Look.Plain, LucideIconKind.Plus, small: true, click: EndpointSheet.Show) }, 16, 8));

        // A protocol with no endpoint address is skipped when the upstream is picked: say which ones are dead weight.
        var detail = PdUi.Stack(4, PdUi.Text(Loc.T("providers.preferredHint"), 11, ArcDesignTokenKind.TextSecondary, wrap: true));
        foreach (var p in _form.PreferredMissing)
            detail.Children.Add(PdUi.Text(Loc.T("providers.preferredMissing", ("protocol", ProviderProtocols.Name(p))), 11, ArcDesignTokenKind.Warning, wrap: true));
        var chips = new ChipGroup { Label = Loc.T("providers.preferred"), IsMultiple = true, AutoScrollToSelectedItem = false };
        foreach (var (value, _) in ProviderProtocols.Options)
            chips.Items.Add(new SelectItem { Content = ProviderSettingsForm.ChipLabel(_form.Preferred, value), Value = value });
        for (var i = 0; i < ProviderProtocols.Options.Count; i++)
            if (_form.Preferred.Contains(ProviderProtocols.Options[i].Value)) chips.Selection.Select(i);
        chips.SelectionChanged += (_, _) =>
        {
            var next = ProviderSettingsForm.ReorderPreferred(_form.Preferred, chips.SelectedValues.OfType<string>().ToList());
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Set(f => f with { Preferred = next }, endpoints: true));
        };
        var row = PdUi.Row(Loc.T("providers.preferred"), detail, chips);
        group.Rows.Add(row);
        return group;
    }

    private Control HeadersEditor()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(PdUi.Text(Loc.T("providers.headers"), 13, ArcDesignTokenKind.Foreground));
        for (var i = 0; i < _form.Headers.Count; i++)
        {
            var index = i;
            var (k, v) = _form.Headers[i];
            var name = PdUi.TextBox(k, t => Set(f => f.WithHeader(index, t, f.Headers[index].Value)), 192, mono: true, name: Loc.T("providers.headerName"), placeholder: "Header");
            var value = PdUi.TextBox(v, t => Set(f => f.WithHeader(index, f.Headers[index].Name, t)), mono: true, name: Loc.T("providers.headerValue"), placeholder: "value");
            var delete = PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.Trash2, small: true, name: Loc.T("common.delete"), click: () => Set(f => f.WithoutHeader(index), headers: true));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(value, 1);
            Grid.SetColumn(delete, 2);
            line.Children.Add(name);
            line.Children.Add(value);
            line.Children.Add(delete);
            stack.Children.Add(line);
        }
        stack.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = PdUi.Button(Loc.T("providers.addHeader"), PdUi.Look.Plain, LucideIconKind.Plus, small: true, click: HeaderSheet.Show) });
        return stack;
    }

    /// <summary>
    /// The "unsaved" pill (web: a glass rounded-full bar, sticky at the bottom of the page): at the end of the tab, held at the viewport's
    /// bottom by <see cref="StickyBottom"/> while the tab runs past it.
    /// </summary>
    private void BuildPill()
    {
        _pill.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _pill.BorderThickness = new Thickness(0.5);
        _pill.BoxShadow = BoxShadows.Parse("0 4 16 0 #22000000");
        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, small: true, click: () => _ = SaveAsync());
        var revert = PdUi.Button(Loc.T("common.revert"), small: true, click: () => { _form = _base; Build(); });
        _pill.Child = PdUi.Inline(8, PdUi.Text(Loc.T("common.unsaved"), 12, ArcDesignTokenKind.TextSecondary), revert, _save);
    }

    private void UpdatePill() => _pill.IsVisible = _form.IsDirty(_base);

    private async Task SaveAsync()
    {
        var patch = _form.Patch(_base);
        if (await PdRun.TryAsync(_ctx, () => _api.UpdateAsync(patch), _save)) _ctx.Feedback.Success(Loc.T("common.saved"));
    }
}

/// <summary>Adds an endpoint (web <c>AddEndpointSheet</c>): protocol, an http(s) address, and whether it is the full request URL.</summary>
public sealed class AddEndpointSheet : PdSheet
{
    private readonly Action<EndpointRow> _add;
    private string _protocol = "openai-chat";
    private string _url = "";
    private bool _full;
    private ArcButton? _ok;
    private Input? _input;
    private TextBlock? _hint;

    /// <summary>The sheet; <paramref name="add"/> receives the endpoint.</summary>
    public AddEndpointSheet(Action<EndpointRow> add) => _add = add;

    /// <summary>Opens with an empty address (the protocol stays, like the web's state).</summary>
    public new void Show()
    {
        _url = "";
        _full = false;
        base.Show();
    }

    /// <summary>Types an address (scenes).</summary>
    internal void Type(string url)
    {
        _url = url;
        Build();
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.addEndpoint");
        Description = Loc.T("providers.addEndpointDetail");
        var protocol = PdUi.Choice(ProviderProtocols.Options, _protocol, v => _protocol = v, name: Loc.T("providers.protocol"));
        protocol.HorizontalAlignment = HorizontalAlignment.Stretch;
        _input = PdUi.TextBox(_url, v => { _url = v; Update(); }, mono: true, name: Loc.T("providers.baseUrl"), placeholder: "https://api.example.com/v1");
        _hint = PdUi.Hint("");
        var full = PdUi.LabeledToggle(_full, on => { _full = on; Update(); }, Loc.T("providers.fullUrl"));
        Content = PdUi.Stack(12,
            PdUi.Field(Loc.T("providers.protocol"), protocol),
            PdUi.Stack(6, PdUi.Text(Loc.T("providers.baseUrl"), 13, ArcDesignTokenKind.Foreground, FontWeight.Medium), _input, _hint),
            full);
        InitialFocus = _input; // web autoFocus
        _ok = PdUi.Button(Loc.T("common.add"), PdUi.Look.Primary, click: () =>
        {
            _add(new EndpointRow(_protocol, _url.Trim(), _full ? true : null));
            Close();
        });
        Footer = [PdUi.Cancel(this), _ok];
        Update();
    }

    private void Update()
    {
        var valid = ProviderSettingsForm.ValidBaseUrl(_url);
        if (_ok is not null) _ok.IsEnabled = valid;
        if (_input is not null) _input.IsInvalid = _url.Length > 0 && !valid;
        if (_hint is not null) _hint.Text = Loc.T(_full ? "providers.fullUrlHint" : "providers.baseUrlHint");
    }
}

/// <summary>Adds an extra header (web <c>AddHeaderSheet</c>): a name of letters, digits and dashes, and a value.</summary>
public sealed class AddHeaderSheet : PdSheet
{
    private readonly Action<string, string> _add;
    private string _name = "";
    private string _value = "";
    private ArcButton? _ok;
    private Input? _nameBox;

    /// <summary>The sheet; <paramref name="add"/> receives (name, value).</summary>
    public AddHeaderSheet(Action<string, string> add) => _add = add;

    /// <summary>Opens empty.</summary>
    public new void Show()
    {
        _name = "";
        _value = "";
        base.Show();
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.addHeader");
        Description = Loc.T("providers.addHeaderDetail");
        _nameBox = PdUi.TextBox(_name, v => { _name = v; Update(); }, mono: true, name: Loc.T("providers.headerName"), placeholder: "X-Custom-Header");
        var value = PdUi.TextBox(_value, v => _value = v, mono: true, name: Loc.T("providers.headerValue"));
        Content = PdUi.Stack(12, PdUi.Field(Loc.T("providers.headerName"), _nameBox), PdUi.Field(Loc.T("providers.headerValue"), value));
        InitialFocus = _nameBox; // web autoFocus
        _ok = PdUi.Button(Loc.T("common.add"), PdUi.Look.Primary, click: () =>
        {
            _add(_name.Trim(), _value);
            Close();
        });
        Footer = [PdUi.Cancel(this), _ok];
        Update();
    }

    private void Update()
    {
        var valid = ProviderSettingsForm.ValidHeaderName(_name);
        if (_ok is not null) _ok.IsEnabled = valid;
        if (_nameBox is not null) _nameBox.IsInvalid = _name.Length > 0 && !valid;
    }
}
