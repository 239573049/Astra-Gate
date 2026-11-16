using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// A provider's several API keys (web <c>ProviderKeyList</c>). A provider that holds one key shows only the "add another key" row; once it
/// has a list (<c>settings.key_pool</c>) each key is a row with its state, protocol, weight, usage and controls (use first, move, edit,
/// delete, enable), under the account switch mode shared with subscription accounts (manual / failover / balanced).
/// </summary>
public sealed class ProviderKeyListView : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderKeysApi _keys;
    private readonly AccountsApi _accounts;
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ProviderDto _provider;
    private readonly List<IDisposable> _watches = [];
    private ProviderKeySheet? _sheet;

    /// <summary>Builds the list for <paramref name="provider"/>.</summary>
    public ProviderKeyListView(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        _keys = new ProviderKeysApi(ctx.Queries, ctx.Api, provider.Id);
        _accounts = new AccountsApi(ctx.Api, ctx.Queries);
        Content = _body;
        Rebuild();
    }

    /// <summary>A new provider object (a save, a refetch): the list follows <c>settings.key_pool</c>.</summary>
    internal void UpdateProvider(ProviderDto provider)
    {
        _provider = provider;
        Rebuild();
    }

    private bool Pool => ProviderKeyLogic.IsPool(_provider);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _keys.Keys.PropertyChanged += OnChanged;
        _api.Usage.PropertyChanged += OnChanged;
        _accounts.Policy(_provider.Id).PropertyChanged += OnChanged;
        WatchWhenPool();
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _keys.Keys.PropertyChanged -= OnChanged;
        _api.Usage.PropertyChanged -= OnChanged;
        _accounts.Policy(_provider.Id).PropertyChanged -= OnChanged;
        foreach (var w in _watches) w.Dispose();
        _watches.Clear();
    }

    private bool _attached;

    // The list and the switch mode are read only for a provider that has a key list (web: useProviderKeys(id, pool)).
    private void WatchWhenPool()
    {
        if (!_attached || !Pool || _watches.Count > 0) return;
        _watches.Add(_keys.Keys.Activate());
        _watches.Add(_accounts.Policy(_provider.Id).Activate());
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private SubscriptionPolicyDto? Policy => _accounts.Policy(_provider.Id).Data;

    private string _sig = "";

    private void Rebuild()
    {
        WatchWhenPool();
        var list = Pool ? _keys.Keys.Data ?? [] : [];
        var signature = string.Join('|', Pool, Json.SerializeApi(list), Policy?.SwitchMode, _api.Usage.Data is { } u ? Json.SerializeApi(u) : "", Loc.Language,
            _provider.Endpoints.Count);
        if (signature == _sig) return;
        _sig = signature;
        _body.Content = Pool ? BuildPool(list) : BuildSingle();
    }

    private Control BuildSingle()
    {
        var group = new SettingsGroup { Footer = Loc.T("providers.keys.footer") };
        group.Rows.Add(PdUi.Row(Loc.T("providers.keys.addAnother"), null, PdUi.Button(Loc.T("providers.keys.add"), icon: LucideIconKind.Plus, click: () => OpenSheet(null))));
        return group;
    }

    private Control BuildPool(List<ProviderAccountDto> list)
    {
        var mode = Policy?.SwitchMode ?? SwitchModeFallback;
        var group = new SettingsGroup
        {
            Title = Loc.T("providers.keys.title"),
            Footer = Loc.T(mode switch { "balanced" => "providers.keys.modeBalanced", "failover" => "providers.keys.modeFailover", _ => "providers.keys.modeManual" }),
        };
        var picker = PdUi.Choice(
            [("manual", Loc.T("providers.subscription.policy.manual")), ("failover", Loc.T("providers.subscription.policy.failover")), ("balanced", Loc.T("providers.subscription.policy.balanced"))],
            mode, m => _ = RunAsync(() => _accounts.UpdatePolicyAsync(_provider.Id, new SubscriptionPolicyPatch(null, m, null))), 140, Loc.T("providers.subscription.policy.switchMode"));
        var add = PdUi.Button(Loc.T("providers.keys.add"), PdUi.Look.Primary, LucideIconKind.Plus, click: () => OpenSheet(null));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 12) };
        header.Children.Add(picker);
        Grid.SetColumn(add, 2);
        header.Children.Add(add);
        group.Rows.Add(header);
        if (_keys.Keys.IsError) group.Rows.Add(PdUi.Pad(PdUi.Alert(AlertTone.Danger, Loc.T("providers.keys.error"), _keys.Keys.Error?.Message)));
        var now = _ctx.Clock.Now;
        for (var i = 0; i < list.Count; i++) group.Rows.Add(KeyRow(list, i, mode == "manual", now));
        return group;
    }

    private const string SwitchModeFallback = "failover";

    private Control KeyRow(List<ProviderAccountDto> list, int index, bool manual, DateTimeOffset now)
    {
        var k = list[index];
        var state = ProviderKeyLogic.StateOf(k, now);
        var title = PdUi.Inline(8, PdUi.Text(k.DisplayName, 13, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        if (k.IsCurrent && manual) title.Children.Add(PdUi.Badge(Loc.T("providers.keys.current"), Tone.Accent));
        if (k.Protocol is { } p && ApiProtocols.TryParse(p, out var protocol)) title.Children.Add(PdUi.Badge(ProviderKeyLogic.ProtocolName(protocol)));
        if (k.Weight > 1) title.Children.Add(PdUi.Badge("×" + k.Weight));
        if (!string.IsNullOrEmpty(k.HttpProxy)) title.Children.Add(PdUi.Badge(Loc.T("providers.keys.proxyBadge")));

        var stateText = state switch
        {
            KeyState.Off => Loc.T("providers.keys.state.off"),
            KeyState.AuthFailed => Loc.T("providers.keys.state.authFailed", ("when", AccountRules.When(k.CooldownUntilUtc))),
            KeyState.Cooling => Loc.T("providers.keys.state.cooling", ("when", AccountRules.When(k.CooldownUntilUtc))),
            _ => Loc.T("providers.keys.state.ok"),
        };
        var detail = string.Join(" · ", new[] { k.KeyMasked ?? "••••", stateText, k.Notes }.Where(s => !string.IsNullOrEmpty(s)));
        var tone = state switch { KeyState.Ok => ArcDesignTokenKind.Success, KeyState.Off => ArcDesignTokenKind.TextMuted, KeyState.AuthFailed => ArcDesignTokenKind.Danger, _ => ArcDesignTokenKind.Warning };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        dot.Token(Border.BackgroundProperty, tone);
        title.Children.Insert(0, dot);

        var usage = _api.Usage.Data?.Accounts.FirstOrDefault(u => u.AccountId == k.Id);
        var usageText = PdUi.Stack(0,
            PdUi.Text($"{Loc.T("providers.usage.today")}: {AccountRules.Count(usage?.Today.Requests ?? 0, true)} {Loc.T("providers.usage.requests")} · {AccountRules.Count(usage?.Today.Tokens ?? 0, true)} {Loc.T("providers.usage.tokens")}", 11, ArcDesignTokenKind.TextSecondary),
            PdUi.Text($"{Loc.T("providers.usage.total")}: {AccountRules.Count(usage?.Total.Requests ?? 0, true)} · {AccountRules.Nanos(usage?.Total.CostNanoUsd ?? 0, true)}", 11, ArcDesignTokenKind.TextMuted));
        usageText.Width = 190;

        var controls = PdUi.Inline(4);
        if (manual && !k.IsCurrent) controls.Children.Add(PdUi.Button(Loc.T("providers.keys.use"), small: true, icon: LucideIconKind.KeyRound, click: () => _ = RunAsync(() => _keys.ActivateAsync(k.Id))));
        var up = PdUi.Button(null, small: true, icon: LucideIconKind.ArrowUp, name: Loc.T("providers.keys.moveUp"), click: () => Move(list, index, -1));
        up.IsEnabled = index > 0;
        var down = PdUi.Button(null, small: true, icon: LucideIconKind.ArrowDown, name: Loc.T("providers.keys.moveDown"), click: () => Move(list, index, 1));
        down.IsEnabled = index < list.Count - 1;
        controls.Children.Add(up);
        controls.Children.Add(down);
        controls.Children.Add(PdUi.Button(null, small: true, icon: LucideIconKind.Pencil, name: Loc.T("common.edit"), click: () => OpenSheet(k)));
        controls.Children.Add(PdUi.Button(null, PdUi.Look.Destructive, LucideIconKind.Trash2, small: true, name: Loc.T("common.delete"), click: () => _ = RemoveAsync(k)));
        controls.Children.Add(PdUi.Toggle(k.Enabled, on => _ = RunAsync(() => _keys.PatchAsync(k.Id, new ProviderKeyPatch(Enabled: on))), Loc.T("providers.keys.enabled")));

        var row = PdUi.Row(title, PdUi.RowText(detail, 11), usageText, controls);
        return row;
    }

    private void Move(List<ProviderAccountDto> list, int index, int delta)
    {
        if (ProviderKeyLogic.Moved(list, index, delta) is { } ids) _ = RunAsync(() => _keys.ReorderAsync(ids));
    }

    private async Task RemoveAsync(ProviderAccountDto key)
    {
        if (!await _ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("providers.keys.removeConfirm", ("name", key.DisplayName)), null, Loc.T("common.delete"), null, true))) return;
        await RunAsync(() => _keys.RemoveAsync(key.Id));
    }

    private async Task RunAsync(Func<Task> work) => await PdRun.TryAsync(_ctx, work);

    private void OpenSheet(ProviderAccountDto? key)
    {
        _sheet ??= new ProviderKeySheet(_ctx, _keys);
        _sheet.Show(_provider, key);
    }
}

