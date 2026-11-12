using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins.Middleware;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.Diagnostics;

public static partial class PluginSelfTest
{
    private const string V2Main = """
        import { definePlugin, ui, storage, commands, providers, clients } from 'astra';
        export default definePlugin({
          activate() {
            commands.register('bump', async (a) => { const n = ((await storage.get('n')) ?? 0) + a.by; await storage.set('n', n); return n; });
            providers.register('acme', { prepare: (req) => ({ url: 'https://api.acme.example/v1/' + req.model, headers: { authorization: 'Bearer x' } }) });
            clients.register('acme-cli', { connect: (input) => ({ 'gateway.url': input.gateway.v1 }) });
          },
        });
        export const counter = ui.defineView({
          async init() { return { n: (await storage.get('n')) ?? 0 }; },
          render(s) { return ui.Stack({ gap: '2' }, [ui.Text({ key: 'n', value: 'n=' + s.n }), ui.Markdown({ value: '# hi *there*' }), ui.Button({ label: '+', onPress: 'inc' })]); },
          actions: { async inc(s) { s.n += 1; await storage.set('n', s.n); } },
        });
        """;

    private const string V2Gateway = """
        import { gateway } from 'astra';
        export default gateway.defineHooks({
          guard: { onRequest(req, ctx) { if (JSON.stringify(req).includes('secret')) return ctx.reject(403, 'v2 says no'); ctx.count('seen'); req.v2 = true; return req; } },
        });
        """;

    private const string V2Manifest = """
        {
          "manifestVersion": 2, "id": "selftest.v2", "version": "2.0.0", "name": "selftest",
          "main": "main.js", "gateway": "gateway.js",
          "permissions": ["ui", "storage", "gateway.request:rewrite", "gateway.request:reject", "providers", "clients", "net:https://api.acme.example"],
          "contributes": {
            "providers": [{ "id": "acme", "protocols": ["chat"] }],
            "clients": [{ "id": "acme-cli", "config": "~/.acme.json", "model": "model" }],
            "commands": [{ "id": "bump", "title": "Bump" }],
            "ui.pages": [{ "id": "counter", "title": "Counter" }],
            "gateway.hooks": [{ "id": "guard", "on": ["request"] }]
          }
        }
        """;

    // v2 plugins (design/plugin-v2): the main context, a view session's patch, a command, and a hook in the middleware chain.
    private static void V2Checks(string dir, Action<string, Func<string>> check)
    {
        var root = Path.Combine(dir, "v2");
        Write(dir, "v2/astra.plugin.json", V2Manifest);
        Write(dir, "v2/main.js", V2Main);
        Write(dir, "v2/gateway.js", V2Gateway);
        var services = new SelfTestV2Services();
        var catalog = new PluginCatalog(new OneStore(new PluginEntry { Spec = root, Name = "selftest.v2", Enabled = true, Source = PluginSources.Path }),
            new AstraPaths(Path.Combine(dir, "home-v2")));
        PluginV2Host? host = null;
        T Wait<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        void WaitAll(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        try
        {
            check("v2 manifest and grant", () =>
            {
                WaitAll(services.Data.SetGrantsAsync("selftest.v2", ["ui", "storage", "gateway.request:rewrite", "gateway.request:reject", "providers", "clients", "net:https://api.acme.example"]));
                host = new PluginV2Host(catalog, services);
                WaitAll(host.RefreshAsync());
                var problems = Wait(host.ProblemsAsync());
                return problems.Count == 0 && Wait(host.FindAsync("selftest.v2")) is not null ? "loaded" : throw Fail(string.Join("; ", problems.Values));
            });
            if (host is null) return;
            check("v2 view session patch", () =>
            {
                var open = Wait(host.Ui.OpenAsync(new UiOpenRequest("selftest.v2", "counter", null, new JsonObject
                {
                    ["shell"] = "web", ["ui"] = "1.0", ["locale"] = "en", ["theme"] = "light", ["width"] = "regular", ["surface"] = "page",
                })));
                var stream = host.Ui.Subscribe(open.SessionId, open.Rev);
                WaitAll(host.Ui.EventAsync(open.SessionId, "inc", null, null, seq: 1));
                var patch = stream.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var (tree, _, _) = host.Ui.Snapshot(open.SessionId);
                var applied = UiDiff.Apply(open.Tree, patch["ops"]!.AsArray());
                var markdown = open.Tree["children"]![1]!["type"]!.GetValue<string>();
                WaitAll(host.Ui.CloseAsync(open.SessionId));
                return JsonNode.DeepEquals(applied, tree) && tree.ToJsonString().Contains("n=1") && markdown != "Markdown"
                    ? $"{patch["ops"]!.AsArray().Count} op(s), markdown → {markdown}" : throw Fail(patch.ToJsonString());
            });
            check("v2 command and storage", () =>
            {
                var r = Wait(host.ExecuteCommandAsync("selftest.v2:bump", new JsonObject { ["by"] = 41 }, null, CancellationToken.None));
                return r?.ToJsonString() == "42" ? "bump → 42" : throw Fail(r?.ToJsonString() ?? "null");
            });
            check("v2 provider prepare and client connect", () =>
            {
                var prepared = Wait(host.ProviderCallAsync("selftest.v2", "acme", "prepare", new JsonObject { ["model"] = "m1", ["protocol"] = "chat" }, null, CancellationToken.None));
                var keys = Wait(host.ConnectClientAsync("selftest.v2", "acme-cli", new JsonObject { ["gateway"] = new JsonObject { ["v1"] = "http://127.0.0.1:1/v1" } }, CancellationToken.None));
                return prepared?["url"]?.GetValue<string>() == "https://api.acme.example/v1/m1" && keys?["gateway.url"]?.GetValue<string>() == "http://127.0.0.1:1/v1"
                    ? "prepare → url, connect → keys" : throw Fail(prepared?.ToJsonString() + " / " + keys?.ToJsonString());
            });
            check("v2 gateway hook", () =>
            {
                var engine = new MiddlewareEngine(catalog, (_, _) => { }) { V2Host = host };
                MiddlewareRun Begin() => engine.BeginAsync(new MiddlewareInfo("anthropic", "m", false, "/v1/messages", null))
                    .AsTask().GetAwaiter().GetResult() ?? throw Fail("no run");
                MiddlewareReject? reject;
                using (var run = Begin()) (_, reject) = run.Request(Encoding.UTF8.GetBytes("""{"text":"a secret"}"""));
                using var ok = Begin();
                var (body, _) = ok.Request(Encoding.UTF8.GetBytes("""{"text":"fine"}"""));
                return reject is (403, "v2 says no") && Encoding.UTF8.GetString(body).Contains("\"v2\":true")
                    ? "reject and rewrite through the chain" : throw Fail($"{reject} / {Encoding.UTF8.GetString(body)}");
            });
        }
        finally
        {
            host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }

    // What the v2 host needs from a server, in memory: no network, no notifications.
    private sealed class SelfTestV2Services : V2.IPluginV2Services
    {
        public string HostVersion => "0.0.0";
        public MemoryPluginDataStore Data { get; } = new();
        IPluginDataStore V2.IPluginV2Services.Data => Data;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public HttpClient Http() => new(new NoNetwork());
        public void Notify(string pluginId, string title, string? body, string tone) { }
        public ValueTask<JsonNode?> QueryAsync(string what, JsonNode? args, CancellationToken ct) => ValueTask.FromResult<JsonNode?>(null);
        public void Log(string pluginId, string level, string message) { }

        private sealed class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
