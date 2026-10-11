using System.Diagnostics;
using System.Text;
using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins.Js;
using Astra.Plugins.Middleware;
using Jint;
using Jint.Native;

namespace Astra.Plugins.Diagnostics;

/// <summary>
/// Exercises the in-process JavaScript runtime end to end: what <c>astra-server plugin-selftest</c>
/// runs on a published binary, because Native AOT can break at run time what the JIT build (and the
/// unit tests) never see. Each check writes its own small plugin into a temporary folder.
/// </summary>
public static class PluginSelfTest
{
    /// <summary>One check's outcome.</summary>
    public sealed record Result(string Name, bool Ok, string Detail);

    /// <summary>Runs every check; <paramref name="report"/> gets a line per check as it finishes.</summary>
    public static IReadOnlyList<Result> Run(Action<Result>? report = null)
    {
        var dir = Directory.CreateTempSubdirectory("astra-plugin-selftest-").FullName;
        var results = new List<Result>();
        void Check(string name, Func<string> body)
        {
            Result r;
            try
            {
                r = new Result(name, true, body());
            }
            catch (Exception e)
            {
                r = new Result(name, false, e.GetType().Name + ": " + e.Message);
            }
            results.Add(r);
            report?.Invoke(r);
        }

        try
        {
            Write(dir, "mw/lib/words.js", "export const words = ['secret']\nexport function count(s) { return (s.match(/secret/gi) || []).length }");
            Write(dir, "mw/guard.middleware.js", """
                import { words, count } from './lib/words.js'
                let calls = 0
                export const events = ['content_block_delta']
                export function onRequest(body, ctx) {
                  calls++
                  ctx.state.seen = (ctx.state.seen || 0) + 1
                  if (count(JSON.stringify(body)) > 0 && ctx.options.action === 'reject') ctx.reject(400, 'has ' + words[0])
                  body.calls = calls
                  return body
                }
                export async function onResponse(body, ctx) { body.status = ctx.status; body.seen = ctx.state.seen; return body }
                export default { onEvent(ev) { return ev.type === 'drop' ? null : undefined } }
                """);
            Write(dir, "mw/bare.middleware.js", "import x from 'lodash'\nexport function onRequest(b) { return b }");
            Write(dir, "mw/escape.middleware.js", "import x from '../outside.js'\nexport function onRequest(b) { return b }");
            Write(dir, "outside.js", "export default 1");
            Write(dir, "mw/log.middleware.js", "console.log('hi', {a: 1})\nconsole.warn(2)\nexport function onRequest(b) { return b }");
            Write(dir, "mw/slow.middleware.js", "export function onRequest(b) { while (true) {} }\nexport function onEvent(e) { return e }\nexport function pending() { return new Promise(() => {}) }\nexport function deep(n) { return deep(n + 1) }\nexport function boom() { throw new TypeError('bad thing') }");

            var root = Path.Combine(dir, "mw");
            var loader = new JsModuleLoader(root, Path.Combine(root, "guard.middleware.js"), "a middleware");
            var logs = new List<string>();
            JsRuntime NewRuntime(JsModuleLoader l)
            {
                var rt = new JsRuntime(l, (level, line) => logs.Add(level + ":" + line));
                rt.Load(TimeSpan.FromSeconds(1));
                return rt;
            }

            JsRuntime guard = null!;
            Check("esm relative import", () =>
            {
                guard = NewRuntime(loader);
                return Has(guard, "onRequest") && Has(guard, "onResponse") && Has(guard, "onEvent") ? "hooks found (named and default)" : throw Fail("hooks missing");
            });
            Check("events export", () =>
            {
                var json = guard.ToJson(guard.Export("events"));
                return json == "[\"content_block_delta\"]" ? json : throw Fail(json ?? "null");
            });
            Check("json round trip and module state", () =>
            {
                var ctx = Ctx(guard, "{}");
                var first = guard.ToJson(guard.Call(guard.Export("onRequest"), Limit, guard.ParseJson("{\"model\":\"m\",\"n\":1.5,\"u\":\"é\\u2028\"}"), ctx));
                var second = guard.ToJson(guard.Call(guard.Export("onRequest"), Limit, guard.ParseJson("{}"), ctx));
                return first == "{\"model\":\"m\",\"n\":1.5,\"u\":\"é\u2028\",\"calls\":1}" && second == """{"calls":2}"""
                    ? first + " / " + second : throw Fail(first + " / " + second);
            });
            Check("ctx.reject from a host function", () =>
            {
                (int, string)? rejected = null;
                var ctx = Ctx(guard, """{"action":"reject"}""");
                ctx.FastSetDataProperty("reject", guard.Function("reject", 2, args =>
                {
                    rejected ??= ((int)args[0].AsNumber(), args[1].AsString());
                    return JsValue.Undefined;
                }));
                guard.Call(guard.Export("onRequest"), Limit, guard.ParseJson("""{"text":"a SECRET"}"""), ctx);
                return rejected is (400, "has secret") ? "400 has secret" : throw Fail(rejected?.ToString() ?? "not rejected");
            });
            Check("async hook settles", () =>
            {
                var ctx = Ctx(guard, "{}");
                ctx.FastSetDataProperty("status", 201);
                ctx.Get("state").AsObject().FastSetDataProperty("seen", 3);
                var json = guard.ToJson(guard.Call(guard.Export("onResponse"), Limit, guard.ParseJson("{}"), ctx));
                return json == """{"status":201,"seen":3}""" ? json : throw Fail(json ?? "null");
            });
            Check("null return", () =>
            {
                var v = guard.Call(guard.Export("onEvent"), Limit, guard.ParseJson("""{"type":"drop"}"""), Ctx(guard, "{}"));
                return v.IsNull() ? "null" : throw Fail(guard.Text(v));
            });
            Check("bare import refused", () => ExpectLoadError(loader: new JsModuleLoader(root, Path.Combine(root, "bare.middleware.js"), "a middleware"), "bundle packages into it"));
            Check("import outside the plugin refused", () => ExpectLoadError(new JsModuleLoader(root, Path.Combine(root, "escape.middleware.js"), "a middleware"), "outside the plugin"));

            var slow = NewRuntime(new JsModuleLoader(root, Path.Combine(root, "slow.middleware.js"), "a middleware"));
            Check("endless loop stopped, engine reusable", () =>
            {
                var sw = Stopwatch.StartNew();
                var err = Expect(() => slow.Call(slow.Export("onRequest"), TimeSpan.FromMilliseconds(50), slow.ParseJson("{}")));
                var took = sw.ElapsedMilliseconds;
                if (err != "took too long" || took > 1000) throw Fail($"{err} after {took}ms");
                var again = slow.ToJson(slow.Call(slow.Export("onEvent"), Limit, slow.ParseJson("""{"a":1}""")));
                return again == """{"a":1}""" ? $"stopped after {took}ms, next call ok" : throw Fail(again ?? "null");
            });
            Check("pending promise is a failure", () => ExpectError(() => slow.Call(slow.Export("pending"), Limit), "never settled"));
            Check("recursion limited", () => ExpectError(() => slow.Call(slow.Export("deep"), TimeSpan.FromSeconds(2), 0), "Recursion"));
            Check("thrown error text", () => ExpectError(() => slow.Call(slow.Export("boom"), Limit), "TypeError: bad thing"));
            Check("console", () =>
            {
                logs.Clear();
                NewRuntime(new JsModuleLoader(root, Path.Combine(root, "log.middleware.js"), "a middleware"));
                var got = string.Join(" | ", logs);
                return got == """info:hi {"a":1} | warn:2""" ? got : throw Fail(got);
            });

            Check("shared parse, per-engine state", () =>
            {
                var a = NewRuntime(loader);
                var b = NewRuntime(loader);
                var ra = a.ToJson(a.Call(a.Export("onRequest"), Limit, a.ParseJson("{}"), Ctx(a, "{}")));
                var rb = b.ToJson(b.Call(b.Export("onRequest"), Limit, b.ParseJson("{}"), Ctx(b, "{}")));
                return ra == """{"calls":1}""" && rb == """{"calls":1}""" ? "each engine counts its own calls" : throw Fail(ra + " / " + rb);
            });
            Check("middleware engine", () =>
            {
                var store = new OneStore(new PluginEntry { Spec = root, Name = "selftest", OptionsJson = """{"action":"reject"}""" });
                File.WriteAllText(Path.Combine(root, "package.json"), """{"name":"selftest","magpie":{"middleware":"./guard.middleware.js"}}""");
                var engine = new MiddlewareEngine(new PluginCatalog(store, new AstraPaths(Path.Combine(dir, "home"))), (_, _) => { });
                MiddlewareRun Begin() => engine.BeginAsync(new MiddlewareInfo("anthropic", "m", true, "/v1/messages", "claude-code"))
                    .AsTask().GetAwaiter().GetResult() ?? throw Fail("no run");
                MiddlewareReject? reject;
                using (var run = Begin()) (_, reject) = run.Request(Encoding.UTF8.GetBytes("""{"text":"a secret"}"""));
                using var ok = Begin();
                var (body, _) = ok.Request(Encoding.UTF8.GetBytes("""{"text":"fine"}"""));
                var dropped = ok.Event(Encoding.UTF8.GetBytes("event: content_block_delta\ndata: {\"type\":\"drop\"}\n\n"));
                var ev = dropped is null ? "" : Encoding.UTF8.GetString(dropped);
                return reject is (400, "has secret") && Encoding.UTF8.GetString(body).Contains("\"calls\":") && ev == ""
                    ? "reject, rewrite and drop through the engine" : throw Fail($"{reject} / {Encoding.UTF8.GetString(body)} / {ev}");
            });
            Check("performance", () =>
            {
                var ev = """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""" + "\"" + new string('x', 900) + "\"}}";
                var rt = NewRuntime(loader);
                var onEvent = rt.Export("onEvent");
                var ctx = Ctx(rt, "{}");
                for (var i = 0; i < 2000; i++) rt.ToJson(rt.Call(onEvent, Limit, rt.ParseJson(ev), ctx));
                var sw = Stopwatch.StartNew();
                const int n = 20000;
                for (var i = 0; i < n; i++)
                {
                    var r = rt.Call(onEvent, Limit, rt.ParseJson(ev), ctx);
                    if (!r.IsUndefined()) rt.ToJson(r);
                }
                var perEvent = sw.Elapsed.TotalMilliseconds * 1000 / n;

                var big = BigBody();
                var onRequest = rt.Export("onRequest");
                for (var i = 0; i < 20; i++) rt.ToJson(rt.Call(onRequest, Limit, rt.ParseJson(big), Ctx(rt, "{}")));
                sw.Restart();
                const int m = 200;
                for (var i = 0; i < m; i++) rt.ToJson(rt.Call(onRequest, Limit, rt.ParseJson(big), Ctx(rt, "{}")));
                var perRequest = sw.Elapsed.TotalMilliseconds / m;
                var detail = $"{perEvent:0.0}µs per 1KB event, {perRequest:0.00}ms per {big.Length / 1024}KB request";
                // typical: ~1-4µs and ~0.2-0.6ms; the budget only catches a pathological regression (an
                // interpreter path gone slow under AOT), not a busy CI machine
                return perEvent <= 100 && perRequest <= 20 ? detail : throw Fail(detail + " (budget 100µs / 20ms)");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return results;
    }

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(250);

    // A plugin list of one, for the engine check.
    private sealed class OneStore(PluginEntry entry) : IPluginListStore
    {
        public Task<IReadOnlyList<PluginEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PluginEntry>>([entry]);
        public Task<PluginEntry?> GetAsync(string spec, CancellationToken ct = default) => Task.FromResult<PluginEntry?>(spec == entry.Spec ? entry : null);
        public Task AddAsync(PluginEntry e, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAsync(string oldSpec, PluginEntry e, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetOptionsAsync(string spec, string? optionsJson, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetInstalledVersionAsync(string spec, string? version, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RemoveAsync(string spec, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> ListPreferencesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task SetPreferenceAsync(string providerId, string spec, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static bool Has(JsRuntime rt, string name) => rt.Export(name) is { } v && !v.IsUndefined();

    private static Jint.Native.JsObject Ctx(JsRuntime rt, string options)
    {
        var ctx = rt.NewObject();
        ctx.FastSetDataProperty("protocol", "anthropic");
        ctx.FastSetDataProperty("options", rt.ParseJson(options));
        ctx.FastSetDataProperty("state", rt.NewObject());
        return ctx;
    }

    private static string ExpectLoadError(JsModuleLoader loader, string contains) =>
        ExpectError(() => new JsRuntime(loader, (_, _) => { }).Load(TimeSpan.FromSeconds(1)), contains);

    private static string ExpectError(Action body, string contains)
    {
        var err = Expect(body);
        return err.Contains(contains, StringComparison.Ordinal) ? err : throw Fail(err);
    }

    private static string Expect(Action body)
    {
        try
        {
            body();
        }
        catch (JsHookException e)
        {
            return e.Message;
        }
        throw Fail("no error");
    }

    private static Exception Fail(string detail) => new InvalidOperationException(detail);

    private static void Write(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // About the size of a Claude Code request: a long system prompt, tools and a conversation.
    private static string BigBody()
    {
        var sb = new System.Text.StringBuilder("""{"model":"claude-sonnet","stream":true,"system":[{"type":"text","text":""");
        sb.Append('"').Append(new string('s', 20000)).Append("\"}],\"tools\":[");
        for (var i = 0; i < 40; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"tool").Append(i).Append("\",\"description\":\"").Append(new string('d', 600))
              .Append("\",\"input_schema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"number\"}}}}");
        }
        sb.Append("],\"messages\":[");
        for (var i = 0; i < 30; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"role\":\"").Append(i % 2 == 0 ? "user" : "assistant").Append("\",\"content\":[{\"type\":\"text\",\"text\":\"")
              .Append(new string('m', 1000)).Append("\"}]}");
        }
        return sb.Append("]}").ToString();
    }
}
