using System.Collections.Concurrent;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>A token's usage over some span (a period of a budget, a day, its lifetime).</summary>
public readonly record struct TokenTally(long Requests, long SuccessRequests, long CostNanoUsd, long InputTokens, long OutputTokens)
{
    public long Tokens => InputTokens + OutputTokens;

    /// <summary>The number a budget of <paramref name="metric"/> compares with its limit.</summary>
    public long Value(string metric) => metric switch
    {
        TokenLimitMetrics.Cost => CostNanoUsd,
        TokenLimitMetrics.Requests => Requests,
        _ => Tokens,
    };

    public static TokenTally Of(RequestRecord r) => new(1, r.Status == RequestStatus.Success ? 1 : 0, r.CostNanoUsd,
        r.TotalInputTokens, r.TotalOutputTokens);

    public static TokenTally Of(TokenDailyUsage d) => new(d.Requests, d.SuccessRequests, d.CostNanoUsd, d.InputTokens, d.OutputTokens);

    public static TokenTally operator +(TokenTally a, TokenTally b) => new(a.Requests + b.Requests,
        a.SuccessRequests + b.SuccessRequests, a.CostNanoUsd + b.CostNanoUsd, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);
}

/// <summary>Where a token stands against one of its budgets right now.</summary>
public sealed record TokenBudgetStatus(TokenBudget Budget, long Used, DateTimeOffset? ResetsAt)
{
    public long Remaining => Math.Max(0, Budget.Limit - Used);
    public bool Exhausted => Used >= Budget.Limit;
    public double UsedPercent => Budget.Limit <= 0 ? 100 : Math.Round(Math.Min(100.0, Used * 100.0 / Budget.Limit), 1);
}

/// <summary>
/// In-memory token usage for limit checks (plan: alerts-webhooks-token-limits §1.3). Request rows reach the database in
/// batches (<see cref="UsageWriter"/>), so the database alone lags behind a burst; the ledger loads a token's daily and
/// lifetime counters once, then adds every finished request the moment it is queued for writing. Records queued but
/// not yet written are kept aside so a token loaded meanwhile still sees them. A request persisted exactly while its
/// token loads may be counted once too often or not at all — acceptable for soft limits. The RPM window lives here too
/// (not persisted: a restart clears it).
/// </summary>
public sealed class TokenUsageLedger(AstraDatabase db, SettingsService settings, TimeProvider? timeProvider = null)
{
    /// <summary>Days of daily counters kept per token: enough for a calendar month plus the week reaching into it.</summary>
    public const int DaysKept = 40;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Book> _books = new();
    private readonly ConcurrentDictionary<string, RequestRecord> _pending = new();
    private readonly Lock _zoneLock = new();
    private string? _zoneId;
    private TimeZoneInfo? _zone;

    private sealed class Book
    {
        public readonly Lock Gate = new();
        public readonly SemaphoreSlim Loading = new(1, 1);
        public bool Loaded;
        public readonly Dictionary<DateOnly, TokenTally> Days = [];
        public TokenTally Total;
        public readonly Queue<DateTimeOffset> Minute = new();
    }

    /// <summary>The usage time zone (<c>AppSettings.UsageTimeZone</c>). A change drops every loaded book.</summary>
    public TimeZoneInfo Zone
    {
        get
        {
            var id = settings.Current.UsageTimeZone;
            lock (_zoneLock)
            {
                if (_zone is not null && _zoneId == id) return _zone;
                if (_zone is not null) _books.Clear();
                _zoneId = id;
                return _zone = UsagePeriods.Zone(id);
            }
        }
    }

    /// <summary>A finished request was queued for writing (<see cref="UsageWriter.Enqueue"/>).</summary>
    public void Add(RequestRecord record)
    {
        if (record.TokenId is null || !TokenLimits.Counts(record)) return;
        var zone = Zone;
        var book = _books.GetOrAdd(record.TokenId, static _ => new Book());
        lock (book.Gate)
        {
            _pending[record.Id] = record;
            if (book.Loaded) Apply(book, record, zone);
        }
    }

    /// <summary>The records are in the database (or failed to write): a token loaded from now on reads them there.</summary>
    public void Persisted(IEnumerable<RequestRecord> records)
    {
        foreach (var record in records) _pending.TryRemove(record.Id, out _);
    }

    /// <summary>Drops a token's book (it was deleted).</summary>
    public void Forget(string tokenId) => _books.TryRemove(tokenId, out _);

    /// <summary>Usage in the current period of <paramref name="period"/> (<see cref="TokenLimitPeriods"/>).</summary>
    public async Task<TokenTally> PeriodAsync(string tokenId, string period, CancellationToken ct)
    {
        var zone = Zone;
        var (from, _) = UsagePeriods.Window(period, _clock.GetUtcNow(), zone);
        var book = await LoadAsync(tokenId, zone, ct);
        lock (book.Gate)
        {
            if (from is not { } first) return book.Total;
            var sum = default(TokenTally);
            foreach (var (day, tally) in book.Days)
                if (day >= first) sum += tally;
            return sum;
        }
    }

