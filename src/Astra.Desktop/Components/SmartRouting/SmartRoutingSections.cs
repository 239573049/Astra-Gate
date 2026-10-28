using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Plugins;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.SmartRouting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.SmartRouting;

/// <summary>Pieces the sections of the Smart routing form share.</summary>
internal static class RoutingUi
{
    /// <summary>
    /// A settings row whose hint wraps onto as many lines as it needs (web <c>HintRow</c>): the shared <c>Row</c> cuts its detail off at
    /// one line, which is too little for the sentences on this page (what a rule does and costs).
    /// </summary>
    public static SettingsRow HintRow(string label, object? detail, params Control[] trailing)
    {
        var row = new SettingsRow { Label = label };
        row.Detail = detail is string s ? Hint(s) : detail;
        foreach (var t in trailing) row.Trailing.Add(t);
        return row;
    }

    /// <summary>The wrapping 11 px hint text of a row.</summary>
    public static TextBlock Hint(string text) => PageUi.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);

    /// <summary>Web <c>px-3 py-2</c> around an alert or block inside a group.</summary>
    public static Border Inset(Control child, Thickness? padding = null) => new() { Padding = padding ?? new Thickness(12, 8), Child = child };

    /// <summary>A closed-by-default collapsible section (web <c>Disclosure</c>, Arc <c>Accordion</c>).</summary>
    public static Accordion Disclosure(string title, Control content, bool open, Action<bool> toggled)
    {
        var item = new AccordionItem { Header = title, Content = content, IsExpanded = open };
        item.ExpandedChanged += (_, _) => toggled(item.IsExpanded);
        var accordion = new Accordion { ExpandedIndex = open ? 0 : -1 };
        accordion.Items.Add(item);
        return accordion;
    }

    /// <summary>A wrapped paragraph of an alert.</summary>
    public static TextBlock Paragraph(string text, ArcDesignTokenKind? color = null, double? size = null) => PageUi.AlertText(text, color, size);
}

/// <summary>Base of the form's groups: a <see cref="SettingsGroup"/> that is rebuilt (<see cref="Build"/>) when its shape changes and refreshed (<see cref="Update"/>) while values are typed.</summary>
internal abstract class FormSection : UserControl
{
    /// <summary>The form being edited.</summary>
    protected SmartRoutingFormModel Form { get; }

    /// <summary>The group card.</summary>
    protected SettingsGroup Group { get; } = new();

    /// <summary>Creates the section over the form.</summary>
    protected FormSection(SmartRoutingFormModel form)
    {
        Form = form;
        Content = Group;
    }

    /// <summary>The draft being edited.</summary>
    protected SmartRoutingDraft Draft => Form.Draft;

    /// <summary>Builds the rows from scratch (and the group's title and footer, which follow the language).</summary>
    public abstract void Build();

    /// <summary>Brings values and visibility in step with the draft without rebuilding any input the user may be typing in.</summary>
    public virtual void Update() { }
}

// ---------------------------------------------------------------------------- status and the switch

/// <summary>The status group (web <c>StatusGroup</c>): the switch with its state, the virtual model to ask for, what the last decisions came to, why a save was refused.</summary>
internal sealed class StatusSection : FormSection
{
    private readonly Func<DateTimeOffset> _now;
    private bool _syncing;

    public StatusSection(SmartRoutingFormModel form, Func<DateTimeOffset> now) : base(form) => _now = now;

    public override void Build()
    {
        var saved = Form.Saved;
        Group.Title = Loc.T("smartRouting.status");
        Group.Footer = Loc.T("smartRouting.statusFooter");
        Group.Rows.Clear();

        var state = RoutingStatus.StateOf(saved);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new StatusDot(PageUi.ToKitTone(state.Tone)));
        line.Children.Add(PageUi.Text(state.Text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        var toggle = new Switch { IsChecked = saved.Enabled, IsEnabled = !Form.IsSaving && Form.IsComplete, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T("smartRouting.enable"));
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_syncing || toggle.IsChecked is not { } on) return;
            // The switch shows what is saved: it only moves when the save succeeded (the page then rebuilds this row).
            _syncing = true;
            toggle.IsChecked = !on;
            _syncing = false;
            _ = Form.SaveAsync(on);
        };
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.enable"), line, toggle));

