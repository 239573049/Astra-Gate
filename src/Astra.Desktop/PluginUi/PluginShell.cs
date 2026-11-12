using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// What the desktop shell does for v2 plugins outside their views (design/plugin-v2 03, 07): runtime permission requests
/// in the host's own dialog, plugin notifications as toasts, Lucide names to icons, and console paths to routes.
/// </summary>
public static class PluginShell
{
    private static PluginV2Api? _api;
    private static PageContext? _context;

    /// <summary>The v2 API the shell shares with plugin pages and slots (null before the shell attached).</summary>
    public static PluginV2Api? Api => _api;

    public static void Attach(Visual owner, PluginV2Api api, PageContext context)
    {
        _api = api;
        _context = context;
        var asking = new HashSet<string>();
        api.List.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(api.List.Data)) return;
            foreach (var request in api.List.Data?.Requests ?? [])
                if (asking.Add(request.Id)) Dispatcher.UIThread.Post(() => _ = AskAsync(owner, api, request));
        };
        _ = PollNotificationsAsync(api, context.Feedback);
    }

    private static async Task AskAsync(Visual owner, PluginV2Api api, PluginPermissionRequestDto request)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var p in request.Permissions) list.Children.Add(Ui.Note(PermissionText(p) + "  (" + p + ")", TKCode.Arc.DesignTokens.ArcDesignTokenKind.Foreground, 13));
        var allow = false;
        var sheet = new Sheet { Title = Loc.T("pluginUi.requestTitle", ("name", request.PluginName)), Content = new StackPanel { Spacing = 12, Children = { Ui.Note(Loc.T("pluginUi.requestDetail")), list } } };
        sheet.Footer =
        [
            Ui.Button(Loc.T("pluginUi.deny"), () => sheet.Close()),
            Ui.Button(Loc.T("pluginUi.allow"), () =>
            {
                allow = true;
                sheet.Close();
            }, ButtonVariant.Primary),
        ];
        await sheet.ShowAsync(owner);
        try
        {
            await api.AnswerRequestAsync(request.Id, allow);
        }
        catch (Exception e) when (e is HttpRequestException or Core.Api.ApiException)
        {
        }
    }

    private static async Task PollNotificationsAsync(PluginV2Api api, IFeedback feedback)
    {
        long? after = null;
        while (true)
        {
            try
            {
                var items = await api.NotificationsAsync(after ?? 0) ?? [];
                if (after is not null)
                    foreach (var n in items)
                    {
                        var body = n.Body is null ? null : n.Body;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (n.Tone == "danger") feedback.Error(n.Title, body);
                            else if (n.Tone == "success") feedback.Success(n.Title, body);
                            else feedback.Info(n.Title, body);
                        });
                    }
                after = items.Count > 0 ? items[^1].Id : after ?? 0;
            }
            catch (Exception e) when (e is HttpRequestException or Core.Api.ApiException or TaskCanceledException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>A host page's slot (empty until a plugin fills it); null when the shell hasn't attached v2 yet.</summary>
    public static Control Slot(string name, Func<JsonObject>? parameters = null) =>
        _context is { } ctx ? new PluginSlotView(name, parameters ?? (() => new JsonObject()), ctx.Feedback) { Margin = new Thickness(0, 16, 0, 0) } : new Panel();

    /// <summary>
    /// Adds the commands v2 plugins contribute to host menu <paramref name="name"/> (ui.menus) to <paramref name="menu"/>, at
    /// <paramref name="at"/> (default: the end), the first one starting a separated group; each runs with <paramref name="args"/>.
    /// </summary>
    public static void AddMenuEntries(DropdownMenu menu, string name, Func<JsonObject> args, int? at = null)
    {
        if (_api is not { } api || _context is not { } ctx) return;
        var items = api.List.Data?.Contributions.Menus.Where(m => m.Menu == name).ToList() ?? [];
        var index = Math.Clamp(at ?? menu.Items.Count, 0, menu.Items.Count);
        foreach (var (m, i) in items.Select((m, i) => (m, i)))
        {
            var entry = new MenuEntry { Header = m.Title, Icon = Lucide.Geometry(IconKind(m.Icon)), HasSeparatorBefore = i == 0 };
            entry.Click += (_, _) => _ = RunAsync(api, ctx.Feedback, m, args());
            menu.Items.Insert(index + i, entry);
        }
    }

    /// <summary>A menu of its own for places with no row menu (request detail, client panel); an empty panel when no plugin adds to it.</summary>
    public static Control Menu(string name, Func<JsonObject> args)
    {
        if (_api?.List.Data?.Contributions.Menus.Any(m => m.Menu == name) != true) return new Panel();
        var menu = new DropdownMenu { Label = Loc.T("pluginUi.menu"), Icon = Lucide.Geometry(LucideIconKind.Puzzle), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        AddMenuEntries(menu, name, args);
        if (menu.Items.Count > 0 && menu.Items[0] is MenuEntry first) first.HasSeparatorBefore = false;
        return menu;
    }

    private static async Task RunAsync(PluginV2Api api, IFeedback feedback, PluginV2MenuDto m, JsonObject args)
    {
        try
        {
            var result = await api.RunCommandAsync(m.Plugin, m.Command, args);
            var text = result is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : Loc.T("pluginUi.commandRan", ("name", m.Title));
            Dispatcher.UIThread.Post(() => feedback.Success(text));
        }
        catch (Exception e) when (e is HttpRequestException or Core.Api.ApiException)
        {
            Dispatcher.UIThread.Post(() => feedback.Error(m.Title, e.Message));
        }
    }

    /// <summary>
    /// A v2 provider's sign-in button (ui.flows, design/plugin-v2 08 §4): opens the plugin's flow view in a sheet with
    /// { flow, provider }; <paramref name="done"/> runs when it closes. Null when the row isn't v2 or has no flow running.
    /// </summary>
    public static Control? FlowButton(Visual owner, ProviderDto provider, Action done)
    {
        if (_api is not { } api || _context is not { } ctx || provider.Settings["plugin"] is not JsonObject s || s["v2"]?.GetValue<bool>() != true) return null;
        var plugin = s["plugin"]?.GetValue<string>();
        var providerId = s["provider"]?.GetValue<string>();
        var flowId = s["signIn"]?.GetValue<string>();
        var flow = api.List.Data?.Contributions.Flows?.FirstOrDefault(f => f.Plugin == plugin && f.Id == flowId);
        if (flow is null || providerId is null) return null;
        return Ui.Button(Loc.T("pluginUi.flow.signIn"), () => _ = ShowFlowAsync(owner, api, ctx, flow, providerId, done), ButtonVariant.Primary, LucideIconKind.LogIn);
    }

    private static async Task ShowFlowAsync(Visual owner, PluginV2Api api, PageContext ctx, PluginV2FlowDto flow, string providerId, Action done)
    {
        var sheet = new Sheet { Title = flow.Title };
        var view = new PluginViewHost(api, new ViewTarget(flow.Plugin, flow.View, new JsonObject { ["flow"] = flow.Id, ["provider"] = providerId }),
            ctx.Feedback, Navigate, _ => sheet.Close(), surface: "sheet");
        sheet.Content = view;
        await sheet.ShowAsync(owner);
        await view.CloseAsync();
        done();
    }

    /// <summary>A permission in words (the same texts as the console's).</summary>
    public static string PermissionText(string p)
    {
        if (p.StartsWith("net:", StringComparison.Ordinal))
        {
            var target = p[4..];
            return target.StartsWith("{config.", StringComparison.Ordinal)
                ? Loc.T("pluginUi.perm.netConfig", ("setting", target[8..^1]))
                : Loc.T("pluginUi.perm.net", ("origin", target));
        }
        var key = "pluginUi.perm." + p;
        var text = Loc.T(key);
        return text == key ? p : text;
    }

    /// <summary>A Lucide icon name ("shield-check") as its kind; a puzzle piece when unknown.</summary>
    public static LucideIconKind IconKind(string? name)
    {
        if (string.IsNullOrEmpty(name)) return LucideIconKind.Puzzle;
        var pascal = string.Concat(name.Split('-', '_').Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
        return Enum.TryParse<LucideIconKind>(pascal, true, out var kind) ? kind : LucideIconKind.Puzzle;
    }

    /// <summary>Opens a console path ("/requests?id=x", "/plugins/v2/acme.x/page/rest") on the desktop's routes.</summary>
    public static void Navigate(string path)
    {
        if (_context?.Navigator is not { } nav) return;
        var clean = path.Split('?', 2)[0].Trim('/');
        var query = path.Contains('?') ? path.Split('?', 2)[1] : null;
        if (clean == "plugins/dev/gallery")
        {
            nav.Navigate(nameof(AppRoute.PluginGallery), query);
            return;
        }
        if (clean.StartsWith("plugins/v2/", StringComparison.Ordinal))
        {
            nav.Navigate(nameof(AppRoute.PluginPage), clean["plugins/v2/".Length..] + (query is null ? "" : "?" + query));
            return;
        }
        var parts = clean.Split('/', 2);
        foreach (var route in RouteInfo.Routes)
        {
            var first = route.WebPath.Trim('/').Split('/', '[')[0];
            if (first.Length == 0 ? parts[0].Length == 0 : string.Equals(first, parts[0], StringComparison.OrdinalIgnoreCase))
            {
                var detail = parts.Length > 1 && route.WebPath.Contains("/:", StringComparison.Ordinal) && !route.WebPath.Contains('[')
                    ? parts[1] : null;
                var target = detail is null ? route.Route : RouteInfo.Routes.FirstOrDefault(r => r.WebPath.StartsWith("/" + first + "/:", StringComparison.Ordinal))?.Route ?? route.Route;
                nav.Navigate(target.ToString(), parts.Length > 1 ? parts[1] : query);
                return;
            }
        }
    }
}

/// <summary>A named slot of a host page (design/plugin-v2 03 §8): each running plugin's view for it, under its title. Empty when none fills it.</summary>
public sealed class PluginSlotView : UserControl
{
    private readonly string _slot;
    private readonly Func<JsonObject> _params;
    private readonly StackPanel _column = new() { Spacing = 16 };
    private readonly List<PluginViewHost> _views = [];
    private string _stamp = "";
    private IFeedback? _feedback;

    public PluginSlotView(string slot, Func<JsonObject> parameters, IFeedback feedback)
    {
        _slot = slot;
        _params = parameters;
        _feedback = feedback;
        Content = _column;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (PluginShell.Api is not { } api) return;
        api.List.PropertyChanged += OnList;
        Sync(api);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (PluginShell.Api is { } api) api.List.PropertyChanged -= OnList;
        foreach (var v in _views) _ = v.CloseAsync();
        _views.Clear();
        _column.Children.Clear();
        _stamp = "";
    }

    private void OnList(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Data" && PluginShell.Api is { } api) Dispatcher.UIThread.Post(() => Sync(api));
    }

    private void Sync(PluginV2Api api)
    {
        var slots = api.List.Data?.Contributions.Slots.Where(s => s.Slot == _slot).ToList() ?? [];
        var stamp = string.Join("|", slots.Select(s => s.Plugin + "/" + s.View)) + "#" + _params().ToJsonString();
        if (stamp == _stamp) return;
        _stamp = stamp;
        foreach (var v in _views) _ = v.CloseAsync();
        _views.Clear();
        _column.Children.Clear();
        foreach (var s in slots)
        {
            var view = new PluginViewHost(api, new ViewTarget(s.Plugin, s.View, _params(), null, s.Slot), _feedback!, PluginShell.Navigate,
                surface: _slot == "tray.panel" ? "tray" : "slot");
            _views.Add(view);
            _column.Children.Add(new StackPanel
            {
                Spacing = 8,
                Children = { Ui.Note((s.Title ?? s.Plugin) + " · " + s.Plugin), view },
            });
        }
    }
}
