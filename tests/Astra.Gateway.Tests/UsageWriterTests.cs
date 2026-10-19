using Astra.Core;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Gateway.Tests;

public sealed class UsageWriterTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-usage-writer-tests", Guid.NewGuid().ToString("N"));
    private AstraDatabase _db = null!;
    private TokenUsageLedger _ledger = null!;
    private UsageWriter _writer = null!;

    public async Task InitializeAsync()
    {
        _db = await AstraDatabase.InitializeAsync(new AstraPaths(_dir), importSeed: false);
        var settings = new SettingsService(_db, new FakeProtector());
        await settings.LoadAsync();
        _ledger = new TokenUsageLedger(_db, settings);
        _writer = new UsageWriter(_db, new LiveRequestFeed(), NullLogger<UsageWriter>.Instance, _ledger);
    }

    [Fact]
    public async Task Saturated_Queue_Waits_And_Preserves_Records_Spans_And_Token_Usage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Hold the consumer until the queue is full: this tests real backpressure deterministically.
        for (var i = 0; i < UsageWriter.QueueCapacity; i++)
            await _writer.EnqueueAsync(Record(i));
        var waiting = Enumerable.Range(UsageWriter.QueueCapacity, 64)
            .Select(i => _writer.EnqueueAsync(Record(i)).AsTask()).ToArray();
        Assert.All(waiting, task => Assert.False(task.IsCompleted));

        var count = UsageWriter.QueueCapacity + waiting.Length;
        // A completed request counts against the token even while admission to the queue waits.
        Assert.Equal(count, (await _ledger.TotalAsync(TokenIds.Default, timeout.Token)).Requests);
        await _writer.StartAsync(timeout.Token);
        await Task.WhenAll(waiting).WaitAsync(timeout.Token);
        await _writer.FlushAsync(timeout.Token);
        await AssertPersistedAsync(count);
        Assert.Equal(count, (await _ledger.TotalAsync(TokenIds.Default, timeout.Token)).Requests);
    }

    [Fact]
    public async Task Shutdown_Persists_An_Accepted_Partial_Batch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Smaller than a batch: shutdown must drain it without waiting for the normal flush timer.
        for (var i = 0; i < 23; i++) await _writer.EnqueueAsync(Record(i));
        await _writer.StartAsync(timeout.Token);
        await _writer.StopAsync(timeout.Token);
        await _writer.FlushAsync(timeout.Token);
        await AssertPersistedAsync(23);
    }

    private static RequestRecord Record(int i)
    {
        var id = $"queued-{i}";
        return new RequestRecord
        {
            Id = id, TokenId = TokenIds.Default, StartedAtUtc = DateTimeOffset.UtcNow,
            Status = RequestStatus.Success, HttpStatus = 200, RequestedModel = "test-model",
            TotalInputTokens = 5, TotalOutputTokens = 2, CostNanoUsd = 7,
            BillingTraceJson = "{\"kept\":true}",
            UsageItems =
            [
                new() { RequestId = id, TokenType = "input", Tokens = 5, CostNanoUsd = 5 },
                new() { RequestId = id, TokenType = "output", Tokens = 2, CostNanoUsd = 2 },
            ],
            Spans =
            [
                new() { RequestId = id, SpanId = $"span-{i}", Name = TraceSpanNames.Request,
                    Kind = TraceSpanKinds.Server, Status = TraceSpanStatus.Ok, AttributesJson = "{\"kept\":true}" },
            ],
        };
    }

    private async Task AssertPersistedAsync(int count)
    {
        await using var connection = await _db.Factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM requests),
                   (SELECT count(*) FROM trace_spans),
                   (SELECT count(*) FROM request_usage_items)
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(count, reader.GetInt64(0));
        Assert.Equal(count, reader.GetInt64(1));
        Assert.Equal(count * 2, reader.GetInt64(2));

        var totals = await _db.Tokens.GetTotalsAsync(TokenIds.Default);
        Assert.NotNull(totals);
        Assert.Equal(count, totals.Requests);
        Assert.Equal(count * 5, totals.InputTokens);
        Assert.Equal(count * 2, totals.OutputTokens);
        Assert.Equal(count * 7, totals.CostNanoUsd);
        var last = await _db.Requests.GetAsync($"queued-{count - 1}");
        Assert.NotNull(last);
        Assert.Equal("{\"kept\":true}", last.BillingTraceJson);
        Assert.Equal(2, last.UsageItems.Count);
        var span = Assert.Single(await _db.Traces.ListSpansAsync(last.Id));
        Assert.Equal("{\"kept\":true}", span.AttributesJson);
    }

    public async Task DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _writer.StopAsync(timeout.Token);
        _writer.Dispose();
        using (var connection = new SqliteConnection(_db.Factory.ConnectionString))
            SqliteConnection.ClearPool(connection);
        Directory.Delete(_dir, recursive: true);
    }
}
