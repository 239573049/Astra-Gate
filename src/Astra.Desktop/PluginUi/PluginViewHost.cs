using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.PluginUi;

/// <summary>One node of the rendered tree: its JSON, the wrapper that holds its control, and its children's hosts.</summary>
internal sealed class NodeHost
{
    public required JsonObject Node { get; set; }
    public Border Wrapper { get; } = new();
    public List<NodeHost> Kids { get; } = [];

    /// <summary>Where the children's wrappers go, in order; null when the node places them itself (a child op rebuilds it).</summary>
    public Avalonia.Controls.Controls? ChildHost { get; set; }
}

/// <summary>
/// One plugin view on the desktop (design/plugin-v2 03): its <see cref="ViewSession"/>, the rendered tree, and the
/// effects and questions it raises. A patch updates only the nodes it touches: each node sits in a wrapper of its own,
/// a changed node gets a new control in its wrapper, and children are inserted, removed and moved as wrappers — no kept
/// control is ever moved into a new parent (the reparenting crash, .agents/memory/desktop-rebuild-reparent-crash.md).
/// </summary>
public sealed class PluginViewHost : UserControl
{
    private readonly ViewSession _session;
    private readonly IFeedback _feedback;
    private readonly Action<string>? _navigate;
    private readonly Action<JsonNode?>? _closed;
    private readonly ContentControl _body = new();
    private NodeHost? _root;
    private string _width = "regular";
    private bool _opened;

    /// <param name="navigate">Opens a console route (web path, e.g. "/requests?model=x").</param>
    /// <param name="closed">A sheet / dialog view called ctx.close(result).</param>
    public PluginViewHost(PluginV2Api api, ViewTarget target, IFeedback feedback, Action<string>? navigate = null, Action<JsonNode?>? closed = null, string surface = "page")
    {
        _feedback = feedback;
        _navigate = navigate;
        _closed = closed;
        Surface = surface;
        _session = new ViewSession(api.Client, target, ClientJson, a => Dispatcher.UIThread.Post(a));
        _session.TreeChanged += OnTree;
        _session.Effect += OnEffect;
        _session.Ask += OnAsk;
        _session.DataChanged += _ => Rebuild();
        _session.StateChanged += Rebuild;
        _session.Failed += why => _body.Content = Failure(why);
        Api = api;
        _body.Content = Ui.Spinner();
        Content = _body;
    }

    /// <summary>
    /// A tree shown with no session (the component gallery): rendered by the same code as a plugin's view; actions it fires go to
    /// <paramref name="sent"/>. <paramref name="width"/> fixes the width class instead of measuring it.
    /// </summary>
    public PluginViewHost(PluginV2Api api, JsonObject tree, IFeedback feedback, Action<string, JsonNode?>? sent = null, string? width = null, string surface = "page")
        : this(api, new ViewTarget("gallery", "scene"), feedback, surface: surface)
    {
        _staticTree = tree;
        _fixedWidth = width;
        if (width is not null) _width = width;
        if (sent is not null) _session.Sent += sent;
    }

    private readonly JsonObject? _staticTree;
    private readonly string? _fixedWidth;

    internal PluginV2Api Api { get; }
    internal string Surface { get; }
    internal ViewSession Session => _session;
    internal string Locale => Loc.Language.StartsWith("zh", StringComparison.Ordinal) ? "zh" : "en";
    internal string WidthClass => _width;
    internal IFeedback Feedback => _feedback;

    /// <summary>
    /// What components keep for themselves between renders (a table's sort and selection, the open tab), by a key that
    /// survives a rebuild (which deep-clones the tree) — the web keeps the same in component state.
    /// </summary>
    internal Dictionary<string, object> UiState { get; } = [];

