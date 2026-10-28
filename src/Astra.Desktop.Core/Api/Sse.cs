namespace Astra.Desktop.Core.Api;

/// <summary>One server-sent-events frame. Port of <c>SseMessage</c> (web/src/api/client.ts), but the payload stays text: decode it with <see cref="ApiClient.StreamAsync{T}"/>.</summary>
/// <param name="Event">The <c>event:</c> field; "" when the frame carried none.</param>
/// <param name="Data">The <c>data:</c> lines joined with "\n".</param>
public sealed record SseMessage(string Event, string Data);

/// <summary>
/// Incremental text/event-stream parser, a port of <c>createSseParser</c> (web/src/api/client.ts): a blank line dispatches a
/// frame, <c>data:</c> lines join with "\n", comment lines (":") and frames without data are dropped. Line ends are
/// CRLF, LF or CR. Not thread-safe (one parser per stream).
/// </summary>
public sealed class SseParser
{
    private string _buffer = "";

    /// <summary>Feeds a decoded chunk and returns the frames it completed.</summary>
    public List<SseMessage> Feed(string chunk)
    {
        _buffer += chunk;
        var messages = new List<SseMessage>();
        while (true)
        {
            var (index, length) = FindBoundary(_buffer);
            if (index < 0) return messages;
            var frame = _buffer[..index];
            _buffer = _buffer[(index + length)..];
            var message = ParseFrame(frame);
            if (message is not null) messages.Add(message);
        }
    }

    private static (int Index, int Length) FindBoundary(string s)
    {
        // Earliest of CRLFCRLF, LFLF, CRCR (the same alternation as the web regex).
        var best = -1;
        var len = 0;
        foreach (var (token, tokenLength) in new[] { ("\r\n\r\n", 4), ("\n\n", 2), ("\r\r", 2) })
        {
            var i = s.IndexOf(token, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) { best = i; len = tokenLength; }
        }
        return (best, len);
    }

    private static SseMessage? ParseFrame(string frame)
    {
        var ev = "";
        var data = new List<string>();
        foreach (var line in frame.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") ev = value;
            else if (field == "data") data.Add(value);
        }
        return data.Count == 0 ? null : new SseMessage(ev, string.Join('\n', data));
    }
}

/// <summary>
/// How <see cref="ApiClient.StreamReconnectingAsync{T}"/> keeps a feed alive. Ports the loop in web/src/api/liveRequests.ts
/// (1 s, doubling to 15 s). Differences: the delay also resets once a connection opened (web resets only when the stream
/// ended cleanly, so a server restart left it at the cap), and the "page hidden" pause becomes the injectable <see cref="Gate"/>.
/// </summary>
public sealed class SseReconnectOptions
{
    /// <summary>Wait before the first retry; also what the wait falls back to after a connection that opened.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap of the doubling wait.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The server accepted the stream (before any event).</summary>
    public Action? OnOpen { get; init; }

    /// <summary>The connection ended or failed (null error: the server closed it cleanly); fires before the wait.</summary>
    public Action<Exception?>? OnDrop { get; init; }

    /// <summary>Awaited before every connection attempt; hold it while the feed is not wanted (window hidden). Replaces the web's visibilitychange handling.</summary>
    public Func<CancellationToken, Task>? Gate { get; init; }

    /// <summary>Clock for the waits; tests inject a manual one.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
