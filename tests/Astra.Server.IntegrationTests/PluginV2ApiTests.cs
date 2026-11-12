using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>v2 plugins through the admin API and the gateway: install, grant, settings, views over HTTP + SSE, ir hooks.</summary>
public class PluginV2ApiTests
{
    private const string Main = """
        import { definePlugin, ui, storage, config, commands } from 'astra';
        export default definePlugin({ activate() { commands.register('hello', (a) => 'hello ' + (a.name ?? config.get().greeting)); } });
        export const home = ui.defineView({
          async init() { return { n: (await storage.get('n')) ?? 0 }; },
          render(s) { return ui.Page({ title: 'Home' }, [ ui.Text({ key: 'n', value: 'n=' + s.n }), ui.Button({ key: 'b', label: '+', onPress: 'inc' }) ]); },
          actions: { async inc(s) { s.n++; await storage.set('n', s.n); } },
        });
        """;

    private const string Gateway = """
        import { gateway } from 'astra';
        export default gateway.defineHooks({
          tag: {
            onRequest(req, ctx) {
              req.system.push({ type: 'text', text: 'Be brief.' });
              return req;
            },
          },
        });
        """;

    private static string Plugin(TestHost host)
    {
        var dir = Path.Combine(host.Root, "plugin-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), Main);
        File.WriteAllText(Path.Combine(dir, "gateway.js"), Gateway);
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """
            {
              "manifestVersion": 2, "id": "acme.hello", "version": "2.0.0", "name": { "en": "Hello", "zh": "你好" },
              "main": "main.js", "gateway": "gateway.js",
              "permissions": ["ui", "storage", "gateway.request:rewrite"],
              "contributes": {
                "configuration": { "properties": { "greeting": { "type": "string", "default": "world" }, "token": { "type": "string", "secret": true } } },
                "ui.pages": [{ "id": "home", "title": { "en": "Hello", "zh": "你好" }, "icon": "hand", "nav": "tools" }],
                "commands": [{ "id": "hello", "title": "Say hello" }],
                "gateway.hooks": [{ "id": "tag", "on": ["request"], "view": "ir" }]
              }
            }
            """);
        return dir;
    }

