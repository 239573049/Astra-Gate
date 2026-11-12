using System.Text.Json.Nodes;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Shell;
using Avalonia.Controls;

namespace Astra.Desktop.PluginUi;

/// <summary>A v2 plugin's page (ui.pages) on the desktop: route <see cref="AppRoute.PluginPage"/>, param "plugin/page[/rest][?query]".</summary>
public sealed class PluginPageModule : IPageModule
{
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.PluginPage, (ctx, param) => new PluginPageViewModel(ctx, param), vm => new PluginPageView((PluginPageViewModel)vm));
}

/// <summary>Which page and route a plugin page's param names, and the page's title.</summary>
public sealed class PluginPageViewModel : PageViewModel
{
    public PluginPageViewModel(PageContext context, string? param) : base(AppRoute.PluginPage, param, context)
    {
        var (path, query) = (param ?? "").Split('?', 2) is var parts && parts.Length == 2 ? (parts[0], parts[1]) : (param ?? "", null);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Plugin = segments.Length > 0 ? segments[0] : "";
        PageId = segments.Length > 1 ? segments[1] : "";
        Rest = string.Join('/', segments.Skip(2));
        Query = new JsonObject();
        foreach (var pair in (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            Query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
    }

    public string Plugin { get; }
    public string PageId { get; }
    public string Rest { get; }
    public JsonObject Query { get; }

    // the page's own title once the plugin list is in; before that (or for a page that went away) its id, then "plugin pages"
    public override string Title =>
        PluginShell.Api?.List.Data?.Contributions.Pages.FirstOrDefault(p => p.Plugin == Plugin && p.Id == PageId)?.Title
        ?? (PageId.Length > 0 ? PageId : Core.I18n.Loc.T("pluginUi.pages"));

    public override string? Subtitle => Plugin;

    /// <summary>The view and its params: the first route whose pattern matches the rest of the path.</summary>
    public (string View, JsonObject Params)? ResolveView()
    {
        var page = PluginShell.Api?.List.Data?.Contributions.Pages.FirstOrDefault(p => p.Plugin == Plugin && p.Id == PageId);
        foreach (var r in page?.Routes ?? [])
            if (Match(r.Path, Rest) is { } bound)
            {
                bound["path"] = Rest;
                return (r.View, bound);
            }
        return null;
    }

    /// <summary>"rules/:id" against "rules/42" → { id: "42" }; null when it doesn't match.</summary>
    public static JsonObject? Match(string pattern, string rest)
    {
        var want = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var have = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (want.Length != have.Length) return null;
        var bound = new JsonObject();
        for (var i = 0; i < want.Length; i++)
        {
            if (want[i].StartsWith(':')) bound[want[i][1..]] = Uri.UnescapeDataString(have[i]);
            else if (want[i] != have[i]) return null;
        }
        return bound;
    }
}

public sealed class PluginPageView : UserControl
{
    private readonly PluginPageViewModel _page;
    private PluginViewHost? _view;

    public PluginPageView(PluginPageViewModel page)
    {
        _page = page;
        Content = Components.Settings.Ui.Spinner();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_view is not null || PluginShell.Api is not { } api || _page.Context is null) return;
        if (api.List.Data is null)
        {
            api.List.PropertyChanged += Retry;
            return;
        }
        Show(api);
    }

    private void Retry(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Data" || PluginShell.Api is not { } api || api.List.Data is null) return;
        api.List.PropertyChanged -= Retry;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(api));
    }

    private void Show(PluginV2Api api)
    {
        if (_page.ResolveView() is not { } route)
        {
            Content = Components.Settings.Ui.Note(Core.I18n.Loc.T("pluginUi.notFound"));
            return;
        }
        _view = new PluginViewHost(api, new ViewTarget(_page.Plugin, route.View, route.Params, _page.Query), _page.Context!.Feedback, PluginShell.Navigate,
            _ => _page.Context.Navigator.GoBack());
        Content = _view;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_view is not null) _ = _view.CloseAsync();
        _view = null;
    }
}
