using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.PluginUi;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The desktop plugin renderer's Table, Tabs and SVG Image against a real view session over a fake server: what is drawn,
/// and the events (with their args) that go back — the same as the web's TableView / Tabs / Image.
/// </summary>
public class PluginUiRenderTests
{
    private sealed class FakeServer : HttpMessageHandler
    {
        public JsonObject Tree = new();
        public string Svg = "";
        public List<JsonObject> Events { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/plugins/v2/ui/sessions")
                return Json(new JsonObject { ["session"] = "s1", ["rev"] = 1, ["tree"] = Tree.DeepClone(), ["warnings"] = new JsonArray() });
            if (path.EndsWith("/stream", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Silent()) { Headers = { { "Content-Type", "text/event-stream" } } } };
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                lock (Events) Events.Add(body);
                return Json(new JsonObject());
            }
            if (path.EndsWith("/assets/logo.svg", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Svg, Encoding.UTF8, "image/svg+xml") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

        // an event stream that stays open and says nothing
        private sealed class Silent : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static (Window Window, PluginViewHost View, FakeServer Server) Show(JsonObject tree, string svg = "")
    {
        var server = new FakeServer { Tree = tree, Svg = svg };
        var client = new ApiClient("http://127.0.0.1:1", server);
        var api = new PluginV2Api(client, new QueryClient(), () => "en");
        var view = new PluginViewHost(api, new ViewTarget("acme.x", "main"), new Astra.Desktop.Core.Shell.NullFeedback());
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Settle(() => Texts(view).Count > 0);
        return (window, view, server);
    }

    private static void Settle(Func<bool> done, int ms = 5000)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (limit.ElapsedMilliseconds > ms) throw new TimeoutException("the view did not settle");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static List<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    private static TextBlock Text(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static void PressKey(Control target)
    {
        var owner = target.GetVisualAncestors().OfType<Border>().First(b => b.Focusable);
        owner.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Control target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static JsonObject Event(FakeServer server, string action)
    {
        JsonObject? hit = null;
        Settle(() =>
        {
            lock (server.Events) hit = server.Events.LastOrDefault(e => e["action"]?.ToString() == action);
            return hit is not null;
        });
        return hit!;
    }

    private static JsonObject On(string action) => new() { ["action"] = action };

    private static JsonObject Table(JsonObject props) => new() { ["type"] = "Table", ["key"] = "t", ["props"] = props };

    private static JsonArray Rows() => new(
        new JsonObject { ["id"] = "a", ["name"] = "pear", ["n"] = 10, ["state"] = "ok" },
        new JsonObject { ["id"] = "b", ["name"] = "apple", ["n"] = 9, ["state"] = "bad" },
        new JsonObject { ["id"] = "c", ["name"] = "fig", ["n"] = 100, ["state"] = "ok" });

    private static JsonArray Columns() => new(
        new JsonObject { ["key"] = "name", ["title"] = "Name", ["sortable"] = true },
        new JsonObject { ["key"] = "n", ["title"] = "N", ["sortable"] = true, ["align"] = "end", ["format"] = "number" },
        new JsonObject
        {
            ["key"] = "state", ["title"] = "State",
            ["cell"] = new JsonObject { ["type"] = "badge", ["labelBy"] = new JsonObject { ["ok"] = "Fine", ["bad"] = "Broken" }, ["toneBy"] = new JsonObject { ["bad"] = "danger" } },
        });

    private static List<string> NameColumn(Control root) =>
        Texts(root).Where(t => t is "pear" or "apple" or "fig").ToList();

    [AvaloniaFact]
    public void A_local_table_sorts_on_its_header_and_tells_the_plugin()
    {
        var (window, view, server) = Show(Table(new JsonObject { ["columns"] = Columns(), ["rows"] = Rows(), ["onSort"] = On("sorted") }));
        Assert.Equal(["pear", "apple", "fig"], NameColumn(view));
        Assert.Contains("Fine", Texts(view));
        Assert.Contains("Broken", Texts(view)); // labelBy by the raw value

        PressKey(Text(view, "Name"));
        Assert.Equal(["apple", "fig", "pear"], NameColumn(view));
        Assert.Contains("Name ↑", Texts(view));
        Assert.Equal("""{"key":"name","dir":"asc"}""", Event(server, "sorted")["args"]!["sort"]!.ToJsonString());

        PressKey(Text(view, "Name ↑"));
        Assert.Equal(["pear", "fig", "apple"], NameColumn(view));

        // numbers sort as numbers (9 < 10 < 100), not as text
        PressKey(Text(view, "N"));
        Assert.Equal(["apple", "pear", "fig"], NameColumn(view));

        // the sort survives a full rebuild (a collection update, a resize)
        typeof(PluginViewHost).GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["apple", "pear", "fig"], NameColumn(view));
        window.Close();
    }

    [AvaloniaFact]
    public void A_server_side_table_keeps_the_plugins_order_and_pages_and_selects_through_events()
    {
        var (window, view, server) = Show(Table(new JsonObject
        {
            ["columns"] = Columns(), ["rows"] = Rows(), ["total"] = 45, ["pageSize"] = 20, ["page"] = 1,
            ["selection"] = "multiple", ["onSelect"] = On("selected"), ["onPage"] = On("paged"), ["onSort"] = On("sorted"),
            ["bulkActions"] = new JsonArray(new JsonObject { ["label"] = "Purge", ["action"] = "purge" }),
        }));
        Assert.Contains("1 / 3", Texts(view));

        PressKey(Text(view, "Name"));
        Assert.Equal("sorted", Event(server, "sorted")["action"]!.ToString());
        Assert.Equal(["pear", "apple", "fig"], NameColumn(view)); // the plugin sorts a server-side table

        var boxes = view.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(4, boxes.Count); // the header's and one per row
        boxes[2].IsChecked = true; // row "apple"
        Settle(() => Texts(view).Any(t => t == "Purge"));
        Assert.Equal("""["b"]""", Event(server, "selected")["args"]!["keys"]!.ToJsonString());
        Assert.Contains(Loc.T("pluginUi.selected", ("count", "1")), Texts(view));

        view.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Purge"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("""["b"]""", Event(server, "purge")["args"]!["keys"]!.ToJsonString());

        // the header box selects every row
        view.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;
        Settle(() => Texts(view).Contains(Loc.T("pluginUi.selected", ("count", "3"))));
        Assert.Equal("""["a","b","c"]""", Event(server, "selected")["args"]!["keys"]!.ToJsonString());

        var next = view.GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyEnabled);
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var paged = Event(server, "paged")["args"]!;
        Assert.Equal(2, paged["page"]!.GetValue<int>());
        Assert.Equal(20, paged["pageSize"]!.GetValue<double>());
        window.Close();
    }

    [AvaloniaFact]
    public void Pressing_a_row_selects_it_and_reports_the_row()
    {
        var (window, view, server) = Show(Table(new JsonObject
        {
            ["columns"] = Columns(), ["rows"] = Rows(), ["selection"] = "single", ["onSelect"] = On("selected"), ["onRowPress"] = On("opened"),
        }));
        Click(window, Text(view, "fig"));
        var opened = Event(server, "opened")["args"]!;
        Assert.Equal("c", opened["key"]!.ToString());
        Assert.Equal("fig", opened["row"]!["name"]!.ToString());
        Assert.Equal("""["c"]""", Event(server, "selected")["args"]!["keys"]!.ToJsonString());
        window.Close();
    }

    [AvaloniaFact]
    public void An_empty_or_loading_table_says_so()
    {
        var (window, view, _) = Show(new JsonObject
        {
            ["type"] = "Stack", ["props"] = new JsonObject(),
            ["children"] = new JsonArray(
                Table(new JsonObject { ["columns"] = Columns(), ["rows"] = new JsonArray() }),
                new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = "after" } }),
        });
        Assert.Contains(Loc.T("pluginUi.empty"), Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void Tabs_show_icons_and_badges_switch_lazily_and_keep_the_choice()
    {
        static JsonObject Tab(string value, string text) => new()
        {
            ["type"] = "Tab", ["props"] = new JsonObject { ["value"] = value },
            ["children"] = new JsonArray(new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = text } }),
        };
        var (window, view, server) = Show(new JsonObject
        {
            ["type"] = "Tabs", ["key"] = "tabs",
            ["props"] = new JsonObject
            {
                ["name"] = "tab", ["onChange"] = On("switched"),
                ["items"] = new JsonArray(
                    new JsonObject { ["value"] = "one", ["label"] = "One", ["icon"] = "house" },
                    new JsonObject { ["value"] = "two", ["label"] = "Two", ["badge"] = "3" }),
            },
            ["children"] = new JsonArray(Tab("one", "first pane"), Tab("two", "second pane")),
        });
        Assert.Contains("first pane", Texts(view));
        Assert.DoesNotContain("second pane", Texts(view)); // only the open tab is built
        Assert.Contains("3", Texts(view));

        PressKey(Text(view, "Two"));
        Assert.Contains("second pane", Texts(view));
        Assert.DoesNotContain("first pane", Texts(view));
        Assert.Equal("two", Event(server, "switched")["args"]!["value"]!.ToString());
        Assert.Equal("two", view.Session.Local["tab"]!.ToString());

        typeof(PluginViewHost).GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("second pane", Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void An_svg_image_is_drawn_from_the_plugins_assets()
    {
        var (window, view, _) = Show(new JsonObject
        {
            ["type"] = "Stack", ["props"] = new JsonObject(),
            ["children"] = new JsonArray(
                new JsonObject { ["type"] = "Image", ["props"] = new JsonObject { ["src"] = "./logo.svg", ["alt"] = "Logo", ["radius"] = "md" } },
                new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = "below" } }),
        }, svg: """<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" fill="currentColor"/><path d="M6 12h12" stroke="#fff" stroke-width="2"/></svg>""");
        var image = view.GetVisualDescendants().OfType<Image>().Single();
        Settle(() => image.Source is not null);
        var drawing = Assert.IsType<DrawingImage>(image.Source);
        Assert.Equal(new Size(24, 24), drawing.Size);
        Assert.Equal("Logo", Avalonia.Automation.AutomationProperties.GetName(image));
        window.Close();
    }

    [AvaloniaFact]
    public void An_inline_svg_data_uri_is_drawn_too()
    {
        var uri = "data:image/svg+xml;base64," + Convert.ToBase64String(
            """<svg viewBox="0 0 10 20"><rect width="10" height="20" fill="#0a0"/></svg>"""u8.ToArray());
        var (window, view, _) = Show(new JsonObject
        {
            ["type"] = "Stack", ["props"] = new JsonObject(),
            ["children"] = new JsonArray(
                new JsonObject { ["type"] = "Image", ["props"] = new JsonObject { ["src"] = uri } },
                new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = "x" } }),
        });
        var image = view.GetVisualDescendants().OfType<Image>().Single();
        Settle(() => image.Source is not null);
        Assert.Equal(new Size(10, 20), Assert.IsType<DrawingImage>(image.Source).Size);
        window.Close();
    }
}
