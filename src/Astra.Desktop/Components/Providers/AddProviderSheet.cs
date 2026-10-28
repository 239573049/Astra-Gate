using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Providers;

/// <summary>The few things the wizard needs from its page (so it can be driven by the page view model or a test).</summary>
internal interface IProviderSheetHost
{
    ProviderQueries Queries { get; }

    Astra.Desktop.Core.Shell.IFeedback Feedback { get; }

    IReadOnlyList<ProviderDto> Items { get; }

    void Open(ProviderDto provider);

    void NavigateTo(string route, string? param);
}

/// <summary>
/// The add-provider wizard (ProvidersPage.tsx <c>AddProviderSheet</c>): a modal <see cref="Sheet"/> (web: 640 px) with a two-step
/// <see cref="Stepper"/> — step 1 chooses a template (search, grouped radio cards, plugin providers, custom), step 2 fills in the details
/// including the "more options" (endpoints, preferred protocols, headers, proxy, multiplier, initial models, notes). The rules live in
/// <see cref="AddProviderWizard"/>; this class only draws them.
/// </summary>
internal sealed class AddProviderSheet : UserControl
{
    private readonly IProviderSheetHost _host;
    private readonly AddProviderWizard _w = new();
    private readonly Sheet _sheet = new() { SheetWidth = 640, Size = ControlSize.Large };
    private readonly List<CompactRadioCards> _cards = [];
    private StackPanel _groups = new() { Spacing = 16 };
    private ArcButton? _create;
    private ArcButton? _next;
    private bool _syncing;
    private bool _creating;
    private bool _moreOpen;

    // The sheet's height around its body: header (title, description, divider), the body's padding and the footer row.
    private const double SheetChrome = 200;

    /// <summary>Builds the (closed) wizard.</summary>
    public AddProviderSheet(IProviderSheetHost host)
    {
        _host = host;
        Content = _sheet;
        _sheet.Closed += (_, _) => _w.Reset();
        _w.StepChanged += RebuildAll;
        _w.Changed += RefreshFooter;
    }

