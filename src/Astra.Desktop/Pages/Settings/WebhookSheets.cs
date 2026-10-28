using Astra.Desktop.Core.Api;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The webhook editor (web <c>WebhookSheet</c>): name, platform, address, the bot's signing secret for Feishu / DingTalk, timeout, custom
/// headers, and the enabled / proxy / signed switches. Which fields show depends on the platform (<see cref="WebhookDraft"/>). A secret is
/// only ever sent when typed.
/// </summary>
public sealed class WebhookEditorSheet
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly WebhookDraft _draft;
    private readonly Action<string> _onSecret;
    private readonly Sheet _sheet;
    private readonly ControlledSelect _format;
    private readonly Input _url;
    private readonly Input _botSecret;
    private readonly StackPanel _headers = new() { Spacing = 8 };
    private readonly SettingsGroup _switches;
    private readonly SettingsRow _signedRow;
    private readonly ControlledSwitch _signed = new();
    private readonly ArcButton _save;
    private readonly Control _botBox;
    private bool _saving;

    /// <summary>Builds the editor for <paramref name="hook"/> (null = a new one); <paramref name="onSecret"/> receives the generated secret of a new generic webhook.</summary>
    public WebhookEditorSheet(UiHost host, SettingsApi api, WebhookDto? hook, Action<string> onSecret)
    {
        _host = host;
        _api = api;
        _draft = new WebhookDraft(hook);
        _onSecret = onSecret;

        var name = new Input { Label = Loc.T("alerts.webhooks.name"), Text = _draft.Name, MaxLength = 64 };
        name.TextChanged += (_, _) => { _draft.Name = name.Text ?? ""; Validate(); };

        _format = new ControlledSelect(WebhookDraft.Formats.Select(f => (f, WebhookDraft.FormatLabel(f).Text)), _draft.Format, double.NaN)
        {
            Label = Loc.T("alerts.webhooks.format"),
            Description = WebhookDraft.FormatHint(_draft.Format).Text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _format.Chose += f => { _draft.ChangeFormat(f); UpdateFields(); };

        _url = new Input { Label = Loc.T("alerts.webhooks.url"), Text = _draft.Url, PlaceholderText = WebhookDraft.UrlExample(_draft.Format) };
        _url.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _url.TextChanged += (_, _) => { _draft.Url = _url.Text ?? ""; Validate(); };

        // web: a plain <Input type="password"> (no reveal button)
        _botSecret = new Input { Label = Loc.T("alerts.webhooks.botSecret"), Description = Loc.T("alerts.webhooks.botSecretHint"), PasswordChar = '•' };
        _botSecret.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _botSecret.TextChanged += (_, _) => _draft.BotSecret = _botSecret.Text ?? "";
        _botBox = _botSecret;

        // web NumberInput in a Field: a full-width number box, right-aligned figures, no stepper buttons
        var timeout = new Astra.Desktop.Components.SmartRouting.NumberInput(Loc.T("alerts.webhooks.timeout"), (double)(_draft.TimeoutSec ?? 10), 1, 1)
        {
            Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch, IsLabelVisible = true,
        };
        timeout.NumberChanged += v => _draft.TimeoutSec = v is null ? null : (decimal)v.Value;

        var addHeader = Ui.Button(Loc.T("alerts.webhooks.addHeader"), () => { _draft.Headers.Add(new HeaderDraft("", "", false)); RebuildHeaders(); }, icon: LucideIconKind.Plus);
        addHeader.HorizontalAlignment = HorizontalAlignment.Left;
        var headersField = Ui.Labeled(Loc.T("alerts.webhooks.headers"), Ui.Stack(8, _headers, addHeader), Loc.T("alerts.webhooks.headersHint"));
        RebuildHeaders();

        var enabled = new ControlledSwitch { IsChecked = _draft.Enabled };
        enabled.ToggleRequested += on => { _draft.Enabled = on; enabled.SetValue(on); };
        var proxy = new ControlledSwitch { IsChecked = _draft.UseProxy };
        proxy.ToggleRequested += on => { _draft.UseProxy = on; proxy.SetValue(on); };
        _signed.IsChecked = _draft.Signed;
        _signed.ToggleRequested += on => { _draft.Signed = on; _signed.SetValue(on); UpdateFields(); };
        _signedRow = RowBuilder.Row(Loc.T("alerts.webhooks.signed"), null, _signed);
        _switches = RowBuilder.Group(null, null,
            RowBuilder.Row(Loc.T("alerts.webhooks.enabled"), null, enabled),
            RowBuilder.Row(Loc.T("alerts.webhooks.useProxy"), Loc.T("alerts.webhooks.useProxyHint"), proxy));
        _switches.Margin = default;
        ((Control)_switches).Margin = new Thickness(0);
        // A SettingsGroup keeps 20 px below itself for the next group on a page; in a sheet the footer's own gap follows.
        if (_switches.Content is StackPanel inner) inner.Margin = default;

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(name);
        body.Children.Add(_format);
        body.Children.Add(_url);
        body.Children.Add(_botBox);
        body.Children.Add(timeout);
        body.Children.Add(headersField);
        body.Children.Add(_switches);

        var cancel = Ui.Button(Loc.T("common.cancel"), () => _sheet!.Close(), size: ControlSize.Medium);
        _save = Ui.Button(Loc.T("common.save"), () => _ = SaveAsync(), ButtonVariant.Primary, size: ControlSize.Medium);
        _sheet = new Sheet
        {
            SheetWidth = 540,
            Title = Loc.T(_draft.IsNew ? "alerts.webhooks.add" : "alerts.webhooks.edit"),
            // web: the whole dialog scrolls when it is taller than the window (Arc's surface does the same), no inner scroller
            Content = body,
            Footer = [cancel, _save],
        };
        UpdateFields();
        Validate();
    }

    /// <summary>The sheet (scenes open it directly).</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it; completes when it closes.</summary>
    public Task ShowAsync() => _host.ShowAsync(_sheet);

    private void Validate() => _save.IsEnabled = _draft.CanSave && !_saving;

    private void UpdateFields()
    {
        _format.Description = WebhookDraft.FormatHint(_draft.Format).Text;
        _url.PlaceholderText = WebhookDraft.UrlExample(_draft.Format);
        _botBox.IsVisible = _draft.BotSecretVisible;
        _botSecret.PlaceholderText = _draft.BotSecretKept?.Text ?? "SEC…";
        _signedRow.Detail = _draft.SignedHint.Text;
        _signed.SetValue(_draft.Signed);
        var rows = _switches.Rows.Where(r => !ReferenceEquals(r, _signedRow)).ToList();
        if (_draft.SignedVisible) rows.Add(_signedRow);
        RowAssembly.Sync(_switches, rows);
    }

    private void RebuildHeaders()
    {
        _headers.Children.Clear();
        foreach (var header in _draft.Headers.ToList())
        {
            var h = header;
            var n = new Input { Text = h.Name, PlaceholderText = Loc.T("alerts.webhooks.headerName"), Size = ControlSize.Small };
            n.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            n.TextChanged += (_, _) => h.Name = n.Text ?? "";
            var v = new Input { Text = h.Value, PlaceholderText = h.Stored ? Loc.T("alerts.webhooks.headerKept") : Loc.T("alerts.webhooks.headerValue"), Size = ControlSize.Small };
            v.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            v.TextChanged += (_, _) => h.Value = v.Text ?? "";
            var remove = Ui.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () => { _draft.Headers.Remove(h); RebuildHeaders(); });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.4*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(v, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(n);
            grid.Children.Add(v);
            grid.Children.Add(remove);
            _headers.Children.Add(grid);
        }
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        _save.IsLoading = true;
        Validate();
        try
        {
            if (_draft.IsNew)
            {
                var r = await _host.TryAsync(() => _api.CreateWebhookAsync(_draft.CreateBody()));
                if (r is null) return;
                _sheet.Close();
                if (!string.IsNullOrEmpty(r.Secret)) _onSecret(r.Secret);
            }
            else
            {
                var r = await _host.TryAsync(() => _api.UpdateWebhookAsync(_draft.Hook!.Id, _draft.UpdateBody()));
                if (r is null) return;
                _sheet.Close();
                _host.Success(Loc.T("common.saved"));
            }
        }
        finally
        {
            _saving = false;
            _save.IsLoading = false;
            Validate();
        }
    }
}

