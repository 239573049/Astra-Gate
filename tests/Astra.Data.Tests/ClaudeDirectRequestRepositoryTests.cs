using Dapper;
using Astra.Data.Repositories;
using Astra.Data.Tests;

namespace Astra.Data;

public class ClaudeDirectRequestRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 2, 23, 10, 0, TimeSpan.Zero);

    private static ClaudeDirectRequest Row(
        string id, string profileId, DateTimeOffset at, string model = "claude-sonnet-4-6",
        string sessionId = "s1", string? accountUuid = null, string status = "success",
        long inTok = 100, long outTok = 50, long cacheRead = 0, long cacheCreation = 0,
        long? durationMs = 1200, long? costNano = null) => new()
        {
            Id = id,
            ProfileId = profileId,
            SessionId = sessionId,
            AccountUuid = accountUuid,
            Model = model,
            OccurredAtUtc = at,
            Status = status,
            InputTokens = inTok,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
            DurationMs = durationMs,
            EstimatedCostNanoUsd = costNano,
        };

    private static ClaudeDirectRequestRepository Repository(AstraDatabase db) => new(db.Factory);

    [Fact]
    public async Task Insert_And_List_Round_Trips_Every_Column()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        await repo.InsertAsync(
        [
            Row("e1", "p1", T0, accountUuid: "acct-1", inTok: 1000, outTok: 500, cacheRead: 400,
                cacheCreation: 25, durationMs: 900, costNano: 1_000_000),
            Row("e2", "p1", T1, model: "claude-opus-4-6", sessionId: "s2", status: "error",
                inTok: 10, outTok: 0, durationMs: null, costNano: null),
        ]);

        var rows = await repo.ListAsync("p1");
        Assert.Equal(["e2", "e1"], rows.Select(r => r.Id).ToList()); // newest first

        var first = rows[1];
        Assert.Equal(("e1", "p1", "s1", "acct-1", "claude-sonnet-4-6", "success"),
            (first.Id, first.ProfileId, first.SessionId, first.AccountUuid, first.Model, first.Status));
        Assert.Equal(T0, first.OccurredAtUtc);
        Assert.Equal((1000L, 500L, 400L, 25L),
            (first.InputTokens, first.OutputTokens, first.CacheReadTokens, first.CacheCreationTokens));
        Assert.Equal((900L, 1_000_000L), (first.DurationMs, first.EstimatedCostNanoUsd));

        var second = rows[0];
        Assert.Equal("error", second.Status);
        Assert.Equal("s2", second.SessionId);
        Assert.Null(second.AccountUuid);
        Assert.Null(second.DurationMs);
        Assert.Null(second.EstimatedCostNanoUsd);
    }

    [Fact]
    public async Task Stored_Timestamp_Is_Iso8601_Utc_Text()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        // A non-UTC client timestamp is normalized to UTC on the way in.
        var at = new DateTimeOffset(2026, 10, 6, 20, 34, 56, 789, TimeSpan.FromHours(8));
        await repo.InsertAsync([Row("e1", "p1", at)]);

        await using var conn = await db.Factory.OpenAsync();
        Assert.Equal("2026-10-06T12:34:56.789Z",
            await conn.ExecuteScalarAsync<string>("SELECT occurred_at_utc FROM claude_direct_requests WHERE id = 'e1'"));
        Assert.Equal(at, (await repo.ListAsync("p1")).Single().OccurredAtUtc);
    }

    [Fact]
    public async Task Replayed_Id_Is_Idempotent_And_Keeps_The_Stored_Row()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        await repo.InsertAsync([Row("e1", "p1", T0, inTok: 100, costNano: 1_000)]);
        // A second upload of the same event (and a batch where it shares the id with a fresh row):
        // the existing row wins, the new one is still written.
        await repo.InsertAsync([Row("e1", "p1", T1, inTok: 999, costNano: 42), Row("e2", "p1", T2)]);

        var rows = await repo.ListAsync("p1");
        Assert.Equal(["e2", "e1"], rows.Select(r => r.Id).ToList());
        var kept = rows[1];
        Assert.Equal(T0, kept.OccurredAtUtc);
        Assert.Equal(100, kept.InputTokens);
        Assert.Equal(1_000, kept.EstimatedCostNanoUsd);

        await using var conn = await db.Factory.OpenAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM claude_direct_requests"));
    }

    [Fact]
    public async Task List_Is_Scoped_To_One_Profile()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        await repo.InsertAsync([Row("e1", "p1", T0), Row("e2", "p2", T1), Row("e3", "p1", T2)]);

        Assert.Equal(["e3", "e1"], (await repo.ListAsync("p1")).Select(r => r.Id).ToList());
        Assert.Equal(["e2"], (await repo.ListAsync("p2")).Select(r => r.Id).ToList());
        Assert.Empty(await repo.ListAsync("p3"));
    }

    [Fact]
    public async Task List_Is_Newest_First_And_Caps_The_Limit()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        var rows = Enumerable.Range(0, 10)
            .Select(i => Row($"e{i:D2}", "p1", T0.AddMinutes(i)))
            .ToList();
        await repo.InsertAsync(rows);

        var limited = await repo.ListAsync("p1", limit: 3);
        Assert.Equal(["e09", "e08", "e07"], limited.Select(r => r.Id).ToList());

        // 0 / negative fall back to 1, anything above the cap is clamped to MaxListLimit.
        Assert.Equal(["e09"], (await repo.ListAsync("p1", limit: 0)).Select(r => r.Id).ToList());
        Assert.Equal(10, (await repo.ListAsync("p1", limit: 999)).Count);
        Assert.Equal(200, ClaudeDirectRequestRepository.MaxListLimit);
    }

    [Fact]
    public async Task DeleteOlderThan_Removes_Only_Rows_Before_The_Cutoff()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        await repo.InsertAsync([Row("e1", "p1", T0), Row("e2", "p1", T1), Row("e3", "p2", T2)]);

        await repo.DeleteOlderThanAsync(T1); // e1 only; the cutoff itself is kept
        Assert.Equal(["e2"], (await repo.ListAsync("p1")).Select(r => r.Id).ToList());
        Assert.Equal(["e3"], (await repo.ListAsync("p2")).Select(r => r.Id).ToList());
    }

    [Fact]
    public async Task Empty_Batch_Insert_Is_A_No_Op()
    {
        var db = await TestDb.InitializeAsync();
        var repo = Repository(db);
        await repo.InsertAsync([]);

        await using var conn = await db.Factory.OpenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM claude_direct_requests"));
        Assert.Empty(await repo.ListAsync("p1"));
    }
}
