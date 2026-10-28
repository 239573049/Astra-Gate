using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;
using Dialog = TKCode.Arc.Dialog;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// The Plugins page's four sheets (web <c>AddPluginSheet</c>, <c>ImportMagpieSheet</c>, <c>JobSheet</c>, <c>OptionsSheet</c>): modal
/// dialogs declared once, opened and closed by the page model's flags (<see cref="PluginsPageModel.Adding"/>, <c>Importing</c>,
/// <c>JobId</c>, <c>Editing</c>). Each is filled when it opens; the ones that show a query (the magpie preview, a job) follow it while open.
/// </summary>
internal sealed class PluginSheets : Panel
{
    private readonly PluginsPageModel _model;
    private readonly Sheet _add = new() { SheetWidth = 460 };
    private readonly Sheet _import = new() { SheetWidth = 560 };
    private readonly Sheet _job = new() { SheetWidth = 620 };
    private readonly Sheet _options = new() { SheetWidth = 560 };
    private IDisposable? _importWatch;
    private IDisposable? _jobWatch;
    private bool _syncing;
    private bool _signIns;
    private bool _busy;

    /// <summary>Creates the sheets over the page model; add the panel to the page's view.</summary>
    public PluginSheets(PluginsPageModel model)
    {
        _model = model;
        Children.Add(_add);
        Children.Add(_import);
        Children.Add(_job);
        Children.Add(_options);
        model.PropertyChanged += OnModelChanged;
        _add.Closed += (_, _) => { if (!_syncing) model.Adding = false; };
        _import.Closed += (_, _) => { if (!_syncing) model.Importing = false; };
        _job.Closed += (_, _) => { if (!_syncing) model.JobId = null; };
        _options.Closed += (_, _) => { if (!_syncing) model.Editing = null; };
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _importWatch?.Dispose();
        _jobWatch?.Dispose();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PluginsPageModel.Adding): Sync(_add, _model.Adding, BuildAdd); break;
            case nameof(PluginsPageModel.Importing): Sync(_import, _model.Importing, BuildImport); break;
            case nameof(PluginsPageModel.JobId): Sync(_job, _model.JobId is not null, BuildJob); break;
            case nameof(PluginsPageModel.Editing): Sync(_options, _model.Editing is not null, BuildOptions); break;
        }
    }

    private void Sync(Sheet sheet, bool open, Action build)
    {
        _syncing = true;
        try
        {
            if (open && !sheet.IsOpen)
            {
                build();
                sheet.IsOpen = true;
            }
            else if (open && sheet == _job) build(); // another job replaced the one shown
            else if (!open && sheet.IsOpen) sheet.IsOpen = false;
        }
        finally { _syncing = false; }
    }

    // ---- install a plugin by hand ----

    private void BuildAdd()
    {
        var spec = new Input { Label = Loc.T("plugins.spec"), Description = Loc.T("plugins.specHint"), Placeholder = "@astragate-community/middleware-word-guard" };
        spec.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var install = PageUi.Button(Loc.T("plugins.install"), null, ButtonVariant.Primary, enabled: false);
        void Submit()
        {
            if (_busy || string.IsNullOrWhiteSpace(spec.Text)) return;
            _busy = true;
            install.IsEnabled = false;
            _ = Run(_model.AddAsync(spec.Text!), () => { _busy = false; install.IsEnabled = !string.IsNullOrWhiteSpace(spec.Text); });
        }
        install.Click += (_, _) => Submit();
        spec.TextChanged += (_, _) => install.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(spec.Text);

        _add.Title = Loc.T("plugins.addTitle");
        _add.Description = Loc.T("plugins.addDetail");
        _add.Content = PageUi.VStack(16, spec, PageUi.Alert(AlertTone.Warning, Loc.T("plugins.trustTitle"), Loc.T("plugins.trustDetail")));
        _add.Footer = [install];
        Avalonia.Threading.Dispatcher.UIThread.Post(() => spec.Focus());
    }

    private static async Task Run(Task work, Action done)
    {
        try { await work; }
        finally { done(); }
    }

    // ---- import from magpie ----

    private void BuildImport()
    {
        _signIns = false;
        _importWatch?.Dispose();
        _importWatch = PageUi.On(_model.Api.MagpiePreview, (_, e) =>
        {
            if (e.PropertyName is "Data" or "Status" && _import.IsOpen) FillImport();
        });
        FillImport();
    }

    private void FillImport()
    {
        var q = _model.Api.MagpiePreview;
        var p = q.Data;
        var content = new StackPanel { Spacing = 12 };
        if (q.IsLoading || (!q.HasData && !q.IsError)) content.Children.Add(new LoadingState(3));
        if (q.IsError && !q.HasData) content.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
        if (p is not null)
        {
            if (!string.IsNullOrEmpty(p.Error)) content.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), p.Error));
            var rows = p.Plugins.Select(x =>
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(12, 8) };
                var spec = PageUi.Text(x.Spec, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, mono: true);
                spec.FontSize = KitStyle.Type12;
                row.Children.Add(spec);
                var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                if (x.Off) badges.Children.Add(KitBadge.Create(Loc.T("plugins.magpie.off")));
                if (x.HasOptions) badges.Children.Add(KitBadge.Create(Loc.T("plugins.options"), Tone.Accent));
                if (x.Installed) badges.Children.Add(KitBadge.Create(Loc.T("plugins.installed"), Tone.Green));
                Grid.SetColumn(badges, 1);
                row.Children.Add(badges);
                return (Control)row;
            }).ToList();
            if (rows.Count > 0) content.Children.Add(PageUi.Card(PageUi.Divided(rows)));
            if (p.SignIns > 0)
            {
                var sw = new Switch { IsChecked = _signIns, VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(sw, Loc.T("plugins.magpie.signIns", ("count", p.SignIns)));
                sw.IsCheckedChanged += (_, _) => _signIns = sw.IsChecked == true;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sw, PageUi.Text(Loc.T("plugins.magpie.signIns", ("count", p.SignIns)), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground) } };
                var detail = PageUi.Text(Loc.T("plugins.magpie.signInsDetail"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
                detail.FontSize = KitStyle.Type12;
                content.Children.Add(PageUi.VStack(8, line, detail));
            }
        }
        var import = PageUi.Button(Loc.T("plugins.magpie.import"), null, ButtonVariant.Primary, enabled: p is { Found: true } && !_busy);
        import.Click += (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            import.IsEnabled = false;
            // A failed import leaves the sheet open: the button comes back (web: disabled only while pending).
            _ = Run(_model.ImportMagpieAsync(_signIns), () => { _busy = false; import.IsEnabled = _model.Api.MagpiePreview.Data is { Found: true }; });
        };
        _import.Title = Loc.T("plugins.magpie.title");
        _import.Description = p?.Path;
        _import.Content = content;
        _import.Footer = [import];
    }

    // ---- a job as it runs ----

    private void BuildJob()
    {
        _jobWatch?.Dispose();
        _jobWatch = null;
        if (_model.JobId is not { } id) return;
        var q = _model.Api.Job(id);
        _jobWatch = PageUi.On(q, (_, e) =>
        {
            if (e.PropertyName is "Data" or "Status" && _job.IsOpen) FillJob(q.Data);
        });
        FillJob(q.Data);
    }

    private void FillJob(PluginJobDto? job)
    {
        var content = new StackPanel { Spacing = 12 };
        if (job is null) content.Children.Add(new LoadingState(3));
        else content.Children.Add(new CodeBlock { Code = PluginJobTexts.LogText(job), Language = "text", MaxLines = 15 });
        if (PluginJobTexts.FailureText(job) is { } failure) content.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("plugins.job.failedTitle"), failure));
        var close = PageUi.Button(Loc.T("common.close"), () => _model.JobId = null);
        _job.Title = Loc.T("plugins.job.title");
        _job.Description = PluginJobTexts.Description(job);
        _job.Content = content;
        _job.Footer = [close];
    }

    // ---- a plugin's options as JSON ----

    private void BuildOptions()
    {
        if (_model.Editing is not { } p) return;
        var error = PageUi.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Danger, wrap: true);
        error.FontSize = KitStyle.Type12;
        error.IsVisible = false;
        var area = new Textarea { Label = Loc.T("plugins.options"), Text = PluginOptions.InitialText(p.Options, p.OptionsExample), Height = 16 + 14 * 20 };
        area.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        area.TextChanged += (_, _) =>
        {
            area.IsInvalid = false;
            error.IsVisible = false;
        };
        var save = PageUi.Button(Loc.T("common.save"), null, ButtonVariant.Primary);
        save.Click += (_, _) =>
        {
            if (!PluginOptions.TryParse(area.Text, out var options, out var message))
            {
                area.IsInvalid = true;
                error.Text = message;
                error.IsVisible = true;
                return;
            }
            Persist(p, options, save);
        };
        var footer = new List<Control>();
        if (p.Options is not null)
        {
            var clear = PageUi.Button(Loc.T("plugins.optionsClear"), null);
            clear.Click += (_, _) => Persist(p, null, save);
            footer.Add(clear);
        }
        footer.Add(save);

        var body = new StackPanel { Spacing = 8, Children = { area, error } };
        _options.Title = Loc.T("plugins.optionsTitle", ("name", p.ShortName));
        _options.Description = PluginOptions.Description(p.Options, p.OptionsExample);
        _options.Content = body;
        _options.Footer = footer;
    }

    private void Persist(PluginDto plugin, JsonObject? options, ArcButton save)
    {
        if (_busy) return;
        _busy = true;
        save.IsEnabled = false;
        _ = Run(_model.SaveOptionsAsync(plugin, options), () =>
        {
            _busy = false;
            save.IsEnabled = true;
        });
    }
}
