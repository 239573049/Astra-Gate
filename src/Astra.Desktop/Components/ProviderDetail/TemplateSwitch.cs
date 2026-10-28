using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// Moving an API-key provider onto another template / variant (web components/TemplateSwitch.tsx <c>TemplateSwitchSheet</c>,
/// <c>POST /api/providers/{id}/template-switch</c>): the target among the services that take an API key, its variant, and the endpoints it
/// would give. The key, models and edited settings stay.
/// </summary>
public sealed class TemplateSwitchSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private ProviderDto? _provider;
    private string _templateId = "";
    private string _variantId = "";
    private ArcButton? _apply;
    private IDisposable? _watch;

    /// <summary>The sheet.</summary>
    public TemplateSwitchSheet(PageContext ctx, ProviderDetailApi api)
    {
        _ctx = ctx;
        _api = api;
        Closed += (_, _) =>
        {
            _api.Templates.PropertyChanged -= OnTemplates;
            _watch?.Dispose();
            _watch = null;
        };
    }

    /// <summary>Opens on the provider's current template (web <c>useEffect([open])</c>).</summary>
    public void Show(ProviderDto provider)
    {
        _provider = provider;
        _templateId = provider.TemplateId ?? "";
        _variantId = "";
        _watch ??= _api.Templates.Activate();
        _api.Templates.PropertyChanged -= OnTemplates;
        _api.Templates.PropertyChanged += OnTemplates;
        Show();
    }

    /// <summary>Picks a target (scenes).</summary>
    internal void Pick(string templateId, string variantId = "")
    {
        _templateId = templateId;
        _variantId = variantId;
        Build();
    }

    private void OnTemplates(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    protected override void Build()
    {
        if (_provider is not { } p) return;
        Title = Loc.T("providers.templateSwitch");
        Description = Loc.T("providers.templateSwitchDetail");
        var q = _api.Templates;
        var choices = (q.Data ?? []).Where(TemplateSwitchLogic.Switchable).ToList();
        var target = choices.FirstOrDefault(x => x.Id == _templateId);
        var stack = new StackPanel { Spacing = 16 };
        if (q.IsLoading) stack.Children.Add(new LoadingState(2));
        if (q.IsError) stack.Children.Add(PdUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message));
        var select = PdUi.Choice(choices.Select(x => (x.Id, TemplateSwitchLogic.OptionLabel(p, x), (string?)x.Id)), _templateId.Length > 0 ? _templateId : null,
            v => { _templateId = v; _variantId = ""; Dispatcher.UIThread.Post(Build); }, name: Loc.T("providers.templateSwitchTarget"));
        select.HorizontalAlignment = HorizontalAlignment.Stretch;
        stack.Children.Add(PdUi.Field(Loc.T("providers.templateSwitchTarget"), select));
        if (target is { Variants.Count: > 0 })
        {
            var variants = new List<(string, string)> { ("", Loc.T("providers.variantDefault")) };
            variants.AddRange(target.Variants.Select(v => (v.Id, v.Name)));
            var vs = PdUi.Choice(variants, _variantId, v => { _variantId = v; Dispatcher.UIThread.Post(Build); }, name: Loc.T("providers.variant"));
            vs.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(PdUi.Field(Loc.T("providers.variant"), vs));
        }
        if (target is not null)
        {
            var list = PdUi.Stack(4);
            foreach (var e in TemplateSwitchLogic.Endpoints(target, _variantId))
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("144,*"), ColumnSpacing = 12 };
                var url = PdUi.Text(e.BaseUrl, 12, ArcDesignTokenKind.Foreground, wrap: true, mono: true);
                Grid.SetColumn(url, 1);
                line.Children.Add(PdUi.Text(ProviderProtocols.Name(e.Protocol), 12, ArcDesignTokenKind.TextSecondary));
                line.Children.Add(url);
                list.Children.Add(line);
            }
            stack.Children.Add(PdUi.Stack(8, PdUi.Text(Loc.T("providers.templateSwitchEndpoints"), 13, ArcDesignTokenKind.Foreground, FontWeight.Medium), list));
        }
        Content = stack;
        _apply = PdUi.Button(Loc.T("common.apply"), PdUi.Look.Primary, click: () => _ = ApplyAsync(target));
        _apply.IsEnabled = target is not null && !(_templateId == p.TemplateId && _variantId.Length == 0);
        Footer = [PdUi.Cancel(this), _apply];
    }

    private async Task ApplyAsync(ProviderTemplateInfo? target)
    {
        if (target is null) return;
        var ok = await PdRun.TryAsync(_ctx, () => _api.SwitchTemplateAsync(target.Id, _variantId.Length > 0 ? _variantId : null), _apply);
        if (!ok) return;
        Close();
        _ctx.Feedback.Success(Loc.T("providers.templateSwitched", ("name", target.Name)));
    }
}