    private JsonObject ClientJson() => new()
    {
        ["shell"] = "desktop",
        ["ui"] = PluginUiProtocol.UiVersion,
        ["components"] = new JsonArray([.. UiRender.Components.Select(c => (JsonNode?)JsonValue.Create(c))]),
        ["locale"] = Locale,
        ["theme"] = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "dark" : "light",
        ["width"] = _width,
        ["surface"] = Surface,
        ["platform"] = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
        ["timeZone"] = TimeZoneInfo.Local.Id,
        ["utcOffset"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
        ["weekStart"] = Locale == "zh" ? 1 : 0,
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnContextChanged;
        ActualThemeVariantChanged += OnTheme;
        if (_opened) return;
        _opened = true;
        if (_staticTree is not null) _session.ShowStatic(_staticTree);
        else _ = _session.OpenAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnContextChanged;
        ActualThemeVariantChanged -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => OnContextChanged();

    private void OnContextChanged() => Dispatcher.UIThread.Post(_session.ClientChanged);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var w = _fixedWidth ?? PluginUiProtocol.WidthClass(e.NewSize.Width);
        if (w == _width) return;
        _width = w;
        _session.ClientChanged();
        Rebuild();
    }

    /// <summary>Closes the session (the page or sheet went away).</summary>
    public ValueTask CloseAsync() => _session.DisposeAsync();

    // ---- tree

    private bool _inspectChecked;

    // a dev plugin's view gets the inspector button once its session is open (06 §2)
    private void OfferInspector()
    {
        if (_inspectChecked || _staticTree is not null || _session.Id is not { } id) return;
        _inspectChecked = true;
        _ = Task.Run(async () =>
        {
            if (!await PluginInspector.AvailableAsync(Api.Client, id)) return;
            Dispatcher.UIThread.Post(() => Content = new Panel { Children = { _body, PluginInspector.Button(this, Api.Client, id) } });
        });
    }

    private void OnTree(JsonArray? ops)
    {
        OfferInspector();
        if (ops is null || _root is null)
        {
            Rebuild();
            return;
        }
        foreach (var op in ops.OfType<JsonObject>())
        {
            if (!Apply(op))
            {
                Rebuild();
                return;
            }
        }
    }

    // A full redraw from the session's tree (a reset, the width class, a data channel, busy state).
    private void Rebuild()
    {
        if (_session.Tree is not { } tree) return;
        var focused = FocusedInputName();
        _root = new NodeHost { Node = (JsonObject)tree.DeepClone() };
        Render(_root);
        _body.Content = _root.Wrapper;
        Refocus(focused);
    }

    private bool Apply(JsonObject op)
    {
        var path = (op["path"] as JsonArray ?? []).Select(p => p!.GetValue<int>()).ToList();
        var kind = op["op"]?.GetValue<string>();
        if (kind == "replace")
        {
            if (path.Count == 0) return false;
            var parent = Find(path.Take(path.Count - 1));
            if (parent is null || path[^1] >= parent.Kids.Count) return false;
            var host = parent.Kids[path[^1]];
            var node = (JsonObject)op["node"]!.DeepClone();
            ((JsonArray)parent.Node["children"]!)[path[^1]] = node;
            host.Node = node;
            Redraw(host);
            return true;
        }
        var target = Find(path);
        if (target is null) return false;
        switch (kind)
        {
            case "props":
            {
                if (target.Node["props"] is not JsonObject props) target.Node["props"] = props = [];
                if (op["set"] is JsonObject set) foreach (var (k, v) in set) props[k] = v?.DeepClone();
                if (op["unset"] is JsonArray unset) foreach (var k in unset) props.Remove(k!.GetValue<string>());
                Redraw(target);
                return true;
            }
            case "insert":
            {
                var index = op["index"]!.GetValue<int>();
                var node = (JsonObject)op["node"]!.DeepClone();
                if (target.Node["children"] is not JsonArray kids) target.Node["children"] = kids = [];
                kids.Insert(index, node);
                if (target.ChildHost is null)
                {
                    Redraw(target);
                    return true;
                }
                var host = new NodeHost { Node = node };
                Render(host);
                target.Kids.Insert(index, host);
                target.ChildHost.Insert(index, host.Wrapper);
                return true;
            }
            case "remove":
            {
                var index = op["index"]!.GetValue<int>();
                var kids = (JsonArray)target.Node["children"]!;
                kids.RemoveAt(index);
                if (kids.Count == 0) target.Node.Remove("children");
                if (target.ChildHost is null)
                {
                    Redraw(target);
                    return true;
                }
                target.ChildHost.Remove(target.Kids[index].Wrapper);
                target.Kids.RemoveAt(index);
                return true;
            }
            case "move":
            {
                var from = op["from"]!.GetValue<int>();
                var to = op["to"]!.GetValue<int>();
                var kids = (JsonArray)target.Node["children"]!;
                var item = kids[from]!;
                kids.RemoveAt(from);
                kids.Insert(to, item);
                if (target.ChildHost is null)
                {
                    Redraw(target);
                    return true;
                }
                var host = target.Kids[from];
                target.Kids.RemoveAt(from);
                target.ChildHost.Remove(host.Wrapper);
                target.Kids.Insert(to, host);
                target.ChildHost.Insert(to, host.Wrapper);
                return true;
            }
        }
        return false;
    }

    private NodeHost? Find(IEnumerable<int> path)
    {
        var host = _root;
        foreach (var i in path)
        {
            if (host is null || i < 0 || i >= host.Kids.Count) return null;
            host = host.Kids[i];
        }
        return host;
    }

    // A node drawn again in its own wrapper: new controls only, so nothing moves between parents.
    private void Redraw(NodeHost host)
    {
        var focused = host.Wrapper.IsKeyboardFocusWithin ? FocusedInputName() : null;
        host.Kids.Clear();
        host.ChildHost = null;
        Render(host);
        Refocus(focused);
    }

    internal void Render(NodeHost host)
    {
        host.Wrapper.Child = null;
        host.Wrapper.Child = UiRender.Build(this, host);
    }

    /// <summary>Builds the children of <paramref name="host"/> into <paramref name="into"/> (each in its wrapper) and remembers where they live.</summary>
    internal void Children(NodeHost host, Avalonia.Controls.Controls into)
    {
        host.ChildHost = into;
        foreach (var child in (host.Node["children"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kid = new NodeHost { Node = child };
            Render(kid);
            host.Kids.Add(kid);
            into.Add(kid.Wrapper);
        }
    }

    /// <summary>A control for a node that isn't part of the children list (a prop that holds a node, a fallback).</summary>
    internal Control Detached(JsonObject node) => UiRender.Build(this, new NodeHost { Node = (JsonObject)node.DeepClone() });

    private string? FocusedInputName()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        for (var v = focused; v is not null && v != this; v = v.GetVisualParent())
            if (v is Control { Tag: string tag } && tag.StartsWith("input:", StringComparison.Ordinal)) return tag;
        return null;
    }

    private void Refocus(string? tag)
    {
        if (tag is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var target = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Equals(c.Tag, tag));
            if (target is null) return;
            var input = target.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? target as TextBox;
            (input ?? (InputElement)target).Focus();
            if (input is not null) input.CaretIndex = input.Text?.Length ?? 0;
        });
    }

