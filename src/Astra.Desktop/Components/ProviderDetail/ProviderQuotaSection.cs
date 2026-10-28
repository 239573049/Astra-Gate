using System.ComponentModel;
using System.Globalization;
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
/// Balance / quota query of an API-key provider (web components/ProviderQuotaSection.tsx; cc-switch's "usage query"): pick a built-in template
/// or write a custom request + declarative extractor, test it unsaved, query now, and see the stored snapshot. Subscription providers show their
/// quota per account instead, so the page renders this only on the API-key tab. The form is a working copy of the saved configuration
/// (<see cref="QuotaForm"/>); a new provider object from the server (save, fetch) resets it like the web's <c>useEffect([base])</c>.
/// </summary>
public sealed class ProviderQuotaSection : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private ProviderDto _provider;
    private QuotaForm _base;
    private QuotaForm _form;
    private readonly SettingsGroup _group = new();
    private readonly QuotaTestSheet _testSheet;
    private ArcButton? _save;
    private ArcButton? _test;
    private ArcButton? _fetch;
    private IDisposable? _watch;

    /// <summary>Builds the section for <paramref name="provider"/>.</summary>
    public ProviderQuotaSection(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        _base = _form = QuotaForm.From(provider.QuotaConfig);
        _testSheet = new QuotaTestSheet(ctx.Clock);
        Content = new Panel { Children = { _group, _testSheet } };
        Build();
    }

    /// <summary>The working copy (tests).</summary>
    public QuotaForm Form => _form;

    /// <summary>The test result sheet.</summary>
    public QuotaTestSheet TestSheet => _testSheet;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watch = _api.QuotaTemplates.Activate();
        _api.QuotaTemplates.PropertyChanged += OnTemplates;
        _api.Provider.PropertyChanged += OnProvider;
        Build();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.QuotaTemplates.PropertyChanged -= OnTemplates;
        _api.Provider.PropertyChanged -= OnProvider;
        _watch?.Dispose();
        _watch = null;
    }

    private void OnTemplates(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_api.QuotaTemplates.Data)) Build();
    }

    private void OnProvider(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_api.Provider.Data) || _api.Provider.Data is not { } p || ReferenceEquals(p, _provider)) return;
        _provider = p;
        var next = QuotaForm.From(p.QuotaConfig);
        // a fetch only changes the snapshot: keep the person's edits then
        if (!next.Same(_base)) _base = _form = next;
        Build();
    }

    /// <summary>Applies an edit; <paramref name="rebuild"/> redraws the rows (a template switch changes which rows exist).</summary>
    internal void Edit(Func<QuotaForm, QuotaForm> change, bool rebuild = false)
    {
        _form = change(_form);
        if (rebuild) Build();
        else UpdateButtons();
    }

    private void UpdateButtons()
    {
        var dirty = !_form.Same(_base);
        if (_save is not null) _save.IsEnabled = dirty;
        if (_fetch is not null) _fetch.IsEnabled = !dirty;
    }

    private void Build()
    {
        var all = _api.QuotaTemplates.Data ?? [];
        var config = _provider.QuotaConfig;
        _group.Title = Loc.T("providers.quota.title");
        _group.Footer = Loc.T("providers.quota.footer");
        _group.Rows.Clear();
        _group.Rows.Add(PdUi.Row(Loc.T("providers.quota.enable"), Loc.T("providers.quota.enableHint"),
            PdUi.Toggle(_form.Enabled, on => Edit(f => f with { Enabled = on }), Loc.T("providers.quota.enable"))));
        _group.Rows.Add(PdUi.Row(Loc.T("providers.quota.template"), null,
            PdUi.Choice(QuotaForm.TemplateOptions(all, config.SuggestedTemplate), _form.Template, v => Edit(f => f.WithTemplate(v, all, config.SuggestedTemplate), rebuild: true), 256, Loc.T("providers.quota.template"))));
        var ps = _form.ParamsFor(all, config.SuggestedTemplate);
        foreach (var p in ps)
        {
            var label = Loc.T("providers.quota.param." + p.Name);
            if (p.Secret)
            {
                var stored = config.Secrets.Contains(p.Name);
                _group.Rows.Add(PdUi.Row(label, stored ? Loc.T("providers.quota.secretStored") : null,
                    PdUi.Secret(_form.Secrets.GetValueOrDefault(p.Name) ?? "", v => Edit(f => f.WithSecret(p.Name, v)), 256, label, stored ? "••••••••" : "")));
            }
            else
                _group.Rows.Add(PdUi.Row(label, null, PdUi.TextBox(_form.Params.GetValueOrDefault(p.Name) ?? "", v => Edit(f => f.WithParam(p.Name, v)), 256, mono: true, name: label)));
        }
        if (ps.Count > 0)
        {
            var hint = PdUi.Hint(Loc.T("providers.quota.paramsHint"));
            _group.Rows.Add(new Border { Padding = new Thickness(16, 0, 16, 8), Child = hint });
        }
        if (_form.IsCustom) _group.Rows.Add(CustomEditor());
        _group.Rows.Add(PdUi.Row(Loc.T("providers.quota.interval"), Loc.T("providers.quota.intervalHint", ("value", QuotaForm.IntervalNow(config))),
            PdUi.Number(_form.IntervalMinutes, v => Edit(f => f with { IntervalMinutes = v }), 112, Loc.T("providers.quota.interval"), integer: true, minimum: 0)));
        _group.Rows.Add(PdUi.Row(Loc.T("providers.quota.timeout"), null,
            PdUi.Number(_form.TimeoutSec, v => Edit(f => f with { TimeoutSec = v }), 112, Loc.T("providers.quota.timeout"), integer: true, minimum: 1)));
        _group.Rows.Add(PdUi.Row(Loc.T("providers.quota.baseUrl"), Loc.T("providers.quota.baseUrlHint"),
            PdUi.TextBox(_form.BaseUrl, v => Edit(f => f with { BaseUrl = v }), 256, mono: true, name: Loc.T("providers.quota.baseUrl"), placeholder: _provider.Endpoints.FirstOrDefault()?.BaseUrl)));

        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, small: true, click: () => _ = SaveAsync());
        _test = PdUi.Button(Loc.T("providers.quota.test"), PdUi.Look.Glass, LucideIconKind.FlaskConical, small: true, click: () => _ = TestAsync());
        ToolTip.SetTip(_test, Loc.T("providers.quota.testDesc"));
        _fetch = PdUi.Button(Loc.T("providers.quota.fetch"), PdUi.Look.Glass, LucideIconKind.Gauge, small: true, click: () => _ = FetchAsync());
        _group.Rows.Add(new Border { Padding = new Thickness(16, 12), Child = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { _save, _test, _fetch } } });
        _group.Rows.Add(new Border { Padding = new Thickness(16, 0, 16, 16), Child = new QuotaSnapshotView(_provider.Quota, _provider.QuotaCheckedAtUtc, _ctx.Clock.Now) });
        UpdateButtons();
    }

    private Control CustomEditor()
    {
        var method = PdUi.Choice([("GET", "GET"), ("POST", "POST")], _form.Method, v => Edit(f => f with { Method = v }), 112, Loc.T("providers.quota.method"));
        var url = PdUi.TextBox(_form.Url, v => Edit(f => f with { Url = v }), mono: true, name: Loc.T("providers.quota.url"));
        url.MinWidth = 240;
        var auth = PdUi.Choice([("provider", Loc.T("providers.quota.auth.provider")), ("none", Loc.T("providers.quota.auth.none"))], _form.Auth, v => Edit(f => f with { Auth = v }), 208, Loc.T("providers.quota.auth"));
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(url, 1);
        Grid.SetColumn(auth, 2);
        line.Children.Add(method);
        line.Children.Add(url);
        line.Children.Add(auth);
        return PdUi.Pad(PdUi.Stack(12,
            line,
            PdUi.Hint(Loc.T("providers.quota.urlHint")),
            PdUi.Area(_form.Headers, v => Edit(f => f with { Headers = v }), Loc.T("providers.quota.headers"), 3 * 20 + 16, mono: true),
            PdUi.Area(_form.Extract, v => Edit(f => f with { Extract = v }), Loc.T("providers.quota.extract"), 8 * 20 + 16, mono: true),
            PdUi.Hint(Loc.T("providers.quota.extractHint"))));
    }

    private async Task SaveAsync()
    {
        if (_form.ToInput(out var error) is not { } body)
        {
            _ctx.Feedback.Error(error!);
            return;
        }
        var turnedOn = _form.Enabled && !_provider.QuotaConfig.Enabled;
        var ok = await PdRun.TryAsync(_ctx, () => _api.SaveQuotaConfigAsync(body), _save);
        if (!ok) return;
        _ctx.Feedback.Success(Loc.T("common.saved"));
        // freshly enabled: show a balance right away instead of waiting for the background refresh
        if (turnedOn) await FetchAsync();
    }

    private async Task TestAsync()
    {
        if (_form.ToInput(out var error) is not { } body)
        {
            _ctx.Feedback.Error(error!);
            return;
        }
        ProviderQuotaTestDto? result = null;
        if (await PdRun.TryAsync(_ctx, async () => result = await _api.TestQuotaAsync(body), _test) && result is not null) _testSheet.ShowResult(result);
    }

    private Task FetchAsync() => PdRun.TryAsync(_ctx, () => _api.FetchQuotaAsync(), _fetch);
}

