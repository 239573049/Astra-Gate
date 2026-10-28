using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Plugins;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.SmartRouting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.SmartRouting;

/// <summary>
/// The Smart routing settings (web <c>SmartRoutingSettings</c>): a small judge model reads a request and it is sent to the model of the
/// grade that fits. Three steps — the judge, the grades and their models, the rules — under a status group with the switch, which is refused
/// until the numbers are complete and saves the page's unsaved changes along with itself. Shows a skeleton while the settings load and an error
/// when they cannot.
/// </summary>
internal sealed class SmartRoutingFormView : LiveView
{
    private readonly SmartRoutingPageModel _model;
    private readonly Func<DateTimeOffset> _now;
    private readonly StackPanel _host = new() { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
    private SettingsFormBody? _body;
    private string _language = Loc.Language;

    /// <summary>Creates the view over the page model; <paramref name="now"/> is the clock the "recent decisions" times are read against.</summary>
    public SmartRoutingFormView(SmartRoutingPageModel model, Func<DateTimeOffset> now)
    {
        _model = model;
        _now = now;
        Watch(model, nameof(SmartRoutingPageModel.Form));
        Watch(model.Api.Settings, "Status");
        Content = _host;
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        if (_model.Form is { } form)
        {
            if (_body is null || !ReferenceEquals(_body.Form, form))
            {
                _body = new SettingsFormBody(form, _model.Api, _now);
                _host.Children.Clear();
                _host.Children.Add(_body);
            }
            // The settings poll and a language switch both land here; only the language needs the texts read again (rebuilding
            // while the user types would take the focus out of the box).
            else if (_language != Loc.Language) _body.Relanguage();
            _language = Loc.Language;
            return;
        }
        _body = null;
        var q = _model.Api.Settings;
        Control content = q.IsError
            ? PageUi.Alert(AlertTone.Danger, Loc.T("smartRouting.loadFailed"), PluginsPageModel.ErrorText(q.Error))
            : new LoadingState { Margin = new Thickness(0, 32) };
        _host.Children.Clear();
        _host.Children.Add(content);
    }
}

/// <summary>The form of one loaded settings object: the four groups and the Save / Discard bar, kept in step with the <see cref="SmartRoutingFormModel"/>.</summary>
internal sealed class SettingsFormBody : UserControl
{
    private readonly StatusSection _status;
    private readonly JudgeSection _judge;
    private readonly GradesSection _grades;
    private readonly RulesSection _rules;
    private readonly TextBlock _missing;
    private readonly TKCode.Arc.Button _discard;
    private readonly TKCode.Arc.Button _save;
    private readonly Stepper _steps = new() { Margin = new Thickness(4, 0, 4, 20) };
    private readonly Border _barCard;
    private readonly Astra.Desktop.Components.ProviderDetail.StickyBottom _bar;

    public SettingsFormBody(SmartRoutingFormModel form, SmartRoutingApi api, Func<DateTimeOffset> now)
    {
        Form = form;
        _status = new StatusSection(form, now);
        _judge = new JudgeSection(form, new RoutingTesterView(form));
        _grades = new GradesSection(form, api);
        _rules = new RulesSection(form);

        _missing = PageUi.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Warning, wrap: true);
        _missing.FontSize = KitStyle.Type12;
        _missing.VerticalAlignment = VerticalAlignment.Center;
        _discard = PageUi.Button(null, () => form.Discard(), ButtonVariant.Ghost);
        _save = PageUi.Button(null, () => _ = form.SaveAsync(), ButtonVariant.Primary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _discard, _save } };
        Grid.SetColumn(buttons, 1);
        // Save / Discard stay on screen while the (long) form scrolls: the unsaved-changes pill, like the provider settings tab.
        var barRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { _missing, buttons } };
        _barCard = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0.5), Child = barRow };
        _barCard.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        _bar = new Astra.Desktop.Components.ProviderDetail.StickyBottom { Offset = 12, Child = _barCard };

        // The three steps of the page, finished one by one; a finished step jumps back to its group.
        _steps.Label = Loc.T("nav.smartRouting");
        _steps.Steps.Add(new StepperStep(Loc.T("smartRouting.judge.title")) { Id = "judge" });
        _steps.Steps.Add(new StepperStep(Loc.T("smartRouting.grades.title")) { Id = "grades" });
        _steps.Steps.Add(new StepperStep(Loc.T("smartRouting.rules.title")) { Id = "rules" });
        _steps.StepSelected += (_, e) => Target(e.Index)?.BringIntoView();

        Content = new StackPanel { Children = { _steps, _status, _judge, _grades, _rules, _bar } };
        // The body lives exactly as long as its form (both belong to one page), so the subscription is never dropped.
        form.Changed += OnChanged;
        BuildAll();
    }

    /// <summary>The form this body edits.</summary>
    public SmartRoutingFormModel Form { get; }

    /// <summary>The language changed: texts are read again.</summary>
    public void Relanguage() => BuildAll();

    private void BuildAll()
    {
        _status.Build();
        _judge.Build();
        _grades.Build();
        _rules.Build();
        UpdateBar();
    }

    private void OnChanged(DraftArea area)
    {
        if (area.HasFlag(DraftArea.Status)) _status.Build();
        if (area.HasFlag(DraftArea.Judge)) _judge.Build();
        if (area.HasFlag(DraftArea.Grades)) _grades.Build();
        if (area.HasFlag(DraftArea.Rules)) _rules.Build();
        _judge.Update();
        _rules.Update();
        UpdateBar();
    }

    private Control? Target(int step) => step switch { 0 => _judge, 1 => _grades, 2 => _rules, _ => null };

    // The first step still missing something is the current one; all three done completes the flow.
    private int CurrentStep()
    {
        var d = Form.Draft;
        var saved = Form.Saved;
        if (!(saved.Judge.HasApiKey || d.ApiKey.Length > 0)) return 0;
        if (d.Grades.Any(g => g.Model.Length == 0)) return 1;
        return Form.IsComplete ? 3 : 2;
    }

    private void UpdateBar()
    {
        _steps.CurrentIndex = CurrentStep();
        _bar.IsVisible = Form.IsDirty;
        _missing.Text = Loc.T("smartRouting.numberMissing");
        _missing.IsVisible = !Form.IsComplete;
        _discard.Content = Loc.T("smartRouting.discard");
        _discard.IsVisible = Form.IsDirty;
        _save.Content = Loc.T("common.save");
        _save.IsEnabled = Form.IsDirty && Form.IsComplete;
        _save.IsLoading = Form.IsSaving;
    }
}