        if (saved.Enabled)
        {
            var model = PageUi.Selectable(saved.VirtualModel, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, mono: true);
            model.FontSize = KitStyle.Type12;
            var copy = PageUi.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () => _ = CopyAsync(saved.VirtualModel));
            Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.use"), Loc.T("smartRouting.useHint"), model, copy));
            AddRecent(saved);
        }
        if (Form.Problems.Count > 0)
        {
            var alert = PageUi.Alert(AlertTone.Danger, Loc.T("smartRouting.problems"), PageUi.Bullets(Form.Problems));
            var inset = RoutingUi.Inset(alert);
            Group.Rows.Add(inset);
            // The Save button is at the bottom of a long page and the reasons are up here: bring them into view when a save fails.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => inset.BringIntoView());
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(text);
            Form.Feedback.Success(Loc.T("common.copied"));
        }
        catch (Exception) { /* a clipboard that refuses is not worth a toast: the web only confirms a success */ }
    }

    // What the last fresh decisions came to. A fallback never fails a request, so without this nobody sees it.
    private void AddRecent(SmartRoutingDto saved)
    {
        var recent = RoutingStatus.RecentOf(saved, _now());
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.recent"), recent.Detail));
        if (recent.Failing is { } failing)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(RoutingUi.Paragraph(failing.Hint));
            if (failing.Last is { } last) body.Children.Add(RoutingUi.Paragraph(last, ArcDesignTokenKind.TextMuted));
            Group.Rows.Add(RoutingUi.Inset(PageUi.Alert(AlertTone.Warning, failing.Title, body)));
        }
        if (recent.Unreachable.Count > 0)
        {
            var items = new StackPanel { Spacing = 6 };
            foreach (var u in recent.Unreachable)
            {
                var item = new StackPanel { Spacing = 0 };
                item.Children.Add(RoutingUi.Paragraph(u.Line));
                if (u.Fix is { } fix) item.Children.Add(RoutingUi.Paragraph(fix, ArcDesignTokenKind.TextMuted));
                items.Children.Add(item);
            }
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(Bulleted(items));
            if (recent.ShowUnreachableHint) body.Children.Add(RoutingUi.Paragraph(Loc.T("smartRouting.unreachable.hint")));
            Group.Rows.Add(RoutingUi.Inset(PageUi.Alert(AlertTone.Warning, Loc.T("smartRouting.unreachable.title"), body)));
        }
    }

    // Web: ul.list-disc.pl-4.space-y-1.5 whose items hold two lines.
    private static Control Bulleted(StackPanel items)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var item in items.Children.ToList())
        {
            items.Children.Remove(item);
            // web list-disc pl-4: the marker hangs in a 16 px gutter, in the alert's inherited text
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
            var dot = PageUi.AlertText("•");
            dot.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(item, 1);
            row.Children.Add(dot);
            row.Children.Add(item);
            list.Children.Add(row);
        }
        return list;
    }
}

// ---------------------------------------------------------------------------- ① the judge

/// <summary>
/// The judge group (web <c>JudgeGroup</c>): dialect, service, key, model, timeout, what it reads and the instructions, and the tester. Built
/// once per dialect; typing only refreshes what depends on it (the key hint, whether the address box shows).
/// </summary>
internal sealed class JudgeSection : FormSection
{
    private readonly RoutingTesterView _tester;
    private bool _instructionsOpen;
    private bool _syncing;
    private SegmentedControl _protocol = null!;
    private TextBlock _protocolHint = null!;
    private Border _openaiDraft = null!;
    private Picker _service = null!;
    private SettingsRow _urlRow = null!;
    private Input _url = null!;
    private TextBlock _keyHint = null!;
    private Input _key = null!;
    private Border _keyDropped = null!;
    private Input _model = null!;
    private NumberInput _timeout = null!;
    private TextBlock _privacyBody = null!;
    private Border _nonEnglish = null!;

    public JudgeSection(SmartRoutingFormModel form, RoutingTesterView tester) : base(form) => _tester = tester;

