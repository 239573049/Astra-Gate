using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.Middleware;

namespace Astra.Plugins.Tests.V2;

/// <summary>v2 gateway hooks running in the middleware chain: permissions, match, ctx.shared, onText, onComplete.</summary>
public class V2GatewayHookTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[]? b) => b is null ? "<dropped>" : Encoding.UTF8.GetString(b);

    private const string Gateway = """
        import { gateway } from 'astra';
        export default gateway.defineHooks({
          guard: {
            onRequest(req, ctx) {
              const words = ctx.shared.words || [];
              const text = JSON.stringify(req);
              for (const w of words) if (text.includes(w)) return ctx.reject(400, 'has ' + w);
              ctx.count('seen');
              req.tagged = ctx.client + '/' + (ctx.token ? ctx.token.name : '-');
              return req;
            },
            onText(text, ctx) { ctx.holdBack(3); return text.replaceAll('secret', '******'); },
            onComplete(info, ctx) { ctx.emit('done', { status: info.status }); },
          },
        });
        """;

    private static JsonObject Manifest(params string[] permissions) => new()
    {
        ["gateway"] = "gateway.js",
        ["permissions"] = new JsonArray([.. permissions.Select(p => (JsonNode?)p)]),
        ["contributes"] = new JsonObject
        {
            ["gateway.hooks"] = new JsonArray(new JsonObject
            {
                ["id"] = "guard",
                ["on"] = new JsonArray("request", "text", "complete"),
                ["view"] = "ir",
                ["reads"] = new JsonArray("words"),
                ["match"] = new JsonObject { ["protocols"] = new JsonArray("chat", "anthropic") },
            }),
            ["events"] = new JsonArray(new JsonObject { ["name"] = "done" }),
        },
    };

    private static readonly string[] All =
        ["storage", "events", "gateway.request:rewrite", "gateway.request:reject", "gateway.response:rewrite"];

    private static async Task<(V2TestHost Host, MiddlewareEngine Engine)> SetupAsync(params string[] permissions)
    {
        var t = new V2TestHost();
        await t.Services.Data.SetAsync("acme.guard", "words", """["forbidden"]""");
        await t.AddAsync("acme.guard", Manifest(permissions), [("gateway.js", Gateway)]);
        var engine = new MiddlewareEngine(t.Plugins.Catalog, (_, _) => { }) { V2Host = t.Host };
        return (t, engine);
    }

    [Fact]
    public async Task A_hook_may_use_the_crypto_encoding_and_web_globals()
    {
        const string code = """
            import { gateway, crypto, encoding } from 'astra';
            export default gateway.defineHooks({
              guard: { onRequest(req) {
                req.md5 = crypto.digest('md5', 'abc');
                req.b64 = encoding.base64.encode(new TextEncoder().encode('hé')) + ':' + btoa('hi') + ':' + new URL('/x?y=1', 'https://a.example').href;
                return req;
              }, onText(t) { return t; }, onComplete() {} },
            });
            """;
        await using var t = new V2TestHost();
        await t.Services.Data.SetAsync("acme.guard", "words", """["forbidden"]""");
        await t.AddAsync("acme.guard", Manifest(All), [("gateway.js", code)]);
        var engine = new MiddlewareEngine(t.Plugins.Catalog, (_, _) => { }) { V2Host = t.Host };
        var states = string.Join("|", (await engine.StatesAsync()).Select(x => x.Key + ":" + x.Value.Error + x.Value.LastError));
        var run0 = await engine.BeginAsync(new MiddlewareInfo("chat", "gpt", true, "/v1/chat/completions", "claude-code", "tk1", "laptop"));
        Assert.True(run0 is not null, states);
        using var run = run0!;
        var (body, reject) = run.Request(B("""{"messages":[]}"""));
        Assert.Null(reject);
        var json = JsonNode.Parse(S(body))!;
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", json["md5"]!.GetValue<string>());
        Assert.Equal("aMOp:aGk=:https://a.example/x?y=1", json["b64"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_hook_rewrites_rejects_from_shared_storage_and_counts()
    {
        var (t, engine) = await SetupAsync(All);
        await using var _ = t;
        var info = new MiddlewareInfo("chat", "gpt", true, "/v1/chat/completions", "claude-code", "tk1", "laptop");

        using (var run = (await engine.BeginAsync(info))!)
        {
            var (body, reject) = run.Request(B("""{"messages":[{"role":"user","content":"hi"}]}"""));
            Assert.Null(reject);
            Assert.Equal("claude-code/laptop", JsonNode.Parse(S(body))!["tagged"]!.GetValue<string>());
        }
        using (var run = (await engine.BeginAsync(info))!)
        {
            var (_, reject) = run.Request(B("""{"messages":[{"role":"user","content":"a forbidden word"}]}"""));
            Assert.Equal(new MiddlewareReject(400, "has forbidden"), reject);
        }
        Assert.Equal(1, t.Host.Gateway.Counters("acme.guard")["seen"]);

        // a write from the main context (storage.set) reaches the hooks without a reload
        t.Host.Gateway.StorageChanged("acme.guard", "words", "[\"hi\"]");
        using (var run = (await engine.BeginAsync(info))!)
            Assert.Equal(new MiddlewareReject(400, "has hi"), run.Request(B("""{"messages":[{"role":"user","content":"hi"}]}""")).Reject);
    }

    [Fact]
    public async Task Without_the_rewrite_and_reject_permissions_the_hook_changes_nothing()
    {
        var (t, engine) = await SetupAsync("storage", "events", "gateway.request:read", "gateway.response:read");
        await using var _ = t;
        using var run = (await engine.BeginAsync(new MiddlewareInfo("chat", "gpt", true, "/v1/chat/completions", null)))!;
        var input = """{"messages":[{"role":"user","content":"a forbidden word"}]}""";
        var (body, reject) = run.Request(B(input));
        Assert.Null(reject);
        Assert.Equal(input, S(body));
    }

    [Fact]
    public async Task A_hook_set_only_sees_requests_its_match_allows()
    {
        var (t, engine) = await SetupAsync(All);
        await using var _ = t;
        using var run = (await engine.BeginAsync(new MiddlewareInfo("gemini", "g", false, "/v1beta/models/g:generateContent", null)))!;
        var input = """{"contents":[{"parts":[{"text":"forbidden"}]}]}""";
        var (body, reject) = run.Request(B(input));
        Assert.Null(reject);
        Assert.Equal(input, S(body));
    }

    [Fact]
    public async Task OnText_rewrites_across_deltas_and_flushes_what_it_held_at_the_end()
    {
        var (t, engine) = await SetupAsync(All);
        await using var _ = t;
        using var run = (await engine.BeginAsync(new MiddlewareInfo("chat", "gpt", true, "/v1/chat/completions", null)))!;
        Assert.True(run.WantsEvents);

        static string Delta(string text) => "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }),
        }.ToJsonString() + "\n\n";

        var out1 = S(run.Event(B(Delta("my sec"))));
        var out2 = S(run.Event(B(Delta("ret plan"))));
        var end = S(run.Event(B("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")));
        string Text(string ev) => string.Concat(ev.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => JsonNode.Parse(e["data: ".Length..])!["choices"]![0]!["delta"]!["content"]?.GetValue<string>() ?? ""));
        Assert.Equal("my ****** plan", Text(out1) + Text(out2) + Text(end));
    }

    [Fact]
    public async Task OnComplete_hears_how_the_request_ended()
    {
        var (t, engine) = await SetupAsync(All);
        await using var _ = t;
        using var run = (await engine.BeginAsync(new MiddlewareInfo("chat", "gpt", false, "/v1/chat/completions", null)))!;
        Assert.True(run.WantsComplete);
        run.Complete(200, new JsonObject { ["input"] = 3 }, 120, null);
        var states = await engine.StatesAsync();
        Assert.Contains("onComplete", Assert.Single(states).Value.Hooks);
    }
}