/// <summary>The unsaved test's result (web: the <c>Sheet</c> at the end of ProviderQuotaSection, 560 px).</summary>
public sealed class QuotaTestSheet : PdSheet
{
    private readonly Astra.Desktop.Core.Shell.IClock _clock;
    private ProviderQuotaTestDto? _result;

    /// <summary>The sheet; times are relative to <paramref name="clock"/>.</summary>
    public QuotaTestSheet(Astra.Desktop.Core.Shell.IClock clock)
    {
        _clock = clock;
        SheetWidth = 560;
    }

    /// <summary>Shows <paramref name="result"/>.</summary>
    public void ShowResult(ProviderQuotaTestDto result)
    {
        _result = result;
        Show();
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.quota.test");
        Description = Loc.T("providers.quota.testDesc");
        if (_result is not { } r) return;
        var stack = new StackPanel { Spacing = 12 };
        object? body = null;
        if (!r.Ok)
        {
            var lines = PdUi.Stack(4, PdUi.Text(QuotaText.FailureText((r.ErrorCode, r.Error ?? "")), 13, ArcDesignTokenKind.Foreground, wrap: true));
            if (!string.IsNullOrEmpty(r.Error) && !string.IsNullOrEmpty(r.ErrorCode))
            {
                var detail = PdUi.Text(r.Error, 11, ArcDesignTokenKind.TextSecondary, wrap: true);
                detail.Margin = new Thickness(0, 4, 0, 0);
                lines.Children.Add(detail);
            }
            body = lines;
        }
        stack.Children.Add(PdUi.Alert(r.Ok ? AlertTone.Success : AlertTone.Danger, r.Ok ? Loc.T("providers.quota.testOk") : Loc.T("providers.quota.testFailed"), body));
        if (!string.IsNullOrEmpty(r.Url)) stack.Children.Add(new KeyValueRow(Loc.T("providers.quota.requestUrl"), r.Url, mono: true));
        if (r.HttpStatus is { } s) stack.Children.Add(new KeyValueRow(Loc.T("providers.quota.httpStatus"), s.ToString(CultureInfo.InvariantCulture)));
        if (r.Snapshot is not null)
            stack.Children.Add(PdUi.Stack(4, PdUi.Text(Loc.T("providers.quota.extracted"), 12, ArcDesignTokenKind.TextSecondary),
                new QuotaSnapshotView(r.Snapshot, null, _clock.Now)));
        if (r.Raw is not null)
            stack.Children.Add(PdUi.Stack(4, PdUi.Text(Loc.T("providers.quota.rawResponse"), 12, ArcDesignTokenKind.TextSecondary),
                new CodeBlock { Code = PricingDocument.PrettyJson(r.Raw), Language = "json", MaxLines = 15, CopyLabel = Loc.T("common.copy") }));
        Content = stack;
        Footer = null;
    }
}