    public override void Build()
    {
        var saved = Form.Saved;
        Group.Title = Loc.T("smartRouting.judge.title");
        Group.Footer = Loc.T("smartRouting.judge.footer");
        Group.Rows.Clear();

        _protocol = new SegmentedControl
        {
            ItemsSource = new[]
            {
                new SegmentedOption(RoutingNames.Typesafe, Loc.T("smartRouting.judge.protocol.typesafe")),
                new SegmentedOption(RoutingNames.OpenAi, Loc.T("smartRouting.judge.protocol.openai")),
            },
            SelectedValue = Draft.Protocol,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _protocol.ContainBringIntoView();
        Avalonia.Automation.AutomationProperties.SetName(_protocol, Loc.T("smartRouting.judge.protocol"));
        _protocol.SelectionChanged += (_, _) =>
        {
            if (_syncing || _protocol.SelectedValue is not string p || p == Draft.Protocol) return;
            Form.Edit(DraftArea.Judge, d => d.ChangeProtocol(p, Form.Saved.Protocols));
        };
        _protocolHint = RoutingUi.Hint(ProtocolHint());
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.judge.protocol"), _protocolHint, _protocol));

        _openaiDraft = RoutingUi.Inset(PageUi.Alert(AlertTone.Warning, Loc.T("smartRouting.judge.openaiDraft")));
        Group.Rows.Add(_openaiDraft);

        _service = new Picker(Loc.T("smartRouting.judge.service"), ServiceOptions(), JudgePresets.PresetOf(Draft.Protocol, Draft.BaseUrl), null, 208);
        _service.Chosen += id => Form.Edit(DraftArea.None, d => d.ChoosePreset(id));
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.judge.service"), null, _service.Control));

        _url = PageUi.Field(Loc.T("smartRouting.judge.baseUrl"), Draft.BaseUrl, v => Edit(d => d.BaseUrl = v), "https://…", mono: true, width: 288);
        _urlRow = RoutingUi.HintRow(Loc.T("smartRouting.judge.baseUrl"), Loc.T("smartRouting.judge.baseUrlHint"), _url);
        Group.Rows.Add(_urlRow);

        // web: a plain <Input type="password"> (no reveal button), w-72
        _key = new Input { Label = Loc.T("smartRouting.judge.apiKey"), IsLabelVisible = false, Width = 288, Text = Draft.ApiKey, PasswordChar = '•' };
        _key.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _key.TextChanged += (_, _) => Edit(d => d.ApiKey = _key.Text ?? "");
        _keyHint = RoutingUi.Hint("");
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.judge.apiKey"), _keyHint, _key));
        _keyDropped = RoutingUi.Inset(PageUi.Alert(AlertTone.Warning, Loc.T("smartRouting.judge.keyDropped")));
        Group.Rows.Add(_keyDropped);

        _model = PageUi.Field(Loc.T("smartRouting.judge.model"), Draft.Model, v => Edit(d => d.Model = v), null, mono: true, width: 288);
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.judge.model"), Loc.T("smartRouting.judge.modelHint"), _model));

        _timeout = new NumberInput(Loc.T("smartRouting.judge.timeout"), Draft.TimeoutMs, 100, 200);
        _timeout.NumberChanged += v => Form.Edit(DraftArea.None, d => d.TimeoutMs = v is null ? null : (int)Math.Round(v.Value));
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.judge.timeout"), Loc.T("smartRouting.judge.timeoutHint"), _timeout));

        Group.Rows.Add(Reads(saved));
        Group.Rows.Add(_tester);
        Update();
    }

    // The block under the timeout: what the judge reads (info), the non-English warning, the instructions in a disclosure.
    private Control Reads(SmartRoutingDto saved)
    {
        _privacyBody = RoutingUi.Paragraph("");
        var info = PageUi.Alert(AlertTone.Info, Loc.T("smartRouting.privacy.title"), _privacyBody);
        _nonEnglish = new Border { Child = PageUi.Alert(AlertTone.Warning, Loc.T("smartRouting.judge.nonEnglish")) };

        var hint = PageUi.Text(Loc.T("smartRouting.judge.instructionsHint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, wrap: true);
        var area = PageUi.Area(Loc.T("smartRouting.judge.instructions"), Draft.Instructions, v => Edit(d => d.Instructions = v), 4, saved.DefaultInstructions);
        var instructions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { hint, area } };
        var disclosure = RoutingUi.Disclosure(Loc.T("smartRouting.judge.instructions"), instructions, _instructionsOpen, open => _instructionsOpen = open);
        return RoutingUi.Inset(new StackPanel { Spacing = 8, Children = { info, _nonEnglish, disclosure } });
    }

    private void Edit(Action<SmartRoutingDraft> change)
    {
        if (_syncing) return;
        Form.Edit(DraftArea.None, change);
    }

    private string ProtocolHint() => Loc.T($"smartRouting.judge.protocol.{Draft.Protocol}.hint");

    private List<SelectOption> ServiceOptions()
        => [.. JudgePresets.For(Draft.Protocol).Select(p => new SelectOption(p.Id, Loc.T($"smartRouting.preset.{p.Id}"))), new SelectOption(JudgePresets.Custom, Loc.T("smartRouting.preset.custom"))];

    public override void Update()
    {
        if (_protocol is null) return;
        var saved = Form.Saved;
        _syncing = true;
        try
        {
            _protocol.SelectedValue = Draft.Protocol;
            _protocolHint.Text = ProtocolHint();
            _openaiDraft.IsVisible = Draft.Protocol == RoutingNames.OpenAi;
            var preset = JudgePresets.PresetOf(Draft.Protocol, Draft.BaseUrl);
            _service.Value = preset;
            _urlRow.IsVisible = preset == JudgePresets.Custom;
            if (_url.Text != Draft.BaseUrl) _url.Text = Draft.BaseUrl;
            if (_model.Text != Draft.Model) _model.Text = Draft.Model;
            if ((_key.Text ?? "") != Draft.ApiKey) _key.Text = Draft.ApiKey;
            if (_timeout.Number != (Draft.TimeoutMs is { } t ? t : null)) _timeout.Number = Draft.TimeoutMs;

            // The key is dropped when the address changes to another host (the server does the same), unless a new one is typed.
            var dropped = saved.Judge.HasApiKey && Draft.ApiKey.Length == 0
                && JudgePresets.OriginOf(Draft.BaseUrl, Draft.Protocol, saved.Protocols) != JudgePresets.OriginOf(saved.Judge.BaseUrl ?? "", saved.Judge.Protocol, saved.Protocols);
            _keyHint.Text = dropped
                ? Loc.T("smartRouting.judge.keyReplace")
                : saved.Judge.HasApiKey ? Loc.T("smartRouting.judge.keySaved", ("mask", saved.Judge.ApiKeyMasked ?? "")) : Loc.T("smartRouting.judge.keyMissing");
            _key.Placeholder = saved.Judge.HasApiKey && !dropped ? "••••••••" : "sk-…";
            _keyDropped.IsVisible = dropped;
            _model.Placeholder = saved.Protocols.FirstOrDefault(p => p.Id == Draft.Protocol)?.DefaultModel is { Length: > 0 } m ? m : "model";
            _privacyBody.Text = Loc.T("smartRouting.privacy.body", ("turns", Draft.ContextTurns ?? 0), ("chars", Draft.MaxChars ?? 0));
            _nonEnglish.IsVisible = Draft.Protocol == RoutingNames.Typesafe;
        }
        finally { _syncing = false; }
    }
}

// ---------------------------------------------------------------------------- ② grades and their models

/// <summary>The grades group (web <c>GradesGroup</c>): each grade's name, description and model, with order, removal and "add a grade".</summary>
internal sealed class GradesSection : FormSection
{
    private readonly SmartRoutingApi _api;

    public GradesSection(SmartRoutingFormModel form, SmartRoutingApi api) : base(form)
    {
        _api = api;
        api.Models.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Data" && TopLevel.GetTopLevel(this) is not null) Build();
        };
    }

    public override void Build()
    {
        Group.Title = Loc.T("smartRouting.grades.title");
        Group.Footer = Loc.T("smartRouting.grades.footer");
        Group.Rows.Clear();

        var served = _api.Models.Data ?? [];
        var choices = RoutingModels.Choices(served);
        var grades = Draft.Grades;
        for (var i = 0; i < grades.Count; i++) Group.Rows.Add(GradeRow(i, grades[i], choices));
        if (_api.Models.Data is { Count: 0 })
        {
            var warn = PageUi.Text(Loc.T("smartRouting.grade.noModels"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Warning, wrap: true);
            warn.FontSize = KitStyle.Type12;
            Group.Rows.Add(RoutingUi.Inset(warn, new Thickness(16, 8)));
        }
        var add = PageUi.Button(Loc.T("smartRouting.grade.add"), () => Form.Edit(DraftArea.Grades, d => d.AddGrade()), size: ControlSize.Small, icon: LucideIconKind.Plus, enabled: grades.Count < SmartRoutingDraft.MaxGrades);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        Group.Rows.Add(RoutingUi.Inset(add, new Thickness(16, 8)));
    }

    private Control GradeRow(int i, GradeDraft g, IReadOnlyList<ModelChoice> choices)
    {
        var grades = Draft.Grades;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var index = PageUi.Text((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, mono: true);
        index.TextAlignment = TextAlignment.Center;
        head.Children.Add(index);
        var name = PageUi.Field(Loc.T("smartRouting.grade.name"), g.Name, v => Form.Edit(DraftArea.None, d => d.Grades[i].Name = v), g.Id);
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var col = 2;
        if (Draft.FallbackGrade == g.Id)
        {
            var badge = KitBadge.Create(Loc.T("smartRouting.grade.fallbackBadge"), Tone.Accent);
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, col);
            head.Children.Add(badge);
        }
        col++;
        var up = PageUi.IconButton(LucideIconKind.ArrowUp, Loc.T("smartRouting.grade.up"), () => Form.Edit(DraftArea.Grades, d => d.MoveGrade(i, -1)), enabled: i > 0);
        var down = PageUi.IconButton(LucideIconKind.ArrowDown, Loc.T("smartRouting.grade.down"), () => Form.Edit(DraftArea.Grades, d => d.MoveGrade(i, 1)), enabled: i < grades.Count - 1);
        var remove = PageUi.IconButton(LucideIconKind.Trash2, Loc.T("smartRouting.grade.remove"), () => Form.Edit(DraftArea.Grades, d => d.RemoveGrade(i)), enabled: grades.Count > SmartRoutingDraft.MinGrades);
        Grid.SetColumn(up, col++);
        Grid.SetColumn(down, col++);
        Grid.SetColumn(remove, col);
        head.Children.Add(up);
        head.Children.Add(down);
        head.Children.Add(remove);

        var description = PageUi.Area(Loc.T("smartRouting.grade.description"), g.Description, v => Form.Edit(DraftArea.None, d => d.Grades[i].Description = v), 2, Loc.T("smartRouting.grade.descriptionPlaceholder"));

        var options = RoutingModels.OptionsFor(choices, g).Select(c => new SelectOption(c.Value, c.Label) { Keywords = c.Detail, Meta = c.Detail }).ToList();
        var picker = new Picker(Loc.T("smartRouting.grade.model"), options, g.Model.Length > 0 ? JudgePresets.ChoiceOf(g.ProviderId, g.Model) : null, Loc.T("smartRouting.grade.pickModel"));
        picker.Chosen += value =>
        {
            Form.Edit(DraftArea.None, d => d.ChooseModel(i, value));
            // web options(g) is recomputed every render: once a real model is picked, the stale "unavailable" entry for the old one goes.
            // Posted: the list must not be swapped inside the picker's own SelectionChanged.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (i >= Draft.Grades.Count) return;
                var now = Draft.Grades[i];
                picker.SetOptions(RoutingModels.OptionsFor(choices, now).Select(c => new SelectOption(c.Value, c.Label) { Keywords = c.Detail, Meta = c.Detail }).ToList(),
                    now.Model.Length > 0 ? JudgePresets.ChoiceOf(now.ProviderId, now.Model) : null);
            });
        };

        return new StackPanel { Spacing = 8, Margin = new Thickness(16, 12), Children = { head, description, picker.Control } };
    }
}

