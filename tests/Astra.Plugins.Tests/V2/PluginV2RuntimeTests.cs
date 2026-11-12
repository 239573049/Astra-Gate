using System.Text.Json.Nodes;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Runtime;
using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.Tests.V2;

/// <summary>The v2 main context, host calls and view sessions, end to end on Jaunt.</summary>
public class PluginV2RuntimeTests
{
    private const string Counter = """
        import { definePlugin, ui, storage, commands, t, log } from 'astra';
        const { Page, Stack, Button, Text, Switch, Row, Group } = ui;

        export default definePlugin({
          async activate(ctx) {
            commands.register('bump', async (args) => {
              const n = (await storage.get('n')) ?? 0;
              await storage.set('n', n + (args.by ?? 1));
              return n + (args.by ?? 1);
            });
            log.info('activated ' + ctx.reason);
          },
        });

        export const counter = ui.defineView({
          async init(ctx) { return { n: (await storage.get('n')) ?? 0, on: false, title: ctx.params.title ?? 'Counter' }; },
          render(s) {
            return Page({ title: s.title }, [
              Group({ title: t('settings') }, [
                Row({ label: 'on' }, Switch({ key: 'sw', checked: s.on, onChange: 'toggle' })),
              ]),
              Stack({ direction: 'row', gap: '2' }, [
                Text({ key: 'count', value: 'n=' + s.n }),
                Button({ key: 'inc', label: '+1', onPress: { action: 'inc', args: { by: 1 } } }),
              ]),
            ]);
          },
          actions: {
            toggle(s, { value }) { s.on = value; },
            async inc(s, { by }, ctx) {
              s.n += by;
              await storage.set('n', s.n);
              ctx.toast({ message: 'now ' + s.n, tone: 'success' });
            },
            async ask(s, _, ctx) { s.answer = await ctx.confirm({ title: 'sure?' }); },
            boom() { throw new Error('nope'); },
            spin() { for (;;) {} },
          },
        });
        """;

    private static JsonObject Manifest(params string[] permissions) => new()
    {
        ["main"] = "main.js",
        ["permissions"] = new JsonArray([.. permissions.Select(p => (JsonNode?)p)]),
        ["contributes"] = new JsonObject
        {
            ["ui.pages"] = new JsonArray(new JsonObject { ["id"] = "counter", ["title"] = "Counter" }),
            ["commands"] = new JsonArray(new JsonObject { ["id"] = "bump", ["title"] = "Bump" }),
        },
    };

