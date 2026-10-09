using Dapper;
using Astra.Core.Requests;
using Astra.Data.Repositories;
using Astra.Data.Tests;

namespace Astra.Data;

public class RequestRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 2, 23, 10, 0, TimeSpan.Zero);

    private static RequestRecord Success(string id, DateTimeOffset at, string client, string provider,
        string providerName, string model, long costNano, long inTok, long outTok, long ttftMs, params RequestUsageItem[] items)
    {
        var record = new RequestRecord
        {
            Id = id,
            StartedAtUtc = at,
            ClientKind = client,
            ProviderId = provider,
            ProviderName = providerName,
            InboundProtocol = "openai-chat",
            UpstreamProtocol = "openai-chat",
            Passthrough = true,
            RequestedModel = model,
            UpstreamModel = model,
            SystemModelId = model,
            Stream = true,
            Status = RequestStatus.Success,
            HttpStatus = 200,
            TtftMs = ttftMs,
            TotalMs = ttftMs + 900,
            GenerationMs = 800,
            OutputTps = 42.5,
            TotalInputTokens = inTok,
            TotalOutputTokens = outTok,
            CostNanoUsd = costNano,
            UsageSource = "reported",
            UsageRawJson = "{\"prompt_tokens\":1}",
            PricingSource = "provider_price",
            PriceKey = provider,
            BillingDescription = "desc",
            UserAgent = "codex/1.0",
        };
        record.UsageItems.AddRange(items);
        return record;
    }

    private static List<RequestRecord> SampleBatch() =>
    [
        Success("01TEST0000000000000000000A", T0, "codex", "p1", "Alpha", "gpt-5", 1_000_000, 1000, 500, 200,
            new RequestUsageItem
            {
                TokenType = "input",
                Tokens = 1000,
                UnitPrice = "1.25",
                BaseUnitPrice = "1.25",
                TierApplied = "base",
                MultipliersJson = "{\"provider\":1.0}",
                CostNanoUsd = 1_000_000,
            },
            new RequestUsageItem
            {
                TokenType = "web_search_call",
                IsPerCall = true,
                Tokens = 1,
                UnitPrice = "0.01",
                PricedAs = "per_call",
                CostNanoUsd = 10_000_000 / 10,
                Note = "per-call item",
            }),
        Success("01TEST0000000000000000000B", T1, "claude-code", "p2", "Beta", "claude-x", 2_000_000, 2000, 1000, 400),
        new RequestRecord
        {
            Id = "01TEST0000000000000000000C",
            StartedAtUtc = T2,
            ClientKind = "codex",
            ProviderId = "p1",
            ProviderName = "Alpha",
            InboundProtocol = "openai-responses",
            RequestedModel = "gpt-5",
            Status = RequestStatus.GatewayError,
            HttpStatus = 500,
            ErrorType = "upstream",
            ErrorMessage = "boom",
        },
    ];

    [Fact]
    public async Task Batch_Insert_And_GetAsync_Return_Items()
    {
        var db = await TestDb.InitializeAsync();
        var records = SampleBatch();
        await db.Requests.InsertBatchAsync(records);

        var got = (await db.Requests.GetAsync("01TEST0000000000000000000A"))!;
        Assert.Equal(T0, got.StartedAtUtc);
        Assert.True(got.Passthrough);
        Assert.True(got.Stream);
        Assert.Equal(42.5, got.OutputTps);
        Assert.Equal("reported", got.UsageSource);
        Assert.Equal("provider_price", got.PricingSource);
        Assert.Equal(2, got.UsageItems.Count);

        var input = got.UsageItems[0];
        Assert.Equal("input", input.TokenType);
        Assert.Equal(1000, input.Tokens);
        Assert.False(input.IsPerCall);
        Assert.Equal("1.25", input.UnitPrice);
        Assert.Equal("1.25", input.BaseUnitPrice);
        Assert.Equal("base", input.TierApplied);
        Assert.Equal(1_000_000, input.CostNanoUsd);

        var perCall = got.UsageItems[1];
        Assert.True(perCall.IsPerCall);
        Assert.Equal("per_call", perCall.PricedAs);
        Assert.Equal("per-call item", perCall.Note);

        Assert.Null(await db.Requests.GetAsync("missing"));
    }

    [Fact]
    public async Task Query_Paging_Newest_First_With_Total()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());

        var page1 = await db.Requests.QueryAsync(new RequestQuery { Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.Total);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal("01TEST0000000000000000000C", page1.Items[0].Id); // newest first
        Assert.Equal("01TEST0000000000000000000B", page1.Items[1].Id);

        var page2 = await db.Requests.QueryAsync(new RequestQuery { Page = 2, PageSize = 2 });
        Assert.Single(page2.Items);
        Assert.Equal("01TEST0000000000000000000A", page2.Items[0].Id);
    }

    [Fact]
    public async Task Query_Filters_By_Time_Client_Provider_Model_Status()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());

        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery { ClientKind = "codex" })).Total);
        Assert.Equal(1, (await db.Requests.QueryAsync(new RequestQuery { ProviderId = "p2" })).Total);
        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery { Status = RequestStatus.Success })).Total);
        Assert.Equal(1, (await db.Requests.QueryAsync(new RequestQuery { Model = "claude-x" })).Total);
        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery { Model = "gpt-5" })).Total); // success + error rows

        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery { From = T1 })).Total);
        Assert.Equal(1, (await db.Requests.QueryAsync(new RequestQuery { To = T0 })).Total);
        // Range endpoints are inclusive: T0 and T1 rows both qualify.
        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery { From = T0, To = T1 })).Total);

        var combined = await db.Requests.QueryAsync(new RequestQuery
        {
            ClientKind = "codex",
            Status = RequestStatus.Success,
        });
        Assert.Equal(1, combined.Total);
        Assert.Equal("01TEST0000000000000000000A", combined.Items[0].Id);
    }

    [Fact]
    public async Task Summary_Counts_Cost_Tokens_And_Average_Ttft()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());

        var summary = await db.Requests.SummaryAsync(T0.AddMinutes(-1), T2.AddMinutes(1));
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(2, summary.SuccessRequests);
        Assert.Equal(2.0 / 3.0, summary.SuccessRate, 6);
        Assert.Equal(3_000_000, summary.TotalCostNanoUsd);
        Assert.Equal(3000, summary.TotalInputTokens);
        Assert.Equal(1500, summary.TotalOutputTokens);
        Assert.Equal(300, summary.AvgTtftMs); // (200 + 400) / 2; the error row has no ttft

        var empty = await db.Requests.SummaryAsync(T0.AddYears(1), T0.AddYears(2));
        Assert.Equal(0, empty.TotalRequests);
        Assert.Equal(0, empty.SuccessRate);
        Assert.Null(empty.AvgTtftMs);
    }

    [Fact]
    public async Task Timeseries_By_Day_Hour_And_Dimension()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());
        var range = (From: T0.AddMinutes(-1), To: T2.AddMinutes(1));

        var byDay = await db.Requests.TimeseriesAsync(range.From, range.To, groupBy: "day", by: "model");
        Assert.Equal(3, byDay.Count); // Oct 1 has two models, Oct 2 one
        var day1 = byDay.Where(p => p.Bucket == "2026-10-01").ToList();
        Assert.Equal(2, day1.Count);
        Assert.Equal(2, day1.Sum(p => p.Requests));
        Assert.Equal(3_000_000, day1.Sum(p => p.TotalCostNanoUsd));
        var day2Point = Assert.Single(byDay, p => p.Bucket == "2026-10-02");
        Assert.Equal("gpt-5", day2Point.GroupKey);
        Assert.Equal(0, day2Point.TotalCostNanoUsd);

        var byHour = await db.Requests.TimeseriesAsync(range.From, range.To, groupBy: "hour", by: "provider");
        Assert.Equal(
            ["2026-10-01T10:00", "2026-10-01T15:00", "2026-10-02T23:00"],
            byHour.Select(p => p.Bucket).ToList());
        var first = byHour[0];
        Assert.Equal("p1", first.GroupKey);
        Assert.Equal("Alpha", first.Label);
        Assert.Equal(1_000_000, first.TotalCostNanoUsd);

        var byClient = await db.Requests.TimeseriesAsync(range.From, range.To, groupBy: "day", by: "client");
        Assert.Equal(3, byClient.Count); // 2 buckets x codex + 1 bucket claude-code
        Assert.Contains(byClient, p => p.GroupKey == "claude-code" && p.Requests == 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => db.Requests.TimeseriesAsync(range.From, range.To, groupBy: "week"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => db.Requests.TimeseriesAsync(range.From, range.To, by: "vendor"));
    }

    [Fact]
    public async Task TopModels_Orders_By_Cost_Desc()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());
        var range = (From: T0.AddMinutes(-1), To: T2.AddMinutes(1));

        var top = await db.Requests.TopModelsAsync(range.From, range.To, limit: 2);
        Assert.Equal(2, top.Count);
        Assert.Equal("claude-x", top[0].ModelId); // 2M nano-USD
        Assert.Equal(2_000_000, top[0].TotalCostNanoUsd);
        Assert.Equal("gpt-5", top[1].ModelId); // 1M (the error row adds 0)
        Assert.Equal(1_000_000, top[1].TotalCostNanoUsd);
        Assert.Equal(2000, top[0].TotalInputTokens);
    }

    [Fact]
    public async Task TopModels_Sum_Cache_Read_Tokens_Per_Model()
    {
        var db = await TestDb.InitializeAsync();
        var batch = SampleBatch();
        batch[0].CacheReadTokens = 600; // gpt-5, 1000 input tokens
        batch[1].CacheReadTokens = 1500; // claude-x, 2000 input tokens
        await db.Requests.InsertBatchAsync(batch);

        var top = await db.Requests.TopModelsAsync(T0.AddMinutes(-1), T2.AddMinutes(1));
        Assert.Equal(1500, Assert.Single(top, m => m.ModelId == "claude-x").TotalCacheReadTokens);
        var gpt = Assert.Single(top, m => m.ModelId == "gpt-5");
        Assert.Equal(600, gpt.TotalCacheReadTokens); // the error row adds 0
        Assert.Equal(1000, gpt.TotalInputTokens);
    }

    [Fact]
    public async Task Stats_Filter_By_Client()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());
        var range = (From: T0.AddMinutes(-1), To: T2.AddMinutes(1));

        var codex = await db.Requests.SummaryAsync(range.From, range.To, clientKind: "codex");
        Assert.Equal(2, codex.TotalRequests);
        Assert.Equal(1_000_000, codex.TotalCostNanoUsd);
        var all = await db.Requests.SummaryAsync(range.From, range.To, clientKind: null);
        Assert.Equal(3, all.TotalRequests);

        var series = await db.Requests.TimeseriesAsync(range.From, range.To, "day", "model", clientKind: "claude-code");
        var point = Assert.Single(series);
        Assert.Equal("claude-x", point.GroupKey);

        var top = await db.Requests.TopModelsAsync(range.From, range.To, clientKind: "claude-code");
        Assert.Equal("claude-x", Assert.Single(top).ModelId);
        Assert.Empty(await db.Requests.TopModelsAsync(range.From, range.To, clientKind: "opencode"));
    }

    [Fact]
    public async Task Rate_Aggregates_Window_Requests_Tokens_And_Cache()
    {
        var db = await TestDb.InitializeAsync();
        var batch = SampleBatch();
        batch[0].CacheReadTokens = 600; // gpt-5, 1000 input tokens
        await db.Requests.InsertBatchAsync(batch);

        // The whole batch: 3 requests, 3000 input + 1500 output tokens, 600 of them cache reads.
        var all = await db.Requests.RateAsync(new RequestQuery { From = T0.AddMinutes(-1), To = T2.AddMinutes(1) });
        Assert.Equal((3L, 4_500L, 600L, 3_000L), (all.Requests, all.Tokens, all.CacheReadTokens, all.InputTokens));

        // Only the rows from T1 on: the T1 success row plus the T2 error row (which has no tokens).
        var tail = await db.Requests.RateAsync(new RequestQuery { From = T1, To = T2.AddMinutes(1) });
        Assert.Equal((2L, 3_000L, 0L, 2_000L), (tail.Requests, tail.Tokens, tail.CacheReadTokens, tail.InputTokens));

        // The list filters apply: client, model contains-match and status.
        var codex = await db.Requests.RateAsync(new RequestQuery { From = T0, To = T2, ClientKind = "codex" });
        Assert.Equal(2, codex.Requests);
        var model = await db.Requests.RateAsync(new RequestQuery { From = T0, To = T2, Model = "  CLAUDE  " });
        Assert.Equal((1L, 3_000L), (model.Requests, model.Tokens));
        var failed = await db.Requests.RateAsync(new RequestQuery { From = T0, To = T2, Status = RequestStatus.GatewayError });
        Assert.Equal((1L, 0L), (failed.Requests, failed.Tokens));

        // An empty window is all zeros, never nulls.
        var empty = await db.Requests.RateAsync(new RequestQuery { From = T0.AddYears(1), To = T0.AddYears(2) });
        Assert.Equal(new RateWindow(0, 0, 0, 0), empty);
    }

    [Fact]
    public async Task Timeseries_Buckets_Follow_The_Utc_Offset()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());
        var range = (From: T0.AddMinutes(-1), To: T2.AddMinutes(1));

        // UTC+8: 10:00Z → 18:00, 15:30Z → 23:30 the same day, 23:10Z on Oct 2 → 07:10 on Oct 3.
        var byHour = await db.Requests.TimeseriesAsync(range.From, range.To, "hour", "client", utcOffsetMinutes: 480);
        Assert.Equal(
            ["2026-10-01T18:00", "2026-10-01T23:00", "2026-10-03T07:00"],
            byHour.Select(p => p.Bucket).ToList());

        // UTC-5:30: 10:00Z → 04:30 on Oct 1, 23:10Z on Oct 2 → 17:40 on Oct 2.
        var byDay = await db.Requests.TimeseriesAsync(range.From, range.To, "day", "client", utcOffsetMinutes: -330);
        Assert.Equal(["2026-10-01", "2026-10-02"], byDay.Select(p => p.Bucket).Distinct().ToList());
        var westHour = await db.Requests.TimeseriesAsync(range.From, range.To, "hour", "client", utcOffsetMinutes: -330);
        Assert.Equal("2026-10-01T04:00", westHour[0].Bucket);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => db.Requests.TimeseriesAsync(range.From, range.To, utcOffsetMinutes: 15 * 60));
    }

    [Fact]
    public async Task DailyActivity_Counts_Requests_Per_Local_Day()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());

        var utc = await db.Requests.DailyActivityAsync(T0.AddDays(-1));
        Assert.Equal(["2026-10-01", "2026-10-02"], utc.Select(r => r.Day).ToList());
        Assert.Equal([2, 1], utc.Select(r => r.Requests).ToList());

        // UTC+8 pushes the Oct 2 23:10Z row onto Oct 3, and days without requests are simply absent.
        var east = await db.Requests.DailyActivityAsync(T0.AddDays(-1), utcOffsetMinutes: 480);
        Assert.Equal(["2026-10-01", "2026-10-03"], east.Select(r => r.Day).ToList());
        Assert.Equal(2, east[0].Requests);

        var codex = await db.Requests.DailyActivityAsync(T0.AddDays(-1), clientKind: "codex");
        Assert.Equal(["2026-10-01", "2026-10-02"], codex.Select(r => r.Day).ToList());
        Assert.Equal([1, 1], codex.Select(r => r.Requests).ToList());

        // Only requests at or after the start of the window are counted.
        var latest = await db.Requests.DailyActivityAsync(T2);
        Assert.Equal("2026-10-02", Assert.Single(latest).Day);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => db.Requests.DailyActivityAsync(T0, utcOffsetMinutes: 15 * 60));
    }

    [Fact]
    public async Task DeleteOlderThan_Removes_Rows_And_Cascades_Items()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync(SampleBatch());

        var removed = await db.Requests.DeleteOlderThanAsync(T1); // deletes only T0 row
        Assert.Equal(1, removed);
        Assert.Null(await db.Requests.GetAsync("01TEST0000000000000000000A"));

        await using var conn = await db.Factory.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM request_usage_items WHERE request_id = '01TEST0000000000000000000A'"));
        Assert.Equal(2, (await db.Requests.QueryAsync(new RequestQuery())).Total);
    }

    [Fact]
    public async Task Empty_Batch_Insert_Is_A_No_Op()
    {
        var db = await TestDb.InitializeAsync();
        await db.Requests.InsertBatchAsync([]);
        Assert.Equal(0, (await db.Requests.QueryAsync(new RequestQuery())).Total);
    }

    [Fact]
    public async Task Token_Breakdown_Round_Trips_And_Is_Summed()
    {
        var db = await TestDb.InitializeAsync();
        var a = Success("01TEST000000000000000000C1", T0, "claude-code", "p1", "Alpha", "claude-sonnet-4-6", 1, 120_000, 2_000, 300);
        a.CacheReadTokens = 100_000;
        a.CacheWriteTokens = 15_000;
        a.ReasoningTokens = 800;
        var b = Success("01TEST000000000000000000C2", T1, "codex", "p1", "Alpha", "gpt-5", 1, 10_000, 500, 300);
        b.CacheReadTokens = 6_000;
        await db.Requests.InsertBatchAsync([a, b]);

        var got = await db.Requests.GetAsync(a.Id);
        Assert.Equal((100_000L, 15_000L, 800L), (got!.CacheReadTokens, got.CacheWriteTokens, got.ReasoningTokens));
        var listed = (await db.Requests.QueryAsync(new RequestQuery { ClientKind = "codex" })).Items.Single();
        Assert.Equal(6_000, listed.CacheReadTokens);

        var summary = await db.Requests.SummaryAsync(T0, T2);
        Assert.Equal(106_000, summary.TotalCacheReadTokens);
        Assert.Equal(15_000, summary.TotalCacheWriteTokens);
        Assert.Equal(800, summary.TotalReasoningTokens);
    }

    [Fact]
    public async Task Privacy_Report_Round_Trips_And_Status_Filters()
    {
        var db = await TestDb.InitializeAsync();

        var blocked = Success("01TEST0000000000000000000D", T0, "claude-code", "p1", "Alpha", "claude-x", 0, 10, 5, 100);
        blocked.Status = RequestStatus.Blocked;
        blocked.PrivacyJson = """
            {"dry_run":false,"blocked":true,"redactions":0,"hits":[{"rule_id":"builtin.aws-access-key","category":"aws_key","action":"block","count":1}]}
            """;
        var redacted = Success("01TEST0000000000000000000E", T1, "codex", "p1", "Alpha", "gpt-5", 1, 10, 5, 100);
        redacted.PrivacyJson = """{"dry_run":false,"blocked":false,"redactions":2,"hits":[]}""";
        await db.Requests.InsertBatchAsync([blocked, redacted]);

        var gotBlocked = await db.Requests.GetAsync(blocked.Id);
        Assert.Equal(RequestStatus.Blocked, gotBlocked!.Status);
        Assert.NotNull(gotBlocked.PrivacyJson);
        Assert.Contains("aws_key", gotBlocked.PrivacyJson);

        var blockedOnly = await db.Requests.QueryAsync(new RequestQuery { Status = RequestStatus.Blocked });
        Assert.Single(blockedOnly.Items);
        Assert.Equal(blocked.Id, blockedOnly.Items[0].Id);

        // Requests without a guard outcome keep privacy_json null.
        var untouched = Success("01TEST0000000000000000000F", T2, "codex", "p1", "Alpha", "gpt-5", 1, 1, 1, 100);
        await db.Requests.InsertBatchAsync([untouched]);
        Assert.Null((await db.Requests.GetAsync(untouched.Id))!.PrivacyJson);
    }

    [Fact]
    public async Task Privacy_Event_Query_And_Stats_Filter_On_Stored_Report()
    {
        var db = await TestDb.InitializeAsync();

        var blocked = Success("01TEST000000000000000000P1", T0, "claude-code", "p1", "Alpha", "claude-x", 0, 10, 5, 100);
        blocked.Status = RequestStatus.Blocked;
        blocked.PrivacyJson = """
            {"dry_run":false,"blocked":true,"redactions":0,"hits":[{"rule_id":"builtin.aws-access-key","category":"aws_key","action":"block","count":1}]}
            """;
        var dry = Success("01TEST000000000000000000P2", T1, "codex", "p1", "Alpha", "gpt-5", 1, 10, 5, 100);
        dry.PrivacyJson = """
            {"dry_run":true,"blocked":false,"redactions":2,"hits":[{"rule_id":"builtin.email","category":"email","action":"redact","count":2}]}
            """;
        var plain = Success("01TEST000000000000000000P3", T2, "codex", "p1", "Alpha", "gpt-5", 1, 1, 1, 100);
        await db.Requests.InsertBatchAsync([blocked, dry, plain]);

        var all = await db.Requests.QueryPrivacyEventsAsync(new PrivacyEventQuery());
        Assert.Equal(2, all.Total);
        Assert.Equal(dry.Id, all.Items[0].Id); // newest first

        var blockedOnly = await db.Requests.QueryPrivacyEventsAsync(new PrivacyEventQuery { Blocked = true });
        Assert.Single(blockedOnly.Items);
        Assert.Equal(blocked.Id, blockedOnly.Items[0].Id);

        var byCategory = await db.Requests.QueryPrivacyEventsAsync(new PrivacyEventQuery { Category = "email" });
        Assert.Single(byCategory.Items);
        Assert.Equal(dry.Id, byCategory.Items[0].Id);

        var byAction = await db.Requests.QueryPrivacyEventsAsync(new PrivacyEventQuery { Action = "block" });
        Assert.Single(byAction.Items);
        Assert.Equal(blocked.Id, byAction.Items[0].Id);

        var stats = await db.Requests.PrivacyEventStatsAsync(new PrivacyEventQuery());
        Assert.Equal((2L, 1L, 1L, 2L), (stats.Events, stats.Blocked, stats.DryRun, stats.Redactions));
        Assert.Equal(2, stats.Categories.Count);
        Assert.Equal("email", stats.Categories[0].Category); // 2 hits outrank 1
        Assert.Equal((1L, 2L, 0L, 0L, 2L), (stats.Categories[0].Requests, stats.Categories[0].Hits,
            stats.Categories[0].WarnHits, stats.Categories[0].BlockHits, stats.Categories[0].RedactHits));

        var scoped = await db.Requests.PrivacyEventStatsAsync(new PrivacyEventQuery { ClientKind = "claude-code" });
        Assert.Equal(1, scoped.Events);
        Assert.Single(scoped.Categories);
        Assert.Equal("aws_key", scoped.Categories[0].Category);
    }

    [Fact]
    public async Task Reasoning_Metadata_Round_Trips_While_Unrecorded_Stays_Null()
    {
        var db = await TestDb.InitializeAsync();
        var full = Success("01TEST000000000000000000RA", T0, "codex", "p1", "Alpha", "gpt-5", 1, 10, 5, 100);
        full.ReasoningEffort = "xhigh";
        full.ReasoningMode = "adaptive";
        full.ReasoningBudgetTokens = 4096;
        var partial = Success("01TEST000000000000000000RB", T1, "codex", "p1", "Alpha", "gpt-5", 1, 10, 5, 100);
        partial.ReasoningMode = "auto";
        var plain = Success("01TEST000000000000000000RC", T2, "codex", "p1", "Alpha", "gpt-5", 1, 10, 5, 100);
        await db.Requests.InsertBatchAsync([full, partial, plain]);

        var got = await db.Requests.GetAsync(full.Id);
        Assert.Equal(("xhigh", "adaptive", 4096L), (got!.ReasoningEffort, got.ReasoningMode, got.ReasoningBudgetTokens));
        var auto = await db.Requests.GetAsync(partial.Id);
        Assert.Null(auto!.ReasoningEffort);
        Assert.Equal("auto", auto.ReasoningMode);
        Assert.Null(auto.ReasoningBudgetTokens);

        // Old rows (and rows whose client set nothing) keep all three fields null - never back-filled.
        var untouched = await db.Requests.GetAsync(plain.Id);
        Assert.Null(untouched!.ReasoningEffort);
        Assert.Null(untouched.ReasoningMode);
        Assert.Null(untouched.ReasoningBudgetTokens);

        // The paged list query carries the metadata too.
        var listed = (await db.Requests.QueryAsync(new RequestQuery { Model = "gpt-5" })).Items;
        Assert.Equal(3, listed.Count);
        Assert.Equal("xhigh", listed.Single(r => r.Id == full.Id).ReasoningEffort);
    }

    [Fact]
    public async Task Response_Model_Round_Trips_While_Unreported_Stays_Null()
    {
        var db = await TestDb.InitializeAsync();
        var declared = Success("01TEST0000000000000000000G", T0, "codex", "p1", "Alpha", "claude-x", 1, 10, 5, 100);
        declared.UpstreamModel = "claude-x-haiku";
        declared.ResponseModel = "claude-x-haiku-2026-01-15";
        var unreported = Success("01TEST0000000000000000000H", T1, "codex", "p1", "Alpha", "claude-x", 1, 10, 5, 100);
        await db.Requests.InsertBatchAsync([declared, unreported]);

        var got = await db.Requests.GetAsync(declared.Id);
        Assert.Equal("claude-x-haiku-2026-01-15", got!.ResponseModel);
        Assert.Equal("claude-x-haiku", got.UpstreamModel);
        Assert.Equal("claude-x", got.RequestedModel);

        // The response model is visible through the list query too.
        var listed = (await db.Requests.QueryAsync(new RequestQuery { Model = "haiku-2026" })).Items.Single();
        Assert.Equal(declared.Id, listed.Id);
        Assert.Equal("claude-x-haiku-2026-01-15", listed.ResponseModel);

        // Rows whose upstream response declared no model keep null - never the requested or sent model.
        Assert.Null((await db.Requests.GetAsync(unreported.Id))!.ResponseModel);
        Assert.Equal(1, (await db.Requests.QueryAsync(new RequestQuery { Model = "haiku-2026" })).Total);
    }

    [Fact]
    public async Task Model_Search_Is_Literal_Contains_Over_All_Four_Model_Columns()
    {
        var db = await TestDb.InitializeAsync();
        var byRequested = Success("01TEST0000000000000000000I", T0, "codex", "p1", "Alpha", "alpha-spec", 1, 10, 5, 100);
        var byUpstream = Success("01TEST0000000000000000000J", T1, "codex", "p1", "Alpha", "plain", 1, 10, 5, 100);
        byUpstream.UpstreamModel = "bravo-spec";
        var bySystem = Success("01TEST0000000000000000000K", T2, "codex", "p1", "Alpha", "plain", 1, 10, 5, 100);
        bySystem.SystemModelId = "charlie-spec";
        var byResponse = Success("01TEST0000000000000000000L", T2, "claude-code", "p1", "Alpha", "plain", 1, 10, 5, 100);
        byResponse.ResponseModel = "delta-spec";
        var wildcards = Success("01TEST0000000000000000000M", T2, "codex", "p1", "Alpha", "plain", 1, 10, 5, 100);
        wildcards.UpstreamModel = "gpt_5%turbo";
        var fives = Success("01TEST0000000000000000000N", T2, "codex", "p1", "Alpha", "gpt-5-turbo", 1, 10, 5, 100);
        var escaped = Success("01TEST0000000000000000000O", T2, "codex", "p1", "Alpha", "plain", 1, 10, 5, 100);
        escaped.UpstreamModel = "ns\\model";
        var slashless = Success("01TEST0000000000000000000Q", T2, "codex", "p1", "Alpha", "ns-model", 1, 10, 5, 100);
        await db.Requests.InsertBatchAsync(
            [byRequested, byUpstream, bySystem, byResponse, wildcards, fives, escaped, slashless]);

        // Trimmed and case-insensitive contains-match.
        Assert.Equal(4, (await db.Requests.QueryAsync(new RequestQuery { Model = "  SPEC  " })).Total);
        // A whitespace-only term filters nothing.
        Assert.Equal(8, (await db.Requests.QueryAsync(new RequestQuery { Model = "   " })).Total);

        // Each of the four model columns is searchable (mixed-case terms prove case-insensitivity).
        foreach (var (id, term) in new[]
                 {
                     (byRequested.Id, "ALPHA-SPEC"), (byUpstream.Id, "Bravo-Spec"),
                     (bySystem.Id, "CHARLIE-SPEC"), (byResponse.Id, "Delta-Spec"),
                 })
        {
            var hit = await db.Requests.QueryAsync(new RequestQuery { Model = term });
            Assert.Equal(id, Assert.Single(hit.Items).Id);
        }

        // '%', '_' and '\' match only their literal characters: as wildcards these terms would
        // also hit the fives row ("gpt-5-turbo").
        var percent = await db.Requests.QueryAsync(new RequestQuery { Model = "5%" });
        Assert.Equal(wildcards.Id, Assert.Single(percent.Items).Id);
        var underscore = await db.Requests.QueryAsync(new RequestQuery { Model = "_5" });
        Assert.Equal(wildcards.Id, Assert.Single(underscore.Items).Id);
        var backslash = await db.Requests.QueryAsync(new RequestQuery { Model = "\\" });
        Assert.Equal(escaped.Id, Assert.Single(backslash.Items).Id);

        // The model search composes with the other filters (AND).
        var combined = await db.Requests.QueryAsync(new RequestQuery { Model = "spec", ClientKind = "claude-code" });
        Assert.Equal(byResponse.Id, Assert.Single(combined.Items).Id);
    }

    [Fact]
    public async Task Token_Counters_Accumulate_In_The_Insert_Transaction_And_Survive_Retention()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Tokens.InsertAsync(new Astra.Core.Tokens.TokenRecord { Id = "t1", Name = "Work", CreatedAt = now, UpdatedAt = now });
        var batch = SampleBatch();
        foreach (var r in batch) r.TokenId = "t1";
        batch[0].TokenName = "Work";
        batch[1].TokenId = "gone"; // token deleted before the writer ran: logged, but no counter row to attach to
        batch[1].TokenName = "Gone";
        batch[0].CacheReadTokens = 400;
        await db.Requests.InsertBatchAsync(batch);

        // Only t1 gains counters (the default token's row from the migration stays at zero).
        var totals = Assert.Single(await db.Tokens.ListTotalsAsync(), t => t.Requests > 0);
        Assert.Equal("t1", totals.TokenId);
        Assert.Equal(2, totals.Requests);
        Assert.Equal(1, totals.SuccessRequests);
        Assert.Equal(1_000_000, totals.CostNanoUsd);
        Assert.Equal(1000, totals.InputTokens);
        Assert.Equal(400, totals.CacheReadTokens);
        Assert.Equal(500, totals.TpsOutputTokens);
        Assert.Equal(800, totals.TpsGenerationMs);
        Assert.Equal(0.4, totals.CacheHitRate);
        Assert.Equal(625.0, totals.Tps);
        Assert.Equal(T2, (await db.Tokens.GetAsync("t1"))!.LastUsedAt);

        // Range usage comes from the log; retention removes rows but never the lifetime counters.
        var usage = (await db.Tokens.UsageAsync(T0.AddMinutes(-1), T2.AddMinutes(1))).ToDictionary(u => u.TokenId);
        Assert.Equal(2, usage["t1"].Requests);
        Assert.Equal(1, usage["gone"].Requests);
        await db.Requests.DeleteOlderThanAsync(T2.AddDays(1));
        Assert.Equal(2, Assert.Single(await db.Tokens.ListTotalsAsync(), t => t.TokenId == "t1").Requests);
    }

    [Fact]
    public async Task Requests_And_Stats_Filter_And_Group_By_Token_With_Current_Name()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Tokens.InsertAsync(new Astra.Core.Tokens.TokenRecord { Id = "t1", Name = "Renamed", CreatedAt = now, UpdatedAt = now });
        var batch = SampleBatch();
        batch[0].TokenId = "t1";
        batch[0].TokenName = "Original";
        batch[1].TokenId = "deleted";
        batch[1].TokenName = "Snapshot";
        await db.Requests.InsertBatchAsync(batch);
        var range = (From: T0.AddMinutes(-1), To: T2.AddMinutes(1));

        var page = await db.Requests.QueryAsync(new RequestQuery { TokenId = "t1" });
        var row = Assert.Single(page.Items);
        Assert.Equal("Renamed", row.TokenName); // current name wins over the snapshot
        Assert.Equal("Snapshot", (await db.Requests.GetAsync(batch[1].Id))!.TokenName); // deleted token: snapshot

        Assert.Equal(1, (await db.Requests.SummaryAsync(range.From, range.To, tokenId: "t1")).TotalRequests);
        Assert.Equal("gpt-5", Assert.Single(await db.Requests.TopModelsAsync(range.From, range.To, tokenId: "t1")).ModelId);
        var byToken = await db.Requests.TimeseriesAsync(range.From, range.To, "day", "token");
        Assert.Contains(byToken, p => p.GroupKey == "t1" && p.Label == "Renamed");
        Assert.Contains(byToken, p => p.GroupKey == "deleted" && p.Label == "Snapshot");
        Assert.Contains(byToken, p => p.GroupKey == null);
        var filtered = await db.Requests.TimeseriesAsync(range.From, range.To, "day", "model", tokenId: "deleted");
        Assert.Equal("claude-x", Assert.Single(filtered).GroupKey);
    }
}