/// <summary>Shows a generated webhook secret once (web <c>SecretSheet</c>): the secret is never fetched again, so the sheet says so and offers copying it.</summary>
public sealed class WebhookSecretSheet
{
    private readonly UiHost _host;
    private readonly Sheet _sheet;

    /// <summary>Builds the sheet for <paramref name="secret"/>.</summary>
    public WebhookSecretSheet(UiHost host, string secret)
    {
        _host = host;
        var code = new SelectableTextBlock { Text = secret, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        code.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        var box = new Border { Padding = new Thickness(12), Child = code };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        var copy = Ui.Button(Loc.T("common.copy"), () => _ = _host.CopyAsync(secret), size: ControlSize.Medium);
        var done = Ui.Button(Loc.T("common.done"), () => _sheet!.Close(), ButtonVariant.Primary, size: ControlSize.Medium);
        _sheet = new Sheet
        {
            Title = Loc.T("alerts.webhooks.secretTitle"),
            Description = Loc.T("alerts.webhooks.secretShown"),
            Content = box,
            Footer = [copy, done],
        };
    }

    /// <summary>The sheet.</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it.</summary>
    public Task ShowAsync() => _host.ShowAsync(_sheet);
}

/// <summary>A webhook's delivery history (web <c>DeliveriesSheet</c>): each delivery expands to its payload, the receiver's answer and a redeliver button.</summary>
public sealed class DeliveriesSheet
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly WebhookDto _hook;
    private readonly Query<List<WebhookDeliveryDto>> _query;
    private readonly ContentControl _body = new();
    private readonly Sheet _sheet;
    private IDisposable? _token;
    private string? _redelivering;