    // The two queries are shared and outlive the page: follow them only while attached, or they keep every Providers page alive.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.Queries.Templates.PropertyChanged += OnTemplates;
        _host.Queries.PriceKeys.PropertyChanged += OnPriceKeys;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host.Queries.Templates.PropertyChanged -= OnTemplates;
        _host.Queries.PriceKeys.PropertyChanged -= OnPriceKeys;
    }

    private void OnTemplates(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_sheet.IsOpen && !_w.IsDetails) RebuildAll();
    }

    private void OnPriceKeys(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_sheet.IsOpen && _w.IsDetails) RebuildAll();
    }

    /// <summary>The wizard's state (for tests and the visual scenes).</summary>
    public AddProviderWizard Wizard => _w;

    /// <summary>Whether the sheet is showing.</summary>
    public bool IsOpen => _sheet.IsOpen;

    /// <summary>Opens at step 1 with a clean form.</summary>
    public void Open()
    {
        _w.Reset();
        _moreOpen = false;
        RebuildAll();
        _sheet.IsOpen = true;
    }

    /// <summary>Test seam: the footer's Continue.</summary>
    internal void Continue() => Proceed();

    /// <summary>Test seam: redraws after the wizard state was edited from outside.</summary>
    internal void Reopen() => RebuildAll();

    /// <summary>Test seam: shows step 2 with "more options" expanded.</summary>
    internal void ExpandMore()
    {
        _moreOpen = true;
        RebuildAll();
    }

    /// <summary>Closes the sheet.</summary>
    public void Close() => _sheet.IsOpen = false;

    private IReadOnlyList<ProviderTemplateInfo> Templates => _host.Queries.Templates.Data ?? [];

    private void RebuildAll()
    {
        var tpl = _w.Template;
        _sheet.Title = _w.IsDetails ? Loc.T("providers.addTitleDetails") : Loc.T("providers.addTitle");
        _sheet.Description = _w.IsDetails ? (tpl is not null ? tpl.Name : Loc.T("providers.custom")) : Loc.T("providers.addDetail");
        var stepper = new Stepper { Label = Loc.T("providers.addTitle"), CurrentIndex = _w.StepIndex, Margin = new Thickness(0, 0, 0, 24) };
        stepper.Steps.Add(new StepperStep(Loc.T("providers.step.template")) { Id = "template" });
        stepper.Steps.Add(new StepperStep(Loc.T("providers.step.details")) { Id = "details" });
        var body = new StackPanel { Children = { stepper, _w.IsDetails ? Details() : TemplateStep() } };
        // The body can be taller than the window: scroll it, keep title and footer in place. Web: the dialog grows to the window less
        // 2rem (arc dialog .content max-height: calc(100vh - var(--space-8))), so the body takes that height less the sheet's chrome
        // (title, description, footer and paddings: about 200 px). The scroller reaches into the panel's right padding, so the cards keep
        // the web's full content width and the scroll bar sits in the gutter.
        var window = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 900;
        _sheet.Content = new ScrollViewer
        {
            MaxHeight = Math.Max(240, window - 32 - SheetChrome),
            Margin = new Thickness(0, 0, -16, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(0, 0, 16, 0), Child = body },
        };
        BuildFooter();
    }

    // ---------------------------------------------------------------- footer

    private void BuildFooter()
    {
        var cancel = ProviderUi.Button(Loc.T("common.cancel"), onClick: Close);
        if (_w.IsDetails)
        {
            // web: Back first with mr-auto (the far left), then Cancel and Create at the right.
            var back = ProviderUi.Button(Loc.T("common.back"), variant: ButtonVariant.Ghost, onClick: () => _w.Back());
            _create = ProviderUi.Button(Loc.T("providers.create"), variant: ButtonVariant.Primary, onClick: Submit);
            _next = null;
            _sheet.Footer = [SheetFooter.Split(back, [cancel, _create])];
        }
        else
        {
            _next = ProviderUi.Button(Loc.T("common.continue"), variant: ButtonVariant.Primary, onClick: Proceed);
            _create = null;
            _sheet.Footer = [cancel, _next];
        }
        RefreshFooter();
    }

    private void RefreshFooter()
    {
        if (_create is not null)
        {
            _create.IsEnabled = _w.CanSubmit && !_creating;
            _create.IsLoading = _creating;
        }
        if (_next is not null) _next.IsEnabled = !string.IsNullOrEmpty(_w.Choice);
    }

    private void Proceed()
    {
        var plugin = _w.Proceed(Templates, ProviderTemplates.AddedSubscriptionTemplateIds(_host.Items), ProviderTemplates.PluginProviders(_host.Items, ""));
        if (plugin is null) return;
        Close();
        if (_host.Items.FirstOrDefault(p => p.Id == plugin) is { } provider) _host.Open(provider);
    }

    private async void Submit()
    {
        _creating = true;
        RefreshFooter();
        try
        {
            var created = await _host.Queries.CreateAsync(_w.BuildRequest());
            Close();
            if (created is null) return;
            _host.Feedback.Success(Loc.T("providers.created", ("name", created.Name)));
            _host.Open(created);
        }
        catch (Exception ex)
        {
            _host.Feedback.Error(ex.Message);
        }
        finally
        {
            _creating = false;
            RefreshFooter();
        }
    }

    // ---------------------------------------------------------------- step 1

    private Control TemplateStep()
    {
        var stack = new StackPanel { Spacing = 16 };
        var search = new SearchField { Placeholder = Loc.T("providers.searchTemplates"), Text = _w.Search };
        search.TextChanged += (_, _) =>
        {
            if ((search.Text ?? "") == _w.Search) return;
            _w.Search = search.Text ?? "";
            BuildGroups();
        };
        stack.Children.Add(search);
        var templates = _host.Queries.Templates;
        if (templates.IsLoading || (!templates.HasData && !templates.IsError)) stack.Children.Add(new LoadingState(4));
        if (templates.IsError) stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = templates.Error?.Message });
        _groups = new StackPanel { Spacing = 16 };
        stack.Children.Add(_groups);
        BuildGroups();
        return stack;
    }

    private void BuildGroups()
    {
        _groups.Children.Clear();
        _cards.Clear();
        var added = ProviderTemplates.AddedSubscriptionTemplateIds(_host.Items);
        foreach (var (category, items) in ProviderTemplates.Group(Templates, _w.Search))
        {
            var options = items.Select(x =>
            {
                var (label, plan) = ProviderTemplates.SplitPlan(x.Name);
                var protocols = x.Endpoints.Select(e => e.Protocol).Distinct().ToList();
                return new RadioCardOption(x.Id, label, plan ?? string.Join(" · ", protocols.Select(ProviderProtocols.Short)))
                {
                    Icon = BrandIcon.Provider(x.Name, x.Icon, 28),
                    IsDisabled = added.Contains(x.Id),
                    DisabledReason = Loc.T("providers.subscriptionAdded"),
                };
            });
            _groups.Children.Add(Section(Loc.T("providers.category." + category), Cards(options, RadioCardsLayout.Grid)));
        }

        // Plugins' providers are generated by plugin sync (nothing to create): choosing one opens it, to sign in there.
        var plugins = ProviderTemplates.PluginProviders(_host.Items, _w.Search);
        Control pluginBody;
        if (plugins.Count > 0)
            pluginBody = Cards(plugins.Select(p => new RadioCardOption(ProviderTemplates.PluginChoice + p.Id, p.Name, Loc.T("providers.pluginProvided")) { Icon = BrandIcon.Provider(p.Name, p.Icon, 28) }), RadioCardsLayout.Grid);
        else
        {
            var hint = ProviderUi.Secondary(Loc.T("providers.pluginHint"));
            var browse = ProviderUi.Button(Loc.T("plugins.browseMarket"), size: ControlSize.Small, onClick: () =>
            {
                Close();
                _host.NavigateTo("Plugins", "market");
            });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(hint);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);
            var dashed = new Border { Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), Child = row };
            dashed.BorderBrush = null;
            dashed.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
            pluginBody = dashed;
        }
        _groups.Children.Add(Section(Loc.T("providers.category.plugin"), pluginBody));

        var custom = new RadioCardOption(ProviderTemplates.CustomChoice, Loc.T("providers.custom"), Loc.T("providers.customDetail"))
        {
            Icon = CustomIcon(),
        };
        _groups.Children.Add(Section(Loc.T("providers.custom"), Cards([custom], RadioCardsLayout.List)));
    }

    // web: a 28 px tile with a dashed border-strong edge (8 px radius) around a 16 px plus in text-secondary.
    private static Control CustomIcon()
    {
        var edge = new Avalonia.Controls.Shapes.Rectangle { RadiusX = 8, RadiusY = 8, StrokeThickness = 1, StrokeDashArray = [3, 2] };
        edge.Token(Avalonia.Controls.Shapes.Shape.StrokeProperty, ArcDesignTokenKind.BorderStrong);
        var plus = new LucideIcon { Kind = LucideIconKind.Plus, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return new Panel { Width = 28, Height = 28, Children = { edge, plus } };
    }

    private static Control Section(string title, Control body) => new StackPanel { Children = { ProviderUi.Heading(title), body } };

    // web RadioCards density="compact" (no radio dot, dense tiles): a local control, Arc's RadioCards has no compact density.
    private CompactRadioCards Cards(IEnumerable<RadioCardOption> options, RadioCardsLayout layout)
    {
        var cards = new CompactRadioCards(options.ToList(), list: layout == RadioCardsLayout.List);
        Avalonia.Automation.AutomationProperties.SetName(cards, layout == RadioCardsLayout.List ? Loc.T("providers.custom") : Loc.T("providers.addTitle"));
        // Group by group, only one tile is ever chosen: picking in one clears the others (web: one `choice`, each group shows it if its own).
        if (_w.Choice is { } choice && cards.Options.Any(o => Equals(o.Value, choice))) cards.SelectedValue = choice;
        cards.SelectionChanged += (_, _) =>
        {
            if (_syncing || cards.SelectedValue is not string value) return;
            _syncing = true;
            _w.Choice = value;
            foreach (var other in _cards.Where(c => c != cards)) other.SelectedValue = null;
            _syncing = false;
            RefreshFooter();
        };
        _cards.Add(cards);
        return cards;
    }

    // ---------------------------------------------------------------- step 2

    private Control Details()
    {
        var tpl = _w.Template;
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(FieldInput(Loc.T("providers.name"), _w.Name, v => _w.Name = v));

        if (tpl is { Variants.Count: > 0 })
        {
            var select = new Select { Label = Loc.T("providers.variant") };
            select.Items.Add(new SelectItem { Content = Loc.T("providers.variantDefault"), Value = "" });
            foreach (var v in tpl.Variants) select.Items.Add(new SelectItem { Content = v.Name, Value = v.Id });
            select.SelectedValue = _w.VariantId;
            select.SelectionChanged += (_, _) =>
            {
                if (select.SelectedValue is not string id || id == _w.VariantId) return;
                _w.ChooseVariant(id);
                RebuildAll();
            };
            stack.Children.Add(select);
        }

        if (_w.IsCustom)
        {
            stack.Children.Add(ProviderUi.Labeled(Loc.T("providers.section.endpoints"), EndpointsEditor(), Loc.T("providers.baseUrlHint")));
            stack.Children.Add(AuthSelect());
        }

        if (_w.Accounts)
        {
            stack.Children.Add(new Alert { Tone = AlertTone.Info, Title = Loc.T("providers.subscriptionWizardHint"), Content = Loc.T("providers.subscriptionWizardDetail") });
        }
        else
        {
            var key = new PasswordField { Label = Loc.T("providers.apiKey"), Description = Loc.T("providers.apiKeyHint"), Placeholder = "sk-…", Text = _w.ApiKey };
            key.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            key.TextChanged += (_, _) =>
            {
                _w.ApiKey = key.Text ?? "";
                _w.NotifyChanged();
            };
            var keyBox = new StackPanel { Spacing = 8, Children = { key } };
            if (tpl?.ApiKeyUrl is { Length: > 0 } url)
            {
                var link = ProviderUi.Button(Loc.T("providers.getKey"), LucideIconKind.ExternalLink, ButtonVariant.Ghost, ControlSize.Small, () => Launch(url));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                keyBox.Children.Add(link);
            }
            stack.Children.Add(keyBox);
        }

        // A key a template's endpoints refuse (a plan key vs a pay-as-you-go key): offer the sibling templates (issues #8/#9).
        var alternatives = _w.Alternatives(Templates);
        if (tpl is not null && alternatives.Count > 0)
        {
            var wrap = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Orientation = Orientation.Horizontal };
            wrap.Children.Add(new TextBlock { Text = Loc.T("providers.keyAlternative.wizard", ("name", tpl.Name), ("alt", string.Join(" / ", alternatives.Select(a => a.Name)))), VerticalAlignment = VerticalAlignment.Center }
                .Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary).With(t => t.FontSize = KitStyle.Type12));
            foreach (var alt in alternatives)
            {
                var picked = alt;
                wrap.Children.Add(ProviderUi.Button(Loc.T("providers.keyAlternative.pick", ("alt", alt.Name)), variant: ButtonVariant.Ghost, size: ControlSize.Small, onClick: () =>
                {
                    _w.Choice = picked.Id;
                    _w.Pick(picked);
                }));
            }
            stack.Children.Add(wrap);
        }

        stack.Children.Add(PriceKeySelect());
        if (!_w.Accounts) stack.Children.Add(MoreOptions(tpl));
        if (_w.ShowTemplateModelsHint) stack.Children.Add(ProviderUi.Secondary(Loc.T("providers.templateModels", ("count", tpl!.ModelCount))));
        return stack;
    }

    private static void Launch(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            _ = w.Launcher.LaunchUriAsync(uri);
    }

    private Input FieldInput(string label, string text, Action<string> set, string? placeholder = null, bool mono = false, string? description = null)
    {
        var input = new Input { Label = label, Text = text, Placeholder = placeholder, Description = description };
        if (mono) input.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        input.TextChanged += (_, _) =>
        {
            set(input.Text ?? "");
            _w.NotifyChanged();
        };
        return input;
    }

    private Select AuthSelect()
    {
        var select = new Select { Label = Loc.T("providers.authScheme") };
        foreach (var (value, label) in ProviderProtocols.AuthOptions) select.Items.Add(new SelectItem { Content = label, Value = value });
        select.SelectedValue = _w.AuthScheme;
        select.SelectionChanged += (_, _) =>
        {
            if (select.SelectedValue is string v)
            {
                _w.AuthScheme = v;
                _w.NotifyChanged();
            }
        };
        return select;
    }

    private Select PriceKeySelect()
    {
        var select = new Select { Label = Loc.T("providers.priceKey"), Description = Loc.T("providers.priceKeyHint") };
        select.Items.Add(new SelectItem { Content = Loc.T("providers.priceKeyNone"), Value = "" });
        foreach (var k in _host.Queries.PriceKeys.Data ?? []) select.Items.Add(new SelectItem { Content = k.Label, Value = k.PriceKey, Meta = k.PriceKey });
        select.SelectedValue = _w.PriceKey;
        select.SelectionChanged += (_, _) =>
        {
            if (select.SelectedValue is string v) _w.PriceKey = v;
        };
        return select;
    }

    private Control MoreOptions(ProviderTemplateInfo? tpl)
    {
        var inner = new StackPanel { Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        if (tpl is not null) inner.Children.Add(ProviderUi.Labeled(Loc.T("providers.section.endpoints"), EndpointsEditor(), Loc.T("providers.endpointsFooter")));
        if (_w.Endpoints.Select(e => e.Protocol).Distinct().Count() > 1) inner.Children.Add(ProviderUi.Labeled(Loc.T("providers.preferred"), PreferredChips(), Loc.T("providers.preferredHint")));
        if (tpl is not null) inner.Children.Add(AuthSelect());
        inner.Children.Add(ProviderUi.Labeled(Loc.T("providers.headers"), HeadersEditor()));
        inner.Children.Add(FieldInput(Loc.T("providers.proxy"), _w.Proxy, v => _w.Proxy = v, "http://127.0.0.1:7890", mono: true, description: Loc.T("providers.proxyHint")));

        var number = new NumberField { Minimum = 0, Step = 0.01m, Value = _w.Multiplier, Width = 112, HorizontalAlignment = HorizontalAlignment.Left, MaximumFractionDigits = 4 };
        Avalonia.Automation.AutomationProperties.SetName(number, Loc.T("providers.multiplier"));
        number.ValueChanged += (_, e) => _w.Multiplier = e.NewValue;
        inner.Children.Add(ProviderUi.Labeled(Loc.T("providers.multiplier"), number, Loc.T("providers.multiplierHint")));

        var models = new Textarea { Label = Loc.T("providers.initialModels"), Description = tpl is not null ? Loc.T("providers.initialModelsTemplateHint", ("count", tpl.ModelCount)) : Loc.T("providers.initialModelsCustomHint"), Text = _w.Models, FrameHeight = 84 };
        models.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        models.TextChanged += (_, _) =>
        {
            _w.Models = models.Text ?? "";
            _w.NotifyChanged();
            RefreshModelsHint();
        };
        inner.Children.Add(models);
        inner.Children.Add(FieldInput(Loc.T("providers.notes"), _w.Notes, v => _w.Notes = v));
        inner.Children.Add(ProviderUi.Hint(Loc.T("providers.moreOptionsDetail")));

        var accordion = new Accordion { ExpandedIndex = _moreOpen ? 0 : -1 };
        var item = new AccordionItem { Header = Loc.T("providers.moreOptions"), Content = inner, IsExpanded = _moreOpen };
        item.ExpandedChanged += (_, _) => _moreOpen = item.IsExpanded;
        accordion.Items.Add(item);
        return accordion;
    }

    private void RefreshModelsHint()
    {
        // The "template models" line under the form depends on whether model ids were typed.
        // It is rebuilt with the step; a light refresh is enough because only its visibility changes.
    }

    private Control PreferredChips()
    {
        var chips = new ChipGroup { Label = Loc.T("providers.preferred"), IsMultiple = true };
        var present = ProviderProtocols.Options.Where(o => _w.Endpoints.Any(e => e.Protocol == o.Value)).ToList();
        foreach (var (value, label) in present)
        {
            var idx = _w.Preferred.IndexOf(value);
            chips.Items.Add(new SelectItem { Content = idx >= 0 ? $"{idx + 1}. {label}" : label, Value = value });
        }
        for (var i = 0; i < present.Count; i++)
            if (_w.Preferred.Contains(present[i].Value)) chips.Selection.Select(i);
        var syncing = false;
        chips.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            _w.SetPreferred(chips.SelectedValues.OfType<string>().ToList());
            RebuildAll(); // the chips show their priority order
            syncing = false;
        };
        return chips;
    }

    private Control EndpointsEditor()
    {
        var stack = new StackPanel { Spacing = 8 };
        var used = _w.Endpoints.Select(e => e.Protocol).ToList();
        for (var i = 0; i < _w.Endpoints.Count; i++)
        {
            var index = i;
            var e = _w.Endpoints[i];
            var protocol = new Select { Width = 176 };
            Avalonia.Automation.AutomationProperties.SetName(protocol, Loc.T("providers.protocol"));
            foreach (var (value, label) in ProviderProtocols.Options.Where(o => o.Value == e.Protocol || !used.Contains(o.Value)))
                protocol.Items.Add(new SelectItem { Content = label, Value = value });
            protocol.SelectedValue = e.Protocol;
            protocol.SelectionChanged += (_, _) =>
            {
                if (protocol.SelectedValue is not string v || v == _w.Endpoints[index].Protocol) return;
                SetEndpoint(index, _w.Endpoints[index] with { Protocol = v });
                RebuildAll();
            };

            var url = new Input { Text = e.BaseUrl, Placeholder = "https://api.example.com/v1", MinWidth = 120 };
            url.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            Avalonia.Automation.AutomationProperties.SetName(url, Loc.T("providers.baseUrl"));
            url.TextChanged += (_, _) =>
            {
                var next = (url.Text ?? "");
                if (next == _w.Endpoints[index].BaseUrl) return;
                SetEndpoint(index, _w.Endpoints[index] with { BaseUrl = next });
                _w.NotifyChanged();
            };

            var full = new ArcSwitch { IsChecked = e.FullUrl == true };
            full.IsCheckedChanged += (_, _) => SetEndpoint(index, _w.Endpoints[index] with { FullUrl = full.IsChecked == true });
            var fullLabel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { full, new TextBlock { Text = Loc.T("providers.fullUrl"), VerticalAlignment = VerticalAlignment.Center }.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary).With(t => t.FontSize = KitStyle.Type12) },
            };
            ToolTip.SetTip(fullLabel, Loc.T("providers.fullUrlHint"));

            var remove = ProviderUi.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () =>
            {
                _w.SetEndpoints(_w.Endpoints.Where((_, j) => j != index).ToList());
                RebuildAll();
            });
            remove.IsEnabled = _w.Endpoints.Count > 1;

            // web: one flex-wrap row (w-44 protocol, min-w-52 flex-1 address, the full-URL switch, delete); the sheet's fixed 640 px
            // always fits it on one line.
            url.MinWidth = 208;
            fullLabel.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(protocol);
            Grid.SetColumn(url, 1);
            row.Children.Add(url);
            Grid.SetColumn(fullLabel, 2);
            row.Children.Add(fullLabel);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            stack.Children.Add(row);
        }

        var free = ProviderProtocols.Options.Where(o => !used.Contains(o.Value)).ToList();
        if (free.Count > 0)
        {
            var add = ProviderUi.Button(Loc.T("providers.addEndpoint"), LucideIconKind.Plus, ButtonVariant.Ghost, ControlSize.Small, () =>
            {
                _w.SetEndpoints([.. _w.Endpoints, new TemplateEndpoint(free[0].Value, _w.Endpoints.FirstOrDefault()?.BaseUrl ?? "")]);
                RebuildAll();
            });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(add);
        }
        return stack;
    }

    // An in-place edit of one endpoint: the preferred order does not change (the protocol set is the same) unless the protocol itself did.
    private void SetEndpoint(int index, TemplateEndpoint next) => _w.SetEndpoints(_w.Endpoints.Select((e, j) => j == index ? next : e).ToList());

    private Control HeadersEditor()
    {
        var stack = new StackPanel { Spacing = 8 };
        for (var i = 0; i < _w.Headers.Count; i++)
        {
            var index = i;
            var (k, v) = _w.Headers[i];
            var name = new Input { Text = k, Placeholder = "Header", Width = 176 };
            var value = new Input { Text = v, Placeholder = "value" };
            name.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            value.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            Avalonia.Automation.AutomationProperties.SetName(name, Loc.T("providers.headerName"));
            Avalonia.Automation.AutomationProperties.SetName(value, Loc.T("providers.headerValue"));
            name.TextChanged += (_, _) => _w.Headers[index] = (name.Text ?? "", _w.Headers[index].Value);
            value.TextChanged += (_, _) => _w.Headers[index] = (_w.Headers[index].Name, value.Text ?? "");
            var remove = ProviderUi.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () =>
            {
                _w.Headers.RemoveAt(index);
                RebuildAll();
            });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(name);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            stack.Children.Add(row);
        }
        var add = ProviderUi.Button(Loc.T("providers.addHeader"), LucideIconKind.Plus, ButtonVariant.Ghost, ControlSize.Small, () =>
        {
            _w.Headers.Add(("", ""));
            RebuildAll();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(add);
        return stack;
    }
}
