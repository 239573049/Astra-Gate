using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Pages.Traces;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The Traces page's scenes (inventory §5.4): the topology with traffic in flight / idle / empty / error / loading / unsupported server and
/// with the inspector open, the sessions list (+ empty), a session as a canvas, as a timeline and with a call in flight, the recent
/// requests, a request's trace with a failed-then-retried attempt (+ inspector, in flight, not found). The default fixture has no trace
/// data, so the trace endpoints are answered by overrides built here (times relative to the fixture's frozen now).
/// </summary>
public static partial class Scenes
{
    static partial void AddTraces(Dictionary<string, Func<Scene>> d)
    {
        d["traces__topology"] = () => TracesScene(TracesData(), null, live: TracesLiveFrames(), waitLive: 4);
        d["traces__topology-idle"] = () => TracesScene(TracesData(), null);
        d["traces__topology-inspector"] = () => TracesScene(TracesData(), null, details: true,
            act: page => ((TopologyViewModel)page.View).Select("provider:prov_anthropic"));
        d["traces__empty"] = () => TracesScene(Fixtures().Override("/api/traces/topology", TracesTopology(empty: true)), null);
        d["traces__error"] = () => TracesScene(Fixtures().Override("/api/traces/topology", """{"error":"数据库暂时不可用"}""", 500), null,
            until: page => ((TopologyViewModel)page.View).IsError);
        d["traces__unsupported"] = () => TracesScene(Fixtures().Override("/api/traces/topology", """{"error":"no route: GET /api/traces/topology"}""", 404), null,
            until: page => ((TopologyViewModel)page.View).IsError);
        d["traces__loading"] = () => TracesScene(TracesData(), null, hang: "/api/traces/topology");

        d["traces__sessions"] = () => TracesScene(TracesData(), "/traces/sessions");
        d["traces__sessions-empty"] = () => TracesScene(TracesData().Override("/api/traces/sessions", "[]"), "/traces/sessions");
        d["traces__session-canvas"] = () => TracesScene(TracesData(), "/traces/sessions/cc%3Aabc");
        d["traces__session-inspector"] = () => TracesScene(TracesData(), "/traces/sessions/cc%3Aabc", details: true,
            act: page => ((SessionViewModel)page.View).Select("m3"));
        d["traces__session-timeline"] = () => TracesScene(TracesData(), "/traces/sessions/cc%3Aabc?view=timeline",
            act: page => ((SessionViewModel)page.View).SelectRow("m3"));
        d["traces__session-live"] = () => TracesScene(TracesData(active: true), "/traces/sessions/cc%3Aabc", live: TracesSessionLiveFrames(), waitLive: 1);

        d["traces__requests"] = () => TracesScene(TracesData(), "/traces/requests");
        d["traces__request"] = () => TracesScene(TracesData(), "/traces/requests/req_trace");
        d["traces__request-inspector"] = () => TracesScene(TracesData(), "/traces/requests/req_trace", details: true,
            act: page => ((RequestTraceViewModel)page.View).Select("attempt:0"));
        d["traces__request-inflight"] = () => TracesScene(TracesData(inFlight: true), "/traces/requests/req_trace");
        d["traces__request-notfound"] = () => TracesScene(Fixtures().Override("/api/traces/requests/req_gone", """{"error":"not found"}""", 404), "/traces/requests/req_gone",
            until: page => ((RequestTraceViewModel)page.View).IsNotFound);
    }