/// <summary>
/// A snapshot's plans (one line each), label, age, and a newer failure shown above the last good result (web <c>QuotaSnapshotView</c>).
/// </summary>
public sealed class QuotaSnapshotView : UserControl
{
    /// <summary>Shows the snapshot JSON <paramref name="raw"/>; <paramref name="checkedAt"/> tells "never queried" from "queried, nothing stored".</summary>
    public QuotaSnapshotView(System.Text.Json.Nodes.JsonObject? raw, DateTimeOffset? checkedAt, DateTimeOffset now)
    {
        var snapshot = QuotaSnapshot.Parse(raw);
        var planLabel = snapshot?.PlanLabel;
        var invalidMessage = snapshot?.InvalidMessage;
        if (snapshot is null && checkedAt is null)
        {
            Content = PdUi.Text(Loc.T("providers.quota.never"), 12, ArcDesignTokenKind.TextMuted);
            return;
        }
        var stack = new StackPanel { Spacing = 8 };
        var plans = snapshot?.Plans ?? [];
        if (QuotaText.Failure(snapshot) is { } f)
        {
            var body = PdUi.Stack(4, PdUi.Text(f.Message, 11, ArcDesignTokenKind.Foreground, wrap: true));
            if (plans.Count > 0) body.Children.Add(PdUi.Text(Loc.T("providers.quota.lastGood"), 11, ArcDesignTokenKind.Foreground, wrap: true));
            stack.Children.Add(PdUi.Alert(AlertTone.Warning, Loc.T("providers.quota.failed", ("error", QuotaText.FailureText(f))), body));
        }
        if (snapshot?.IsValid == false) stack.Children.Add(PdUi.Alert(AlertTone.Warning, Loc.T("providers.quota.invalid"), invalidMessage));
        if (plans.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var p in plans)
            {
                var line = new WrapPanel { ItemSpacing = 8, LineSpacing = 2 };
                if (!string.IsNullOrEmpty(p.Name)) line.Children.Add(PdUi.Badge(p.Name));
                var phrase = PdUi.Text(QuotaText.PlanPhrase(p) ?? "—", 13, ArcDesignTokenKind.Foreground);
                phrase.FontFeatures = FontFeatureCollection.Parse("tnum");
                line.Children.Add(phrase);
                if (QuotaTimes.PlanResets(p, now) is { } resets) line.Children.Add(PdUi.Text(resets, 11, ArcDesignTokenKind.TextMuted));
                if (!string.IsNullOrEmpty(p.Extra)) line.Children.Add(PdUi.Text(p.Extra, 11, ArcDesignTokenKind.TextMuted));
                list.Children.Add(line);
            }
            stack.Children.Add(list);
        }
        var meta = new WrapPanel { ItemSpacing = 8, LineSpacing = 2 };
        if (planLabel is { Length: > 0 } label) meta.Children.Add(PdUi.Badge(label, Tone.Accent));
        if (snapshot?.FetchedAt is { } at)
            meta.Children.Add(PdUi.Text(Loc.T("providers.quota.fetchedAt", ("time", QuotaTimes.UpdatedAgo(at, now, Loc.Language, Loc.T("tray.justNow")) ?? "")), 11, ArcDesignTokenKind.TextMuted));
        if (meta.Children.Count > 0) stack.Children.Add(meta);
        Content = stack;
    }
}