    private static Control Failure(string why) => new Alert { Tone = AlertTone.Danger, Title = Loc.T("pluginUi.failed"), Content = why };

    // ---- effects (03 §3.4)

    internal void Link(string href)
    {
        if (href.StartsWith("https://", StringComparison.Ordinal)) _ = OpenExternalAsync(href);
        else if (href.StartsWith('/')) _navigate?.Invoke(href);
        else if (href.StartsWith("astra://plugin/", StringComparison.Ordinal)) _navigate?.Invoke("/plugins/v2/" + href["astra://plugin/".Length..]);
    }

    private async Task OpenExternalAsync(string url)
    {
        if (!await _feedback.ConfirmAsync(new ConfirmRequest(Loc.T("pluginUi.openExternal"), url, Loc.T("pluginUi.open")))) return;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
    }

    private void OnEffect(JsonObject e)
    {
        var type = e["type"]?.GetValue<string>();
        switch (type)
        {
            case "toast":
            {
                var message = e["message"]?.ToString() ?? "";
                var tone = e["tone"]?.GetValue<string>();
                if (tone == "danger" || e["error"] is not null) _feedback.Error(message);
                else if (tone == "success") _feedback.Success(message);
                else _feedback.Info(message);
                break;
            }
            case "navigate":
            {
                var to = e["to"];
                if (to is JsonValue s && s.TryGetValue<string>(out var path)) _navigate?.Invoke(path.StartsWith('/') ? path : $"/plugins/v2/{_session.Target.Plugin}/{path}");
                else if (to is JsonObject o)
                {
                    var q = o["query"] is JsonObject query ? "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value?.ToString() ?? ""))) : "";
                    if (o["host"]?.GetValue<string>() is { } host) _navigate?.Invoke("/" + host + q);
                    else if (o["page"]?.GetValue<string>() is { } page) _navigate?.Invoke($"/plugins/v2/{_session.Target.Plugin}/{page}{(o["path"]?.GetValue<string>() is { } p ? "/" + p : "")}{q}");
                }
                break;
            }
            case "openExternal":
                Link(e["url"]?.GetValue<string>() ?? "");
                break;
            case "copy":
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(e["text"]?.ToString() ?? "");
                _feedback.Success(Loc.T("pluginUi.copied"));
                break;
            case "download":
                _ = SaveAsync(e);
                break;
            case "focus":
            case "scrollTo":
            {
                var key = e["key"]?.ToString();
                var target = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Equals(c.Tag, "key:" + key));
                if (type == "focus") (target?.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(i => i.Focusable) ?? target)?.Focus();
                else target?.BringIntoView();
                break;
            }
            case "close":
                _closed?.Invoke(e["result"]?.DeepClone());
                break;
        }
    }

    private async Task SaveAsync(JsonObject e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = e["name"]?.ToString() ?? "download" });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        var bytes = e["base64"]?.GetValue<string>() is { } b ? Convert.FromBase64String(b) : System.Text.Encoding.UTF8.GetBytes(e["text"]?.ToString() ?? "");
        await stream.WriteAsync(bytes);
    }

    // ---- questions: answered with the shell's own dialogs

    private void OnAsk(string id, JsonObject e) => _ = AskAsync(id, e);

    private async Task AskAsync(string id, JsonObject e)
    {
        switch (e["type"]?.GetValue<string>())
        {
            case "confirm":
                _session.Answer(id, JsonValue.Create(await _feedback.ConfirmAsync(new ConfirmRequest(e["title"]?.ToString() ?? "", e["message"]?.ToString(),
                    e["confirmLabel"]?.ToString(), null, e["tone"]?.GetValue<string>() == "danger"))));
                break;
            case "prompt":
            {
                var input = new Input { Text = e["value"]?.ToString() ?? "", PlaceholderText = e["label"]?.ToString(), PasswordChar = e["secret"] is not null ? '•' : '\0' };
                var sheet = new Sheet { Title = e["title"]?.ToString() ?? "", Content = input };
                string? answer = null;
                sheet.Footer =
                [
                    Ui.Button(Loc.T("common.cancel"), () => sheet.Close()),
                    Ui.Button(Loc.T("common.confirm"), () =>
                    {
                        answer = input.Text ?? "";
                        sheet.Close();
                    }, ButtonVariant.Primary),
                ];
                await sheet.ShowAsync(this);
                _session.Answer(id, answer is null ? null : JsonValue.Create(answer));
                break;
            }
            case "pickFile":
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
                {
                    _session.Answer(id, null);
                    return;
                }
                var multiple = e["multiple"] is JsonValue m && m.GetValueKind() == System.Text.Json.JsonValueKind.True;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = multiple });
                var refs = new JsonArray();
                foreach (var f in files)
                {
                    await using var s = await f.OpenReadAsync();
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    if (await _session.UploadAsync(f.Name, "application/octet-stream", ms.ToArray()) is { } r) refs.Add(r);
                }
                _session.Answer(id, multiple ? refs : refs.Count > 0 ? refs[0]!.DeepClone() : null);
                break;
            }
            case "openSheet":
            case "openDialog":
            {
                JsonNode? result = null;
                Sheet? sheet = null;
                var child = new PluginViewHost(Api, new ViewTarget(_session.Target.Plugin, e["view"]?.ToString() ?? "", e["params"] as JsonObject, null, null, _session.Id),
                    _feedback, _navigate, r =>
                    {
                        result = r;
                        sheet?.Close();
                    }, e["type"]?.GetValue<string>() == "openDialog" ? "dialog" : "sheet");
                sheet = new Sheet
                {
                    Title = e["title"]?.ToString() ?? "",
                    SheetWidth = e["size"]?.GetValue<string>() switch { "lg" => 720, "sm" => 380, _ => 520 },
                    Content = new ScrollViewer { Content = child, MaxHeight = 640 },
                };
                await sheet.ShowAsync(this);
                await child.CloseAsync();
                _session.Answer(id, result);
                break;
            }
        }
    }

    /// <summary>Uploads a dropped file for a FileDrop node; the ref object the plugin reads it by.</summary>
    internal async Task<JsonNode?> UploadAsync(IStorageFile file)
    {
        await using var s = await file.OpenReadAsync();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return await _session.UploadAsync(file.Name, "application/octet-stream", ms.ToArray());
    }

    internal static TopLevel? Top(Visual v) => TopLevel.GetTopLevel(v);

    internal static void Copy(Visual v, string text) => _ = TopLevel.GetTopLevel(v)?.Clipboard?.SetTextAsync(text);

    internal static bool IsKey(KeyEventArgs e, Key key) => e.Key == key;
}
