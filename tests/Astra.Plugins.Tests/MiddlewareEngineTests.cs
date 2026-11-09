using System.Text;
using Astra.Plugins.Middleware;

namespace Astra.Plugins.Tests;

/// <summary>magpie's gateway middleware contract (internal/middleware/run.go), hook by hook.</summary>
public class MiddlewareEngineTests
{
    private static readonly MiddlewareInfo Anthropic = new("anthropic", "claude-sonnet", true, "/v1/messages", "claude-code");
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[]? b) => b is null ? "<dropped>" : Encoding.UTF8.GetString(b);

    private static async Task<(MiddlewareEngine Engine, List<string> Log, TestPlugins Plugins)> EngineAsync(
        params (string Name, string Code, string? Options)[] middlewares)
    {
        var plugins = new TestPlugins();
        foreach (var (name, code, options) in middlewares)
            await plugins.AddAsync(plugins.Folder(name, ("package.json", $$$"""{"name":"{{{name}}}","magpie":{"middleware":"./m.middleware.js"}}"""), ("m.middleware.js", code)), options);
        var log = new List<string>();
        return (new MiddlewareEngine(plugins.Catalog, (_, line) => { lock (log) log.Add(line); }), log, plugins);
    }

    [Fact]
    public async Task No_middleware_is_no_run()
    {
        var (engine, _, _) = await EngineAsync();
        Assert.Null(await engine.BeginAsync(Anthropic));
    }

    [Fact]
    public async Task Word_guard_from_magpie_rejects_a_word_in_the_last_user_message()
    {
        var plugins = new TestPlugins();
        await plugins.AddAsync(plugins.Fixture("word-guard"), """{"words":["project-x"],"action":"reject","check":"last"}""");
        var engine = new MiddlewareEngine(plugins.Catalog, (_, _) => { });

        using var run = (await engine.BeginAsync(Anthropic))!;
        var (_, reject) = run.Request(B("""{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"ship Project-X now"}]}]}"""));

        Assert.Equal(new MiddlewareReject(400, "this request has \"Project-X\", which this gateway doesn't send"), reject);
    }

    [Fact]
    public async Task Word_guard_masks_requests_and_streamed_replies_in_each_api()
    {
        var plugins = new TestPlugins();
        await plugins.AddAsync(plugins.Fixture("word-guard"), """{"words":["secret"],"action":"mask","mask":"***","replies":true}""");
        var engine = new MiddlewareEngine(plugins.Catalog, (_, _) => { });

        using (var run = (await engine.BeginAsync(new MiddlewareInfo("chat", "gpt", true, "/v1/chat/completions", null)))!)
        {
            var (body, reject) = run.Request(B("""{"messages":[{"role":"user","content":"my secret plan"}]}"""));
            Assert.Null(reject);
            Assert.Equal("""{"messages":[{"role":"user","content":"my *** plan"}]}""", S(body));
            Assert.Equal("data: {\"choices\":[{\"delta\":{\"content\":\"a *** b\"}}]}\n\n",
                S(run.Event(B("data: {\"choices\":[{\"delta\":{\"content\":\"a secret b\"}}]}\n\n"))));
        }
        using (var run = (await engine.BeginAsync(Anthropic))!)
        {
            Assert.Equal("event: content_block_delta\r\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"*** sauce\"}}\r\n\r\n",
                S(run.Event(B("event: content_block_delta\r\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"secret sauce\"}}\r\n\r\n"))));
            var whole = """{"content":[{"type":"text","text":"the secret"}]}""";
            Assert.Equal("""{"content":[{"type":"text","text":"the ***"}]}""", S(run.Response(B(whole), 200)));
            Assert.Equal(whole, S(run.Response(B(whole), 500)));
        }
    }

    [Fact]
    public async Task Undefined_keeps_the_exact_bytes_and_an_object_replaces_them()
    {
        var (engine, _, _) = await EngineAsync(
            ("keep", "export function onRequest(b) {}", null),
            ("add", "export function onRequest(b) { b.added = true; return b }", null));
        using var run = (await engine.BeginAsync(Anthropic))!;

        var (body, _) = run.Request(B("{ \"spaced\" : 1 }"));

        Assert.Equal("""{"spaced":1,"added":true}""", S(body));
    }

    [Fact]
    public async Task Failures_leave_the_input_as_it_was_and_are_counted()
    {
        var (engine, log, plugins) = await EngineAsync(
            ("nulls", "export function onRequest() { return null }", null),
            ("number", "export function onRequest() { return 42 }", null),
            ("throws", "export function onRequest() { throw new RangeError('nope') }", null),
            ("loops", "export function onRequest() { while (true) {} }", null));
        using var run = (await engine.BeginAsync(Anthropic))!;

        var (body, reject) = run.Request(B("""{"a":1}"""));

        Assert.Equal("""{"a":1}""", S(body));
        Assert.Null(reject);
        var states = await engine.StatesAsync();
        var specs = (await plugins.Store.ListAsync()).Select(p => p.Spec).ToList();
        Assert.Equal("onRequest: dropped", states[specs[0]].LastError);
        Assert.Equal("onRequest: returned neither an object, null nor undefined", states[specs[1]].LastError);
        Assert.Equal("onRequest: RangeError: nope", states[specs[2]].LastError);
        Assert.Equal("onRequest: took too long", states[specs[3]].LastError);
        Assert.All(states.Values, s => Assert.Equal(1, s.Failures));
        Assert.Contains(log, l => l.Contains("middleware throws [error]: onRequest: RangeError: nope"));
    }

    [Fact]
    public async Task The_first_reject_wins_and_stops_the_chain()
    {
        var (engine, _, _) = await EngineAsync(
            ("first", "export function onRequest(b, ctx) { ctx.reject(999); ctx.reject(451, 'second call') }", null),
            ("never", "export function onRequest(b) { b.reached = true; return b }", null));
        using var run = (await engine.BeginAsync(Anthropic))!;

        var (body, reject) = run.Request(B("{}"));

        Assert.Equal(new MiddlewareReject(403, "turned away by a gateway middleware"), reject);
        Assert.Equal("{}", S(body));
    }

    [Fact]
    public async Task Ctx_carries_the_request_and_state_is_shared_by_one_requests_hooks()
    {
        var (engine, _, _) = await EngineAsync(("ctx", """
            export function onRequest(b, ctx) {
              ctx.state.n = (ctx.state.n || 0) + 1
              b.ctx = { protocol: ctx.protocol, model: ctx.model, stream: ctx.stream, path: ctx.path, agent: ctx.agent, options: ctx.options, hasStatus: 'status' in ctx }
              return b
            }
            export function onResponse(b, ctx) { b.n = ctx.state.n; b.status = ctx.status; return b }
            """, """{"x":1}"""));

        using (var run = (await engine.BeginAsync(Anthropic))!)
        {
            Assert.Equal("""{"ctx":{"protocol":"anthropic","model":"claude-sonnet","stream":true,"path":"/v1/messages","agent":"claude-code","options":{"x":1},"hasStatus":false}}""",
                S(run.Request(B("{}")).Body));
            Assert.Equal("""{"n":1,"status":201}""", S(run.Response(B("{}"), 201)));
        }
        using (var run = (await engine.BeginAsync(new MiddlewareInfo("gemini", null, false, "/v1beta/models/x", null)))!)
        {
            Assert.Equal("""{"ctx":{"protocol":"gemini","model":"","stream":false,"path":"/v1beta/models/x","options":{"x":1},"hasStatus":false}}""",
                S(run.Request(B("{}")).Body));
            Assert.Equal("""{"n":1,"status":200}""", S(run.Response(B("{}"), 200)));
        }
    }

    [Fact]
    public async Task Module_globals_persist_across_requests_on_a_pooled_engine()
    {
        var (engine, _, _) = await EngineAsync(("count", "let calls = 0\nexport function onRequest(b) { b.calls = ++calls; return b }", null));

        for (var i = 1; i <= 3; i++)
        {
            using var run = (await engine.BeginAsync(Anthropic))!;
            Assert.Equal($$"""{"calls":{{i}}}""", S(run.Request(B("{}")).Body));
        }
    }

    [Fact]
    public async Task Events_filter_drop_and_pass_through()
    {
        var (engine, _, _) = await EngineAsync(
            ("named", "export const events = ['message_delta']\nexport function onEvent(e) { e.seen = true; return e }", null),
            ("dropper", "export function onEvent(e) { return e.type === 'ping' ? null : undefined }", null));
        using var run = (await engine.BeginAsync(Anthropic))!;

        Assert.Equal("event: message_delta\ndata: {\"type\":\"message_delta\",\"seen\":true}\n\n",
            S(run.Event(B("event: message_delta\ndata: {\"type\":\"message_delta\"}\n\n"))));
        Assert.Equal("event: other\ndata: {\"type\":\"other\"}\n\n", S(run.Event(B("event: other\ndata: {\"type\":\"other\"}\n\n"))));
        Assert.Null(run.Event(B("event: ping\ndata: {\"type\":\"ping\"}\n\n")));
        // a Chat event has no event: line, so a middleware naming events never sees it
        Assert.Equal("data: {\"choices\":[]}\n\n", S(run.Event(B("data: {\"choices\":[]}\n\n"))));
        Assert.Equal("data: [DONE]\n\n", S(run.Event(B("data: [DONE]\n\n"))));
        Assert.Equal(": comment\n\n", S(run.Event(B(": comment\n\n"))));
        Assert.Equal("data: {\"a\":1}\ndata: {\"b\":2}\n\n", S(run.Event(B("data: {\"a\":1}\ndata: {\"b\":2}\n\n"))));
    }

    [Fact]
    public async Task Off_plugins_and_load_errors()
    {
        var (engine, _, plugins) = await EngineAsync(
            ("bare", "import x from 'lodash'\nexport function onRequest(b) { return b }", null),
            ("none", "export const nothing = 1", null));
        await plugins.AddAsync(plugins.Folder("off", ("package.json", """{"magpie":{"middleware":"./m.middleware.js"}}"""), ("m.middleware.js", "export function onRequest(b){}")), enabled: false);

        Assert.Null(await engine.BeginAsync(Anthropic));
        var states = (await engine.StatesAsync()).Values.Select(s => s.Error).Order().ToList();
        Assert.Equal(["\"lodash\": a middleware imports only files beside it; bundle packages into it", "exports none of onRequest, onEvent, onResponse and onUpstream"], states);
    }

    private static readonly MiddlewareUpstream ToUpstream = new("anthropic", "prov_1", "Channel", "claude-sonnet-4-5", true);

    [Fact]
    public async Task On_upstream_sees_where_the_attempt_goes_and_sets_headers()
    {
        var (engine, log, plugins) = await EngineAsync(("cc", """
            export function onUpstream(b, ctx) {
              if (ctx.upstream.provider.id !== 'prov_1' || ctx.upstream.protocol !== 'anthropic') return
              ctx.setHeader('User-Agent', 'claude-cli/9.9.9 (external, cli)')
              ctx.setHeader('x-app', 'cli')
              ctx.removeHeader('x-stale')
              ctx.setHeader('Authorization', 'Bearer stolen')
              ctx.setHeader('bad name', 'x')
              ctx.setHeader('x-split', 'a\r\nb')
              if (ctx.sha256([97, 98, 99]) !== ctx.sha256('abc')) throw new Error('bytes and text disagree')
              b.system = ctx.sha256('abc') + '/' + ctx.upstream.model + '/' + ctx.upstream.stream + '/' + ctx.upstream.provider.name
              return b
            }
            """, null));
        using var run = (await engine.BeginAsync(Anthropic))!;
        Assert.True(run.WantsUpstream);

        var (body, headers) = run.Upstream(ToUpstream, B("""{"model":"claude-sonnet-4-5"}"""));

        Assert.Equal("""{"model":"claude-sonnet-4-5","system":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad/claude-sonnet-4-5/true/Channel"}""", S(body));
        Assert.Equal([new("User-Agent", "claude-cli/9.9.9 (external, cli)"), new("x-app", "cli"), new MiddlewareHeader("x-stale", null)], headers);
        Assert.Contains(log, l => l.Contains("ctx.setHeader(Authorization) ignored: a header the gateway keeps"));
        Assert.Contains(log, l => l.Contains("ctx.setHeader(bad name) ignored: not a header name"));
        Assert.Contains(log, l => l.Contains("ctx.setHeader(x-split) ignored: not a header value"));
        Assert.Equal(["onUpstream"], (await engine.StatesAsync())[(await plugins.Store.ListAsync()).Single().Spec].Hooks);

        // another channel: the plugin leaves it alone
        var (other, none) = run.Upstream(ToUpstream with { ProviderId = "prov_2" }, B("""{"a":1}"""));
        Assert.Null(other);
        Assert.Empty(none);
    }

    [Fact]
    public async Task A_failing_on_upstream_keeps_neither_its_body_nor_its_headers_and_setters_do_nothing_elsewhere()
    {
        var (engine, log, _) = await EngineAsync(
            ("throws", "export function onUpstream(b, ctx) { ctx.setHeader('x-a', '1'); b.x = 1; throw new Error('late') }", null),
            ("nulls", "export function onUpstream(b, ctx) { ctx.setHeader('x-b', '1'); return null }", null),
            ("early", "export function onRequest(b, ctx) { ctx.setHeader('x-c', '1') }\nexport function onUpstream(b, ctx) { ctx.setHeader('x-d', '1') }", null));
        using var run = (await engine.BeginAsync(Anthropic))!;

        var (requestBody, _) = run.Request(B("""{"a":1}"""));
        var (body, headers) = run.Upstream(ToUpstream, B("""{"a":1}"""));

        Assert.Equal("""{"a":1}""", S(requestBody));
        Assert.Null(body);
        Assert.Equal([new MiddlewareHeader("x-d", "1")], headers);
        Assert.Contains(log, l => l.Contains("onUpstream: Error: late"));
        Assert.Contains(log, l => l.Contains("onUpstream: returned null"));
        Assert.Contains(log, l => l.Contains("ctx.setHeader works in onUpstream only"));
    }

    [Fact]
    public async Task A_changed_file_is_loaded_again()
    {
        var (engine, _, plugins) = await EngineAsync(("v", "export function onRequest(b) { b.v = 1; return b }", null));
        using (var run = (await engine.BeginAsync(Anthropic))!) Assert.Equal("""{"v":1}""", S(run.Request(B("{}")).Body));

        var file = Path.Combine(plugins.Home, "src", "v", "m.middleware.js");
        File.WriteAllText(file, "export function onRequest(b) { b.v = 2; return b }");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        await Task.Delay(1100);

        using (var run = (await engine.BeginAsync(Anthropic))!) Assert.Equal("""{"v":2}""", S(run.Request(B("{}")).Body));
    }

    [Theory]
    [InlineData("a\n\nb", 1, 2)]
    [InlineData("a\r\n\r\nb", 1, 4)]
    [InlineData("a\r\n\r\nb\n\n", 1, 4)]
    [InlineData("a\n\nb\r\n\r\n", 1, 2)]
    [InlineData("partial\n", -1, 0)]
    public void Event_end(string text, int index, int length) => Assert.Equal((index, length), SseEventFramer.EventEnd(B(text)));

    [Fact]
    public void Split_keeps_what_surrounds_the_json()
    {
        var ev = B("id: 7\nevent: x\ndata:{\"a\":1}\nretry: 5\n\n");
        Assert.True(SseEventFramer.TrySplit(ev, out var name, out var start, out var end));
        Assert.Equal("x", name);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(ev[start..end]));
        Assert.Equal("id: 7\nevent: x\ndata:", Encoding.UTF8.GetString(ev[..start]));
    }
}
