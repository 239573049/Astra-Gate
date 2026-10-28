using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The provider connection test dialog (web components/ProviderTestDialog.tsx, 1040 px): one model at a time with its configuration on the left
/// and the live result on the right, or a batch (three at a time) with progress, a results table and each result full size. Every test is a real
/// request — it applies the privacy guard, is logged and billed, and is never retried. The state machine is <see cref="ProviderTestSession"/>;
/// closing the dialog cancels whatever is in flight (and is refused while a test runs, like the web's disabled Close).
/// </summary>
public sealed class ProviderTestDialog : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly StackPanel _body = new();
    private readonly ContentControl _mode = new();
    private readonly BatchConfigSheet _configSheet;
    private TestResultPanel? _panel;
    private ModelPicker? _picker;
    private StackPanel? _batchResults;
    private TextBlock? _selectedText;
    private ContentControl? _batchList;
    private IDisposable? _models;
    private bool _pending;

    /// <summary>
    /// Opens the dialog for <paramref name="provider"/> over the main window. The Providers list hands this to its module (the card's test button):
    /// <c>new ProvidersModule(ProviderTestDialog.Opener)</c>.
    /// </summary>
    public static Action<PageContext, ProviderDto> Opener => OpenOver;

    private static void OpenOver(PageContext ctx, ProviderDto provider)
    {
        var owner = OwnerResolver();
        if (owner is null) return;
        var dialog = new ProviderTestDialog(ctx, provider, new ProviderDetailApi(ctx.Queries, ctx.Api, provider.Id));
        dialog.Prepare();
        _ = dialog.ShowAsync(owner);
    }

    /// <summary>Where <see cref="Opener"/> shows the dialog: the desktop lifetime's main window (tests replace it).</summary>
    public static Func<Visual?> OwnerResolver { get; set; } = () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>The dialog for <paramref name="provider"/>.</summary>
    public ProviderTestDialog(PageContext ctx, ProviderDto provider, ProviderDetailApi api)
    {
        _ctx = ctx;
        _api = api;
        SheetWidth = 1040;
        Session = new ProviderTestSession(provider, api.TestStream, api.AfterTestRun);
        Session.Changed += () => Dispatcher.UIThread.Post(OnChanged);
        _configSheet = new BatchConfigSheet(Session);
        Closing += (_, e) =>
        {
            if (Session.Busy) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            Session.Reset();
            _api.Models.PropertyChanged -= OnModels;
            _models?.Dispose();
            _models = null;
        };
        Content = new Panel { Children = { _body, _configSheet } };
    }

    /// <summary>The state machine (tests and scenes drive it).</summary>
    public ProviderTestSession Session { get; }

    /// <summary>The provider under test.</summary>
    public ProviderDto Provider
    {
        get => Session.Provider;
        set => Session.Provider = value;
    }

    /// <summary>Opens (the detail page's header button).</summary>
    public new void Open()
    {
        Prepare();
        base.Open();
    }

    /// <summary>Loads the models and builds the body before showing.</summary>
    internal void Prepare()
    {
        _models ??= _api.Models.Activate();
        _api.Models.PropertyChanged -= OnModels;
        _api.Models.PropertyChanged += OnModels;
        if (_api.Models.Data is { } list) Session.SetModels(list);
        Build();
    }

    private void OnModels(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_api.Models.Data) && _api.Models.Data is { } list) Dispatcher.UIThread.Post(() => Session.SetModels(list));
    }

    /// <summary>Coalesces the session's many change events (one per streamed delta) into one redraw per UI turn.</summary>
    private void OnChanged()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Update();
        }, DispatcherPriority.Background);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        var s = Session;
        Title = Loc.T("providers.testDialog.title", ("name", s.Provider.Name));
        Description = Loc.T("providers.testDialog.subtitle");
        _body.Children.Clear();

        var segmented = Components.Tokens.Pick.Segmented(
            [new SegmentedOption("single", Loc.T("providers.testDialog.modeSingle")), new SegmentedOption("batch", Loc.T("providers.testDialog.modeBatch"))],
            s.BatchMode ? "batch" : "single", v => { s.SetMode(v == "batch"); BuildMode(); Update(); }, Loc.T("providers.testDialog.config"));
        segmented.AutoScrollToSelectedItem = false;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(segmented, Dock.Right);
        top.Children.Add(segmented);
        top.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = BrandIcon.Provider(s.Provider.Name, s.Provider.Icon, 32) });
        _body.Children.Add(top);
        _body.Children.Add(_mode);
        BuildMode();
        Update();
    }

    /// <summary>Builds the mode's static structure (configuration inputs are never rebuilt by a streamed update).</summary>
    private void BuildMode()
    {
        var s = Session;
        _builtMode = ModeKey;
        _builtBatch = s.BatchMode;
        _panel = null;
        _picker = null;
        _batchResults = null;
        _batchList = null;
        if (!s.BatchMode)
        {
            _picker = new ModelPicker { Models = s.Known, Value = s.Model };
            _picker.ValueChanged += v => { s.Model = v; UpdateFooter(); };
            var left = PdUi.Stack(16,
                PdUi.Text(Loc.T("providers.testDialog.config"), 13, ArcDesignTokenKind.Foreground, FontWeight.Medium),
                PdUi.Field(Loc.T("providers.testDialog.model"), _picker));
            foreach (var c in ConfigFields(s)) left.Children.Add(c);
            var note = PdUi.Hint(Loc.T("providers.testDialog.savedCredentials"));
            var column = new DockPanel();
            DockPanel.SetDock(note, Dock.Bottom);
            column.Children.Add(note);
            column.Children.Add(left);
            var leftBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 24, 0), Child = column };
            leftBorder.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
            _panel = new TestResultPanel(s.Provider, retest: () => _ = s.StartSingleAsync(), hint: status => new KeyAlternativeHint(_ctx, _api, s.Provider, status));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*"), ColumnSpacing = 24 };
            Grid.SetColumn(_panel, 1);
            grid.Children.Add(leftBorder);
            grid.Children.Add(_panel);
            _mode.Content = grid;
        }
        else if (s.Detail is { } detail)
        {
            var back = PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.ArrowLeft, name: Loc.T("common.back"), click: () => { s.Detail = null; BuildMode(); Update(); });
            _panel = new TestResultPanel(s.Provider, retest: null, hint: status => new KeyAlternativeHint(_ctx, _api, s.Provider, status));
            _mode.Content = PdUi.Stack(16, PdUi.Inline(12, back, PdUi.Text(detail, 13, ArcDesignTokenKind.Foreground, mono: true)), _panel);
        }
        else
        {
            _selectedText = PdUi.Text("", 12, ArcDesignTokenKind.TextSecondary);
            var search = new SearchField { Placeholder = Loc.T("providers.testDialog.searchModels"), IsLabelVisible = false, Text = s.Search, MinWidth = 220 };
            search.TextChanged += (_, _) => { s.Search = search.Text ?? ""; FillBatchList(); };
            var clear = PdUi.Button(Loc.T("providers.testDialog.clearSelection"), PdUi.Look.Plain, click: () => s.SelectAll(false));
            clear.Tag = "clear";
            var config = PdUi.Button(Loc.T("providers.testDialog.config"), PdUi.Look.Glass, LucideIconKind.FlaskConical, click: _configSheet.Show);
            var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(search, 1);
            Grid.SetColumn(clear, 2);
            Grid.SetColumn(config, 3);
            toolbar.Children.Add(_selectedText);
            toolbar.Children.Add(search);
            toolbar.Children.Add(clear);
            toolbar.Children.Add(config);
            _batchList = new ContentControl();
            _batchResults = new StackPanel { Spacing = 16, Margin = new Thickness(0, 16, 0, 0) };
            _mode.Content = PdUi.Stack(12, toolbar, _batchList, _batchResults);
            FillBatchList();
        }
    }

    /// <summary>The configuration shared by both modes (web <c>ConfigFields</c>).</summary>
    internal static IEnumerable<Control> ConfigFields(ProviderTestSession s)
    {
        var protocol = PdUi.Choice(s.Protocols.Select(p => (p, ProviderProtocols.Name(p))), s.Protocol, v => s.Protocol = v, name: Loc.T("providers.testDialog.protocol"));
        protocol.HorizontalAlignment = HorizontalAlignment.Stretch;
        yield return PdUi.Field(Loc.T("providers.testDialog.protocol"), protocol);
        var stream = new DockPanel();
        var sw = PdUi.Toggle(s.Stream, on => s.Stream = on, Loc.T("providers.testDialog.stream"));
        DockPanel.SetDock(sw, Dock.Right);
        stream.Children.Add(sw);
        stream.Children.Add(PdUi.Text(Loc.T("providers.testDialog.stream"), 12, ArcDesignTokenKind.TextSecondary));
        yield return stream;
        var max = PdUi.Number(s.MaxTokens, v => s.SetMaxTokens(v), null, Loc.T("providers.testDialog.maxTokens"), integer: true, minimum: 1,
            placeholder: ProviderTestSession.DefaultMaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        max.HorizontalAlignment = HorizontalAlignment.Stretch;
        yield return PdUi.Field(Loc.T("providers.testDialog.maxTokens"), max);
        var prompt = PdUi.Area(s.Prompt, v => s.Prompt = v, null, 4 * 20 + 16, placeholder: "Reply with OK.");
        Avalonia.Automation.AutomationProperties.SetName(prompt, Loc.T("providers.testDialog.prompt"));
        yield return PdUi.Field(Loc.T("providers.testDialog.prompt"), prompt, Loc.T("providers.testDialog.promptHint"));
    }

    /// <summary>Redraws what depends on the run state: the result panel, the batch progress and table, the footer.</summary>
    private void Update()
    {
        var s = Session;
        // the session's mode or detail can change from code (tests, scenes) as well as from the controls
        if (_builtBatch != s.BatchMode)
        {
            Build();
            return;
        }
        if (_builtMode != ModeKey) BuildMode();
        if (_picker is not null)
        {
            _picker.Models = s.Known;
            _picker.Value = s.Model;
        }
        if (!s.BatchMode) _panel?.SetRun(s.Single);
        else if (s.Detail is { } d) _panel?.SetRun(s.Batch.GetValueOrDefault(d));
        else
        {
            if (_selectedText is not null) _selectedText.Text = Loc.T("providers.testDialog.selected", ("n", s.Selected.Count), ("total", s.Known.Count));
            FillBatchList();
            FillBatchResults();
        }
        UpdateFooter();
    }

    private string _listSig = "";
    private string? _builtMode;
    private bool _builtBatch;

    private string ModeKey => $"{Session.BatchMode}|{Session.Detail}";

    private void FillBatchList()
    {
        var s = Session;
        if (_batchList is null) return;
        var visible = s.Visible;
        var sig = $"{string.Join('\n', visible)}|{string.Join('\n', s.Selected.Order())}|{s.CanAdd}|{s.Search}|{Loc.Language}";
        if (sig == _listSig && _batchList.Content is not null) return;
        _listSig = sig;
        if (_selectedText is not null) _selectedText.Text = Loc.T("providers.testDialog.selected", ("n", s.Selected.Count), ("total", s.Known.Count));
        Control content;
        if (visible.Count == 0 && !s.CanAdd)
            content = new Border { Padding = new Thickness(16, 24), Child = new TextBlock { Text = Loc.T("providers.testDialog.noModels"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }.With(t => t.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted)) };
        else
        {
            var rows = new List<Control>();
            var all = new Checkbox { IsThreeState = true, IsChecked = s.AllSelected, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(all, Loc.T("providers.testDialog.clearSelection"));
            all.IsCheckedChanged += (_, _) => s.SelectAll(all.IsChecked != false);
            rows.Add(new Border { Padding = new Thickness(16, 8), Child = PdUi.Inline(12, all, PdUi.Text(Loc.T("providers.testDialog.ownFilter"), 11, ArcDesignTokenKind.TextMuted)) });
            if (s.CanAdd)
            {
                var q = s.Search.Trim();
                var add = new Avalonia.Controls.Button { Background = Brushes.Transparent, BorderThickness = default, Padding = default, Content = PdUi.Text(Loc.T("providers.testDialog.addModel", ("id", q)), 12, ArcDesignTokenKind.Accent, mono: true) };
                add.Click += (_, _) => s.AddTyped(q);
                rows.Add(new Border { Padding = new Thickness(16, 10), Child = PdUi.Inline(12, PdUi.Icon(LucideIconKind.Plus, 14, ArcDesignTokenKind.Accent), add) });
            }
            foreach (var id in visible)
            {
                var box = new Checkbox { IsChecked = s.Selected.Contains(id), VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(box, id);
                box.IsCheckedChanged += (_, _) => s.Toggle(id, box.IsChecked == true);
                var text = PdUi.Text(id, 12, ArcDesignTokenKind.Foreground, mono: true);
                ToolTip.SetTip(text, id);
                rows.Add(new Border { Padding = new Thickness(16, 10), Child = PdUi.Inline(12, box, text) });
            }
            content = new ScrollViewer { MaxHeight = 420, Content = PdUi.Divided(rows) };
        }
        _batchList.Content = PdUi.Card(content);
    }

    private void FillBatchResults()
    {
        var s = Session;
        if (_batchResults is null) return;
        _batchResults.Children.Clear();
        if (s.Order.Count == 0) return;
        var busy = s.Busy;
        var line = PdUi.Text(busy
            ? Loc.T("providers.testDialog.progress", ("done", s.Done), ("total", s.Order.Count), ("ok", s.Succeeded), ("failed", s.Failed))
            : Loc.T("providers.testDialog.progressDone", ("total", s.Order.Count), ("ok", s.Succeeded), ("failed", s.Failed)), 12, ArcDesignTokenKind.TextSecondary);
        var fraction = (double)s.Done / s.Order.Count;
        var bar = new Grid { Height = 4, ColumnDefinitions = new ColumnDefinitions($"{Math.Max(fraction, 0.0001).ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{Math.Max(1 - fraction, 0.0001).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
        var fill = new Border { CornerRadius = new CornerRadius(2), IsVisible = fraction > 0 };
        fill.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
        var track = new Border { CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = bar };
        track.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        bar.Children.Add(fill);
        _batchResults.Children.Add(PdUi.Stack(8, line, track));
        _batchResults.Children.Add(TestTable());
    }

    /// <summary>The batch results (web <c>TestTable</c>): model, status, TTFT, total, "view".</summary>
    private Control TestTable()
    {
        var s = Session;
        // Web: an auto-layout <table> whose cells carry px-4 py-2. Its column split for the dialog's model ids comes out at about
        // 243 / 192 / 178 / 155 / 221 of 990 px; one shared star split keeps header and rows aligned (an Auto column sized per row
        // did not: the header's long "API 提供商回复" widened it and shifted every other column).
        const string columns = "243*,192*,178*,155*,221*";
        var rows = new List<Control>();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        string[] titles = [Loc.T("providers.testDialog.model"), Loc.T("providers.testDialog.statusColumn"), Loc.T("providers.testDialog.metrics.ttft"), Loc.T("providers.testDialog.metrics.total"), Loc.T("providers.testDialog.reply")];
        for (var i = 0; i < titles.Length; i++)
        {
            var t = PdUi.Text(titles[i], 11, ArcDesignTokenKind.TextMuted);
            t.Margin = new Thickness(16, 8);
            Grid.SetColumn(t, i);
            head.Children.Add(t);
        }
        rows.Add(head);
        foreach (var id in s.Order)
        {
            var run = s.Batch.GetValueOrDefault(id);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
            Control[] cells =
            [
                PdUi.Text(id, 12, ArcDesignTokenKind.Foreground, mono: true),
                TestResultPanel.Status(run),
                PdUi.Text(ProviderTestSession.FormatMs(run?.TtftMs), 12, ArcDesignTokenKind.Foreground, mono: true),
                PdUi.Text(ProviderTestSession.FormatMs(run?.TotalMs), 12, ArcDesignTokenKind.Foreground, mono: true),
                ViewLink(id, run is not null),
            ];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i].Margin = new Thickness(16, 8);
                cells[i].HorizontalAlignment = HorizontalAlignment.Left;
                cells[i].VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cells[i], i);
                row.Children.Add(cells[i]);
            }
            rows.Add(row);
        }
        return PdUi.Card(PdUi.Divided(rows));
    }

    private Control ViewLink(string id, bool enabled)
    {
        var link = new Avalonia.Controls.Button { Background = Brushes.Transparent, BorderThickness = default, Padding = default, IsEnabled = enabled, Content = PdUi.Text(Loc.T("providers.testDialog.view"), 12, enabled ? ArcDesignTokenKind.Accent : ArcDesignTokenKind.TextMuted) };
        link.Click += (_, _) =>
        {
            Session.Detail = id;
            BuildMode();
            Update();
        };
        return link;
    }

    private void UpdateFooter()
    {
        var s = Session;
        var busy = s.Busy;
        var note = PdUi.Text(Loc.T("providers.testDialog.realRequest"), 11, ArcDesignTokenKind.TextMuted);
        var close = PdUi.Button(Loc.T("common.close"), PdUi.Look.Glass, LucideIconKind.X, click: () => Close());
        close.IsEnabled = !busy;
        ArcButton primary;
        if (!s.BatchMode)
        {
            if (busy) primary = PdUi.Button(Loc.T("providers.testDialog.stop"), PdUi.Look.Primary, LucideIconKind.Square, click: s.StopSingle);
            else
            {
                primary = PdUi.Button(s.Single is not null ? Loc.T("providers.testDialog.retest") : Loc.T("providers.testDialog.start"), PdUi.Look.Primary, LucideIconKind.FlaskConical, click: () => _ = s.StartSingleAsync());
                primary.IsEnabled = s.Model.Trim().Length > 0;
            }
        }
        else if (busy) primary = PdUi.Button(Loc.T("providers.testDialog.stopRest"), PdUi.Look.Primary, LucideIconKind.Square, click: s.StopBatch);
        else
        {
            primary = PdUi.Button(Loc.T("providers.testDialog.testN", ("count", s.Selected.Count)), PdUi.Look.Primary, LucideIconKind.FlaskConical, click: () => _ = s.StartBatchAsync(s.Selected.ToList()));
            primary.IsEnabled = s.Selected.Count > 0;
        }
        var footer = new DockPanel { LastChildFill = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(note, Dock.Left);
        var right = PdUi.Inline(8, close, primary);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(note);
        footer.Children.Add(right);
        Actions = footer;
    }
}

/// <summary>The batch mode's configuration in its own small dialog (web: the 480 px <c>Sheet</c> inside <c>BatchList</c>).</summary>
public sealed class BatchConfigSheet : PdSheet
{
    private readonly ProviderTestSession _session;

    /// <summary>The sheet over <paramref name="session"/>.</summary>
    public BatchConfigSheet(ProviderTestSession session)
    {
        _session = session;
        SheetWidth = 480;
    }

    /// <inheritdoc />
    protected override void Build()
    {
        Title = Loc.T("providers.testDialog.config");
        Description = Loc.T("providers.testDialog.savedCredentials");
        var stack = new StackPanel { Spacing = 16 };
        foreach (var c in ProviderTestDialog.ConfigFields(_session)) stack.Children.Add(c);
        Content = stack;
        Footer = null;
    }
}

/// <summary>
/// One run's result (web <c>TestResultPanel</c>): retest, a metrics help tooltip, the status and HTTP code; the four timings; and the reply card
/// with preview / raw / headers views, a copy button and a fixed 360 px scrolling body. The selected view survives updates.
/// </summary>
public sealed class TestResultPanel : UserControl
{
    private readonly Action? _retest;
    private readonly Func<int?, Control> _hint;
    private readonly WrapPanel _top = new() { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
    private readonly Grid _metrics = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
    private readonly ContentControl _view = new();
    private readonly CopyButton _copy = new() { IsIconOnly = true };
    private string _mode = "preview";
    private TestRun? _run;

    /// <summary>A panel; <paramref name="retest"/> null hides the retest button (batch detail); <paramref name="hint"/> builds the key-alternative hint.</summary>
    public TestResultPanel(ProviderDto provider, Action? retest, Func<int?, Control> hint)
    {
        _retest = retest;
        _hint = hint;
        var segmented = Components.Tokens.Pick.Segmented(
            [new SegmentedOption("preview", Loc.T("providers.testDialog.preview")), new SegmentedOption("raw", Loc.T("providers.testDialog.raw")), new SegmentedOption("headers", Loc.T("providers.testDialog.headers"))],
            _mode, v => { _mode = v; Fill(); }, Loc.T("providers.testDialog.reply"));
        segmented.AutoScrollToSelectedItem = false;
        _copy.CopiedLabel = Loc.T("common.copied");
        var header = new DockPanel { Margin = new Thickness(16, 10) };
        var right = PdUi.Inline(8, segmented, _copy);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        header.Children.Add(PdUi.Text(Loc.T("providers.testDialog.reply"), 12, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        var reply = PdUi.Card(PdUi.Stack(0, header, PdUi.Divider(), new ScrollViewer { Height = 360, Padding = new Thickness(16, 12), Content = _view }));
        // the metrics: a 1px-gap grid over the border color (web gap-px bg-[var(--border)])
        var metricsCard = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1), Child = _metrics };
        metricsCard.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        _metrics.ColumnSpacing = 1;
        Content = PdUi.Stack(16, _top, metricsCard, reply);
        SetRun(null);
    }

    /// <summary>Shows <paramref name="run"/> (null = idle).</summary>
    public void SetRun(TestRun? run)
    {
        _run = run;
        _top.Children.Clear();
        if (_retest is not null && run is not null && run.Status != RunStatus.Running)
            _top.Children.Add(PdUi.Button(Loc.T("providers.testDialog.retest"), PdUi.Look.Glass, LucideIconKind.FlaskConical, small: true, click: _retest));
        var help = PdUi.Icon(LucideIconKind.CircleHelp, 16, ArcDesignTokenKind.TextMuted);
        ToolTip.SetTip(help, Loc.T("providers.testDialog.metricsHelp"));
        _top.Children.Add(help);
        _top.Children.Add(Status(run));
        if (run?.HttpStatus is { } code and not 0) _top.Children.Add(PdUi.Badge("HTTP " + code));

        _metrics.Children.Clear();
        (string Key, double? Value)[] cells =
        [
            ("providers.testDialog.metrics.http", run?.HttpMs), ("providers.testDialog.metrics.firstWait", run?.FirstWait),
            ("providers.testDialog.metrics.ttft", run?.TtftMs), ("providers.testDialog.metrics.total", run?.TotalMs),
        ];
        for (var i = 0; i < cells.Length; i++)
        {
            var value = PdUi.Text(ProviderTestSession.FormatMs(cells[i].Value), 15, ArcDesignTokenKind.Foreground, mono: true);
            value.Margin = new Thickness(0, 4, 0, 0);
            // Web Metric: bg-[var(--surface)], a 78% white that lets the grid's border colour through (a faint grey cell), so the
            // fill is the surface at that opacity over the border-coloured grid rather than an opaque surface.
            var fill = new Border { Opacity = 0.78 };
            fill.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface);
            var cell = new Panel { Children = { fill, new Border { Padding = new Thickness(16, 12), Child = PdUi.Stack(0, PdUi.Text(Loc.T(cells[i].Key), 11, ArcDesignTokenKind.TextMuted), value) } } };
            Grid.SetColumn(cell, i);
            _metrics.Children.Add(cell);
        }
        Fill();
    }

    /// <summary>The status line: dot + text (web <c>StatusBadge</c>).</summary>
    public static Control Status(TestRun? run)
    {
        var tone = run?.Status switch
        {
            RunStatus.Success => Tone.Green,
            RunStatus.Failed => Tone.Red,
            RunStatus.Running => Tone.Orange,
            _ => Tone.Neutral,
        };
        return PdUi.Inline(6, new StatusDot(tone), PdUi.Text(Loc.T(ProviderTestSession.StatusKey(run)), 12, ArcDesignTokenKind.TextSecondary));
    }

    private void Fill()
    {
        var run = _run;
        var headers = run?.Headers ?? [];
        var raw = run?.Raw ?? "";
        _copy.Value = _mode == "raw" ? raw : _mode == "headers" ? string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}")) : run?.Text ?? "";
        _copy.Label = _mode == "headers" ? Loc.T("providers.testDialog.headers") : Loc.T("providers.testDialog.reply");
        Control content;
        if (_mode == "headers")
        {
            if (headers.Count == 0) content = PdUi.Text(Loc.T("providers.testDialog.headersEmpty"), 12, ArcDesignTokenKind.TextMuted);
            else
            {
                var rows = headers.Select(h =>
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(0, 6) };
                    var value = new SelectableTextBlock { Text = h.Value, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                    value.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
                    Grid.SetColumn(value, 1);
                    var name = PdUi.Text(h.Key, 11, ArcDesignTokenKind.TextSecondary, mono: true);
                    name.Margin = new Thickness(0, 0, 16, 0);
                    grid.Children.Add(name);
                    grid.Children.Add(value);
                    return (Control)grid;
                });
                content = PdUi.Divided(rows);
            }
        }
        else if (_mode == "raw")
        {
            if (raw.Length > 0)
            {
                var stack = PdUi.Stack(8, PdUi.Hint(Loc.T("providers.testDialog.rawNote") + (run?.ContentType is { } ct ? " · " + ct : "")), Pre(raw, 11));
                if (run?.RawTruncated == true) stack.Children.Add(PdUi.Text(Loc.T("providers.testDialog.rawTruncated"), 11, ArcDesignTokenKind.Warning, wrap: true));
                content = stack;
            }
            else content = Placeholder(run);
        }
        else if (run?.Error is { Length: > 0 } error)
            content = PdUi.Stack(12, PdUi.Alert(AlertTone.Danger, Loc.T("providers.testDialog.status.failed"), error), _hint(run.HttpStatus));
        else if (run is not null && (run.Text.Length > 0 || run.Reasoning.Length > 0))
        {
            var stack = new StackPanel { Spacing = 12 };
            if (run.Reasoning.Length > 0)
            {
                // the upstream's own words: never restored through the privacy guard's placeholder map
                var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = PdUi.Stack(4, PdUi.Text(Loc.T("providers.testDialog.reasoning"), 11, ArcDesignTokenKind.TextMuted), Pre(run.Reasoning, 11)) };
                box.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                stack.Children.Add(box);
            }
            if (run.Text.Length > 0) stack.Children.Add(Pre(run.Text, 12));
            content = stack;
        }
        else if (run?.Status == RunStatus.Success) content = PdUi.Text(Loc.T("providers.testDialog.noText"), 12, ArcDesignTokenKind.TextMuted);
        else content = Placeholder(run);
        _view.Content = content;
    }

    private static SelectableTextBlock Pre(string text, double size)
    {
        var block = new SelectableTextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.65 };
        block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        return block;
    }

    private static Control Placeholder(TestRun? run)
    {
        if (run?.Status is RunStatus.Running or RunStatus.Queued)
            return new Border { Height = 330, Child = PdUi.Text(Loc.T(ProviderTestSession.StatusKey(run)), 12, ArcDesignTokenKind.TextMuted).With(t => t.HorizontalAlignment = HorizontalAlignment.Center) };
        var detail = PdUi.Text(Loc.T("providers.testDialog.emptyDetail"), 11, ArcDesignTokenKind.TextMuted, wrap: true);
        detail.MaxWidth = 320;
        detail.TextAlignment = TextAlignment.Center;
        var stack = PdUi.Stack(8, PdUi.Icon(LucideIconKind.Activity, 24, ArcDesignTokenKind.TextMuted), PdUi.Text(Loc.T("providers.testDialog.emptyTitle"), 13, ArcDesignTokenKind.Foreground).With(t => t.HorizontalAlignment = HorizontalAlignment.Center), detail);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Height = 330, Child = stack };
    }
}
