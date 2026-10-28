using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Requests;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The request log's scenes (inventory §5.4: data (live on / off), filter active, empty, loading, error, detail sheet in its variants, body
/// expand). The default fixture's live stream is an empty stream that ends, so <c>requests__data</c> is the web's capture (the stream is down and
/// the list polls); <c>requests__live-on</c> keeps the stream open with in-flight requests in it.
/// </summary>
public static partial class Scenes
{
    static partial void AddRequests(Dictionary<string, Func<Scene>> d)
    {
        // The web's two captures (scripts/visual/capture-web.mjs SCENES).
        d["requests__data"] = () => RequestsScene(Fixtures());
        d["requests__empty"] = () => RequestsScene(Fixtures().Override("/api/requests", EmptyPage));

        d["requests__loading"] = () => RequestsScene(Fixtures(), hang: "/api/requests", settle: _ => Task.Delay(600));
        // A failing list keeps polling every 5 s (and retries once after 1 s), so "no request in flight" never comes: wait for the error itself.
        d["requests__error"] = () => RequestsScene(Fixtures().Override("/api/requests", """{"error":"数据库暂时不可用"}""", 500), settle: async page =>
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!page.IsError && DateTime.UtcNow < until) await Task.Delay(20);
            await Task.Delay(100);
        });
        d["requests__filter-active"] = () => RequestsScene(Fixtures(), param: "status=success&model=gpt");
        d["requests__filter-empty"] = () => RequestsScene(Fixtures().Override("/api/requests", EmptyPage), param: "token=tok_ci&model=nothing");
        d["requests__paged"] = () => RequestsScene(WithList(Fixtures(), list => list["total"] = 230));
        d["requests__remote"] = () => RequestsScene(RemoteRows(Fixtures()));
        d["requests__live-on"] = () => LiveOn();
        d["requests__live-off"] = () => RequestsScene(Fixtures());

        d["requests__detail-ok"] = () => Detail("req_0049", f => Patch(f, "req_0049", Billing));
        d["requests__detail-error"] = () => Detail("req_0041", f => Patch(f, "req_0041", n =>
        {
            n["status"] = "upstream_error";
            n["httpStatus"] = 502;
            n["errorType"] = "upstream_error";
            n["errorMessage"] = "上游返回 502：Bad gateway while reading the response from api.openai.com (connection reset by peer)";
            n["usageSource"] = "missing";
            n["costNanoUsd"] = 0;
            n["totalInputTokens"] = 0;
            n["totalOutputTokens"] = 0;
            n["responseModel"] = null;
        }));
        d["requests__detail-streaming"] = () => Detail("req_live", f => Patch(f, "req_live", n =>
        {
            n["status"] = "pending";
            n["httpStatus"] = null;
            n["ttftMs"] = null;
            n["ttfbMs"] = null;
            n["totalMs"] = null;
            n["outputTps"] = null;
            n["responseModel"] = null;
            n["usageSource"] = "missing";
            n["costNanoUsd"] = 0;
            n["totalInputTokens"] = 0;
            n["totalOutputTokens"] = 0;
            n["cacheReadTokens"] = 0;
            n["reasoningTokens"] = 0;
        }));
        d["requests__detail-routing"] = () => Detail("req_0049", f => Patch(f, "req_0049", n =>
        {
            Billing(n);
            n["requestedModel"] = "astra-auto";
            n["routing"] = JsonNode.Parse("""{"grade":"high","model":"o4-mini","source":"judge","mode":"turn","confidence":0.91,"judgeMs":420}""");
            n["privacy"] = JsonNode.Parse("""{"dryRun":false,"blocked":false,"redactions":3,"hits":[{"ruleId":"email","category":"email","action":"redact","count":2,"samples":["a***@example.com"]},{"ruleId":"api-key","category":"api_key","action":"redact","count":1}]}""");
        }));
        d["requests__detail-fallback"] = () => Detail("req_0049", f => Patch(f, "req_0049", n =>
        {
            n["requestedModel"] = "astra-auto";
            n["routing"] = JsonNode.Parse("""{"grade":"low","model":"gpt-4.1-mini","source":"fallback","mode":"turn","fallbackReason":"timeout"}""");
        }));
        d["requests__body-expand"] = () => Detail("req_0049", f => Patch(f, "req_0049", Billing), expandRaw: 2);
        d["requests__body-truncated"] = () => Detail("req_0049", f => Patch(f, "req_0049", n =>
        {
            Billing(n);
            var bodies = n["bodies"]!;
            bodies["sizes"]!["clientRequest"] = 3_500_000;
            bodies["previewLimit"] = 1200;
        }), expandRaw: 2);
    }

    private const string EmptyPage = """{"items":[],"total":0,"page":1,"pageSize":50,"totalCostNanoUsd":0}""";

    private static readonly Uri Probe = new("http://fixture/");

    private static JsonNode FixtureJson(FixtureSet f, string path)
    {
        var hit = f.Answer("GET", new Uri(Probe, path)) ?? throw new InvalidOperationException($"no fixture for {path}");
        return JsonNode.Parse(hit.Body)!;
    }

    /// <summary>The detail of <paramref name="id"/> from the fixtures (or, for an id without one, request 50's) with <paramref name="patch"/> applied.</summary>
    private static FixtureSet Patch(FixtureSet f, string id, Action<JsonNode> patch)
    {
        var path = "/api/requests/" + id;
        var node = (f.Answer("GET", new Uri(Probe, path)) is { } hit ? JsonNode.Parse(hit.Body) : FixtureJson(f, "/api/requests/req_0050"))!;
        node["id"] = id;
        patch(node);
        return f.Override(path, node.ToJsonString());
    }

    private static FixtureSet WithList(FixtureSet f, Action<JsonNode> patch)
    {
        var node = FixtureJson(f, "/api/requests");
        patch(node);
        return f.Override("/api/requests", node.ToJsonString());
    }

    /// <summary>Some rows came in through remote access and the tunnel is on, so the origin filter and the globe marks show.</summary>
    private static FixtureSet RemoteRows(FixtureSet f)
    {
        WithList(f, list =>
        {
            foreach (var i in new[] { 1, 4, 7 })
            {
                var row = list["items"]![i]!;
                row["remote"] = true;
                row["clientIp"] = "203.0.113.7";
            }
        });
        var tunnel = FixtureJson(f, "/api/tunnel");
        tunnel["enabled"] = true;
        tunnel["state"] = "running";
        return f.Override("/api/tunnel", tunnel.ToJsonString());
    }

    /// <summary>Usage items, a price source, raw usage / pricing and captured bodies: what a stored request's detail carries.</summary>
    private static void Billing(JsonNode n)
    {
        n["pricingSource"] = "provider_price";
        n["priceKey"] = "openai/o4-mini";
        n["billingDescription"] = "输入 32,981 tokens（其中缓存命中 21,330）× $1.1 / 1M，输出 1,393 tokens × $4.4 / 1M。";
        n["cacheWriteTokens"] = 1200;
        n["usageItems"] = JsonNode.Parse("""
            [
              {"tokenType":"input","tokens":10451,"isPerCall":false,"unitPrice":"1.1","tierApplied":"base","costNanoUsd":11496100},
              {"tokenType":"cache_read","tokens":21330,"isPerCall":false,"unitPrice":"0.275","tierApplied":"base","costNanoUsd":5865750,"multipliers":["×0.25"]},
              {"tokenType":"cache_write_5m","tokens":1200,"isPerCall":false,"unitPrice":"1.375","tierApplied":"base","costNanoUsd":1650000},
              {"tokenType":"output","tokens":772,"isPerCall":false,"unitPrice":"4.4","tierApplied":"base","costNanoUsd":3396800,"pricedAs":"output"},
              {"tokenType":"reasoning","tokens":621,"isPerCall":false,"unitPrice":"4.4","tierApplied":"base","costNanoUsd":2732400,"pricedAs":"output"}
            ]
            """);
        n["usageRaw"] = JsonNode.Parse("""{"prompt_tokens":32981,"completion_tokens":1393,"prompt_tokens_details":{"cached_tokens":21330},"completion_tokens_details":{"reasoning_tokens":621}}""");
        n["pricingSnapshot"] = JsonNode.Parse("""{"base":{"input":1.1,"output":4.4,"cache_read":0.275}}""");
        n["bodies"] = JsonNode.Parse("""
            {
              "clientRequest":"{\"model\":\"o4-mini\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"Summarise the last release notes in three bullets.\"}]}",
              "upstreamRequest":"{\"model\":\"o4-mini\",\"input\":[{\"role\":\"user\",\"content\":\"Summarise the last release notes in three bullets.\"}],\"stream\":true}",
              "upstreamResponse":"data: {\"type\":\"response.output_text.delta\",\"delta\":\"- Faster startup\"}\n\n",
              "clientResponse":"data: {\"choices\":[{\"delta\":{\"content\":\"- Faster startup\"}}]}\n\n",
              "sizes":{"clientRequest":132,"upstreamRequest":151,"upstreamRequestHeaders":null,"upstreamResponse":78,"clientResponse":72},
              "previewLimit":65536
            }
            """);
    }

    private static Scene Detail(string id, Func<FixtureSet, FixtureSet> setup, int expandRaw = -1)
    {
        var fixtures = setup(Fixtures());
        return RequestsScene(fixtures, open: id, expandRaw: expandRaw);
    }

    /// <summary>
    /// The shell on the request log over <paramref name="fixtures"/>, with an optional hanging path, an open detail and an expanded raw section;
    /// <paramref name="extraSettle"/> runs after the queries answered (the live scene waits for its stream).
    /// </summary>
    private static Scene RequestsScene(FixtureSet fixtures, string? hang = null, string? open = null, int expandRaw = -1, Func<RequestsPageViewModel, Task>? settle = null,
        HttpMessageHandler? handler = null, Func<RequestsPageViewModel, Task>? extraSettle = null, string? param = null)
    {
        var shell = BuildRequestsShell(fixtures, hang, handler);
        shell.Navigator.Navigate(AppRoute.Requests, param);
        var page = (RequestsPageViewModel)shell.Navigator.Current;
        // The web capture runs in UTC (index.timeZone); the machine's zone would shift every time cell.
        page.TableContext.TimeZone = TimeZoneInfo.Utc;
        if (open is not null)
        {
            page.Open(open);
            if (expandRaw >= 0) page.Detail!.ExpandedRawIndex = expandRaw;
        }
        return new Scene(new ShellView { DataContext = shell }, settle is not null ? () => settle(page) : async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await page.WhenSettledAsync();
            if (extraSettle is not null) await extraSettle(page);
        });
    }

    private static ShellViewModel BuildRequestsShell(FixtureSet fixtures, string? hang, HttpMessageHandler? handler)
    {
        var api = FixtureApi(handler ?? (hang is null ? fixtures.Handler() : new PathHangingHandler(fixtures.Handler(), hang)));
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        return new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
    }

    /// <summary>Two requests in flight on an open live stream: one still waiting for its first token (the counter), one already streaming.</summary>
    private static Scene LiveOn()
    {
        var fixtures = Fixtures();
        var now = fixtures.Now;
        string Row(string id, DateTimeOffset at, string? ttft, string model, string token)
            => $$$"""
            {"type":"started","request":{"id":"{{{id}}}","startedAtUtc":"{{{at:O}}}","clientKind":"claude-code","providerId":"prov_anthropic","providerName":"Anthropic","inboundProtocol":"anthropic","upstreamProtocol":"anthropic","passthrough":true,"requestedModel":"{{{model}}}","upstreamModel":"{{{model}}}","responseModel":null,"systemModelId":"{{{model}}}","stream":true,"status":"pending","httpStatus":null,"ttftMs":{{{ttft ?? "null"}}},"totalMs":null,"outputTps":null,"totalInputTokens":{{{(ttft is null ? 0 : 4200)}}},"totalOutputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0,"costNanoUsd":0,"usageSource":"missing","tokenId":"tok_default","tokenName":"{{{token}}}","remote":false}}
            """;
        var frames = "data: " + Row("req_live2", now.AddMilliseconds(-5400), null, "claude-sonnet-4-5", "default") + "\n\n"
            + "data: " + Row("req_live1", now.AddMilliseconds(-1800), "640", "claude-haiku-4-5", "default") + "\n\n";
        var handler = new LiveStreamHandler(fixtures.Handler(), frames);
        return RequestsScene(fixtures, handler: handler, extraSettle: async page =>
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!(page.Live.Connected && page.Live.List.Count == 2) && DateTime.UtcNow < until) await Task.Delay(20);
            await Task.Delay(100);
        });
    }

    /// <summary>Serves the live stream from <c>frames</c> and keeps it open; everything else comes from the fixtures.</summary>
    private sealed class LiveStreamHandler(HttpMessageHandler inner, string frames) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/requests/live") return base.SendAsync(request, cancellationToken);
            var content = new StreamContent(new OpenStream(Encoding.UTF8.GetBytes(frames)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Yields its bytes once, then waits until cancelled: a server-sent-events connection with nothing more to say.</summary>
    private sealed class OpenStream(byte[] first) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