    /// <summary>The traces page over <paramref name="fixtures"/> at <paramref name="param"/> (a web path), with an optional live stream.</summary>
    private static Scene TracesScene(FixtureSet fixtures, string? param, string? live = null, int waitLive = 0, string? hang = null, bool details = false,
        Action<TracesPageViewModel>? act = null, Func<TracesPageViewModel, bool>? until = null)
    {
        // the 详情 switch is one app-wide setting: every scene states it
        TraceInspectorSwitch.Set(details);
        HttpMessageHandler handler = live is not null ? new LiveStreamHandler(fixtures.Handler(), live) : fixtures.Handler();
        if (hang is not null) handler = new PathHangingHandler(handler, hang);
        var api = FixtureApi(handler);
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Traces, param);
        var page = (TracesPageViewModel)shell.Navigator.Current;
        // the web capture runs in UTC
        page.Services.TimeZone = TimeZoneInfo.Utc;
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            if (hang is not null)
            {
                await Task.Delay(600);
                return;
            }
            if (until is not null)
                while (!until(page) && DateTime.UtcNow < deadline) await Task.Delay(20);
            else
            {
                await shell.Context.Queries.WhenIdleAsync();
                await page.WhenSettledAsync();
            }
            while (waitLive > 0 && page.Live.List.Count < waitLive && DateTime.UtcNow < deadline) await Task.Delay(20);
            act?.Invoke(page);
            await Task.Delay(150);
        });
    }

    // ------------------------------------------------------------------ fixture data

    private static string TracesAt(FixtureSet f, double ms) => f.Now.AddMilliseconds(ms).ToString("O");

    /// <summary>The default fixtures plus every trace endpoint the scenes ask for.</summary>
    private static FixtureSet TracesData(bool active = false, bool inFlight = false)
    {
        var f = Fixtures();
        f.Override("/api/traces/topology", TracesTopology(empty: false));
        f.Override("/api/traces/activity", TracesActivity(f).ToJsonString());
        f.Override("/api/traces/sessions", TracesSessions(f).ToJsonString());
        f.Override("/api/traces/sessions/cc%3Aabc", TracesSessionDetail(f, active).ToJsonString());
        f.Override("/api/traces/requests/req_trace", TracesRequest(f, inFlight).ToJsonString());
        return f;
    }

    private static string TracesTopology(bool empty)
    {
        if (empty)
            return """{"range":"live","windowMinutes":10,"gateway":{"requests":0,"errors":0,"inFlight":0,"overheadP50Us":null,"overheadP95Us":null,"failovers":0,"stages":[]},"clients":[],"providers":[]}""";
        return """
        {"range":"live","windowMinutes":10,
         "gateway":{"requests":128,"errors":3,"inFlight":2,"overheadP50Us":820,"overheadP95Us":2400,"failovers":2,
           "stages":[{"name":"gateway.route","p50Us":110,"p95Us":290,"samples":128},{"name":"gateway.privacy","p50Us":340,"p95Us":980,"samples":128},
                     {"name":"gateway.resolve","p50Us":160,"p95Us":420,"samples":128},{"name":"gateway.encode","p50Us":210,"p95Us":640,"samples":96}]},
         "clients":[{"clientKind":"claude-code","requests":84,"errors":2,"perMinute":8.4,"activeSessions":2},
                    {"clientKind":"codex","requests":40,"errors":1,"perMinute":4.0,"activeSessions":1},
                    {"clientKind":null,"requests":4,"errors":0,"perMinute":0.4,"activeSessions":0}],
         "providers":[{"providerId":"prov_anthropic","name":"Anthropic","icon":"anthropic","templateId":"anthropic","requests":90,"errors":2,"attempts":94,"rateLimited":3,"failovers":2,"perMinute":9.0,"ttfbP95Ms":1840,
                       "accounts":[{"id":"acc_a","name":"work","status":"ok","enabled":true,"cooldownUntilUtc":null,"seenAtUtc":null,"remainingRequests":"412","remainingTokens":null,"limitRequests":"500"},
                                   {"id":"acc_b","name":"personal","status":"cooldown","enabled":true,"cooldownUntilUtc":"2026-10-26T12:04:00Z","seenAtUtc":null,"remainingRequests":null,"remainingTokens":null,"limitRequests":null}]},
                      {"providerId":"prov_openai","name":"OpenAI","icon":"openai","templateId":"openai","requests":38,"errors":1,"attempts":38,"rateLimited":0,"failovers":0,"perMinute":3.8,"ttfbP95Ms":910,"accounts":[]}]}
        """;
    }

    /// <summary>The topology's activity feed: a stored call that asked for two tools (their arguments recorded) after a 429 on the cooling account.</summary>
    private static JsonArray TracesActivity(FixtureSet f) =>
    [
        new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["id"] = "act_1", ["startedAtUtc"] = TracesAt(f, -30_000), ["clientKind"] = "claude-code", ["providerId"] = "prov_anthropic", ["providerName"] = "Anthropic",
                ["inboundProtocol"] = "anthropic", ["upstreamProtocol"] = "anthropic", ["passthrough"] = true, ["requestedModel"] = "claude-sonnet-4-5",
                ["upstreamModel"] = "claude-sonnet-4-5", ["responseModel"] = null, ["systemModelId"] = "claude-sonnet-4-5", ["stream"] = true, ["status"] = "success",
                ["httpStatus"] = 200, ["ttftMs"] = 900, ["totalMs"] = 4_100, ["outputTps"] = null, ["totalInputTokens"] = 21_700, ["totalOutputTokens"] = 236,
                ["cacheReadTokens"] = 0, ["cacheWriteTokens"] = 0, ["reasoningTokens"] = 0, ["costNanoUsd"] = 0, ["usageSource"] = "reported",
                ["tokenId"] = "tok_default", ["tokenName"] = "default", ["accountId"] = "acc_a", ["remote"] = false, ["sessionKey"] = "cc:abc", ["turnId"] = "t3", ["callKind"] = "main",
            },
            ["step"] = 2, ["finishReason"] = "tool_calls", ["errorMessage"] = null, ["toolCallCount"] = 2,
            ["cause"] = new JsonObject { ["kind"] = "tool_result", ["toolResults"] = new JsonArray(new JsonObject { ["name"] = "Read", ["error"] = false, ["chars"] = 3_120 }), ["prompt"] = null },
            ["attempts"] = new JsonArray(
                new JsonObject { ["providerId"] = "prov_anthropic", ["providerName"] = "Anthropic", ["accountId"] = "acc_b", ["status"] = 429, ["retry"] = "rate_limited_failover", ["startMs"] = 4, ["endMs"] = 320 },
                new JsonObject { ["providerId"] = "prov_anthropic", ["providerName"] = "Anthropic", ["accountId"] = "acc_a", ["status"] = 200, ["startMs"] = 330, ["endMs"] = 1_300 }),
            ["reasoningMs"] = 900, ["reasoningChars"] = 288, ["textChars"] = null, ["reply"] = null,
            ["toolCalls"] = new JsonArray(
                new JsonObject { ["name"] = "Edit", ["id"] = "t1", ["arguments"] = """{"file_path":"README.md","old_string":"npm install -g astragate","new_string":"pnpm add -g astragate"}""" },
                new JsonObject { ["name"] = "Bash", ["id"] = "t2", ["arguments"] = """{"command":"grep -rn \"npm install\" docs/content","description":"查找文档里的 npm 安装命令"}""" }),
        },
    ];

    private static JsonArray TracesSessions(FixtureSet f) =>
    [
        new JsonObject
        {
            ["sessionKey"] = "cc:abc", ["clientKind"] = "claude-code", ["firstAtUtc"] = TracesAt(f, -1_800_000), ["lastAtUtc"] = TracesAt(f, -20_000),
            ["requests"] = 24, ["turns"] = 3, ["errors"] = 1, ["costNanoUsd"] = 412_000_000, ["inputTokens"] = 812_000, ["outputTokens"] = 24_000,
            ["providerIds"] = new JsonArray("prov_anthropic"), ["active"] = true,
        },
        new JsonObject
        {
            ["sessionKey"] = "codex:7f3e2a91", ["clientKind"] = "codex", ["firstAtUtc"] = TracesAt(f, -7_200_000), ["lastAtUtc"] = TracesAt(f, -3_000_000),
            ["requests"] = 11, ["turns"] = 2, ["errors"] = 0, ["costNanoUsd"] = 96_500_000, ["inputTokens"] = 210_000, ["outputTokens"] = 8_100,
            ["providerIds"] = new JsonArray("prov_openai"), ["active"] = false,
        },
        new JsonObject
        {
            ["sessionKey"] = "fp:9c1d55e0", ["clientKind"] = null, ["firstAtUtc"] = TracesAt(f, -20_000_000), ["lastAtUtc"] = TracesAt(f, -19_000_000),
            ["requests"] = 3, ["turns"] = 1, ["errors"] = 0, ["costNanoUsd"] = 1_200_000, ["inputTokens"] = 9_000, ["outputTokens"] = 600,
            ["providerIds"] = new JsonArray("prov_openai"), ["active"] = false,
        },
    ];

    private static JsonObject TracesCall(FixtureSet f, string id, double startMs, double? totalMs, Action<JsonObject>? extra = null)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["startedAtUtc"] = TracesAt(f, startMs), ["clientKind"] = "claude-code", ["providerId"] = "prov_anthropic", ["providerName"] = "Anthropic",
            ["accountId"] = "acc_a", ["accountName"] = "work", ["inboundProtocol"] = "anthropic", ["upstreamProtocol"] = "anthropic", ["passthrough"] = true,
            ["stream"] = true, ["requestedModel"] = "claude-sonnet-4-5", ["upstreamModel"] = "claude-sonnet-4-5", ["responseModel"] = "claude-sonnet-4-5",
            ["status"] = "success", ["httpStatus"] = 200, ["errorType"] = null, ["errorMessage"] = null, ["ttfbMs"] = 620, ["ttftMs"] = 1_400,
            ["totalMs"] = totalMs, ["generationMs"] = totalMs is { } t ? t - 1_400 : null, ["outputTps"] = 58.2, ["totalInputTokens"] = 42_000,
            ["totalOutputTokens"] = 900, ["cacheReadTokens"] = 38_000, ["reasoningTokens"] = 0, ["costNanoUsd"] = 18_000_000, ["upstreamRequestId"] = "msg_" + id,
            ["traceId"] = "4bf92f3577b34da6a3ce929d0e0e4736", ["parentSpanId"] = null, ["sessionKey"] = "cc:abc", ["chainKey"] = "fp:main", ["turnId"] = "t3",
            ["parentRequestId"] = null, ["step"] = 1, ["callKind"] = "main", ["linkSource"] = "new_turn", ["finishReason"] = "tool_use", ["toolCalls"] = 0,
            ["reasoningEffort"] = null, ["reasoningMode"] = null,
        };
        extra?.Invoke(o);
        return o;
    }

    private static JsonObject TracesSpan(string requestId, string name, long startUs, long durationUs, string status = "ok", JsonObject? attributes = null,
        JsonArray? events = null, string? message = null) => new()
    {
        ["requestId"] = requestId, ["spanId"] = $"{requestId}:{name}:{startUs}", ["parentSpanId"] = name == "gateway.request" ? null : $"{requestId}:root",
        ["name"] = name, ["kind"] = name == "gateway.request" ? "server" : name == "upstream.attempt" ? "client" : "internal",
        ["startUs"] = startUs, ["durationUs"] = durationUs, ["status"] = status, ["statusMessage"] = message, ["attributes"] = attributes, ["events"] = events,
    };

    /// <summary>A request that was rate limited on its first account and answered on the second (the failover the request view shows).</summary>
    private static JsonArray TracesFailoverSpans(string id) =>
    [
        TracesSpan(id, "gateway.request", 0, 8_420_000, attributes: new JsonObject { ["astra.request.messages"] = 42, ["astra.request.tools"] = 18, ["astra.request.system_chars"] = 21_400, ["gen_ai.request.max_tokens"] = 32_000 }),
        TracesSpan(id, "gateway.route", 0, 600, attributes: new JsonObject { ["astra.token.id"] = "tok_default", ["astra.client.kind"] = "claude-code" }),
        TracesSpan(id, "gateway.body", 100, 400, attributes: new JsonObject { ["astra.body.bytes"] = 182_000 }),
        TracesSpan(id, "gateway.privacy", 700, 2_000, attributes: new JsonObject { ["astra.privacy.hits"] = 2, ["astra.privacy.categories"] = "email", ["astra.privacy.rewritten"] = true, ["astra.privacy.restorable"] = 2 }),
        TracesSpan(id, "gateway.resolve", 2_800, 180, attributes: new JsonObject { ["astra.provider.name"] = "Anthropic", ["gen_ai.request.model"] = "claude-sonnet-4-5", ["astra.protocol.upstream"] = "anthropic" }),
        TracesSpan(id, "gateway.encode", 3_000, 900, attributes: new JsonObject { ["astra.passthrough"] = true, ["astra.protocol.inbound"] = "anthropic", ["astra.protocol.upstream"] = "anthropic", ["astra.body.bytes"] = 182_000 }),
        TracesSpan(id, "upstream.attempt", 5_000, 620_000, "error", new JsonObject
        {
            ["http.response.status_code"] = 429, ["astra.account.id"] = "acc_b", ["astra.provider.name"] = "Anthropic", ["astra.attempt.retry"] = "rate_limited_failover",
            ["astra.ratelimit.remaining-requests"] = "0", ["astra.ratelimit.reset-requests"] = "38s",
        }, message: "429 Too Many Requests"),
        TracesSpan(id, "upstream.attempt", 625_000, 940_000, attributes: new JsonObject { ["http.response.status_code"] = 200, ["astra.account.id"] = "acc_a", ["astra.provider.name"] = "Anthropic", ["astra.upstream.request_id"] = "msg_01XYZ" }),
        TracesSpan(id, "gateway.response", 1_565_000, 6_850_000, "ok", new JsonObject
        {
            ["astra.restore.placeholders"] = 2, ["astra.response.tool_names"] = "Read,Read,Grep", ["astra.response.events"] = 96, ["astra.response.reads"] = 61,
            ["astra.response.reasoning_chars"] = 1_840, ["astra.response.text_chars"] = 620, ["gen_ai.response.finish_reasons"] = "tool_use",
        }, new JsonArray(
            new JsonObject { ["name"] = "first_token", ["offset_us"] = 1_835_000 },
            new JsonObject { ["name"] = "reasoning_start", ["offset_us"] = 1_835_000 },
            new JsonObject { ["name"] = "reasoning_end", ["offset_us"] = 4_200_000 },
            new JsonObject { ["name"] = "text_start", ["offset_us"] = 4_300_000 },
            new JsonObject { ["name"] = "text_end", ["offset_us"] = 5_600_000 },
            new JsonObject { ["name"] = "tool_call", ["offset_us"] = 5_700_000, ["attributes"] = new JsonObject { ["name"] = "Read" } },
            new JsonObject { ["name"] = "tool_call", ["offset_us"] = 6_000_000, ["attributes"] = new JsonObject { ["name"] = "Grep" } })),
    ];

    private static JsonObject TracesSessionDetail(FixtureSet f, bool active)
    {
        // turn 3 of a Claude Code session: a main loop with tools between calls, a sub-agent chain off m2, a retried call (m3) and a failure (m5)
        var t0 = -60_000;
        var calls = new JsonArray(
            TracesCall(f, "m1", t0, 2_000, o => { o["toolCalls"] = 1; }),
            TracesCall(f, "m2", t0 + 2_500, 3_000, o => { o["step"] = 2; o["parentRequestId"] = "m1"; o["linkSource"] = "tool_call"; o["toolCalls"] = 1; }),
            TracesCall(f, "s1", t0 + 3_000, 1_500, o => { o["callKind"] = "sidechain"; o["chainKey"] = "fp:sub"; o["parentRequestId"] = "m2"; o["linkSource"] = "inferred"; o["requestedModel"] = "claude-haiku-4-5"; o["upstreamModel"] = "claude-haiku-4-5"; }),
            TracesCall(f, "s2", t0 + 4_700, 1_000, o => { o["callKind"] = "sidechain"; o["chainKey"] = "fp:sub"; o["parentRequestId"] = "s1"; o["linkSource"] = "inferred"; o["step"] = 2; o["requestedModel"] = "claude-haiku-4-5"; o["upstreamModel"] = "claude-haiku-4-5"; }),
            TracesCall(f, "m3", t0 + 7_000, 8_420, o => { o["step"] = 3; o["parentRequestId"] = "m2"; o["linkSource"] = "tool_call"; o["toolCalls"] = 3; o["totalInputTokens"] = 123_456; o["totalOutputTokens"] = 15_300; }),
            TracesCall(f, "m4", t0 + 17_000, 2_600, o => { o["step"] = 4; o["parentRequestId"] = "m3"; o["linkSource"] = "tool_call"; o["toolCalls"] = 1; }),
            TracesCall(f, "m5", t0 + 21_000, 900, o => { o["step"] = 5; o["parentRequestId"] = "m4"; o["linkSource"] = "tool_call"; o["status"] = "upstream_error"; o["httpStatus"] = 500; o["finishReason"] = null; o["costNanoUsd"] = 0; }));
        var spans = TracesFailoverSpans("m3");
        spans.Add(TracesSpan("m1", "gateway.response", 0, 1_900_000, attributes: new JsonObject { ["astra.response.tool_names"] = "Task" }));
        spans.Add(TracesSpan("m2", "gateway.response", 0, 3_000_000, events: new JsonArray(
            new JsonObject { ["name"] = "reasoning_start", ["offset_us"] = 100_000 }, new JsonObject { ["name"] = "reasoning_end", ["offset_us"] = 900_000 },
            new JsonObject { ["name"] = "tool_call", ["offset_us"] = 2_000_000, ["attributes"] = new JsonObject { ["name"] = "Bash" } })));
        spans.Add(TracesSpan("m4", "gateway.response", 0, 2_400_000, attributes: new JsonObject { ["astra.response.tool_names"] = "Edit" }));
        return new JsonObject
        {
            ["session"] = new JsonObject
            {
                ["sessionKey"] = "cc:abc", ["clientKind"] = "claude-code", ["firstAtUtc"] = TracesAt(f, -1_800_000), ["lastAtUtc"] = TracesAt(f, -20_000),
                ["requests"] = 24, ["turns"] = 3, ["errors"] = 1, ["costNanoUsd"] = 412_000_000, ["inputTokens"] = 812_000, ["outputTokens"] = 24_000,
                ["providerIds"] = new JsonArray("prov_anthropic"), ["active"] = active,
            },
            ["turns"] = new JsonArray(
                new JsonObject { ["turnId"] = "t1", ["startedAtUtc"] = TracesAt(f, -1_800_000), ["requests"] = 9, ["errors"] = 0, ["costNanoUsd"] = 150_000_000, ["durationMs"] = 64_000.0 },
                new JsonObject { ["turnId"] = "t2", ["startedAtUtc"] = TracesAt(f, -900_000), ["requests"] = 8, ["errors"] = 0, ["costNanoUsd"] = 140_000_000, ["durationMs"] = 51_000.0 },
                new JsonObject { ["turnId"] = "t3", ["startedAtUtc"] = TracesAt(f, t0), ["requests"] = 7, ["errors"] = 1, ["costNanoUsd"] = 122_000_000, ["durationMs"] = 21_900.0 }),
            ["turnId"] = "t3",
            ["calls"] = calls,
            ["spans"] = spans,
        };
    }

    private static JsonObject TracesRequest(FixtureSet f, bool inFlight)
    {
        var call = TracesCall(f, "req_trace", -30_000, inFlight ? null : 8_420, o =>
        {
            o["step"] = 3;
            o["toolCalls"] = 3;
            if (!inFlight) return;
            o["status"] = "pending";
            o["httpStatus"] = null;
            o["costNanoUsd"] = 0;
        });
        JsonArray spans = inFlight
            ? [TracesSpan("req_trace", "gateway.route", 0, 600), TracesSpan("req_trace", "gateway.privacy", 700, 2_000)]
            : TracesFailoverSpans("req_trace");
        return new JsonObject
        {
            ["request"] = call, ["inFlight"] = inFlight, ["spans"] = spans,
            ["parent"] = TracesCall(f, "req_parent", -40_000, 3_000, o => o["step"] = 2),
            ["children"] = new JsonArray(),
        };
    }

    private static string TracesLiveRow(FixtureSet f, string id, double startMs, string status, string? providerId, int? http, string? stage, string? client = "claude-code",
        string? session = null, JsonArray? attempts = null, JsonObject? cause = null)
        => "data: " + new JsonObject
        {
            ["type"] = "started",
            ["request"] = new JsonObject
            {
                ["id"] = id, ["startedAtUtc"] = TracesAt(f, startMs), ["clientKind"] = client, ["providerId"] = providerId, ["providerName"] = providerId is null ? null : providerId == "prov_openai" ? "OpenAI" : "Anthropic",
                ["inboundProtocol"] = "anthropic", ["upstreamProtocol"] = "anthropic", ["passthrough"] = true, ["requestedModel"] = "claude-sonnet-4-5",
                ["upstreamModel"] = "claude-sonnet-4-5", ["responseModel"] = null, ["systemModelId"] = "claude-sonnet-4-5", ["stream"] = true, ["status"] = status,
                ["httpStatus"] = http, ["ttftMs"] = null, ["totalMs"] = null, ["outputTps"] = null, ["totalInputTokens"] = 0, ["totalOutputTokens"] = 0,
                ["cacheReadTokens"] = 0, ["cacheWriteTokens"] = 0, ["reasoningTokens"] = 0, ["costNanoUsd"] = 0, ["usageSource"] = "missing",
                ["tokenId"] = "tok_default", ["tokenName"] = "default", ["remote"] = false, ["sessionKey"] = session, ["stage"] = stage, ["turnId"] = session is null ? null : "t3",
                ["phases"] = stage is null ? null : "thinking@600",
                ["attempts"] = attempts, ["cause"] = cause,
            },
        }.ToJsonString() + "\n\n";

    /// <summary>
    /// Four requests in flight on the topology lines: a user prompt routed to the work account and waiting, one streaming back on it, one slow on
    /// the upstream, and a tool result the cooling account just refused (429) on its retry to the work account.
    /// </summary>
    private static string TracesLiveFrames()
    {
        var f = Fixtures();
        static JsonObject Attempt(string account, int? status, string? retry, int start, int? end) =>
            new() { ["providerId"] = "prov_anthropic", ["providerName"] = "Anthropic", ["accountId"] = account, ["status"] = status, ["retry"] = retry, ["startMs"] = start, ["endMs"] = end };
        return TracesLiveRow(f, "live_out", -1_200, "pending", "prov_anthropic", null, null,
                   attempts: [Attempt("acc_a", null, null, 300, null)], cause: new JsonObject { ["kind"] = "user", ["toolResults"] = new JsonArray() })
            + TracesLiveRow(f, "live_back", -3_400, "pending", "prov_anthropic", 200, "writing", attempts: [Attempt("acc_a", 200, null, 200, 900)])
            + TracesLiveRow(f, "live_slow", -11_000, "pending", "prov_openai", null, null, client: "codex")
            + TracesLiveRow(f, "live_retry", -1_000, "pending", "prov_anthropic", null, null,
                attempts: [Attempt("acc_b", 429, "rate_limited_failover", 100, 400), Attempt("acc_a", null, null, 500, null)],
                cause: new JsonObject { ["kind"] = "tool_result", ["toolResults"] = new JsonArray(new JsonObject { ["name"] = "Edit", ["error"] = false, ["chars"] = 40 }) });
    }

    /// <summary>The session's next call in flight (it thinks), drawn after its last step.</summary>
    private static string TracesSessionLiveFrames()
    {
        var f = Fixtures();
        return TracesLiveRow(f, "live_m6", -4_000, "pending", "prov_anthropic", 200, "thinking", session: "cc:abc");
    }
}