    /// <summary>Builds the sheet for <paramref name="hook"/>.</summary>
    public DeliveriesSheet(UiHost host, SettingsApi api, WebhookDto hook)
    {
        _host = host;
        _api = api;
        _hook = hook;
        _query = api.Deliveries(hook.Id);
        _sheet = new Sheet { SheetWidth = 640, Title = $"{Loc.T("alerts.webhooks.deliveries")} · {hook.Name}", Content = _body };
        _sheet.Opened += (_, _) => Start();
        _sheet.Closed += (_, _) => Stop();
        Render();
    }

    /// <summary>The sheet.</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it.</summary>
    public Task ShowAsync() => _host.ShowAsync(_sheet);

    /// <summary>Starts watching the deliveries (done on open).</summary>
    public void Start()
    {
        if (_token is not null) return;
        _query.PropertyChanged += OnChanged;
        _token = _query.Activate();
        Render();
    }

    private void Stop()
    {
        _query.PropertyChanged -= OnChanged;
        _token?.Dispose();
        _token = null;
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Render);

    private Accordion? _accordion;
    private readonly List<(string Id, AccordionItem Item)> _items = [];
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    private void Render()
    {
        var data = _query.Data;
        if (data is null)
        {
            _body.Content = _query.IsError ? Ui.ErrorText(UiHost.ErrorText(_query.Error!)) : new LoadingState(4);
            return;
        }
        if (data.Count == 0)
        {
            _body.Content = Ui.Note(Loc.T("alerts.delivery.empty"));
            return;
        }
        // A refetch (after a redeliver, or the poll) rebuilds the list: the open items stay open (web: keyed by delivery id).
        if (_accordion is not null)
            foreach (var (id, item) in _items) if (item.IsExpanded) _open.Add(id); else _open.Remove(id);
        var accordion = new Accordion { ExpandedIndex = -1, ExpandMode = AccordionExpandMode.Multiple };
        _items.Clear();
        foreach (var d in data)
        {
            var item = new AccordionItem { Header = Header(d), Content = Detail(d) };
            accordion.Items.Add(item);
            _items.Add((d.Id, item));
        }
        _accordion = accordion;
        foreach (var (id, item) in _items) if (_open.Contains(id)) item.IsExpanded = true;
        _body.Content = accordion; // web: the dialog itself scrolls, no inner scroller
    }

    private static Control Header(WebhookDeliveryDto d)
    {
        var panel = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Ui.Badge(Loc.T("alerts.delivery.status." + d.Status), WebhookLogic.StatusTone(d.Status)));
        panel.Children.Add(KitStyle.Text(WebhookLogic.EventName(d.Event).Text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground));
        panel.Children.Add(KitStyle.Text(SettingsFormat.DateTime(d.CreatedAt, Loc.Language), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        var outcome = WebhookLogic.Outcome(d);
        if (outcome != "") panel.Children.Add(KitStyle.Text(outcome, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        panel.Children.Add(KitStyle.Text(WebhookLogic.Attempts(d, Loc.Language).Text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        return panel;
    }

    private Control Detail(WebhookDeliveryDto d)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Ui.Note(Loc.T("alerts.delivery.payload")));
        stack.Children.Add(new CodeBlock { Code = JsonText.Indented(d.Payload), Language = "json" });
        if (!string.IsNullOrEmpty(d.ResponseSnippet))
        {
            stack.Children.Add(Ui.Note(Loc.T("alerts.delivery.response")));
            stack.Children.Add(new CodeBlock { Code = d.ResponseSnippet, Language = "text" });
        }
        if (d.Status != "pending")
        {
            var id = d.Id;
            stack.Children.Add(Ui.Button(Loc.T("alerts.delivery.redeliver"), () => _ = RedeliverAsync(id), loading: _redelivering == id));
        }
        return stack;
    }

    private async Task RedeliverAsync(string id)
    {
        _redelivering = id;
        Render();
        var r = await _host.TryAsync(() => _api.RedeliverAsync(id));
        _redelivering = null;
        if (r is not null) _host.Success(Loc.T("alerts.delivery.redelivered"));
        Render();
    }
}