    [Fact]
    public async Task A_view_opens_renders_and_patches_on_an_action()
    {
        await using var t = new V2TestHost();
        await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter), ("locales/en.json", """{"settings":"Settings"}""")]);

        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.counter", "counter", new JsonObject { ["title"] = "Mine" }, V2TestHost.Client()));
        Assert.Equal("Page", open.Tree["type"]!.GetValue<string>());
        Assert.Equal("Mine", open.Tree["props"]!["title"]!.GetValue<string>());
        Assert.Equal("Settings", open.Tree["children"]![0]!["props"]!["title"]!.GetValue<string>());
        // an event prop names one of the view's actions
        Assert.Equal("inc", open.Tree["children"]![1]!["children"]![1]!["props"]!["onPress"]!["action"]!.GetValue<string>());

        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);
        await t.Host.Ui.EventAsync(open.SessionId, "inc", new JsonObject { ["by"] = 1 }, null, seq: 1);

        var patch = await stream.ReadAsync();
        Assert.Equal("patch", patch["type"]!.GetValue<string>());
        Assert.Equal(1, patch["ack"]!.GetValue<long>());
        var op = Assert.Single(patch["ops"]!.AsArray())!;
        Assert.Equal("props", op["op"]!.GetValue<string>());
        Assert.Equal("n=1", op["set"]!["value"]!.GetValue<string>());
        var toast = await stream.ReadAsync();
        Assert.Equal("now 1", toast["effect"]!["message"]!.GetValue<string>());
        Assert.Equal("1", await t.Services.Data.GetAsync("acme.counter", "n"));

        // the host's tree after the patch is what a shell gets by applying it
        var (tree, rev, _) = t.Host.Ui.Snapshot(open.SessionId);
        Assert.Equal(2, rev);
        Assert.True(JsonNode.DeepEquals(UiDiff.Apply(open.Tree, patch["ops"]!.AsArray()), tree));
    }

    [Fact]
    public async Task A_failed_action_keeps_the_state_and_shows_the_error()
    {
        await using var t = new V2TestHost();
        await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter)]);
        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.counter", "counter", null, V2TestHost.Client()));
        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);

        await t.Host.Ui.EventAsync(open.SessionId, "boom", null, null, seq: 4);

        var patch = await stream.ReadAsync();
        Assert.Empty(patch["ops"]!.AsArray());
        Assert.Equal(4, patch["ack"]!.GetValue<long>());
        var toast = await stream.ReadAsync();
        Assert.Equal("nope", toast["effect"]!["message"]!.GetValue<string>());
        Assert.Equal(0, t.Host.Ui.Snapshot(open.SessionId).State!["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_endless_action_is_stopped_and_the_view_keeps_working_on_a_new_engine()
    {
        await using var t = new V2TestHost();
        await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter)]);
        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.counter", "counter", null, V2TestHost.Client()));
        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);

        await t.Host.Ui.EventAsync(open.SessionId, "spin", null, null, seq: 1);
        await stream.ReadAsync();
        var error = await stream.ReadAsync();
        Assert.Contains("too long", error["effect"]!["message"]!.GetValue<string>());

        // the state lives with the host: a fresh engine carries on from it
        await t.Host.Ui.EventAsync(open.SessionId, "inc", new JsonObject { ["by"] = 2 }, null, seq: 2);
        var patch = await stream.ReadAsync();
        Assert.Equal("n=2", patch["ops"]![0]!["set"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_question_waits_for_the_shells_answer()
    {
        await using var t = new V2TestHost();
        await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter)]);
        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.counter", "counter", null, V2TestHost.Client()));
        // ask is not rendered, so it isn't an action the shell can send — call the op through a refreshed action list
        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);
        var pending = t.Host.Ui.EventAsync(open.SessionId, "ask", null, null, seq: 1);

        var ask = await stream.ReadAsync();
        Assert.Equal("ask", ask["type"]!.GetValue<string>());
        Assert.Equal("confirm", ask["effect"]!["type"]!.GetValue<string>());
        Assert.True(t.Host.Ui.Answer(open.SessionId, ask["id"]!.GetValue<string>(), JsonValue.Create(true)));
        await pending;
        Assert.True(t.Host.Ui.Snapshot(open.SessionId).State!["answer"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Commands_run_and_storage_needs_its_permission()
    {
        await using var t = new V2TestHost();
        await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter)]);
        Assert.Equal(5, (await t.Host.ExecuteCommandAsync("acme.counter:bump", new JsonObject { ["by"] = 5 }, null, default))!.GetValue<int>());
        Assert.Equal(7, (await t.Host.ExecuteCommandAsync("acme.counter:bump", new JsonObject { ["by"] = 2 }, null, default))!.GetValue<int>());

        await using var u = new V2TestHost();
        await u.AddAsync("acme.counter", Manifest("ui"), [("main.js", Counter)]);
        var e = await Assert.ThrowsAsync<PluginCallException>(() => u.Host.ExecuteCommandAsync("acme.counter:bump", null, null, default));
        Assert.Contains("storage", e.Message);
    }

    [Fact]
    public async Task A_plugin_waits_until_its_permissions_are_granted()
    {
        await using var t = new V2TestHost();
        var dir = await t.AddAsync("acme.counter", Manifest("ui", "storage"), [("main.js", Counter)], grant: false);
        Assert.Null(await t.Host.FindAsync("acme.counter"));
        Assert.Contains("waiting for permissions", (await t.Host.ProblemsAsync())[dir]);

        await t.Services.Data.SetGrantsAsync("acme.counter", ["ui", "storage"]);
        t.Plugins.Catalog.Invalidate();
        Assert.NotNull(await t.Host.FindAsync("acme.counter"));
    }

    [Fact]
    public async Task Http_fetch_reaches_only_granted_origins()
    {
        const string code = """
            import { http, commands, definePlugin } from 'astra';
            export default definePlugin({
              activate() {
                commands.register('get', async ({ url }) => {
                  try { const r = await http.fetch(url); return { status: r.status, body: await r.json() }; }
                  catch (e) { return { error: e.code }; }
                });
              },
            });
            """;
        await using var t = new V2TestHost();
        t.Services.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"to":"x"}""") };
        var manifest = new JsonObject
        {
            ["main"] = "main.js",
            ["permissions"] = new JsonArray("net:https://api.example.com"),
            ["contributes"] = new JsonObject { ["commands"] = new JsonArray(new JsonObject { ["id"] = "get" }) },
        };
        await t.AddAsync("acme.fetch", manifest, [("main.js", code)]);

        var ok = await t.Host.ExecuteCommandAsync("acme.fetch:get", new JsonObject { ["url"] = "https://api.example.com/v1/x" }, null, default);
        Assert.Equal(200, ok!["status"]!.GetValue<int>());
        Assert.True(ok["body"]!["ok"]!.GetValue<bool>());

        var denied = await t.Host.ExecuteCommandAsync("acme.fetch:get", new JsonObject { ["url"] = "https://evil.example.com/" }, null, default);
        Assert.Equal("permission_denied", denied!["error"]!.GetValue<string>());
        var activity = await t.Services.Data.ActivityAsync("acme.fetch", 10);
        Assert.Contains(activity, a => a.Target == "https://evil.example.com" && a.Outcome == "denied");
    }

    [Fact]
    public async Task Markdown_in_a_view_arrives_as_ordinary_nodes()
    {
        const string code = """
            import { ui } from 'astra';
            export const doc = ui.defineView({
              render() { return ui.Markdown({ value: '# Title\n\nSome **bold** and a [link](https://x.example).\n\n- [x] done\n- todo\n\n| a | b |\n|---|--:|\n| 1 | 2 |\n\n```js\nlet x = 1;\n```\n\n> [!WARNING]\n> careful\n\n<b>raw</b> [bad](javascript:alert(1))' }); },
            });
            """;
        await using var t = new V2TestHost();
        await t.AddAsync("acme.doc", new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("ui"),
            ["contributes"] = new JsonObject { ["ui.pages"] = new JsonArray(new JsonObject { ["id"] = "doc" }) },
        }, [("main.js", code)]);

        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.doc", "doc", null, V2TestHost.Client()));
        var json = open.Tree.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.DoesNotContain("\"Markdown\"", json);
        var blocks = open.Tree["children"]!.AsArray();
        Assert.Equal("Heading", blocks[0]!["type"]!.GetValue<string>());
        Assert.Contains("\"bold\":true", json);
        Assert.Contains("\"link\":\"https://x.example\"", json);
        Assert.Contains("☑", json);
        Assert.Contains("\"Table\"", json);
        Assert.Contains("\"language\":\"js\"", json);
        Assert.Contains("\"tone\":\"warning\"", json);
        Assert.Contains("<b>raw</b>", json);
        Assert.DoesNotContain("javascript:", json);
    }

    [Fact]
    public async Task Unknown_components_and_props_are_dropped_with_warnings()
    {
        const string code = """
            import { ui } from 'astra';
            export const v = ui.defineView({
              render() { return ui.Stack([ ui.node('Marquee', {}), ui.Button({ label: 'x', color: 'red', onPress: 'missing' }), ui.IconButton({ icon: 'x' }) ]); },
              actions: {},
            });
            """;
        await using var t = new V2TestHost();
        await t.AddAsync("acme.bad", new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("ui"),
            ["contributes"] = new JsonObject { ["ui.pages"] = new JsonArray(new JsonObject { ["id"] = "v" }) },
        }, [("main.js", code)]);

        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.bad", "v", null, V2TestHost.Client()));
        var kids = open.Tree["children"]!.AsArray();
        Assert.Equal("Alert", kids[0]!["type"]!.GetValue<string>());
        Assert.Null(kids[1]!["props"]!["color"]);
        Assert.Null(kids[1]!["props"]!["onPress"]);
        Assert.Equal("x", kids[2]!["props"]!["a11yLabel"]!.GetValue<string>());
        Assert.Contains(open.Warnings, w => w.Contains("Marquee"));
        Assert.Contains(open.Warnings, w => w.Contains("missing"));
    }

    [Fact]
    public async Task Data_channels_push_rows_without_a_render()
    {
        const string code = """
            import { ui } from 'astra';
            export const v = ui.defineView({
              render() { return ui.Table({ columns: [{ key: 'id' }], source: ui.collection('rows') }); },
              actions: { load(s, _, ctx) { ctx.collection('rows').replace([{ id: 1 }, { id: 2 }], { total: 2 }); ctx.collection('rows').append({ id: 3 }); } },
            });
            """;
        await using var t = new V2TestHost();
        await t.AddAsync("acme.rows", new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("ui"),
            ["contributes"] = new JsonObject { ["ui.pages"] = new JsonArray(new JsonObject { ["id"] = "v" }) },
        }, [("main.js", code)]);
        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.rows", "v", null, V2TestHost.Client()));
        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);

        await t.Host.Ui.EventAsync(open.SessionId, "load", null, null, 1);
        var first = await stream.ReadAsync();
        Assert.Equal("data", first["type"]!.GetValue<string>());
        Assert.Equal("replace", first["op"]!.GetValue<string>());
        Assert.Equal(2, first["meta"]!["total"]!.GetValue<int>());
        var second = await stream.ReadAsync();
        Assert.Equal("append", second["op"]!.GetValue<string>());

        // a shell that reconnects behind gets the rows in its reset
        var again = t.Host.Ui.Subscribe(open.SessionId, 0);
        var reset = await again.ReadAsync();
        Assert.Equal("reset", reset["type"]!.GetValue<string>());
        Assert.Equal(3, reset["collections"]!["rows"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Settings_come_from_the_configuration_defaults_and_options()
    {
        const string code = """
            import { config, commands, definePlugin } from 'astra';
            export default definePlugin({ activate() { commands.register('cfg', () => config.get()); } });
            """;
        await using var t = new V2TestHost();
        await t.AddAsync("acme.cfg", new JsonObject
        {
            ["main"] = "main.js",
            ["contributes"] = new JsonObject
            {
                ["commands"] = new JsonArray(new JsonObject { ["id"] = "cfg" }),
                ["configuration"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("a", "b"), ["default"] = "a" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["default"] = 3 },
                        ["key"] = new JsonObject { ["type"] = "string", ["secret"] = true },
                    },
                },
            },
        }, [("main.js", code)], options: """{"mode":"b","key":"leak"}""");

        var cfg = (await t.Host.ExecuteCommandAsync("acme.cfg:cfg", null, null, default))!.AsObject();
        Assert.Equal("b", cfg["mode"]!.GetValue<string>());
        Assert.Equal(3, cfg["limit"]!.GetValue<int>());
        Assert.False(cfg.ContainsKey("key"));
    }
}