// ---------------------------------------------------------------------------- ③ the rules

/// <summary>The rules group (web <c>RulesGroup</c>): when to judge, for how long, the fallback grade, the confidence and what the judge reads, and the virtual model's name.</summary>
internal sealed class RulesSection : FormSection
{
    private bool _advancedOpen;
    private bool _syncing;
    private SegmentedControl _mode = null!;
    private TextBlock _modeHint = null!;
    private SettingsRow _ttlRow = null!;
    private NumberInput _ttl = null!;
    private Picker _fallback = null!;
    private NumberInput _confidence = null!;
    private NumberInput _turns = null!;
    private NumberInput _chars = null!;
    private Input _virtual = null!;

    public RulesSection(SmartRoutingFormModel form) : base(form) { }

    public override void Build()
    {
        Group.Title = Loc.T("smartRouting.rules.title");
        Group.Footer = Loc.T("smartRouting.rules.footer");
        Group.Rows.Clear();

        _mode = new SegmentedControl
        {
            ItemsSource = new[]
            {
                new SegmentedOption(RoutingNames.Session, Loc.T("smartRouting.rules.mode.session")),
                new SegmentedOption(RoutingNames.Turn, Loc.T("smartRouting.rules.mode.turn")),
            },
            SelectedValue = Draft.Mode,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _mode.ContainBringIntoView();
        Avalonia.Automation.AutomationProperties.SetName(_mode, Loc.T("smartRouting.rules.mode"));
        _mode.SelectionChanged += (_, _) =>
        {
            if (_syncing || _mode.SelectedValue is not string m || m == Draft.Mode) return;
            Form.Edit(DraftArea.None, d => d.Mode = m);
        };
        _modeHint = RoutingUi.Hint(Loc.T($"smartRouting.rules.mode.{Draft.Mode}.hint"));
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.rules.mode"), _modeHint, _mode));

        _ttl = new NumberInput(Loc.T("smartRouting.rules.ttl"), Draft.SessionTtlMinutes, 1, 1);
        _ttl.NumberChanged += v => Form.Edit(DraftArea.None, d => d.SessionTtlMinutes = v is null ? null : (int)Math.Round(v.Value));
        _ttlRow = RoutingUi.HintRow(Loc.T("smartRouting.rules.ttl"), Loc.T("smartRouting.rules.ttlHint"), _ttl);
        Group.Rows.Add(_ttlRow);

        _fallback = new Picker(Loc.T("smartRouting.rules.fallback"), GradeOptions(), Draft.FallbackGrade, null, 176);
        _fallback.Chosen += id => Form.Edit(DraftArea.Grades, d => d.FallbackGrade = id);
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.rules.fallback"), Loc.T("smartRouting.rules.fallbackHint"), _fallback.Control));

        _confidence = new NumberInput(Loc.T("smartRouting.rules.minConfidence"), Draft.MinConfidence, 0.05, 0);
        _confidence.NumberChanged += v => Form.Edit(DraftArea.None, d => d.MinConfidence = v);
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.rules.minConfidence"), Loc.T("smartRouting.rules.minConfidenceHint"), _confidence));

        _turns = new NumberInput(Loc.T("smartRouting.rules.contextTurns"), Draft.ContextTurns, 1, 0);
        _turns.NumberChanged += v => Form.Edit(DraftArea.None, d => d.ContextTurns = v is null ? null : (int)Math.Round(v.Value));
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.rules.contextTurns"), Loc.T("smartRouting.rules.contextTurnsHint"), _turns));

        _chars = new NumberInput(Loc.T("smartRouting.rules.maxChars"), Draft.MaxChars, 500, 500);
        _chars.NumberChanged += v => Form.Edit(DraftArea.None, d => d.MaxChars = v is null ? null : (int)Math.Round(v.Value));
        Group.Rows.Add(RoutingUi.HintRow(Loc.T("smartRouting.rules.maxChars"), Loc.T("smartRouting.rules.maxCharsHint"), _chars));

        _virtual = PageUi.Field(Loc.T("smartRouting.rules.virtualModel"), Draft.VirtualModel, v => { if (!_syncing) Form.Edit(DraftArea.None, d => d.VirtualModel = v); }, mono: true, width: 224);
        var advanced = RoutingUi.HintRow(Loc.T("smartRouting.rules.virtualModel"), Loc.T("smartRouting.rules.virtualModelHint"), _virtual);
        Group.Rows.Add(RoutingUi.Inset(RoutingUi.Disclosure(Loc.T("smartRouting.rules.advanced"), advanced, _advancedOpen, open => _advancedOpen = open)));
        Update();
    }

    private List<SelectOption> GradeOptions() => [.. Draft.Grades.Select(g => new SelectOption(g.Id, g.DisplayName))];

    public override void Update()
    {
        if (_mode is null) return;
        _syncing = true;
        try
        {
            _mode.SelectedValue = Draft.Mode;
            _modeHint.Text = Loc.T($"smartRouting.rules.mode.{Draft.Mode}.hint");
            _ttlRow.IsVisible = Draft.Mode == RoutingNames.Session;
            _fallback.SetOptions(GradeOptions(), Draft.FallbackGrade);
            if (_ttl.Number != (Draft.SessionTtlMinutes is { } a ? a : null)) _ttl.Number = Draft.SessionTtlMinutes;
            if (_confidence.Number != Draft.MinConfidence) _confidence.Number = Draft.MinConfidence;
            if (_turns.Number != (Draft.ContextTurns is { } b ? b : null)) _turns.Number = Draft.ContextTurns;
            if (_chars.Number != (Draft.MaxChars is { } c ? c : null)) _chars.Number = Draft.MaxChars;
            if (_virtual.Text != Draft.VirtualModel) _virtual.Text = Draft.VirtualModel;
        }
        finally { _syncing = false; }
    }
}