/// <summary>
/// Applying the template's newer version (web <c>TemplateUpdateSheet</c> in ProviderDetailPage.tsx): versions in the description, the list of
/// changes, or "up to date".
/// </summary>
public sealed class TemplateUpdateSheet : PdSheet
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private ArcButton? _apply;
    private IDisposable? _watch;

    /// <summary>The sheet.</summary>
    public TemplateUpdateSheet(PageContext ctx, ProviderDetailApi api)
    {
        _ctx = ctx;
        _api = api;
        Closed += (_, _) =>
        {
            _api.TemplateUpdate.PropertyChanged -= OnInfo;
            _watch?.Dispose();
            _watch = null;
            _api.TemplateUpdate.Enabled = false;
        };
    }

    /// <summary>Opens and loads the diff (web <c>useTemplateUpdate(id, open)</c>).</summary>
    public void Show(ProviderDto provider)
    {
        _api.TemplateUpdate.Enabled = true;
        _watch ??= _api.TemplateUpdate.Activate();
        _api.TemplateUpdate.PropertyChanged -= OnInfo;
        _api.TemplateUpdate.PropertyChanged += OnInfo;
        Show();
    }

    private void OnInfo(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    protected override void Build()
    {
        var q = _api.TemplateUpdate;
        var info = q.HasData ? q.Data : null;
        var upToDate = ProviderDetailLogic.UpToDate(info);
        Title = Loc.T("providers.templateUpdate");
        Description = info is null ? null : Loc.T("providers.templateVersions", ("current", info.CurrentVersion), ("latest", info.LatestVersion));
        var stack = new StackPanel { Spacing = 12 };
        if (q.IsLoading) stack.Children.Add(new LoadingState(2));
        if (q.IsError) stack.Children.Add(PdUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message));
        if (upToDate) stack.Children.Add(PdUi.Alert(AlertTone.Success, Loc.T("providers.templateUpToDate")));
        if (info is not null && !upToDate)
        {
            var list = PdUi.Stack(2);
            foreach (var c in info.Changes) list.Children.Add(PdUi.Text("•  " + c, 12, ArcDesignTokenKind.Foreground, wrap: true));
            stack.Children.Add(list);
        }
        Content = stack;
        _apply = PdUi.Button(Loc.T("common.apply"), PdUi.Look.Primary, click: () => _ = ApplyAsync());
        _apply.IsEnabled = info is not null && !upToDate;
        Footer = [PdUi.Cancel(this), _apply];
    }

    private async Task ApplyAsync()
    {
        if (!await PdRun.TryAsync(_ctx, () => _api.ApplyTemplateUpdateAsync(), _apply)) return;
        Close();
        _ctx.Feedback.Success(Loc.T("providers.templateApplied"));
    }
}

/// <summary>
/// Shown under an upstream 401/403 (model list, connection test) when the provider's template names key alternatives (web
/// <c>KeyAlternativeHint</c>, issue #9): the key probably belongs to one of them, and switching keeps the key and models. Renders nothing otherwise.
/// </summary>
public sealed class KeyAlternativeHint : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private IDisposable? _watch;
    private readonly bool _applies;

    /// <summary>A hint for <paramref name="provider"/> after an answer with <paramref name="httpStatus"/>.</summary>
    public KeyAlternativeHint(PageContext ctx, ProviderDetailApi api, ProviderDto provider, int? httpStatus)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        _applies = TemplateSwitchLogic.OffersAlternatives(provider, httpStatus);
        IsVisible = false;
        Build();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_applies) return;
        _watch = _api.Templates.Activate();
        _api.Templates.PropertyChanged += OnTemplates;
        Build();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.Templates.PropertyChanged -= OnTemplates;
        _watch?.Dispose();
        _watch = null;
    }

    private void OnTemplates(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var (current, alts) = _applies ? TemplateSwitchLogic.Alternatives(_provider, _api.Templates.Data) : (null, []);
        if (current is null || alts.Count == 0)
        {
            IsVisible = false;
            Content = null;
            return;
        }
        IsVisible = true;
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var a in alts)
        {
            ArcButton? b = null;
            b = PdUi.Button(Loc.T("providers.keyAlternative.switch", ("alt", a.Name)), small: true, click: () => _ = GoAsync(a, b!));
            buttons.Children.Add(b);
        }
        var body = PdUi.Stack(0,
            PdUi.Text(Loc.T("providers.keyAlternative.rejected", ("name", current.Name), ("alt", string.Join(" / ", alts.Select(x => x.Name)))), 13, ArcDesignTokenKind.Foreground, wrap: true),
            buttons);
        Content = PdUi.Alert(AlertTone.Warning, Loc.T("providers.keyAlternative.title"), body);
    }

    private async Task GoAsync(ProviderTemplateInfo alt, ArcButton button)
    {
        var ok = await _ctx.Feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("providers.templateSwitchConfirm", ("alt", alt.Name)),
            Loc.T("providers.templateSwitchConfirmDetail", ("urls", TemplateSwitchLogic.Urls(alt))),
            Loc.T("providers.keyAlternative.switch", ("alt", alt.Name))));
        if (!ok) return;
        if (await PdRun.TryAsync(_ctx, () => _api.SwitchTemplateAsync(alt.Id, null), button))
            _ctx.Feedback.Success(Loc.T("providers.templateSwitched", ("name", alt.Name)));
    }
}
