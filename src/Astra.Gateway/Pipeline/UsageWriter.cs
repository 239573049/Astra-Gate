using System.Threading.Channels;
using Astra.Core.Requests;
using Astra.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Background writer for request records: batches of up to 50 or every second, one transaction per batch.
/// Drains the queue on shutdown so no finished request is lost. Written records leave the <see cref="LiveRequestFeed"/>.
/// </summary>
public sealed class UsageWriter(AstraDatabase db, LiveRequestFeed live, ILogger<UsageWriter> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<RequestRecord> _queue = Channel.CreateUnbounded<RequestRecord>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _pending;
    private Exception? _writeError;

    public void Enqueue(RequestRecord record)
    {
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(record))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogWarning("Usage writer closed; dropping request {Id}", record.Id);
        }
    }

    /// <summary>Waits for accepted records to finish writing; reports database failures rather than claiming a successful flush.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Single-reader unbounded channels do not expose Count on every runtime.
        while (Volatile.Read(ref _pending) > 0 || _writing) await Task.Delay(10, ct);
        if (Volatile.Read(ref _writeError) is { } error) throw new IOException("Request record persistence failed", error);
    }

    private volatile bool _writing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RequestRecord>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                _writing = true;
                var deadline = DateTime.UtcNow + FlushInterval;
                while (batch.Count < BatchSize)
                {
                    if (_queue.Reader.TryRead(out var r))
                    {
                        batch.Add(r);
                        continue;
                    }
                    var wait = deadline - DateTime.UtcNow;
                    if (wait <= TimeSpan.Zero) break;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(wait);
                    try
                    {
                        if (!await _queue.Reader.WaitToReadAsync(cts.Token)) break;
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                await WriteAsync(batch, CancellationToken.None);
                _writing = false;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Drain whatever is left (shutdown).
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var r)) batch.Add(r);
            await WriteAsync(batch, CancellationToken.None);
            _writing = false;
        }
    }

    private async Task WriteAsync(List<RequestRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var count = batch.Count;
        try
        {
            await db.Requests.InsertBatchAsync(batch, ct);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _writeError, ex);
            logger.LogError(ex, "Failed to write {Count} request records", count);
        }
        finally
        {
            live.Persisted(batch);
            batch.Clear();
            Interlocked.Add(ref _pending, -count);
        }
    }
}

/// <summary>Daily cleanup: request logs older than the retention setting and debug bodies older than their retention.</summary>
public sealed class RetentionService(AstraDatabase db, SettingsService settings, BodyStore bodies, ILogger<RetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var s = settings.Current;
                if (s.RequestRetentionDays is { } days)
                {
                    var before = DateTimeOffset.UtcNow.AddDays(-days);
                    var removed = await db.Requests.DeleteOlderThanAsync(before, stoppingToken);
                    await new Astra.Data.Repositories.ClaudeDirectRequestRepository(db.Factory).DeleteOlderThanAsync(before, stoppingToken);
                    if (removed > 0) logger.LogInformation("Retention: removed {Count} request records", removed);
                }
                bodies.Cleanup(s.BodyRetentionDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