    /// <summary>Where the token stands against each of <paramref name="limits"/>' budgets.</summary>
    public async Task<IReadOnlyList<TokenBudgetStatus>> StatusAsync(string tokenId, TokenLimits limits, CancellationToken ct)
    {
        var list = new List<TokenBudgetStatus>(limits.Budgets.Count);
        var now = _clock.GetUtcNow();
        foreach (var budget in limits.Budgets)
        {
            var used = await PeriodAsync(tokenId, budget.Period, ct);
            list.Add(new TokenBudgetStatus(budget, used.Value(budget.Metric), UsagePeriods.Window(budget.Period, now, Zone).ResetsAt));
        }
        return list;
    }

    /// <summary>The last <paramref name="days"/> local days (today included, oldest first, days without use as zeros).</summary>
    public async Task<IReadOnlyList<(DateOnly Day, TokenTally Usage)>> DailyAsync(string tokenId, int days, CancellationToken ct)
    {
        var zone = Zone;
        var today = UsagePeriods.LocalDay(_clock.GetUtcNow(), zone);
        var book = await LoadAsync(tokenId, zone, ct);
        lock (book.Gate)
        {
            return Enumerable.Range(0, Math.Clamp(days, 1, DaysKept)).Reverse()
                .Select(i => today.AddDays(-i))
                .Select(d => (d, book.Days.GetValueOrDefault(d)))
                .ToList();
        }
    }

    /// <summary>Lifetime usage.</summary>
    public Task<TokenTally> TotalAsync(string tokenId, CancellationToken ct) => PeriodAsync(tokenId, TokenLimitPeriods.Total, ct);

    /// <summary>Requests the token was let through during the last minute.</summary>
    public int RecentRequests(string tokenId)
    {
        if (!_books.TryGetValue(tokenId, out var book)) return 0;
        lock (book.Gate)
        {
            Trim(book, _clock.GetUtcNow());
            return book.Minute.Count;
        }
    }

    /// <summary>
    /// Takes a slot of the token's rolling one-minute window. False (with the wait until the oldest slot frees up) when
    /// <paramref name="rpm"/> requests already went through within the last minute.
    /// </summary>
    public bool TryTakeMinuteSlot(string tokenId, int rpm, out TimeSpan retryAfter)
    {
        var book = _books.GetOrAdd(tokenId, static _ => new Book());
        var now = _clock.GetUtcNow();
        lock (book.Gate)
        {
            Trim(book, now);
            if (book.Minute.Count >= rpm)
            {
                retryAfter = book.Minute.Peek() + TimeSpan.FromMinutes(1) - now;
                return false;
            }
            book.Minute.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private static void Trim(Book book, DateTimeOffset now)
    {
        while (book.Minute.Count > 0 && now - book.Minute.Peek() >= TimeSpan.FromMinutes(1)) book.Minute.Dequeue();
    }

    private async Task<Book> LoadAsync(string tokenId, TimeZoneInfo zone, CancellationToken ct)
    {
        var book = _books.GetOrAdd(tokenId, static _ => new Book());
        if (Volatile.Read(ref book.Loaded)) return book;
        await book.Loading.WaitAsync(ct);
        try
        {
            if (book.Loaded) return book;
            var firstDay = UsagePeriods.LocalDay(_clock.GetUtcNow(), zone).AddDays(-(DaysKept - 1));
            var daily = await db.Tokens.DailyAsync(tokenId, UsagePeriods.DayKey(firstDay), ct);
            var total = await db.Tokens.GetTotalsAsync(tokenId, ct);
            lock (book.Gate)
            {
                book.Days.Clear();
                foreach (var d in daily)
                    if (DateOnly.TryParseExact(d.Day, "yyyy-MM-dd", out var day)) book.Days[day] = TokenTally.Of(d);
                book.Total = total is null
                    ? default
                    : new TokenTally(total.Requests, total.SuccessRequests, total.CostNanoUsd, total.InputTokens, total.OutputTokens);
                foreach (var record in _pending.Values)
                    if (record.TokenId == tokenId) Apply(book, record, zone);
                book.Loaded = true;
            }
            return book;
        }
        finally
        {
            book.Loading.Release();
        }
    }

    private void Apply(Book book, RequestRecord record, TimeZoneInfo zone)
    {
        var tally = TokenTally.Of(record);
        var day = UsagePeriods.LocalDay(record.StartedAtUtc, zone);
        book.Days[day] = book.Days.GetValueOrDefault(day) + tally;
        book.Total += tally;
        var oldest = UsagePeriods.LocalDay(_clock.GetUtcNow(), zone).AddDays(-(DaysKept - 1));
        if (book.Days.Count > DaysKept + 1)
            foreach (var stale in book.Days.Keys.Where(d => d < oldest).ToList()) book.Days.Remove(stale);
    }
}
