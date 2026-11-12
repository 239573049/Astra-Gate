using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.PluginUi;

/// <summary>The component gallery page (route <see cref="AppRoute.PluginGallery"/>, web /plugins/dev/gallery).</summary>
public sealed class PluginGalleryModule : IPageModule
{
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.PluginGallery, (ctx, param) => new PluginGalleryViewModel(ctx, param), vm => new PluginGalleryView((PluginGalleryViewModel)vm));
}

/// <summary>Which component (or, with "scene:&lt;id&gt;", which single scene) the gallery shows.</summary>
public sealed class PluginGalleryViewModel(PageContext context, string? param) : PageViewModel(AppRoute.PluginGallery, param, context)
{
    public override string? Subtitle => Loc.T("pluginUi.gallery.subtitle");

    /// <summary>One scene shown alone (what the visual comparison captures), or null.</summary>
    public string? OnlyScene => Param is { } p && p.StartsWith("scene:", StringComparison.Ordinal) ? p[6..] : null;

    /// <summary>The component chosen by the route, or null for the first.</summary>
    public string? Component => Param is { Length: > 0 } p && !p.StartsWith("scene:", StringComparison.Ordinal) ? p : null;
}

/// <summary>
/// Every scene of the UI scene set (06 §2, §5) rendered by the desktop's own renderer, at a chosen width class, theme and
/// surface: the same scenes the web gallery shows and the visual comparison captures on both shells.
/// </summary>
public sealed class PluginGalleryView : UserControl
{
    private readonly PluginGalleryViewModel _page;
    private readonly StackPanel _list = new() { Spacing = 2, Width = 180 };
    private readonly StackPanel _scenes = new() { Spacing = 12 };
    private readonly ThemeVariantScope _scope = new();
    private readonly TextBlock _log = KitStyle.Text("", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
    private JsonArray _all = [];
    private string? _component;
    private string _width = "regular";
    private string _surface = "page";
    private string? _theme;

    public PluginGalleryView(PluginGalleryViewModel page)
    {
        _page = page;
        _component = page.Component;
        Content = Ui.Spinner();
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (PluginShell.Api is not { } api || _all.Count > 0) return;
        try
        {
            _all = await api.ScenesAsync() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or Core.Api.ApiException)
        {
            Content = Ui.Note(ex.Message);
            return;
        }
        if (_page.OnlyScene is { } only)
        {
            var scene = _all.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == only);
            Content = scene is null ? Ui.Note(only) : new Border { Padding = new Thickness(16), Child = Scene(api, scene) };
            return;
        }
        Build(api);
    }

    private Control Scene(Core.Plugins.V2.PluginV2Api api, JsonObject scene)
    {
        var id = scene["id"]?.GetValue<string>() ?? "";
        return new PluginViewHost(api, (JsonObject)scene["tree"]!.DeepClone(), _page.Context!.Feedback,
            (action, args) => _log.Text = $"{id}: {action} {args?.ToJsonString() ?? "null"}", _width, _surface)
        {
            MaxWidth = _surface == "tray" ? 340 : _surface == "sheet" ? 520 : double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private IEnumerable<IGrouping<string, JsonObject>> Groups() =>
        _all.OfType<JsonObject>().GroupBy(s => s["component"]?.GetValue<string>() ?? Loc.T("pluginUi.gallery.patterns"));

    private void Build(Core.Plugins.V2.PluginV2Api api)
    {
        var groups = Groups().ToList();
        _component ??= groups.FirstOrDefault()?.Key;
        _list.Children.Clear();
        foreach (var g in groups)
        {
            var key = g.Key;
            var row = Ui.Button($"{key}  ({g.Count()})", () =>
            {
                _component = key;
                Build(api);
            }, key == _component ? TKCode.Arc.ButtonVariant.Secondary : TKCode.Arc.ButtonVariant.Ghost);
            row.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.HorizontalContentAlignment = HorizontalAlignment.Left;
            _list.Children.Add(row);
        }
        _scenes.Children.Clear();
        foreach (var scene in groups.FirstOrDefault(g => g.Key == _component) ?? Enumerable.Empty<JsonObject>())
        {
            var head = new DockPanel();
            var idText = KitStyle.Text(scene["id"]?.GetValue<string>() ?? "", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            DockPanel.SetDock(idText, Dock.Right);
            head.Children.Add(idText);
            head.Children.Add(KitStyle.Text(scene["state"]?.GetValue<string>() ?? "", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
            var card = new Border
            {
                Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Child = new StackPanel { Spacing = 10, Children = { head, Scene(api, scene) } },
            };
            _scenes.Children.Add(card.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border));
        }

        Control Seg(string label, string value, string[] values, Action<string> set)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
            foreach (var v in values)
            {
                var b = Ui.Button(v, () =>
                {
                    set(v);
                    Build(api);
                }, v == value ? TKCode.Arc.ButtonVariant.Secondary : TKCode.Arc.ButtonVariant.Ghost);
                row.Children.Add(b);
            }
            return row;
        }
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16 };
        bar.Children.Add(Seg(Loc.T("pluginUi.gallery.width"), _width, ["compact", "regular", "wide"], v => _width = v));
        bar.Children.Add(Seg(Loc.T("pluginUi.gallery.theme"), _theme ?? "light", ["light", "dark"], v => _theme = v));
        bar.Children.Add(Seg(Loc.T("pluginUi.gallery.surface"), _surface, ["page", "sheet", "tray"], v => _surface = v));
        // the theme preview changes only the scenes: a theme scope, not the app's theme
        _scope.RequestedThemeVariant = _theme == "dark" ? ThemeVariant.Dark : _theme == "light" ? ThemeVariant.Light : ThemeVariant.Default;
        _scope.Child = new StackPanel { Spacing = 12, Children = { _scenes, _log } };

        var main = new StackPanel { Spacing = 12, Children = { bar, _scope } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var left = new ScrollViewer { Content = _list, MaxHeight = 720, Margin = new Thickness(0, 0, 16, 0) };
        grid.Children.Add(left);
        Grid.SetColumn(main, 1);
        grid.Children.Add(main);
        Content = grid;
    }
}