    private static async Task InstallAsync(TestHost host, string dir)
    {
        await host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "acme.hello", Source = PluginSources.Path });
        host.App.Services.GetRequiredService<Astra.Plugins.PluginCatalog>().Invalidate();
    }

    [Fact]
    public async Task A_plugin_waits_for_its_permissions_then_serves_settings_commands_and_a_view()
    {
        await using var host = await TestHost.StartAsync();
        await InstallAsync(host, Plugin(host));

        var list = await host.GetJsonAsync("/api/plugins/v2?locale=zh");
        var row = list["plugins"]!.AsArray().Single()!;
        Assert.Equal("你好", row["name"]!.GetValue<string>());
        Assert.False(row["running"]!.GetValue<bool>());
        Assert.Contains("waiting for permissions", row["problem"]!.GetValue<string>());
        Assert.Equal(3, row["missing"]!.AsArray().Count);
        Assert.Contains("gateway.request:rewrite", row["sensitive"]!.AsArray().Select(x => x!.GetValue<string>()));

        var (bad, _) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/grant", new { permissions = new[] { "net:https://x.example" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        var (granted, after) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/grant",
            new { permissions = new[] { "ui", "storage", "gateway.request:rewrite" } });
        Assert.Equal(HttpStatusCode.OK, granted);
        var page = after!["contributions"]!["pages"]!.AsArray().Single()!;
        Assert.Equal("home", page["id"]!.GetValue<string>());
        Assert.Equal("tools", page["nav"]!.GetValue<string>());

        var (_, said) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/commands/hello", new { args = new { } });
        Assert.Equal("hello world", said!["result"]!.GetValue<string>());
        var (saved, detail) = await host.SendAsync(HttpMethod.Put, "/api/plugins/v2/acme.hello/settings",
            new { values = new { greeting = "there" }, secrets = new { token = "s3cret" } });
        Assert.Equal(HttpStatusCode.OK, saved);
        Assert.Equal(["token"], detail!["plugin"]!["secretsSet"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("s3cret", detail.ToJsonString());
        (_, said) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/commands/hello", new { args = new { } });
        Assert.Equal("hello there", said!["result"]!.GetValue<string>());
        var (wrong, _) = await host.SendAsync(HttpMethod.Put, "/api/plugins/v2/acme.hello/settings", new { values = new { greeting = 3 } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong);

        // a view over HTTP: open, stream, event
        var (opened, open) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/ui/sessions",
            new { plugin = "acme.hello", view = "home", client = new { shell = "web", ui = "1.0", locale = "en" } });
        Assert.Equal(HttpStatusCode.OK, opened);
        var session = open!["session"]!.GetValue<string>();
        Assert.Equal("n=0", open["tree"]!["children"]![0]!["props"]!["value"]!.GetValue<string>());

        using var stream = await host.Client.GetAsync($"/api/plugins/v2/ui/sessions/{session}/stream?rev=1", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
        var (evented, _) = await host.SendAsync(HttpMethod.Post, $"/api/plugins/v2/ui/sessions/{session}/events", new { action = "inc", seq = 1 });
        Assert.Equal(HttpStatusCode.NoContent, evented);
        JsonNode? patch = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (patch is null && await reader.ReadLineAsync(timeout.Token) is { } line)
            if (line.StartsWith("data: ", StringComparison.Ordinal)) patch = JsonNode.Parse(line[6..]);
        Assert.Equal("patch", patch!["type"]!.GetValue<string>());
        Assert.Equal("n=1", patch["ops"]![0]!["set"]!["value"]!.GetValue<string>());

        var (closed, _) = await host.SendAsync(HttpMethod.Delete, $"/api/plugins/v2/ui/sessions/{session}");
        Assert.Equal(HttpStatusCode.NoContent, closed);
    }

    [Fact]
    public async Task An_ir_hook_set_rewrites_a_request_in_any_protocol()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json(
            """{"id":"m","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""));
        var dir = Plugin(gw.Host);
        await InstallAsync(gw.Host, dir);
        await gw.Host.Db.PluginData.SetGrantsAsync("acme.hello", ["ui", "storage", "gateway.request:rewrite"]);
        gw.Host.App.Services.GetRequiredService<Astra.Plugins.PluginCatalog>().Invalidate();

        var response = await gw.PostAsync("/v1/messages", """{"model":"claude-sonnet-5","max_tokens":10,"system":"You help.","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = gw.Upstream.Requests.Single().Json.ToJsonString();
        Assert.Contains("You help.", sent);
        Assert.Contains("Be brief.", sent);
    }

    [Fact]
    public async Task Dev_tools_gallery_scenes_the_inspector_and_tests_in_the_sandbox()
    {
        await using var host = await TestHost.StartAsync();
        var dir = Plugin(host);
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, "test", "home.test.js"), """
            import { it, expect } from 'vitest';
            import { mount } from '@astragate/plugin-sdk/testing';
            import { home } from '../main.js';
            it('counts', async () => { const v = await mount(home, { storage: { n: 4 } }); await v.press('inc'); expect(v.state.n).toBe(5); });
            it('fails', () => expect('a').toBe('b'));
            """);
        await InstallAsync(host, dir);

        // the gallery's scenes: every component, normalised
        var scenes = (await host.GetJsonAsync("/api/plugins/v2/ui/scenes")).AsArray();
        Assert.True(scenes.Count > 200);
        Assert.Contains(scenes, s => s!["id"]!.GetValue<string>() == "Chart/default");

        // tests run in the sandbox even before the plugin is granted (an isolated host grants what the manifest asks)
        var (status, results) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/test", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        var list = results!.AsArray();
        Assert.Equal("passed", list.Single(r => r!["name"]!.GetValue<string>() == "counts")!["status"]!.GetValue<string>());
        Assert.Equal("failed", list.Single(r => r!["name"]!.GetValue<string>() == "fails")!["status"]!.GetValue<string>());
        // and leave the plugin's own storage alone
        Assert.Null(await host.Db.PluginData.GetAsync("acme.hello", "n"));

        // the inspector answers for a dev plugin's session: tree, state, the timeline of its round trips
        await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.hello/grant", new { permissions = new[] { "ui", "storage", "gateway.request:rewrite" } });
        var (_, open) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/ui/sessions",
            new { plugin = "acme.hello", view = "home", client = new { shell = "web", ui = "1.0", locale = "en" } });
        var session = open!["session"]!.GetValue<string>();
        await host.SendAsync(HttpMethod.Post, $"/api/plugins/v2/ui/sessions/{session}/events", new { action = "inc", seq = 1 });
        var inspect = await host.GetJsonAsync($"/api/plugins/v2/ui/sessions/{session}/inspect");
        Assert.Equal(1, inspect["state"]!["n"]!.GetValue<int>());
        Assert.Equal("inc", inspect["timeline"]!.AsArray().Single()!["action"]!.GetValue<string>());
        Assert.Contains("inc", inspect["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
    }
}