/// <summary>Add or change one key (web <c>KeySheet</c>): name, secret, protocol limit, weight, own proxy and a note.</summary>
public sealed class ProviderKeySheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderKeysApi _keys;
    private ProviderDto? _provider;
    private ProviderAccountDto? _existing;
    private string _name = "", _secret = "", _protocol = "", _proxy = "", _notes = "";
    private decimal? _weight = 1;
    private ArcButton? _save;

    /// <summary>The (closed) sheet.</summary>
    public ProviderKeySheet(PageContext ctx, ProviderKeysApi keys)
    {
        _ctx = ctx;
        _keys = keys;
    }

    /// <summary>Opens to add a key (<paramref name="key"/> null) or change <paramref name="key"/>.</summary>
    public void Show(ProviderDto provider, ProviderAccountDto? key)
    {
        _provider = provider;
        _existing = key;
        _name = key?.DisplayName ?? "";
        _secret = "";
        _protocol = key?.Protocol ?? "";
        _weight = key?.Weight ?? 1;
        _proxy = key?.HttpProxy ?? "";
        _notes = key?.Notes ?? "";
        Show();
    }

    private bool CanSave => ProviderKeyLogic.WeightOk(_weight) && (_existing is not null || _secret.Trim().Length > 0);

    /// <inheritdoc />
    protected override void Build()
    {
        if (_provider is not { } p) return;
        Title = Loc.T(_existing is null ? "providers.keys.addTitle" : "providers.keys.editTitle");
        var protocols = new List<(string, string)> { ("", Loc.T("providers.keys.protocolAll")) };
        protocols.AddRange(p.Endpoints.Select(e => (e.Protocol.ToId(), ProviderKeyLogic.ProtocolName(e.Protocol))));
        var secret = PdUi.Secret("", v => { _secret = v; Enable(); }, 360, Loc.T("providers.apiKey"), _existing is null ? "sk-…" : Loc.T("providers.replaceKey"));
        secret.HorizontalAlignment = HorizontalAlignment.Stretch;
        var name = PdUi.TextBox(_name, v => _name = v, null, name: Loc.T("providers.keys.name"), placeholder: Loc.T("providers.keys.namePlaceholder"));
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var protocol = PdUi.Choice(protocols, _protocol, v => _protocol = v, name: Loc.T("providers.keys.protocol"));
        protocol.HorizontalAlignment = HorizontalAlignment.Stretch;
        var weight = PdUi.Number(_weight, v => { _weight = v; Enable(); }, 112, Loc.T("providers.keys.weight"), integer: true, minimum: 1);
        var proxy = PdUi.TextBox(_proxy, v => _proxy = v, null, mono: true, name: Loc.T("providers.keys.proxy"), placeholder: "http://127.0.0.1:7890");
        proxy.HorizontalAlignment = HorizontalAlignment.Stretch;
        var notes = PdUi.Area(_notes, v => _notes = v, null, 64);
        InitialFocus = _existing is null ? secret : name;
        Content = PdUi.Stack(16,
            PdUi.Field(Loc.T("providers.keys.name"), name),
            PdUi.Field(Loc.T("providers.apiKey"), secret, _existing is null ? null : Loc.T("providers.keys.replaceHint", ("mask", _existing.KeyMasked ?? "••••"))),
            PdUi.Field(Loc.T("providers.keys.protocol"), protocol, Loc.T("providers.keys.protocolHint")),
            PdUi.Field(Loc.T("providers.keys.weight"), weight, Loc.T("providers.keys.weightHint")),
            PdUi.Field(Loc.T("providers.keys.proxy"), proxy, Loc.T("providers.keys.proxyHint")),
            PdUi.Field(Loc.T("providers.keys.notes"), notes));
        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, click: () => _ = SaveAsync());
        Footer = [PdUi.Cancel(this), _save];
        Enable();
    }

    private void Enable()
    {
        if (_save is not null) _save.IsEnabled = CanSave;
    }

    private async Task SaveAsync()
    {
        var ok = await PdRun.TryAsync(_ctx, () => _existing is null
            ? _keys.AddAsync(new ProviderKeyAdd(
                _secret.Trim(), _name.Trim() is { Length: > 0 } n ? n : null, _protocol.Length > 0 ? _protocol : null,
                _weight is > 1 ? (int)_weight : null, _proxy.Trim() is { Length: > 0 } px ? px : null, _notes.Trim() is { Length: > 0 } no ? no : null))
            : _keys.PatchAsync(_existing.Id, new ProviderKeyPatch(
                _name.Trim() is { Length: > 0 } en ? en : _existing.DisplayName, _secret.Trim() is { Length: > 0 } s ? s : null, null, _protocol,
                (int)(_weight ?? 1), _proxy.Trim(), _notes.Trim())), _save);
        if (ok) Close();
    }
}
