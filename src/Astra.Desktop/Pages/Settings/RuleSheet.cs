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
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The alert-rule editor (web <c>RuleSheet</c>). The server's event descriptors drive it: picking an event shows that event's condition
/// fields (a number, a choice, a text), the subjects it can be scoped to (providers, subscription accounts or tokens), the webhooks that
/// receive it, how often to repeat, the cooldown, and whether to tell when it recovers.
/// </summary>
public sealed class RuleSheet
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly RuleDraft _draft;
    private readonly IReadOnlyList<AlertEventDto> _events;
    private readonly IReadOnlyList<WebhookDto> _webhooks;
    private readonly Sheet _sheet;
    private readonly ControlledSelect _event;
    private readonly StackPanel _params = new() { Spacing = 16 };
    private readonly ContentControl _subjects = new();
    private readonly ArcButton _save;
    private bool _saving;
    private readonly Dictionary<string, List<ProviderAccountDto>> _accounts = [];
    private readonly List<(Query<List<ProviderAccountDto>> Query, IDisposable Token)> _watching = [];

    /// <summary>Builds the editor for <paramref name="rule"/> (null = a new one).</summary>
    public RuleSheet(UiHost host, SettingsApi api, AlertRuleDto? rule, IReadOnlyList<AlertEventDto> events, IReadOnlyList<WebhookDto> webhooks)
    {
        _host = host;
        _api = api;
        _events = events;
        _webhooks = webhooks;
        _draft = new RuleDraft(rule, events, webhooks);

        var name = new Input { Label = Loc.T("alerts.rules.name"), Text = _draft.Name, MaxLength = 64 };
        name.TextChanged += (_, _) => { _draft.Name = name.Text ?? ""; Validate(); };

        _event = new ControlledSelect(events.Select(e => (e.Id, AlertLogic.EventName(e.Id).Text)), _draft.EventId, double.NaN)
        {
            Label = Loc.T("alerts.rules.event"),
            Description = _draft.EventId == "" ? null : AlertLogic.EventHint(_draft.EventId).Text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _event.Chose += id => { _draft.ChangeEvent(id, _events); OnEventChanged(); };

        var webhooksField = _webhooks.Count == 0
            ? Ui.Labeled(Loc.T("alerts.rules.webhooks"), Ui.Note(Loc.T("alerts.rules.noWebhooks")))
            : Ui.Labeled(Loc.T("alerts.rules.webhooks"), Chips(Loc.T("alerts.rules.webhooks"), _webhooks.Select(w => new Option(w.Id, w.Name)).ToList(), _draft.WebhookIds));

        // web NumberInput: empty = null ("notify once"); Arc's NumberField has no empty state and would draw (and clamp to) a number.
        var repeat = Optional(Loc.T("alerts.rules.repeat"), Loc.T("alerts.rules.repeatHint"), _draft.Repeat, 1);
        repeat.NumberChanged += v => _draft.Repeat = v is { } r && r > 0 ? (decimal)r : null;
        var cooldown = Optional(Loc.T("alerts.rules.cooldown"), Loc.T("alerts.rules.cooldownHint"), _draft.Cooldown, 0);
        cooldown.NumberChanged += v => _draft.Cooldown = v is { } c ? (decimal)c : null;
        var twin = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        Grid.SetColumn(cooldown, 1);
        twin.Children.Add(repeat);
        twin.Children.Add(cooldown);

        var notify = new ControlledSwitch { IsChecked = _draft.NotifyResolved };
        notify.ToggleRequested += on => { _draft.NotifyResolved = on; notify.SetValue(on); };
        var card = RowBuilder.Group(null, null, RowBuilder.Row(Loc.T("alerts.rules.notifyResolved"), null, notify));
        card.Margin = new Thickness(0);
        if (card.Content is StackPanel inner) inner.Margin = default; // no page-group gap above the sheet's footer

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(name);
        body.Children.Add(_event);
        body.Children.Add(_params);
        body.Children.Add(_subjects);
        body.Children.Add(webhooksField);
        body.Children.Add(twin);
        body.Children.Add(card);

        var cancel = Ui.Button(Loc.T("common.cancel"), () => _sheet!.Close(), size: ControlSize.Medium);
        _save = Ui.Button(Loc.T("common.save"), () => _ = SaveAsync(), ButtonVariant.Primary, size: ControlSize.Medium);
        _sheet = new Sheet
        {
            SheetWidth = 560,
            Title = Loc.T(_draft.IsNew ? "alerts.rules.add" : "alerts.rules.edit"),
            // web: the whole dialog scrolls when it is taller than the window (Arc's surface does the same), no inner scroller
            Content = body,
            Footer = [cancel, _save],
        };
        _sheet.Closed += (_, _) => StopWatching();
        OnEventChanged();
        Validate();
    }

    /// <summary>The sheet (scenes open it directly).</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it; completes when it closes.</summary>
    public Task ShowAsync() => _host.ShowAsync(_sheet);

    private AlertEventDto? Event => _events.FirstOrDefault(e => e.Id == _draft.EventId);

    private void Validate() => _save.IsEnabled = _draft.CanSave && !_saving;

    private static ControlledChips Chips(string label, IReadOnlyList<Option> options, List<string> selected)
    {
        var chips = new ControlledChips(label, options.Select(o => (o.Value, o.Label)), selected);
        chips.Changed += values =>
        {
            selected.Clear();
            selected.AddRange(values);
        };
        return chips;
    }

    private void OnEventChanged()
    {
        var evt = Event;
        _event.Description = evt is null ? null : AlertLogic.EventHint(evt.Id).Text;
        _params.Children.Clear();
        foreach (var p in evt?.Params ?? [])
        {
            var key = p.Name;
            var label = AlertLogic.ParamLabel(key).Text;
            if (p.Kind == "number")
            {
                // web: a number with no value is an empty box (null), not 0.
                var number = Optional(label, null, _draft.Condition.GetValueOrDefault(key) as decimal?, 0);
                number.NumberChanged += v => _draft.Condition[key] = v is { } n ? (decimal)n : null;
                _params.Children.Add(number);
            }
            else if (p.Kind == "choice")
            {
                var choice = new ControlledSelect((p.Choices ?? []).Select(c => (c, AlertLogic.ChoiceLabel(key, c).Text)), _draft.Condition.GetValueOrDefault(key) as string, double.NaN)
                {
                    Label = label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                choice.Chose += v => _draft.Condition[key] = v;
                _params.Children.Add(choice);
            }
            else
            {
                var text = new Input { Label = label, Text = _draft.Condition.GetValueOrDefault(key) as string ?? "", PlaceholderText = AlertLogic.ParamPlaceholder(key).Text };
                text.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                text.TextChanged += (_, _) => _draft.Condition[key] = text.Text ?? "";
                _params.Children.Add(text);
            }
        }
        RenderSubjects();
    }

    private void RenderSubjects()
    {
        StopWatching();
        var evt = Event;
        if (evt is null)
        {
            _subjects.Content = null;
            return;
        }
        var chipsHost = new ContentControl();
        var scope = new ControlledSegmented([("all", Loc.T("alerts.rules.subjectsAll")), ("only", Loc.T("alerts.rules.subjectsOnly"))], _draft.OnlySelected ? "only" : "all");
        Avalonia.Automation.AutomationProperties.SetName(scope, Loc.T("alerts.rules.subjects"));
        scope.HorizontalAlignment = HorizontalAlignment.Stretch; // web: a Segmented in a grid cell spans it
        scope.Chose += v =>
        {
            _draft.OnlySelected = v == "only";
            ShowChips(chipsHost, evt);
        };
        ShowChips(chipsHost, evt);
        _subjects.Content = Ui.Labeled($"{Loc.T("alerts.rules.subjects")} · {AlertLogic.SubjectLabel(evt.SubjectType).Text}", Ui.Stack(8, scope, chipsHost));

        // Subscription accounts are listed per provider; watch them while the editor is open.
        if (evt.SubjectType == "account")
            foreach (var (id, _) in AlertLogic.SubscriptionProviders(_api.Providers.Data ?? []))
            {
                var q = _api.Accounts(id);
                var providerId = id;
                q.PropertyChanged += OnAccountsChanged;
                _watching.Add((q, q.Activate()));
                if (q.Data is { } list) _accounts[providerId] = list;
            }
    }

    private void OnAccountsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var (q, _) in _watching)
            if (q.Data is { } list && q.Key.Parts.Count > 1) _accounts[q.Key.Parts[1]] = list;
        if (Event is { } evt && _subjects.Content is not null) Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowChipsIfOnly(evt));
    }

    private ContentControl? _chipsHostRef;

    private void ShowChipsIfOnly(AlertEventDto evt)
    {
        if (_draft.OnlySelected && _chipsHostRef is not null) ShowChips(_chipsHostRef, evt);
    }

    private void ShowChips(ContentControl host, AlertEventDto evt)
    {
        _chipsHostRef = host;
        if (!_draft.OnlySelected)
        {
            host.Content = null;
            return;
        }
        var options = AlertLogic.SubjectOptions(evt.SubjectType, _api.Providers.Data ?? [], _api.Tokens.Data ?? [], _accounts);
        host.Content = Chips(Loc.T("alerts.rules.subjects"), AlertLogic.WithOrphans(options, _draft.Subjects), _draft.Subjects);
    }

    private void StopWatching()
    {
        foreach (var (q, token) in _watching)
        {
            q.PropertyChanged -= OnAccountsChanged;
            token.Dispose();
        }
        _watching.Clear();
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        _save.IsLoading = true;
        Validate();
        var body = _draft.Body();
        var ok = await _host.TryAsync(async () =>
        {
            if (_draft.IsNew) await _api.CreateRuleAsync(body);
            else await _api.UpdateRuleAsync(_draft.Rule!.Id, body);
        });
        _saving = false;
        _save.IsLoading = false;
        Validate();
        if (!ok) return;
        _sheet.Close();
        _host.Success(Loc.T("common.saved"));
    }

    /// <summary>An optional whole-or-decimal number with a visible label (the web form's <c>Field</c> + <c>NumberInput</c>).</summary>
    private static Astra.Desktop.Components.SmartRouting.NumberInput Optional(string label, string? hint, decimal? value, double min)
    {
        var box = new Astra.Desktop.Components.SmartRouting.NumberInput(label, value is { } v ? (double)v : null, 1, min, double.NaN)
        {
            IsLabelVisible = true,
            Description = hint,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return box;
    }
}
