using Dapper;
using Astra.Core.Requests;
using Astra.Data.Tests;

namespace Astra.Data;

/// <summary>Spans and agent links written with their request rows, and the tracing read side (.plan/observability.md §2, §4).</summary>
public class TraceRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static RequestRecord Call(string id, DateTimeOffset at, string turn, string? parent, int step, string provider = "copilot",
        string status = RequestStatus.Success, long totalMs = 1000, params TraceSpan[] spans) => new()
    {
        Id = id,
        StartedAtUtc = at,
        ClientKind = "claude-code",
        ProviderId = provider,
        ProviderName = provider == "copilot" ? "GitHub Copilot" : "Anthropic",
        InboundProtocol = "anthropic",
        UpstreamProtocol = "openai-chat",
        RequestedModel = "claude-sonnet-5-5",
        Status = status,
        HttpStatus = status == RequestStatus.Success ? 200 : 502,
        TtfbMs = 300,
        TotalMs = totalMs,
        CostNanoUsd = 1_000,
        TraceId = "0af7651916cd43dd8448eb211c80319c",
        SessionKey = "cc:9f3e",
        ChainKey = "fp:main",
        TurnId = turn,
        ParentRequestId = parent,
        Step = step,
        CallKind = TraceCallKinds.Main,
        LinkSource = parent is null ? TraceLinkSources.NewTurn : TraceLinkSources.ToolCall,
        FinishReason = "tool_calls",
        ToolCalls = 2,
        Spans = [.. spans],
    };

    private static TraceSpan Span(string id, string? parent, string name, long start, long duration, string? attributes = null) => new()
    {
        SpanId = id,
        ParentSpanId = parent,
        Name = name,
        Kind = name == TraceSpanNames.Request ? TraceSpanKinds.Server : name == TraceSpanNames.Attempt ? TraceSpanKinds.Client : TraceSpanKinds.Internal,
        StartUs = start,
        DurationUs = duration,
        Status = TraceSpanStatus.Ok,
        AttributesJson = attributes,
    };

    [Fact]
    public async Task Spans_And_Links_Round_Trip_With_The_Request()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([
            Call("r1", T0, "r1", null, 1, spans:
            [
                Span("a0000000000000a1", null, TraceSpanNames.Request, 0, 1_000_000),
                Span("a0000000000000a2", "a0000000000000a1", TraceSpanNames.Privacy, 600, 2_000, """{"astra.privacy.hits":2}"""),
                Span("a0000000000000a3", "a0000000000000a1", TraceSpanNames.Attempt, 5_000, 620_000, """{"http.response.status_code":429,"astra.account.id":"gh-alice"}"""),
            ]),
        ]);

        var record = await db.Requests.GetAsync("r1");
        Assert.NotNull(record);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", record!.TraceId);
        Assert.Equal("cc:9f3e", record.SessionKey);
        Assert.Equal("fp:main", record.ChainKey);
        Assert.Equal("r1", record.TurnId);
        Assert.Null(record.ParentRequestId);
        Assert.Equal(1, record.Step);
        Assert.Equal(TraceCallKinds.Main, record.CallKind);
        Assert.Equal(TraceLinkSources.NewTurn, record.LinkSource);
        Assert.Equal("tool_calls", record.FinishReason);
        Assert.Equal(2, record.ToolCalls);

        var spans = await db.Traces.ListSpansAsync("r1");
        Assert.Equal([TraceSpanNames.Request, TraceSpanNames.Privacy, TraceSpanNames.Attempt], spans.Select(s => s.Name));
        Assert.All(spans, s => Assert.Equal("r1", s.RequestId));
        Assert.Null(spans[0].ParentSpanId);
        Assert.Equal("a0000000000000a1", spans[1].ParentSpanId);
        Assert.Equal(620_000, spans[2].DurationUs);
        Assert.Equal(TraceSpanKinds.Client, spans[2].Kind);
        Assert.Contains("gh-alice", spans[2].AttributesJson);
    }

    [Fact]
    public async Task Retention_Removes_Spans_With_Their_Request()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([
            Call("old", T0, "old", null, 1, spans: [Span("b0000000000000b1", null, TraceSpanNames.Request, 0, 10)]),
            Call("new", T0.AddDays(2), "new", null, 1, spans: [Span("c0000000000000c1", null, TraceSpanNames.Request, 0, 10)]),
        ]);

        await db.Requests.DeleteOlderThanAsync(T0.AddDays(1));

        await using var conn = await db.Factory.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM trace_spans WHERE request_id = 'old'"));
        Assert.Single(await db.Traces.ListSpansAsync("new"));
    }

    [Fact]
    public async Task Sessions_Turns_And_Turn_Members_Aggregate()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([
            Call("t1a", T0, "t1a", null, 1, totalMs: 3_000),
            Call("t1b", T0.AddSeconds(5), "t1a", "t1a", 2, totalMs: 2_000),
            Call("t1c", T0.AddSeconds(9), "t1a", "t1b", 3, provider: "anthropic", status: RequestStatus.UpstreamError, totalMs: 500),
            Call("t2a", T0.AddMinutes(10), "t2a", null, 1, totalMs: 1_000),
        ]);

        var sessions = await db.Traces.ListSessionsAsync(T0.AddHours(-1), null, 20);
        var session = Assert.Single(sessions);
        Assert.Equal("cc:9f3e", session.SessionKey);
        Assert.Equal("claude-code", session.ClientKind);
        Assert.Equal(4, session.Requests);
        Assert.Equal(2, session.Turns);
        Assert.Equal(1, session.Errors);
        Assert.Equal(4_000, session.CostNanoUsd);
        Assert.Equal(T0, session.FirstAtUtc);
        Assert.Equal(T0.AddMinutes(10), session.LastAtUtc);
        Assert.Contains("anthropic", session.ProviderIds);
        Assert.Empty(await db.Traces.ListSessionsAsync(T0.AddHours(-1), "codex", 20));

        Assert.Equal(4, (await db.Traces.GetSessionAsync("cc:9f3e"))!.Requests);
        Assert.Null(await db.Traces.GetSessionAsync("cc:none"));

        var turns = await db.Traces.ListTurnsAsync("cc:9f3e");
        Assert.Equal(["t1a", "t2a"], turns.Select(t => t.TurnId));
        Assert.Equal(3, turns[0].Requests);
        Assert.Equal(1, turns[0].Errors);
        // first call at T0, last one starts at +9 s and runs 0.5 s
        Assert.Equal(9_500, turns[0].DurationMs, 0);

        var members = await db.Requests.ListByTurnAsync("t1a");
        Assert.Equal(["t1a", "t1b", "t1c"], members.Select(r => r.Id));
        Assert.Equal(["t1c"], (await db.Requests.ListChildrenAsync("t1b")).Select(r => r.Id));
    }

    [Fact]
    public async Task Topology_Counts_Attempts_Rate_Limits_And_Failovers()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([
            Call("x1", T0, "x1", null, 1, spans:
            [
                Span("d0000000000000d1", null, TraceSpanNames.Request, 0, 9_000_000),
                Span("d0000000000000d2", "d0000000000000d1", TraceSpanNames.Route, 0, 600),
                Span("d0000000000000d3", "d0000000000000d1", TraceSpanNames.Attempt, 4_800, 620_000, """{"http.response.status_code":429,"astra.account.id":"gh-alice"}"""),
                Span("d0000000000000d4", "d0000000000000d1", TraceSpanNames.Attempt, 625_000, 940_000, """{"http.response.status_code":200,"astra.account.id":"gh-bob"}"""),
            ]),
            Call("x2", T0.AddSeconds(30), "x1", "x1", 2, spans:
            [
                Span("e0000000000000e1", null, TraceSpanNames.Request, 0, 2_000_000),
                Span("e0000000000000e2", "e0000000000000e1", TraceSpanNames.Route, 0, 400),
                Span("e0000000000000e3", "e0000000000000e1", TraceSpanNames.Attempt, 3_000, 900_000, """{"http.response.status_code":200,"astra.account.id":"gh-bob"}"""),
            ]),
        ]);

        var data = await db.Traces.TopologyAsync(T0.AddHours(-1));

        var client = Assert.Single(data.Clients);
        Assert.Equal("claude-code", client.ClientKind);
        Assert.Equal(2, client.Requests);
        var provider = Assert.Single(data.Providers);
        Assert.Equal("copilot", provider.ProviderId);
        Assert.Equal(2, provider.Requests);
        Assert.Equal(3, provider.Attempts);
        Assert.Equal(1, provider.RateLimited);
        Assert.Equal(1, provider.Failovers);
        Assert.Equal([3_000L, 4_800L], data.Timings.Select(t => t.FirstAttemptUs!.Value).Order());
        Assert.Equal([400L, 600L], data.Stages.Where(s => s.Name == TraceSpanNames.Route).Select(s => s.DurationUs).Order());
        Assert.Equal(["gh-alice", "gh-bob"], data.AccountAttempts.Select(a => a.AccountId).Order());
    }

    [Fact]
    public async Task Provider_Stats_Count_Answers_And_Pool_Failovers_Per_Provider()
    {
        var db = await TestDb.InitializeAsync();
        static string Attempt(string provider, string? retry) =>
            $$"""{"astra.provider.id":"{{provider}}","astra.provider.name":"{{provider}}-name"{{(retry is null ? "" : $",\"astra.attempt.retry\":\"{retry}\"")}}}""";
        RequestRecord Pooled(string id, string provider, string token, params TraceSpan[] spans)
        {
            var r = Call(id, T0, id, null, 1, provider, spans: spans);
            r.TokenId = token;
            return r;
        }
        await db.Requests.InsertBatchAsync([
            // a failed, b answered (a pool failover)
            Pooled("r1", "b", "t1",
                Span("a0000000000000a1", null, TraceSpanNames.Attempt, 0, 10, Attempt("a", "provider_failover")),
                Span("a0000000000000a2", null, TraceSpanNames.Attempt, 10, 10, Attempt("b", null))),
            Pooled("r2", "b", "t1", Span("a0000000000000b1", null, TraceSpanNames.Attempt, 0, 10, Attempt("b", null))),
            Pooled("r3", "a", "t1", Span("a0000000000000c1", null, TraceSpanNames.Attempt, 0, 10, Attempt("a", null))),
            // another token: filtered out
            Pooled("r4", "a", "t2", Span("a0000000000000d1", null, TraceSpanNames.Attempt, 0, 10, Attempt("a", null))),
            // c only failed (never answered within the range)
            Pooled("r5", "b", "t1",
                Span("a0000000000000e1", null, TraceSpanNames.Attempt, 0, 10, Attempt("c", "provider_failover")),
                Span("a0000000000000e2", null, TraceSpanNames.Attempt, 10, 10, Attempt("b", null))),
        ]);

        var list = await db.Requests.ProviderStatsAsync(T0.AddMinutes(-1), T0.AddMinutes(1), tokenId: "t1");
        // Busiest first; a provider that only failed still shows.
        Assert.Equal(["b", "a", "c"], list.Select(s => s.ProviderId));
        var stats = list.ToDictionary(s => s.ProviderId);
        Assert.Equal((3L, 0L, 2L), (stats["b"].Requests, stats["b"].FailoversOut, stats["b"].FailoversIn));
        Assert.Equal((1L, 1L, 0L), (stats["a"].Requests, stats["a"].FailoversOut, stats["a"].FailoversIn));
        Assert.Equal((0L, 1L, 0L), (stats["c"].Requests, stats["c"].FailoversOut, stats["c"].FailoversIn));
        Assert.Equal("c-name", stats["c"].ProviderName);
        Assert.Equal(3_000, stats["b"].CostNanoUsd);
        Assert.Null(stats["b"].AvgTtftMs); // no TTFT measured in these rows

        var all = await db.Requests.ProviderStatsAsync(T0.AddMinutes(-1), T0.AddMinutes(1));
        Assert.Equal(2, all.Single(s => s.ProviderId == "a").Requests);
    }

    [Fact]
    public async Task Topology_Credits_A_Pool_Attempt_To_The_Provider_It_Went_To()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([
            Call("r1", T0, "r1", null, 1, "b", spans:
            [
                Span("a0000000000000f1", null, TraceSpanNames.Attempt, 0, 10,
                    """{"astra.provider.id":"a","astra.provider.name":"Alpha","http.response.status_code":429,"astra.attempt.retry":"provider_failover"}"""),
                Span("a0000000000000f2", null, TraceSpanNames.Attempt, 10, 10, """{"astra.provider.id":"b","http.response.status_code":200}"""),
            ]),
        ]);
        var data = await db.Traces.TopologyAsync(T0.AddHours(-1));
        var a = data.Providers.Single(p => p.ProviderId == "a");
        Assert.Equal((0L, 1L, 1L, "Alpha"), (a.Requests, a.Attempts, a.RateLimited, a.ProviderName));
        var b = data.Providers.Single(p => p.ProviderId == "b");
        Assert.Equal((1L, 1L, 0L, 1L), (b.Requests, b.Attempts, b.RateLimited, b.Failovers));
    }
}
